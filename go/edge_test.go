package pagerush

import (
	"context"
	"errors"
	"io"
	"math"
	"net/http"
	"testing"
	"time"
)

func TestParseRetryAfter(t *testing.T) {
	future := time.Now().Add(10 * time.Second).UTC().Format(http.TimeFormat)
	past := time.Now().Add(-time.Hour).UTC().Format(http.TimeFormat)
	cases := []struct {
		in     string
		ok     bool
		min    time.Duration
		max    time.Duration
		reason string
	}{
		{"", false, 0, 0, "empty"},
		{"   ", false, 0, 0, "blank"},
		{"soon", false, 0, 0, "garbage"},
		{"NaN", false, 0, 0, "NaN"},
		{"3", true, 3 * time.Second, 3 * time.Second, "seconds"},
		{"1.5", true, 1500 * time.Millisecond, 1500 * time.Millisecond, "fractional"},
		{"-4", true, 0, 0, "negative"},
		{future, true, 8 * time.Second, 10 * time.Second, "HTTP-date"},
		{past, true, 0, 0, "HTTP-date in the past"},
	}
	for _, tc := range cases {
		d, ok := parseRetryAfter(tc.in)
		if ok != tc.ok || d < tc.min || d > tc.max {
			t.Errorf("%s: parseRetryAfter(%q) = %v, %v", tc.reason, tc.in, d, ok)
		}
	}
}

func TestEmptyRetryAfterUsesBackoff(t *testing.T) {
	f := newFakeAPI(t, func(w http.ResponseWriter, r *http.Request, n int) {
		if n == 1 {
			w.Header().Set("Retry-After", "")
			writeProblem(w, 429, CodeRateLimited, nil)
			return
		}
		writeJSON(w, 200, jobJSON("queued", nil))
	})
	c, s := newTestClient(t, f)
	if _, err := c.Jobs.Get(context.Background(), "job_1"); err != nil {
		t.Fatal(err)
	}
	if len(s.delays) != 1 || s.delays[0] < 375*time.Millisecond || s.delays[0] > 625*time.Millisecond {
		t.Fatalf("delays = %v", s.delays)
	}
}

func TestNonJSONSuccessBody(t *testing.T) {
	f := newFakeAPI(t, func(w http.ResponseWriter, r *http.Request, n int) {
		w.Header().Set("Content-Type", "text/html")
		_, _ = io.WriteString(w, "<html>proxy page</html>")
	})
	c, _ := newTestClient(t, f)
	_, err := c.Jobs.Get(context.Background(), "job_1")
	if err == nil {
		t.Fatal("expected decoding error")
	}
	assertInvalidResponse(t, err, 200)

	// A 200 render that is not a PDF.
	f3 := newFakeAPI(t, func(w http.ResponseWriter, r *http.Request, n int) {
		w.Header().Set("Content-Type", "text/html")
		_, _ = io.WriteString(w, "<html></html>")
	})
	c3, _ := newTestClient(t, f3)
	_, err = c3.Templates.Render(context.Background(), "tpl_1", &RenderParams{Data: 1})
	assertInvalidResponse(t, err, 200)

	// A queued render whose body is not a job.
	f2 := newFakeAPI(t, func(w http.ResponseWriter, r *http.Request, n int) {
		w.WriteHeader(202)
		_, _ = io.WriteString(w, "accepted")
	})
	c2, _ := newTestClient(t, f2)
	_, err = c2.Templates.Render(context.Background(), "tpl_1", &RenderParams{Data: 1})
	assertInvalidResponse(t, err, 202)
}

func assertInvalidResponse(t *testing.T, err error, status int) {
	t.Helper()
	var perr *Error
	if !errors.As(err, &perr) || perr.Code != CodeInvalidResponse || perr.Status != status {
		t.Fatalf("err = %#v, want INVALID_RESPONSE with status %d", err, status)
	}
	if !errors.Is(err, ErrUnexpectedResponse) {
		t.Fatalf("err %v does not wrap ErrUnexpectedResponse", err)
	}
}

