package pagerush

import (
	"context"
	"errors"
	"fmt"
	"io"
	"net"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strings"
	"sync"
	"testing"
	"time"
)

func TestNewClientKeyValidation(t *testing.T) {
	t.Setenv("PAGERUSH_API_KEY", "")
	c, err := NewClient("  pk_test_123\n")
	if err != nil || c.apiKey != "pk_test_123" {
		t.Fatalf("key not trimmed: %v", err)
	}
	for _, bad := range []string{"pk_test 123", "pk_test\x00123", "pk_test 123", "pk\x7f"} {
		_, err := NewClient(bad)
		if err == nil {
			t.Fatalf("%q accepted", bad)
		}
		if strings.Contains(err.Error(), "pk") {
			t.Fatalf("error echoes the key: %v", err)
		}
	}
	t.Setenv("PAGERUSH_API_KEY", " pk_env ")
	if c, err := NewClient("   "); err != nil || c.apiKey != "pk_env" {
		t.Fatalf("blank key should fall back to env: %v", err)
	}
}

func TestNewClientBaseURLValidation(t *testing.T) {
	for _, bad := range []string{"pagerush.dev", "ftp://pagerush.dev", "https://", "://x"} {
		if _, err := NewClient("k", WithBaseURL(bad)); err == nil {
			t.Errorf("%q accepted", bad)
		}
	}
}

func TestClientStringRedactsKey(t *testing.T) {
	c, err := NewClient("pk_test_secret_value", WithBaseURL("https://pagerush.dev"))
	if err != nil {
		t.Fatal(err)
	}
	for _, out := range []string{
		fmt.Sprintf("%v", c), fmt.Sprintf("%+v", c), fmt.Sprintf("%#v", c), c.String(),
		fmt.Sprintf("%v", *c), fmt.Sprintf("%+v", *c), fmt.Sprintf("%#v", *c),
		fmt.Sprintf("%+v", c.Templates), fmt.Sprintf("%+v", c.Jobs),
	} {
		if strings.Contains(out, "secret_value") {
			t.Fatalf("key printed: %s", out)
		}
	}
}

func TestInvalidIdempotencyKey(t *testing.T) {
	f := newFakeAPI(t, func(w http.ResponseWriter, r *http.Request, n int) {
		t.Error("request sent")
	})
	c, s := newTestClient(t, f)
	for _, key := range []string{"has space", "new\nline", strings.Repeat("a", 256), "ü"} {
		_, err := c.Templates.Render(context.Background(), "t", &RenderParams{Data: 1, IdempotencyKey: key})
		var perr *Error
		if !errors.As(err, &perr) || perr.Code != CodeValidationFailed || perr.Status != 0 {
			t.Fatalf("%q: err = %v", key, err)
		}
	}
	if len(f.reqs()) != 0 || len(s.delays) != 0 {
		t.Fatal("request sent or retried")
	}
	// The longest and most varied valid key is accepted.
	ok := strings.Repeat("a", 250) + "_-:.9"
	f2 := newFakeAPI(t, func(w http.ResponseWriter, r *http.Request, n int) {
		w.Header().Set("Content-Type", "application/pdf")
		_, _ = io.WriteString(w, "%PDF")
	})
	c2, _ := newTestClient(t, f2)
	if _, err := c2.Templates.Render(context.Background(), "t", &RenderParams{Data: 1, IdempotencyKey: ok}); err != nil {
		t.Fatal(err)
	}
}

func TestRenderRetriesConflict(t *testing.T) {
	f := newFakeAPI(t, func(w http.ResponseWriter, r *http.Request, n int) {
		if n == 1 {
			w.Header().Set("Retry-After", "2")
			writeProblem(w, 409, CodeConflict, nil)
			return
		}
		w.Header().Set("Content-Type", "application/pdf")
		_, _ = io.WriteString(w, "%PDF")
	})
	c, s := newTestClient(t, f)
	if _, err := c.Templates.Render(context.Background(), "t", &RenderParams{Data: 1}); err != nil {
		t.Fatal(err)
	}
	if len(s.delays) != 1 || s.delays[0] != 2*time.Second {
		t.Fatalf("delays = %v", s.delays)
	}
	reqs := f.reqs()
	if reqs[0].Header.Get("Idempotency-Key") != reqs[1].Header.Get("Idempotency-Key") {
		t.Fatal("key changed between attempts")
	}
}

