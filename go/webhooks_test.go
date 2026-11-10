package pagerush

import (
	"crypto/hmac"
	"crypto/sha256"
	"encoding/base64"
	"encoding/hex"
	"encoding/json"
	"errors"
	"net/http"
	"os"
	"strconv"
	"testing"
	"time"
)

type webhookVector struct {
	Name             string            `json:"name"`
	BodyBase64       string            `json:"body_base64"`
	Headers          map[string]string `json:"headers"`
	Secrets          []string          `json:"secrets"`
	Now              int64             `json:"now"`
	ToleranceSeconds int64             `json:"tolerance_seconds"`
	Valid            bool              `json:"valid"`
	Reason           string            `json:"reason"`
}

func TestWebhookVectors(t *testing.T) {
	raw, err := os.ReadFile("testdata/webhooks.json")
	if err != nil {
		t.Fatal(err)
	}
	var file struct {
		Cases []webhookVector `json:"cases"`
	}
	if err := json.Unmarshal(raw, &file); err != nil {
		t.Fatal(err)
	}
	if len(file.Cases) == 0 {
		t.Fatal("no cases")
	}
	for _, tc := range file.Cases {
		t.Run(tc.Name, func(t *testing.T) {
			body, err := base64.StdEncoding.DecodeString(tc.BodyBase64)
			if err != nil {
				t.Fatal(err)
			}
			h := http.Header{}
			for k, v := range tc.Headers {
				h.Set(k, v)
			}
			opts := []WebhookOption{
				WithWebhookTolerance(time.Duration(tc.ToleranceSeconds) * time.Second),
				WithWebhookNow(time.Unix(tc.Now, 0)),
			}
			err = VerifyWebhook(body, h, tc.Secrets, opts...)
			if !tc.Valid {
				var verr *WebhookVerificationError
				if !errors.As(err, &verr) {
					t.Fatalf("expected *WebhookVerificationError, got %v", err)
				}
				if verr.Reason != tc.Reason {
					t.Fatalf("reason = %q, want %q", verr.Reason, tc.Reason)
				}
				return
			}
			if err != nil {
				t.Fatalf("expected valid, got %v", err)
			}

			ev, err := ConstructEvent(body, h, tc.Secrets, opts...)
			if err != nil {
				t.Fatalf("ConstructEvent: %v", err)
			}
			if ev.Event != tc.Headers["X-PageRush-Event"] || ev.JobID != tc.Headers["X-PageRush-Job-Id"] {
				t.Fatalf("event = %+v", ev)
			}
			switch ev.Event {
			case EventPDFSucceeded:
				if string(ev.PDF) != string(body) || ev.Failure != nil {
					t.Fatalf("event = %+v", ev)
				}
				if md := tc.Headers["X-PageRush-Metadata"]; md != "" {
					var want map[string]string
					if err := json.Unmarshal([]byte(md), &want); err != nil {
						t.Fatal(err)
					}
					if len(ev.Metadata) != len(want) {
						t.Fatalf("metadata = %v, want %v", ev.Metadata, want)
					}
					for k, v := range want {
						if ev.Metadata[k] != v {
							t.Fatalf("metadata = %v, want %v", ev.Metadata, want)
						}
					}
				} else if ev.Metadata != nil {
					t.Fatalf("metadata = %v, want nil", ev.Metadata)
				}
			case EventPDFFailed:
				if ev.PDF != nil || ev.Failure == nil || ev.Failure.Error.Code == "" {
					t.Fatalf("event = %+v", ev)
				}
			}
		})
	}
}

// signedRequest returns headers for a delivery signed with secret.
func signedRequest(secret string, ts int64, event, jobID, metadata string, body []byte) http.Header {
	t := strconv.FormatInt(ts, 10)
	h := http.Header{}
	h.Set(HeaderWebhookEvent, event)
	h.Set(HeaderWebhookJobID, jobID)
	if metadata != "" {
		h.Set(HeaderWebhookMetadata, metadata)
	}
	h.Set(HeaderWebhookSignature, "t="+t+",v2="+signV2(secret, t, event, jobID, metadata, body))
	return h
}