func TestMissingWrapperIsInvalidResponse(t *testing.T) {
	cases := []struct {
		name, body string
		call       func(c *Client) error
	}{
		{"get without template", `{"items":[]}`, func(c *Client) error {
			_, err := c.Templates.Get(context.Background(), "t")
			return err
		}},
		{"rename without template", `{}`, func(c *Client) error {
			_, err := c.Templates.Rename(context.Background(), "t", "n")
			return err
		}},
		{"publish without template", `{"template":null}`, func(c *Client) error {
			_, err := c.Templates.Publish(context.Background(), "t", 1)
			return err
		}},
		{"get version without version", `{"template":{}}`, func(c *Client) error {
			_, err := c.Templates.GetVersion(context.Background(), "t", 1)
			return err
		}},
		{"create without version", `{"template":{"id":"t"}}`, func(c *Client) error {
			_, err := c.Templates.Create(context.Background(), &CreateTemplateParams{Name: "n"})
			return err
		}},
		{"job without id", `{"status":"queued"}`, func(c *Client) error {
			_, err := c.Jobs.Get(context.Background(), "j")
			return err
		}},
		{"empty body", ``, func(c *Client) error {
			_, err := c.Jobs.Get(context.Background(), "j")
			return err
		}},
		{"queued render without job id", `{"status":"queued"}`, func(c *Client) error {
			_, err := c.Templates.Render(context.Background(), "t", &RenderParams{Data: 1})
			return err
		}},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			status := 200
			if tc.name == "queued render without job id" {
				status = 202
			}
			f := newFakeAPI(t, func(w http.ResponseWriter, r *http.Request, n int) {
				w.Header().Set("Content-Type", "application/json")
				w.WriteHeader(status)
				_, _ = io.WriteString(w, tc.body)
			})
			c, _ := newTestClient(t, f)
			assertInvalidResponse(t, tc.call(c), status)
			if len(f.reqs()) != 1 {
				t.Fatalf("invalid response was retried")
			}
		})
	}
}

func TestRetryPolicyByMethod(t *testing.T) {
	cases := []struct {
		name      string
		status    int
		call      func(c *Client) error
		wantCalls int
	}{
		{"render retried on 504", 504, func(c *Client) error {
			_, err := c.Templates.Render(context.Background(), "t", &RenderParams{Data: 1})
			return err
		}, 3},
		{"delete retried on 502", 502, func(c *Client) error { return c.Templates.Delete(context.Background(), "t") }, 3},
		{"rename not retried on 504", 504, func(c *Client) error {
			_, err := c.Templates.Rename(context.Background(), "t", "n")
			return err
		}, 1},
		{"publish not retried on 502", 502, func(c *Client) error {
			_, err := c.Templates.Publish(context.Background(), "t", 1)
			return err
		}, 1},
		{"create version retried on 429", 429, func(c *Client) error {
			_, err := c.Templates.CreateVersion(context.Background(), "t", nil)
			return err
		}, 3},
		{"get not retried on 500", 500, func(c *Client) error {
			_, err := c.Templates.Get(context.Background(), "t")
			return err
		}, 1},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			f := newFakeAPI(t, func(w http.ResponseWriter, r *http.Request, n int) {
				writeProblem(w, tc.status, CodeInternal, nil)
			})
			c, _ := newTestClient(t, f)
			err := tc.call(c)
			var perr *Error
			if !errors.As(err, &perr) || perr.Status != tc.status {
				t.Fatalf("err = %v", err)
			}
			if got := len(f.reqs()); got != tc.wantCalls {
				t.Fatalf("calls = %d, want %d", got, tc.wantCalls)
			}
		})
	}
}

func TestContextCanceledDuringBodyRead(t *testing.T) {
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	f := newFakeAPI(t, func(w http.ResponseWriter, r *http.Request, n int) {
		w.Header().Set("Content-Type", "application/pdf")
		_, _ = io.WriteString(w, "%PDF partial")
		w.(http.Flusher).Flush()
		cancel()
		<-r.Context().Done()
	})
	c, s := newTestClient(t, f)
	_, err := c.Templates.Render(ctx, "tpl_1", &RenderParams{Data: 1})
	var perr *Error
	if !errors.As(err, &perr) || perr.Code != CodeConnectionError || !errors.Is(err, context.Canceled) {
		t.Fatalf("err = %v", err)
	}
	if len(s.delays) != 0 {
		t.Fatalf("retried after cancel: %v", s.delays)
	}
}

func TestContextDeadlineIsTimeout(t *testing.T) {
	f := newFakeAPI(t, func(w http.ResponseWriter, r *http.Request, n int) {
		<-r.Context().Done()
	})
	c, s := newTestClient(t, f)
	ctx, cancel := context.WithTimeout(context.Background(), 50*time.Millisecond)
	defer cancel()
	_, err := c.Jobs.Get(ctx, "job_1")
	var perr *Error
	if !errors.As(err, &perr) || perr.Code != CodeTimeout || !errors.Is(err, context.DeadlineExceeded) {
		t.Fatalf("err = %v", err)
	}
	if len(s.delays) != 0 {
		t.Fatalf("retried after deadline: %v", s.delays)
	}
}

