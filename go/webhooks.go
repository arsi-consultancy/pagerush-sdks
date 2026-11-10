package pagerush

import (
	"bytes"
	"crypto/hmac"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"math"
	"net/http"
	"regexp"
	"strconv"
	"strings"
	"time"
	"unicode/utf8"
)

// Webhook header names sent with every callback_url delivery.
const (
	HeaderWebhookEvent     = "X-PageRush-Event"
	HeaderWebhookJobID     = "X-PageRush-Job-Id"
	HeaderWebhookTimestamp = "X-PageRush-Timestamp"
	HeaderWebhookSignature = "X-PageRush-Signature"
	HeaderWebhookMetadata  = "X-PageRush-Metadata"
)

// Webhook event names.
const (
	EventPDFSucceeded = "pdf.succeeded"
	EventPDFFailed    = "pdf.failed"
)

// DefaultWebhookTolerance is how far the signature timestamp may be from the
// current time before VerifyWebhook rejects it.
const DefaultWebhookTolerance = 300 * time.Second

// Reasons in WebhookVerificationError.Reason.
const (
	// WebhookReasonHeader: a required header is missing or malformed.
	WebhookReasonHeader = "header"
	// WebhookReasonTimestamp: the timestamp is outside the tolerance.
	WebhookReasonTimestamp = "timestamp"
	// WebhookReasonSignature: no v2 signature matched any secret.
	WebhookReasonSignature = "signature"
)

// WebhookVerificationError is returned when a webhook delivery does not
// verify. Respond with 400 and do not trust the request.
type WebhookVerificationError struct {
	// Reason is WebhookReasonHeader, WebhookReasonTimestamp or
	// WebhookReasonSignature.
	Reason string
	msg    string
}

// Error describes the failure, including its Reason.
func (e *WebhookVerificationError) Error() string {
	return "pagerush: webhook verification failed (" + e.Reason + "): " + e.msg
}

// ErrNoWebhookSecret is returned by VerifyWebhook and ConstructEvent when no
// secret, or an empty secret, is passed. It is a configuration problem, not a
// verification failure.
var ErrNoWebhookSecret = errors.New("pagerush: no webhook secret: pass your project's webhook secret (is PAGERUSH_WEBHOOK_SECRET set?)")

type webhookConfig struct {
	tolerance time.Duration
	now       func() time.Time
}

// WebhookOption configures VerifyWebhook and ConstructEvent.
type WebhookOption func(*webhookConfig)

// WithWebhookTolerance sets how far the signature timestamp may be from now
// (default 300 s).
func WithWebhookTolerance(d time.Duration) WebhookOption {
	return func(c *webhookConfig) { c.tolerance = d }
}

// WithWebhookNow sets the time to check the signature timestamp against,
// instead of time.Now. Useful in tests.
func WithWebhookNow(t time.Time) WebhookOption {
	return func(c *webhookConfig) { c.now = func() time.Time { return t } }
}

func newWebhookConfig(opts []WebhookOption) webhookConfig {
	cfg := webhookConfig{tolerance: DefaultWebhookTolerance, now: time.Now}
	for _, o := range opts {
		if o != nil {
			o(&cfg)
		}
	}
	return cfg
}

// VerifyWebhook checks that a callback delivery was signed by PageRush. The
// signature in the X-PageRush-Signature header covers the X-PageRush-Event,
// X-PageRush-Job-Id and X-PageRush-Metadata headers as well as the body.
// Header lookup is case-insensitive.
//
// Pass every secret you accept (during a secret rotation, the current and
// the previous one). VerifyWebhook returns nil if any signature matches, a
// *WebhookVerificationError if the delivery does not verify, or
// ErrNoWebhookSecret if secrets is empty or contains an empty string.
//
// body must be the exact bytes received. Do not parse and re-serialise JSON
// before verifying; the signature will not match.
func VerifyWebhook(body []byte, h http.Header, secrets []string, opts ...WebhookOption) error {
	_, _, err := verifyWebhook(body, h, secrets, newWebhookConfig(opts))
	return err
}

var timestampPattern = regexp.MustCompile(`^[0-9]{1,15}$`)

// signedHeaders are the header values a signature covers.
type signedHeaders struct {
	event, jobID, metadata string
}