func signV2(secret, t, event, jobID, metadata string, body []byte) string {
	mac := hmac.New(sha256.New, []byte(secret))
	mac.Write([]byte(t + "\n" + event + "\n" + jobID + "\n" + metadata + "\n"))
	mac.Write(body)
	return hex.EncodeToString(mac.Sum(nil))
}

func TestVerifyWebhookEdgeCases(t *testing.T) {
	body := []byte("%PDF-1.7")
	now := time.Unix(1_767_225_600, 0)
	good := signedRequest("s", now.Unix(), EventPDFSucceeded, "job_1", "", body)

	t.Run("missing event header", func(t *testing.T) {
		h := good.Clone()
		h.Del(HeaderWebhookEvent)
		assertReason(t, VerifyWebhook(body, h, []string{"s"}, WithWebhookNow(now)), WebhookReasonHeader)
	})
	t.Run("missing job id header", func(t *testing.T) {
		h := good.Clone()
		h.Del(HeaderWebhookJobID)
		assertReason(t, VerifyWebhook(body, h, []string{"s"}, WithWebhookNow(now)), WebhookReasonHeader)
	})
	t.Run("signed t", func(t *testing.T) {
		h := good.Clone()
		h.Set(HeaderWebhookSignature, "t=+1767225600,v2=00")
		assertReason(t, VerifyWebhook(body, h, []string{"s"}, WithWebhookNow(now)), WebhookReasonHeader)
	})
	t.Run("pair without =", func(t *testing.T) {
		h := good.Clone()
		h.Set(HeaderWebhookSignature, "garbage,"+good.Get(HeaderWebhookSignature))
		if err := VerifyWebhook(body, h, []string{"s"}, WithWebhookNow(now)); err != nil {
			t.Fatal(err)
		}
	})
	t.Run("lowercase header keys", func(t *testing.T) {
		h := http.Header{}
		for k, v := range good {
			h[lower(k)] = v
		}
		if err := VerifyWebhook(body, h, []string{"s"}, WithWebhookNow(now)); err != nil {
			t.Fatal(err)
		}
	})
	t.Run("timestamp signed as sent", func(t *testing.T) {
		h := good.Clone()
		h.Set(HeaderWebhookSignature, "t=01767225600,v2="+signV2("s", "01767225600", EventPDFSucceeded, "job_1", "", body))
		if err := VerifyWebhook(body, h, []string{"s"}, WithWebhookNow(now)); err != nil {
			t.Fatal(err)
		}
	})
	t.Run("default tolerance", func(t *testing.T) {
		if err := VerifyWebhook(body, signedRequest("s", time.Now().Unix()-299, EventPDFSucceeded, "j", "", body), []string{"s"}); err != nil {
			t.Fatal(err)
		}
		err := VerifyWebhook(body, signedRequest("s", time.Now().Unix()-400, EventPDFSucceeded, "j", "", body), []string{"s"})
		assertReason(t, err, WebhookReasonTimestamp)
	})
	t.Run("missing or empty secrets", func(t *testing.T) {
		for _, secrets := range [][]string{nil, {}, {""}, {"s", ""}} {
			err := VerifyWebhook(body, good, secrets, WithWebhookNow(now))
			var verr *WebhookVerificationError
			if !errors.Is(err, ErrNoWebhookSecret) || errors.As(err, &verr) {
				t.Fatalf("secrets %q: err = %v", secrets, err)
			}
		}
		// A delivery signed with an empty key is never accepted.
		empty := signedRequest("", now.Unix(), EventPDFSucceeded, "job_1", "", body)
		if err := VerifyWebhook(body, empty, []string{""}, WithWebhookNow(now)); err == nil {
			t.Fatal("empty secret verified a delivery")
		}
	})
}

func lower(s string) string {
	b := []byte(s)
	for i, c := range b {
		if c >= 'A' && c <= 'Z' {
			b[i] = c + 'a' - 'A'
		}
	}
	return string(b)
}

func assertReason(t *testing.T, err error, reason string) {
	t.Helper()
	var verr *WebhookVerificationError
	if !errors.As(err, &verr) || verr.Reason != reason {
		t.Fatalf("err = %v, want reason %q", err, reason)
	}
}

