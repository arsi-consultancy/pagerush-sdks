package pagerush

import (
	"encoding/json"
	"errors"
	"fmt"
	"math"
	"net/http"
	"strconv"
	"strings"
	"time"
)

// Error codes the API returns in the code field of an error, plus the SDK's own
// CodeUnknown, CodeConnectionError and CodeTimeout. The API may add codes;
// compare Error.Code against these constants rather than assuming the list is
// complete.
const (
	CodeValidationFailed      = "VALIDATION_FAILED"
	CodeInvalidAPIKey         = "INVALID_API_KEY"
	CodeProjectDisabled       = "PROJECT_DISABLED"
	CodeQuotaExceeded         = "QUOTA_EXCEEDED"
	CodeConcurrencyLimit      = "CONCURRENCY_LIMIT"
	CodeRateLimited           = "RATE_LIMITED"
	CodeIdempotencyConflict   = "IDEMPOTENCY_CONFLICT"
	CodeRequestTooLarge       = "REQUEST_TOO_LARGE"
	CodeTemplateNotFound      = "TEMPLATE_NOT_FOUND"
	CodeTemplateNotPublished  = "TEMPLATE_NOT_PUBLISHED"
	CodeInvalidData           = "INVALID_DATA"
	CodeTemplateCompileFailed = "TEMPLATE_COMPILE_FAILED"
	CodeRenderTimeout         = "RENDER_TIMEOUT"
	CodeRenderFailed          = "RENDER_FAILED"
	CodeRenderUnavailable     = "RENDER_UNAVAILABLE"
	CodeFileExpired           = "FILE_EXPIRED"
	CodeNotFound              = "NOT_FOUND"
	CodeInternal              = "INTERNAL"
	CodeUnauthenticated       = "UNAUTHENTICATED"
	CodeForbidden             = "FORBIDDEN"
	CodeURLNotAllowed         = "URL_NOT_ALLOWED"
	CodeInvalidOutput         = "INVALID_OUTPUT"
	// CodeConflict on a render means the API is still rendering an earlier
	// attempt with the same idempotency key. Render retries it.
	CodeConflict = "CONFLICT"

	// CodeUnknown is set when an error response has no problem+json code.
	CodeUnknown = "UNKNOWN"
	// CodeConnectionError is set when the request could not be sent or the
	// response could not be read (Status is 0).
	CodeConnectionError = "CONNECTION_ERROR"
	// CodeTimeout is set when an attempt timed out or the context deadline
	// passed (Status is 0).
	CodeTimeout = "TIMEOUT"
	// CodeWaitTimeout is set by Jobs.Wait when the job has not finished
	// within the wait timeout (Status is 0).
	CodeWaitTimeout = "WAIT_TIMEOUT"
	// CodeInvalidResponse is set when a 2xx response cannot be used: a body
	// that is not the expected JSON, or a render that is not a PDF. Status is
	// the response's status, and the error wraps ErrUnexpectedResponse.
	CodeInvalidResponse = "INVALID_RESPONSE"
)

// ErrUnexpectedResponse is wrapped by the *Error (code CodeInvalidResponse)
// returned when a 2xx response cannot be used, for example when a proxy
// answers with an HTML page. Check for it with errors.Is.
var ErrUnexpectedResponse = errors.New("pagerush: unexpected response body")

// FieldError is one entry of an error's errors list: a validation problem with
// the field at Path, or, for TEMPLATE_COMPILE_FAILED, a Typst diagnostic.
type FieldError struct {
	Path    string `json:"path"`
	Message string `json:"message"`
}

// Error is returned for every non-2xx API response and for connection
// failures and timeouts. Use errors.As to inspect it:
//
//	var perr *pagerush.Error
//	if errors.As(err, &perr) && perr.Code == pagerush.CodeTemplateCompileFailed {
//		for _, d := range perr.Errors {
//			fmt.Println(d.Path, d.Message)
//		}
//	}
type Error struct {
	// Status is the HTTP status, or 0 for connection errors and timeouts.
	Status int
	// Code is the machine-readable error code, such as "TEMPLATE_NOT_FOUND".
	Code string
	// Title is the short, human-readable summary from the response.
	Title string
	// Detail explains this occurrence of the error.
	Detail string
	// Errors lists field problems or Typst diagnostics. It may be empty.
	Errors []FieldError
	// RequestID identifies the request for PageRush support: the body's
	// request_id, or else the X-Request-Id header.
	RequestID string
	// RetryAfter is the Retry-After header as a duration; zero when absent.
	RetryAfter time.Duration
	// Type is the problem type URI from the response.
	Type string

	retryAfterSet bool
	cause         error
}

// Error returns "<code>: <detail or title>", falling back to "HTTP <status>".
func (e *Error) Error() string {
	msg := e.Detail
	if msg == "" {
		msg = e.Title
	}
	if msg == "" {
		msg = "HTTP " + strconv.Itoa(e.Status)
	}
	return e.Code + ": " + msg
}

// Unwrap returns the underlying cause: for a connection error or timeout, an
// error such as a *net.OpError or context.DeadlineExceeded; for
// CodeInvalidResponse, an error wrapping ErrUnexpectedResponse. If the context
// ended while waiting to retry an HTTP error, that error is returned and
// Unwrap gives the context's error. Otherwise it is nil for HTTP errors.
func (e *Error) Unwrap() error { return e.cause }

// HasRetryAfter reports whether the response carried a Retry-After header.
func (e *Error) HasRetryAfter() bool { return e.retryAfterSet }

type problem struct {
	Type      string       `json:"type"`
	Title     string       `json:"title"`
	Code      string       `json:"code"`
	Detail    string       `json:"detail"`
	Errors    []FieldError `json:"errors"`
	RequestID string       `json:"request_id"`
}

func errorFromResponse(resp *response) *Error {
	e := &Error{Status: resp.status, Code: CodeUnknown}
	var p problem
	if json.Unmarshal(resp.body, &p) == nil {
		if p.Code != "" {
			e.Code = p.Code
		}
		e.Type = p.Type
		e.Title = p.Title
		e.Detail = p.Detail
		e.Errors = p.Errors
		e.RequestID = p.RequestID
	}
	if e.RequestID == "" {
		e.RequestID = resp.header.Get("X-Request-Id")
	}
	if d, ok := parseRetryAfter(resp.header.Get("Retry-After")); ok {
		e.RetryAfter = d
		e.retryAfterSet = true
	}
	return e
}

func parseRetryAfter(v string) (time.Duration, bool) {
	v = strings.TrimSpace(v)
	if v == "" {
		return 0, false
	}
	if secs, err := strconv.ParseFloat(v, 64); err == nil && !math.IsNaN(secs) && !math.IsInf(secs, 0) {
		secs = math.Max(0, math.Min(secs, 86400))
		return time.Duration(secs * float64(time.Second)), true
	}
	if t, err := http.ParseTime(v); err == nil {
		d := time.Until(t)
		if d < 0 {
			d = 0
		}
		return d, true
	}
	return 0, false
}

func invalidResponse(status int, detail string, cause error) *Error {
	wrapped := ErrUnexpectedResponse
	if cause != nil {
		wrapped = fmt.Errorf("%w: %w", ErrUnexpectedResponse, cause)
	}
	return &Error{Status: status, Code: CodeInvalidResponse, Detail: detail, cause: wrapped}
}

func clientError(code, format string, args ...any) *Error {
	return &Error{Code: code, Detail: fmt.Sprintf(format, args...)}
}
