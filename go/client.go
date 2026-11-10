package pagerush

import (
	"bytes"
	"context"
	"crypto/rand"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"math"
	mathrand "math/rand"
	"net"
	"net/http"
	"net/url"
	"os"
	"strings"
	"time"
	"unicode"
)

// DefaultBaseURL is the API host used when neither WithBaseURL nor the
// PAGERUSH_BASE_URL environment variable is set.
const DefaultBaseURL = "https://pagerush.dev"

// DefaultTimeout is the default limit for a single HTTP attempt.
const DefaultTimeout = 60 * time.Second

// DefaultMaxRetries is the default number of retries after the first attempt.
const DefaultMaxRetries = 2

const (
	maxRetryAfter = 30 * time.Second
	maxBackoff    = 8 * time.Second
	baseBackoff   = 500 * time.Millisecond
)

// Client calls the PageRush HTTP API (v1). Create one with NewClient and reuse
// it; it is safe for concurrent use.
//
//	client, err := pagerush.NewClient("") // reads PAGERUSH_API_KEY
//	if err != nil {
//		log.Fatal(err)
//	}
//	res, err := client.Templates.Render(ctx, "tpl_123", &pagerush.RenderParams{
//		Data: map[string]any{"name": "Ada"},
//	})
//	if err != nil {
//		log.Fatal(err)
//	}
//	err = res.WriteFile("invoice.pdf")
type Client struct {
	// Templates manages templates and renders them to PDF.
	Templates *TemplatesService
	// Jobs reads render jobs, waits for them and downloads their PDFs.
	Jobs *JobsService

	apiKey     string
	baseURL    string
	httpClient *http.Client
	timeout    time.Duration
	maxRetries int

	// Test hooks.
	sleep func(ctx context.Context, d time.Duration) error
	now   func() time.Time
}

// Option configures a Client. Pass options to NewClient.
type Option func(*Client)

// WithBaseURL sets the API base URL (default https://pagerush.dev, or the
// PAGERUSH_BASE_URL environment variable). A trailing slash is removed.
func WithBaseURL(baseURL string) Option {
	return func(c *Client) { c.baseURL = baseURL }
}

// WithHTTPClient sets the *http.Client used for every request. Use it to add
// a proxy, custom transport or test server. A nil client is ignored.
func WithHTTPClient(hc *http.Client) Option {
	return func(c *Client) {
		if hc != nil {
			c.httpClient = hc
		}
	}
}

// WithTimeout sets the limit for each HTTP attempt (default 60 s). Retries get
// a fresh timeout each. A value of zero or less disables the per-attempt limit;
// the context passed to each call still applies.
func WithTimeout(d time.Duration) Option {
	return func(c *Client) { c.timeout = d }
}

// WithMaxRetries sets how many times a failed request is retried after the
// first attempt (default 2, so up to 3 attempts). Negative values mean 0.
func WithMaxRetries(n int) Option {
	return func(c *Client) {
		if n < 0 {
			n = 0
		}
		c.maxRetries = n
	}
}

// NewClient returns a Client for the given project API key (pk_live_...).
// Surrounding whitespace is trimmed. If apiKey is empty, the PAGERUSH_API_KEY
// environment variable is used. NewClient returns an error if there is no
// key, if the key contains spaces or control characters, or if the base URL
// is not an absolute http or https URL. Error messages never include the key.
func NewClient(apiKey string, opts ...Option) (*Client, error) {
	apiKey = strings.TrimSpace(apiKey)
	if apiKey == "" {
		apiKey = strings.TrimSpace(os.Getenv("PAGERUSH_API_KEY"))
	}
	if apiKey == "" {
		return nil, errors.New("pagerush: missing API key: pass one to NewClient or set PAGERUSH_API_KEY")
	}
	if strings.IndexFunc(apiKey, func(r rune) bool { return r <= ' ' || r == 0x7f || unicode.IsSpace(r) || unicode.IsControl(r) }) >= 0 {
		return nil, errors.New("pagerush: invalid API key: it contains spaces or control characters")
	}
	baseURL := os.Getenv("PAGERUSH_BASE_URL")
	if baseURL == "" {
		baseURL = DefaultBaseURL
	}
	c := &Client{
		apiKey:     apiKey,
		baseURL:    baseURL,
		httpClient: http.DefaultClient,
		timeout:    DefaultTimeout,
		maxRetries: DefaultMaxRetries,
		sleep:      sleepContext,
		now:        time.Now,
	}
	for _, opt := range opts {
		if opt != nil {
			opt(c)
		}
	}
	c.baseURL = strings.TrimRight(c.baseURL, "/")
	if u, err := url.Parse(c.baseURL); err != nil || (u.Scheme != "http" && u.Scheme != "https") || u.Host == "" {
		return nil, fmt.Errorf("pagerush: invalid base URL %q: want an absolute http or https URL", c.baseURL)
	}
	c.httpClient = withSafeRedirects(c.httpClient)
	c.Templates = &TemplatesService{client: c}
	c.Jobs = &JobsService{client: c}
	return c, nil
}