func verifyWebhook(body []byte, h http.Header, secrets []string, cfg webhookConfig) (signedHeaders, int64, error) {
	var sh signedHeaders
	if len(secrets) == 0 {
		return sh, 0, ErrNoWebhookSecret
	}
	for _, secret := range secrets {
		if secret == "" {
			return sh, 0, ErrNoWebhookSecret
		}
	}

	var (
		ts     int64
		rawTS  string
		haveTS bool
		sigs   []string
	)
	for _, part := range strings.Split(headerValue(h, HeaderWebhookSignature), ",") {
		key, value, ok := strings.Cut(strings.TrimSpace(part), "=")
		if !ok {
			continue
		}
		switch key {
		case "t":
			if haveTS || !timestampPattern.MatchString(value) {
				return sh, 0, headerError("need exactly one t of 1 to 15 digits")
			}
			n, err := strconv.ParseInt(value, 10, 64)
			if err != nil {
				return sh, 0, headerError("need exactly one t of 1 to 15 digits")
			}
			ts, rawTS, haveTS = n, value, true
		case "v2":
			sigs = append(sigs, value)
		}
	}
	if !haveTS || len(sigs) == 0 {
		return sh, 0, headerError("need t=<unix seconds> and at least one v2=<hex> in " + HeaderWebhookSignature)
	}
	var hasEvent, hasJobID bool
	sh.event, hasEvent = lookupHeader(h, HeaderWebhookEvent)
	sh.jobID, hasJobID = lookupHeader(h, HeaderWebhookJobID)
	sh.metadata, _ = lookupHeader(h, HeaderWebhookMetadata)
	if !hasEvent || !hasJobID {
		return sh, 0, headerError("missing " + HeaderWebhookEvent + " or " + HeaderWebhookJobID)
	}

	age := math.Abs(float64(cfg.now().Unix() - ts))
	if age > cfg.tolerance.Seconds() {
		return sh, 0, &WebhookVerificationError{Reason: WebhookReasonTimestamp, msg: fmt.Sprintf("timestamp is %.0f s from now, tolerance is %s", age, cfg.tolerance)}
	}

	// The signed string uses the timestamp exactly as it appears in the header.
	prefix := rawTS + "\n" + sh.event + "\n" + sh.jobID + "\n" + sh.metadata + "\n"
	for _, secret := range secrets {
		mac := hmac.New(sha256.New, []byte(secret))
		mac.Write([]byte(prefix))
		mac.Write(body)
		expected := []byte(hex.EncodeToString(mac.Sum(nil)))
		for _, sig := range sigs {
			if hmac.Equal(expected, []byte(sig)) {
				return sh, ts, nil
			}
		}
	}
	return sh, 0, &WebhookVerificationError{Reason: WebhookReasonSignature, msg: "no v2 signature matches"}
}

// WebhookEvent is a verified callback delivery. Every field comes from
// signed data. For an event other than pdf.succeeded and pdf.failed, PDF and
// Failure are nil.
type WebhookEvent struct {
	// Event is EventPDFSucceeded or EventPDFFailed.
	Event string `json:"event"`
	// JobID identifies the job. It is the same on every retry of a delivery;
	// delivery is at-least-once, so ignore job IDs you have already handled.
	JobID string `json:"job_id"`
	// Timestamp is the signed t value of the signature header.
	Timestamp time.Time `json:"timestamp"`
	// PDF is the document (pdf.succeeded only).
	PDF []byte `json:"pdf,omitempty"`
	// Failure is the parsed body (pdf.failed only).
	Failure *WebhookFailure `json:"failure,omitempty"`
	// Metadata is the job's metadata: from the X-PageRush-Metadata header on
	// pdf.succeeded, from the body on pdf.failed. It is nil if absent or not
	// a JSON object of strings.
	Metadata map[string]string `json:"metadata"`
}

// WebhookFailure is the JSON body of a pdf.failed delivery.
type WebhookFailure struct {
	Event      string    `json:"event"`
	JobID      string    `json:"job_id"`
	TemplateID *string   `json:"template_id"`
	Error      JobError  `json:"error"`
	CreatedAt  time.Time `json:"created_at"`
	// Metadata is the job's metadata, or nil if it has none or it is not an
	// object of strings.
	Metadata map[string]string `json:"-"`
}

// ConstructEvent verifies a callback delivery, as VerifyWebhook does, and
// parses it. It returns the same errors as VerifyWebhook, and a
// *WebhookVerificationError with reason WebhookReasonHeader if a pdf.failed
// body is not a JSON object.
//
// body must be the exact bytes received.
func ConstructEvent(body []byte, h http.Header, secrets []string, opts ...WebhookOption) (*WebhookEvent, error) {
	sh, ts, err := verifyWebhook(body, h, secrets, newWebhookConfig(opts))
	if err != nil {
		return nil, err
	}
	ev := &WebhookEvent{Event: sh.event, JobID: sh.jobID, Timestamp: time.Unix(ts, 0).UTC()}
	switch ev.Event {
	case EventPDFSucceeded:
		ev.PDF = body
		ev.Metadata = decodeMetadata([]byte(sh.metadata))
	case EventPDFFailed:
		var raw struct {
			WebhookFailure
			RawMetadata json.RawMessage `json:"metadata"`
		}
		trimmed := bytes.TrimSpace(body)
		if !bytes.HasPrefix(trimmed, []byte("{")) || json.Unmarshal(trimmed, &raw) != nil {
			return nil, headerError("pdf.failed body is not a JSON object")
		}
		f := raw.WebhookFailure
		f.Metadata = decodeMetadata(raw.RawMetadata)
		ev.Failure, ev.Metadata = &f, f.Metadata
	}
	return ev, nil
}

func headerError(msg string) *WebhookVerificationError {
	return &WebhookVerificationError{Reason: WebhookReasonHeader, msg: msg}
}

// decodeMetadata returns a JSON object of strings as a map, or nil if raw is
// empty, null, not valid UTF-8 or not such an object.
func decodeMetadata(raw []byte) map[string]string {
	if len(raw) == 0 || !utf8.Valid(raw) {
		return nil
	}
	var m map[string]string
	if json.Unmarshal(raw, &m) != nil {
		return nil
	}
	return m
}

// headerValue looks a header up case-insensitively, including in maps built
// by hand with non-canonical keys. It returns "" if the header is absent.
func headerValue(h http.Header, name string) string {
	v, _ := lookupHeader(h, name)
	return v
}

// lookupHeader returns a header's first value and whether it is present.
func lookupHeader(h http.Header, name string) (string, bool) {
	if vs, ok := h[http.CanonicalHeaderKey(name)]; ok && len(vs) > 0 {
		return vs[0], true
	}
	for k, vs := range h {
		if strings.EqualFold(k, name) && len(vs) > 0 {
			return vs[0], true
		}
	}
	return "", false
}