func TestSleepContext(t *testing.T) {
	ctx, cancel := context.WithCancel(context.Background())
	cancel()
	if err := sleepContext(ctx, time.Hour); !errors.Is(err, context.Canceled) {
		t.Fatalf("err = %v", err)
	}
	if err := sleepContext(context.Background(), time.Millisecond); err != nil {
		t.Fatal(err)
	}
	if err := sleepContext(context.Background(), 0); err != nil {
		t.Fatal(err)
	}
}

func TestJobsWaitContextCanceled(t *testing.T) {
	f := newFakeAPI(t, func(w http.ResponseWriter, r *http.Request, n int) {
		writeJSON(w, 200, jobJSON("rendering", nil))
	})
	c, _ := newTestClient(t, f)
	ctx, cancel := context.WithCancel(context.Background())
	c.sleep = func(ctx context.Context, d time.Duration) error {
		cancel()
		return sleepContext(ctx, d)
	}
	_, err := c.Jobs.Wait(ctx, "job_1", nil)
	if !errors.Is(err, context.Canceled) {
		t.Fatalf("err = %v", err)
	}
}

func TestJobsDownloadErrors(t *testing.T) {
	t.Run("signed link rejected", func(t *testing.T) {
		files := newFakeAPI(t, func(w http.ResponseWriter, r *http.Request, n int) {
			w.WriteHeader(403)
			_, _ = io.WriteString(w, "<Error>AccessDenied</Error>")
		})
		f := newFakeAPI(t, func(w http.ResponseWriter, r *http.Request, n int) {
			writeJSON(w, 200, jobJSON("succeeded", files.server.URL+"/f"))
		})
		c, _ := newTestClient(t, f)
		_, err := c.Jobs.Download(context.Background(), "job_1")
		var perr *Error
		if !errors.As(err, &perr) || perr.Status != 403 || perr.Code != CodeUnknown {
			t.Fatalf("err = %v", err)
		}
	})
	t.Run("job not found", func(t *testing.T) {
		f := newFakeAPI(t, func(w http.ResponseWriter, r *http.Request, n int) {
			writeProblem(w, 404, CodeNotFound, nil)
		})
		c, _ := newTestClient(t, f)
		_, err := c.Jobs.Download(context.Background(), "job_1")
		var perr *Error
		if !errors.As(err, &perr) || perr.Code != CodeNotFound {
			t.Fatalf("err = %v", err)
		}
	})
	t.Run("relative download_url resolved against base URL", func(t *testing.T) {
		f := newFakeAPI(t, func(w http.ResponseWriter, r *http.Request, n int) {
			if r.URL.Path == "/files/job_1" {
				if r.Header.Get("Authorization") != "" {
					t.Error("Authorization sent to download_url")
				}
				_, _ = io.WriteString(w, "%PDF")
				return
			}
			writeJSON(w, 200, jobJSON("succeeded", "/files/job_1"))
		})
		c, _ := newTestClient(t, f)
		pdf, err := c.Jobs.Download(context.Background(), "job_1")
		if err != nil || string(pdf) != "%PDF" {
			t.Fatalf("%q %v", pdf, err)
		}
	})
}

func TestPathEscaping(t *testing.T) {
	f := newFakeAPI(t, func(w http.ResponseWriter, r *http.Request, n int) {
		writeJSON(w, 200, jobJSON("queued", nil))
	})
	c, _ := newTestClient(t, f)
	_, _ = c.Jobs.Get(context.Background(), "a/b?c#d e")
	if got := f.reqs()[0].Path; got != "/v1/jobs/a%2Fb%3Fc%23d%20e" {
		t.Fatalf("path = %s", got)
	}
}

func TestEncodeError(t *testing.T) {
	c, _ := NewClient("k", WithBaseURL("http://127.0.0.1:1"))
	_, err := c.Templates.Render(context.Background(), "t", &RenderParams{Data: make(chan int)})
	if err == nil {
		t.Fatal("expected encoding error")
	}
	var perr *Error
	if !errors.As(err, &perr) || perr.Code != CodeValidationFailed || perr.Status != 0 {
		t.Fatalf("err = %v", err)
	}
	_, err = c.Templates.Render(context.Background(), "t", &RenderParams{Data: math.NaN()})
	if !errors.As(err, &perr) || perr.Code != CodeValidationFailed {
		t.Fatalf("NaN: err = %v", err)
	}
}

func TestErrorUnwrapNilForHTTPErrors(t *testing.T) {
	e := &Error{Status: 404, Code: CodeNotFound}
	if e.Unwrap() != nil {
		t.Fatal("Unwrap should be nil")
	}
	if e.Error() != "NOT_FOUND: HTTP 404" {
		t.Fatalf("message = %q", e.Error())
	}
}