// BaseURL returns the API base URL the client sends requests to.
func (c *Client) BaseURL() string { return c.baseURL }

// String describes the client without its API key, so printing a Client
// (for example with %v or %+v) never reveals the key.
func (c Client) String() string {
	return fmt.Sprintf("pagerush.Client{BaseURL: %q, APIKey: %q}", c.baseURL, redactedKey)
}

// GoString is like String, for the %#v verb.
func (c Client) GoString() string { return c.String() }

const redactedKey = "[redacted]"

// withSafeRedirects returns a copy of hc whose redirects never carry the
// Authorization header to another host or from https to http. Any
// CheckRedirect policy of hc still applies.
func withSafeRedirects(hc *http.Client) *http.Client {
	cp := *hc
	next := hc.CheckRedirect
	cp.CheckRedirect = func(req *http.Request, via []*http.Request) error {
		first := via[0].URL
		if req.URL.Host != first.Host || (first.Scheme == "https" && req.URL.Scheme != "https") {
			req.Header.Del("Authorization")
		}
		if next != nil {
			return next(req, via)
		}
		if len(via) >= 10 {
			return errors.New("stopped after 10 redirects")
		}
		return nil
	}
	return &cp
}

func sleepContext(ctx context.Context, d time.Duration) error {
	if d <= 0 {
		return ctx.Err()
	}
	t := time.NewTimer(d)
	defer t.Stop()
	select {
	case <-ctx.Done():
		return ctx.Err()
	case <-t.C:
		return nil
	}
}

// retryPolicy says which failures a request may be retried on.
type retryPolicy int

const (
	// retrySafe: GET, DELETE and render (which carries an idempotency key).
	// Retries on connection errors, timeouts, 429, 502, 503 and 504.
	retrySafe retryPolicy = iota
	// retryUnsafe: other POST and PATCH requests. Retries only on 429 and 503,
	// which the server returns before doing anything.
	retryUnsafe
	// retryRender: as retrySafe, plus 409 CONFLICT, which the server returns
	// while it is still rendering an earlier attempt with the same
	// idempotency key.
	retryRender
)

type request struct {
	method  string
	url     string // absolute URL
	body    any    // JSON-encoded when non-nil
	headers map[string]string
	accept  string
	noAuth  bool
	policy  retryPolicy
}

type response struct {
	status int
	header http.Header
	body   []byte
}

func (c *Client) apiURL(path string, query url.Values) string {
	u := c.baseURL + path
	if len(query) > 0 {
		u += "?" + query.Encode()
	}
	return u
}

// do sends a request with retries and returns the final 2xx response, or an
// error (*Error for HTTP and connection failures).
func (c *Client) do(ctx context.Context, r request) (*response, error) {
	var payload []byte
	if r.body != nil {
		b, err := json.Marshal(r.body)
		if err != nil {
			return nil, &Error{Code: CodeValidationFailed, Detail: "request body cannot be encoded as JSON: " + err.Error(), cause: err}
		}
		payload = b
	}
	for attempt := 0; ; attempt++ {
		resp, err := c.attempt(ctx, r, payload)
		if err == nil && resp.status >= 200 && resp.status < 300 {
			return resp, nil
		}
		if err == nil {
			err = errorFromResponse(resp)
		}
		if attempt >= c.maxRetries || ctx.Err() != nil || !retryable(r.policy, err) {
			return nil, err
		}
		var delay time.Duration
		var apiErr *Error
		errors.As(err, &apiErr)
		if apiErr != nil && apiErr.retryAfterSet {
			delay = apiErr.RetryAfter
			if delay > maxRetryAfter {
				delay = maxRetryAfter
			}
		} else {
			delay = backoff(attempt + 1)
		}
		if serr := c.sleep(ctx, delay); serr != nil {
			// Report the failure that caused the retry, and still let
			// errors.Is find the context error.
			if apiErr != nil {
				last := *apiErr
				last.cause = joinCause(serr, apiErr.cause)
				return nil, &last
			}
			return nil, connectionError(ctx, serr)
		}
	}
}

// backoff returns min(8, 0.5 * 2^(n-1)) seconds with ±25 % jitter.
func backoff(n int) time.Duration {
	d := float64(baseBackoff) * math.Pow(2, float64(n-1))
	if d > float64(maxBackoff) {
		d = float64(maxBackoff)
	}
	jitter := 0.75 + mathrand.Float64()*0.5
	return time.Duration(d * jitter)
}

func retryable(policy retryPolicy, err error) bool {
	var apiErr *Error
	if !errors.As(err, &apiErr) {
		return false
	}
	idempotent := policy == retrySafe || policy == retryRender
	if apiErr.Status == 0 {
		return idempotent && (apiErr.Code == CodeConnectionError || apiErr.Code == CodeTimeout)
	}
	switch apiErr.Status {
	case http.StatusTooManyRequests, http.StatusServiceUnavailable:
		return true
	case http.StatusBadGateway, http.StatusGatewayTimeout:
		return idempotent
	case http.StatusConflict:
		return policy == retryRender && apiErr.Code == CodeConflict
	}
	return false
}