func TestConflictRetryOnlyForRender(t *testing.T) {
	cases := []struct {
		name string
		code string
		call func(c *Client) error
	}{
		{"render IDEMPOTENCY_CONFLICT", CodeIdempotencyConflict, func(c *Client) error {
			_, err := c.Templates.Render(context.Background(), "t", &RenderParams{Data: 1})
			return err
		}},
		{"get CONFLICT", CodeConflict, func(c *Client) error {
			_, err := c.Templates.Get(context.Background(), "t")
			return err
		}},
		{"publish CONFLICT", CodeConflict, func(c *Client) error {
			_, err := c.Templates.Publish(context.Background(), "t", 1)
			return err
		}},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			f := newFakeAPI(t, func(w http.ResponseWriter, r *http.Request, n int) {
				writeProblem(w, 409, tc.code, nil)
			})
			c, _ := newTestClient(t, f)
			var perr *Error
			if err := tc.call(c); !errors.As(err, &perr) || perr.Code != tc.code {
				t.Fatalf("err = %v", err)
			}
			if len(f.reqs()) != 1 {
				t.Fatalf("retried %d times", len(f.reqs())-1)
			}
		})
	}
}

func TestCancelDuringRetrySleepKeepsLastError(t *testing.T) {
	f := newFakeAPI(t, func(w http.ResponseWriter, r *http.Request, n int) {
		w.Header().Set("Retry-After", "10")
		writeProblem(w, 429, CodeRateLimited, map[string]any{"request_id": "req_9"})
	})
	c, err := NewClient("k", WithBaseURL(f.server.URL))
	if err != nil {
		t.Fatal(err)
	}
	ctx, cancel := context.WithTimeout(context.Background(), 50*time.Millisecond)
	defer cancel()
	start := time.Now()
	_, err = c.Jobs.Get(ctx, "j")
	if time.Since(start) > 5*time.Second {
		t.Fatal("sleep was not cancelled")
	}
	var perr *Error
	if !errors.As(err, &perr) || perr.Status != 429 || perr.RequestID != "req_9" || !errors.Is(err, context.DeadlineExceeded) {
		t.Fatalf("err = %v", err)
	}
}

func TestRedirectsDropAuthorization(t *testing.T) {
	var mu sync.Mutex
	seen := map[string]string{}
	other := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		mu.Lock()
		seen["other"] = r.Header.Get("Authorization")
		mu.Unlock()
		writeJSON(w, 200, jobJSON("queued", nil))
	}))
	defer other.Close()
	f := newFakeAPI(t, func(w http.ResponseWriter, r *http.Request, n int) {
		switch r.URL.Path {
		case "/v1/jobs/same":
			http.Redirect(w, r, "/v1/jobs/same-target", http.StatusFound)
		case "/v1/jobs/same-target":
			mu.Lock()
			seen["same"] = r.Header.Get("Authorization")
			mu.Unlock()
			writeJSON(w, 200, jobJSON("queued", nil))
		default:
			http.Redirect(w, r, other.URL+"/v1/jobs/x", http.StatusFound)
		}
	})
	c, _ := newTestClient(t, f)
	if _, err := c.Jobs.Get(context.Background(), "same"); err != nil {
		t.Fatal(err)
	}
	if _, err := c.Jobs.Get(context.Background(), "elsewhere"); err != nil {
		t.Fatal(err)
	}
	if seen["same"] != "Bearer pk_test_123" {
		t.Errorf("same-host redirect lost auth: %q", seen["same"])
	}
	if seen["other"] != "" {
		t.Errorf("auth forwarded to another host: %q", seen["other"])
	}
}

func TestRedirectKeepsCustomPolicy(t *testing.T) {
	f := newFakeAPI(t, func(w http.ResponseWriter, r *http.Request, n int) {
		http.Redirect(w, r, "/elsewhere", http.StatusFound)
	})
	hc := &http.Client{CheckRedirect: func(*http.Request, []*http.Request) error { return http.ErrUseLastResponse }}
	c, _ := newTestClient(t, f, WithHTTPClient(hc), WithMaxRetries(0))
	_, err := c.Jobs.Get(context.Background(), "j")
	var perr *Error
	if !errors.As(err, &perr) || perr.Status != http.StatusFound {
		t.Fatalf("err = %v", err)
	}
	if hc.CheckRedirect == nil {
		t.Fatal("caller's client was modified")
	}
}