func TestConstructEventSucceeded(t *testing.T) {
	body := []byte("%PDF-1.7")
	now := time.Unix(1_767_225_600, 0)
	h := signedRequest("whsec", now.Unix(), EventPDFSucceeded, "job_1", `{"order":"42","note":"₹"}`, body)
	ev, err := ConstructEvent(body, h, []string{"other", "whsec"}, WithWebhookNow(now))
	if err != nil {
		t.Fatal(err)
	}
	if ev.Event != EventPDFSucceeded || ev.JobID != "job_1" || string(ev.PDF) != "%PDF-1.7" ||
		ev.Failure != nil || ev.Metadata["order"] != "42" || ev.Metadata["note"] != "₹" || !ev.Timestamp.Equal(now) {
		t.Fatalf("event = %+v", ev)
	}
}

func TestConstructEventFailed(t *testing.T) {
	body := []byte(`{"event":"pdf.failed","job_id":"job_2","template_id":"tpl_1","error":{"code":"TEMPLATE_COMPILE_FAILED","message":"unknown variable: foo"},"metadata":{"order":"42"},"created_at":"2026-01-01T00:00:00.000Z"}`)
	now := time.Unix(1_767_225_600, 0)
	ev, err := ConstructEvent(body, signedRequest("whsec", now.Unix(), EventPDFFailed, "job_2", "", body), []string{"whsec"}, WithWebhookNow(now))
	if err != nil {
		t.Fatal(err)
	}
	if ev.Event != EventPDFFailed || ev.JobID != "job_2" || ev.PDF != nil || ev.Failure == nil ||
		ev.Failure.Error.Code != CodeTemplateCompileFailed || *ev.Failure.TemplateID != "tpl_1" ||
		ev.Metadata["order"] != "42" || ev.Failure.Metadata["order"] != "42" {
		t.Fatalf("event = %+v", ev)
	}
}

func TestConstructEventFailedBodyNotObject(t *testing.T) {
	now := time.Unix(1_767_225_600, 0)
	for _, body := range []string{"not json", "[]", "null", `"x"`, `{"event":`} {
		b := []byte(body)
		_, err := ConstructEvent(b, signedRequest("whsec", now.Unix(), EventPDFFailed, "job_2", "", b), []string{"whsec"}, WithWebhookNow(now))
		assertReason(t, err, WebhookReasonHeader)
	}
}

func TestConstructEventUnknownEvent(t *testing.T) {
	now := time.Unix(1_767_225_600, 0)
	body := []byte(`{}`)
	ev, err := ConstructEvent(body, signedRequest("whsec", now.Unix(), "pdf.archived", "job_3", "", body), []string{"whsec"}, WithWebhookNow(now))
	if err != nil || ev.Event != "pdf.archived" || ev.JobID != "job_3" || ev.PDF != nil || ev.Failure != nil {
		t.Fatalf("ev = %+v, err = %v", ev, err)
	}
}

func TestConstructEventMetadata(t *testing.T) {
	now := time.Unix(1_767_225_600, 0)
	body := []byte("%PDF-1.7")
	for _, raw := range []string{"{", `{"a":1}`, `["a"]`, "null", "\xff\xfe"} {
		h := signedRequest("whsec", now.Unix(), EventPDFSucceeded, "job_1", raw, body)
		ev, err := ConstructEvent(body, h, []string{"whsec"}, WithWebhookNow(now))
		if err != nil || ev.Metadata != nil {
			t.Fatalf("metadata %q: ev = %+v, err = %v", raw, ev, err)
		}
	}
	// Metadata in a pdf.failed body that is not an object of strings is nil.
	failed := []byte(`{"event":"pdf.failed","job_id":"job_2","metadata":{"n":1}}`)
	h := signedRequest("whsec", now.Unix(), EventPDFFailed, "job_2", "", failed)
	ev, err := ConstructEvent(failed, h, []string{"whsec"}, WithWebhookNow(now))
	if err != nil || ev.Metadata != nil || ev.Failure.Metadata != nil {
		t.Fatalf("ev = %+v, err = %v", ev, err)
	}
	if _, err := ConstructEvent(failed, h, nil); !errors.Is(err, ErrNoWebhookSecret) {
		t.Fatalf("err = %v", err)
	}
}