func (c *Client) attempt(ctx context.Context, r request, payload []byte) (*response, error) {
	actx := ctx
	if c.timeout > 0 {
		var cancel context.CancelFunc
		actx, cancel = context.WithTimeout(ctx, c.timeout)
		defer cancel()
	}
	var body io.Reader
	if payload != nil {
		body = bytes.NewReader(payload)
	}
	req, err := http.NewRequestWithContext(actx, r.method, r.url, body)
	if err != nil {
		err = redactURLError(err)
		return nil, &Error{Code: CodeValidationFailed, Detail: "cannot build request: " + err.Error(), cause: err}
	}
	if !r.noAuth {
		req.Header.Set("Authorization", "Bearer "+c.apiKey)
	}
	req.Header.Set("User-Agent", "pagerush-go/"+Version)
	accept := r.accept
	if accept == "" {
		accept = "application/json"
	}
	req.Header.Set("Accept", accept)
	if payload != nil {
		req.Header.Set("Content-Type", "application/json")
	}
	for k, v := range r.headers {
		req.Header.Set(k, v)
	}
	res, err := c.httpClient.Do(req)
	if err != nil {
		return nil, connectionError(ctx, redactURLError(err))
	}
	defer res.Body.Close()
	data, err := io.ReadAll(res.Body)
	if err != nil {
		return nil, connectionError(ctx, redactURLError(err))
	}
	return &response{status: res.StatusCode, header: res.Header, body: data}, nil
}

// connectionError wraps a transport failure. parent is the caller's context:
// if it is done, its error decides the code; otherwise a deadline error came
// from the per-attempt timeout.
func connectionError(parent context.Context, err error) *Error {
	code := CodeConnectionError
	var netErr net.Error
	switch {
	case parent.Err() != nil:
		if errors.Is(parent.Err(), context.DeadlineExceeded) {
			code = CodeTimeout
		}
	case errors.Is(err, context.DeadlineExceeded),
		errors.As(err, &netErr) && netErr.Timeout():
		code = CodeTimeout
	}
	cause := err
	if perr := parent.Err(); perr != nil && !errors.Is(err, perr) {
		// Keep errors.Is(err, context.Canceled) working when the transport
		// reported the cancellation in its own words.
		cause = joinCause(perr, err)
	}
	return &Error{Code: code, Detail: err.Error(), cause: cause}
}

// joinCause wraps both errors so errors.Is and errors.As find either.
func joinCause(outer, inner error) error {
	if inner == nil {
		return outer
	}
	return fmt.Errorf("%w: %w", outer, inner)
}

// redactURLError removes the query string and fragment from the URL in a
// *url.Error, so signed download links never appear in error messages.
func redactURLError(err error) error {
	var uerr *url.Error
	if !errors.As(err, &uerr) {
		return err
	}
	cp := *uerr
	cp.URL = stripQuery(uerr.URL)
	return &cp
}

func stripQuery(raw string) string {
	if i := strings.IndexAny(raw, "?#"); i >= 0 {
		return raw[:i]
	}
	return raw
}

// newIdempotencyKey returns a random UUID v4.
func newIdempotencyKey() string {
	var b [16]byte
	_, _ = rand.Read(b[:]) // crypto/rand does not fail on supported platforms.
	b[6] = (b[6] & 0x0f) | 0x40
	b[8] = (b[8] & 0x3f) | 0x80
	return fmt.Sprintf("%x-%x-%x-%x-%x", b[0:4], b[4:6], b[6:8], b[8:10], b[10:16])
}

func (c *Client) getJSON(ctx context.Context, path string, query url.Values, out any) error {
	return c.sendJSON(ctx, request{method: http.MethodGet, url: c.apiURL(path, query), policy: retrySafe}, out)
}

func (c *Client) sendJSON(ctx context.Context, r request, out any) error {
	resp, err := c.do(ctx, r)
	if err != nil {
		return err
	}
	if out == nil {
		return nil
	}
	if err := json.Unmarshal(resp.body, out); err != nil {
		return invalidResponse(resp.status, "response body is not the expected JSON", err)
	}
	if v, ok := out.(validator); ok && !v.valid() {
		return invalidResponse(resp.status, "response body is missing required fields", nil)
	}
	return nil
}

// validator is implemented by response types that can tell whether the
// fields the SDK relies on are present.
type validator interface {
	valid() bool
}

// Int returns a pointer to v, for optional int fields.
func Int(v int) *int { return &v }

// String returns a pointer to v, for optional string fields.
func String(v string) *string { return &v }

// Bool returns a pointer to v, for optional bool fields.
func Bool(v bool) *bool { return &v }