func TestDownloadErrorHidesSignedQuery(t *testing.T) {
	ln, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	go func() {
		for {
			conn, err := ln.Accept()
			if err != nil {
				return
			}
			conn.Close()
		}
	}()
	defer ln.Close()
	f := newFakeAPI(t, func(w http.ResponseWriter, r *http.Request, n int) {
		writeJSON(w, 200, jobJSON("succeeded", "http://"+ln.Addr().String()+"/f/job_1?X-Signature=topsecret&exp=1"))
	})
	c, _ := newTestClient(t, f)
	_, err = c.Jobs.Download(context.Background(), "job_1")
	var perr *Error
	if !errors.As(err, &perr) || perr.Code != CodeConnectionError {
		t.Fatalf("err = %v", err)
	}
	if strings.Contains(err.Error(), "topsecret") || strings.Contains(fmt.Sprint(errors.Unwrap(err)), "topsecret") {
		t.Fatalf("signed query leaked: %v", err)
	}
	if !strings.Contains(err.Error(), "/f/job_1") {
		t.Fatalf("path missing from error: %v", err)
	}
}

func TestDownloadRejectsNonHTTPURL(t *testing.T) {
	f := newFakeAPI(t, func(w http.ResponseWriter, r *http.Request, n int) {
		writeJSON(w, 200, jobJSON("succeeded", "file:///etc/passwd"))
	})
	c, s := newTestClient(t, f)
	_, err := c.Jobs.Download(context.Background(), "job_1")
	var perr *Error
	if !errors.As(err, &perr) || perr.Code != CodeInvalidResponse || len(s.delays) != 0 {
		t.Fatalf("err = %v", err)
	}
}

func TestTemplateFileFromPathUsesForwardSlashes(t *testing.T) {
	dir := t.TempDir()
	local := filepath.Join(dir, "a.typ")
	if err := writeFile(local, "x"); err != nil {
		t.Fatal(err)
	}
	f, err := TemplateFileFromPath(local, filepath.Join("parts", "a.typ"))
	if err != nil || f.Path != "parts/a.typ" {
		t.Fatalf("%+v %v", f, err)
	}
}

func writeFile(path, s string) error { return os.WriteFile(path, []byte(s), 0o644) }

func TestRedirectHTTPSToHTTPDropsAuthorization(t *testing.T) {
	var gotAuth string
	var mu sync.Mutex
	plain := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		mu.Lock()
		gotAuth = r.Header.Get("Authorization")
		mu.Unlock()
		writeJSON(w, 200, jobJSON("queued", nil))
	}))
	defer plain.Close()
	secure := httptest.NewTLSServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		http.Redirect(w, r, plain.URL+r.URL.Path, http.StatusFound)
	}))
	defer secure.Close()

	c, err := NewClient("pk_test_123", WithBaseURL(secure.URL), WithHTTPClient(secure.Client()))
	if err != nil {
		t.Fatal(err)
	}
	if _, err := c.Jobs.Get(context.Background(), "j"); err != nil {
		t.Fatal(err)
	}
	mu.Lock()
	defer mu.Unlock()
	if gotAuth != "" {
		t.Fatalf("Authorization sent over http after redirect: %q", gotAuth)
	}
}

func TestSafeRedirectPolicy(t *testing.T) {
	check := withSafeRedirects(&http.Client{}).CheckRedirect
	req := func(raw string) *http.Request {
		r, _ := http.NewRequest(http.MethodGet, raw, nil)
		r.Header.Set("Authorization", "Bearer pk_test_123")
		return r
	}
	cases := []struct {
		from, to string
		keep     bool
	}{
		{"https://pagerush.dev/a", "https://pagerush.dev/b", true},
		{"https://pagerush.dev/a", "http://pagerush.dev/b", false},
		{"https://pagerush.dev/a", "https://files.pagerush.dev/b", false},
		{"http://localhost:8787/a", "http://localhost:8787/b", true},
		{"http://localhost:8787/a", "http://localhost:9999/b", false},
	}
	for _, tc := range cases {
		next := req(tc.to)
		if err := check(next, []*http.Request{req(tc.from)}); err != nil {
			t.Fatal(err)
		}
		if kept := next.Header.Get("Authorization") != ""; kept != tc.keep {
			t.Errorf("%s -> %s: kept = %v", tc.from, tc.to, kept)
		}
	}
	via := make([]*http.Request, 10)
	for i := range via {
		via[i] = req("https://pagerush.dev/a")
	}
	if check(req("https://pagerush.dev/b"), via) == nil {
		t.Fatal("no redirect limit")
	}
}
