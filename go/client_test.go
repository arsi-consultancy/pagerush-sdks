package pagerush

import (
	"context"
	"encoding/json"
	"errors"
	"io"
	"net"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"regexp"
	"strings"
	"sync"
	"testing"
	"time"
)

type recorded struct {
	Method string
	Path   string
	Query  string
	Header http.Header
	Body   []byte
}

type fakeAPI struct {
	t        *testing.T
	server   *httptest.Server
	mu       sync.Mutex
	requests []recorded
	handler  func(w http.ResponseWriter, r *http.Request, n int)
}

func newFakeAPI(t *testing.T, handler func(w http.ResponseWriter, r *http.Request, n int)) *fakeAPI {
	t.Helper()
	f := &fakeAPI{t: t, handler: handler}
	f.server = httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		body, _ := io.ReadAll(r.Body)
		f.mu.Lock()
		f.requests = append(f.requests, recorded{r.Method, r.URL.EscapedPath(), r.URL.RawQuery, r.Header.Clone(), body})
		n := len(f.requests)
		f.mu.Unlock()
		f.handler(w, r, n)
	}))
	t.Cleanup(f.server.Close)
	return f
}

func (f *fakeAPI) reqs() []recorded {
	f.mu.Lock()
	defer f.mu.Unlock()
	return append([]recorded(nil), f.requests...)
}

// sleeps records the delays the client asked to sleep for, without sleeping.
type sleeps struct {
	mu     sync.Mutex
	delays []time.Duration
}

func (s *sleeps) sleep(ctx context.Context, d time.Duration) error {
	s.mu.Lock()
	s.delays = append(s.delays, d)
	s.mu.Unlock()
	return ctx.Err()
}

func newTestClient(t *testing.T, f *fakeAPI, opts ...Option) (*Client, *sleeps) {
	t.Helper()
	c, err := NewClient("pk_test_123", append([]Option{WithBaseURL(f.server.URL + "/")}, opts...)...)
	if err != nil {
		t.Fatal(err)
	}
	s := &sleeps{}
	c.sleep = s.sleep
	return c, s
}

func writeJSON(w http.ResponseWriter, status int, v any) {
	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(status)
	_ = json.NewEncoder(w).Encode(v)
}

func writeProblem(w http.ResponseWriter, status int, code string, extra map[string]any) {
	body := map[string]any{
		"type":   "https://pagerush.dev/docs#x",
		"title":  http.StatusText(status),
		"status": status,
		"code":   code,
		"detail": "something went wrong",
	}
	for k, v := range extra {
		body[k] = v
	}
	w.Header().Set("Content-Type", "application/problem+json")
	w.WriteHeader(status)
	_ = json.NewEncoder(w).Encode(body)
}

const templateJSON = `{"id":"tpl_1","name":"Invoice","live_version":1,"latest_version":1,"created_at":"2026-01-01T00:00:00.000Z","updated_at":"2026-01-01T00:00:00.000Z"}`
const versionJSON = `{"version":1,"source":"Hello","document":null,"files":[],"sample_data":null,"typst_version":"0.13.1","created_at":"2026-01-01T00:00:00.000Z"}`

func jsonBody(t *testing.T, b []byte) map[string]any {
	t.Helper()
	var m map[string]any
	if err := json.Unmarshal(b, &m); err != nil {
		t.Fatalf("body is not JSON: %s", b)
	}
	return m
}

func TestNewClientAPIKey(t *testing.T) {
	t.Setenv("PAGERUSH_API_KEY", "")
	if _, err := NewClient(""); err == nil {
		t.Fatal("expected error for missing key")
	}
	t.Setenv("PAGERUSH_API_KEY", "pk_env")
	c, err := NewClient("")
	if err != nil || c.apiKey != "pk_env" {
		t.Fatalf("env key not used: %v", err)
	}
}

func TestNewClientBaseURL(t *testing.T) {
	t.Setenv("PAGERUSH_BASE_URL", "")
	c, _ := NewClient("k")
	if c.BaseURL() != DefaultBaseURL {
		t.Fatalf("default base URL = %q", c.BaseURL())
	}
	t.Setenv("PAGERUSH_BASE_URL", "https://eu.example.com/")
	c, _ = NewClient("k")
	if c.BaseURL() != "https://eu.example.com" {
		t.Fatalf("env base URL = %q", c.BaseURL())
	}
	c, _ = NewClient("k", WithBaseURL("http://localhost:8787//"))
	if c.BaseURL() != "http://localhost:8787" {
		t.Fatalf("option base URL = %q", c.BaseURL())
	}
	if c.timeout != DefaultTimeout || c.maxRetries != DefaultMaxRetries {
		t.Fatal("wrong defaults")
	}
}

func TestAuthAndUserAgent(t *testing.T) {
	f := newFakeAPI(t, func(w http.ResponseWriter, r *http.Request, n int) {
		writeJSON(w, 200, map[string]any{"items": []any{}, "next_cursor": nil})
	})
	c, _ := newTestClient(t, f)
	if _, err := c.Templates.List(context.Background(), nil); err != nil {
		t.Fatal(err)
	}
	h := f.reqs()[0].Header
	if got := h.Get("Authorization"); got != "Bearer pk_test_123" {
		t.Errorf("Authorization = %q", got)
	}
	if got := h.Get("User-Agent"); got != "pagerush-go/"+Version {
		t.Errorf("User-Agent = %q", got)
	}
	if got := h.Get("Accept"); got != "application/json" {
		t.Errorf("Accept = %q", got)
	}
	if got := h.Get("Content-Type"); got != "" {
		t.Errorf("Content-Type on GET = %q", got)
	}
}

func TestTemplateMethods(t *testing.T) {
	f := newFakeAPI(t, func(w http.ResponseWriter, r *http.Request, n int) {
		w.Header().Set("Content-Type", "application/json")
		switch {
		case r.Method == "GET" && r.URL.Path == "/v1/templates":
			_, _ = io.WriteString(w, `{"items":[`+templateJSON+`],"next_cursor":"c2"}`)
		case r.Method == "POST" && r.URL.Path == "/v1/templates":
			w.WriteHeader(201)
			_, _ = io.WriteString(w, `{"template":`+templateJSON+`,"version":`+versionJSON+`}`)
		case r.Method == "POST" && strings.HasSuffix(r.URL.Path, "/versions"):
			w.WriteHeader(201)
			_, _ = io.WriteString(w, `{"template":`+templateJSON+`,"version":`+versionJSON+`}`)
		case r.Method == "GET" && strings.Contains(r.URL.Path, "/versions/"):
			_, _ = io.WriteString(w, `{"version":`+versionJSON+`}`)
		case r.Method == "DELETE":
			w.WriteHeader(204)
		default:
			_, _ = io.WriteString(w, `{"template":`+templateJSON+`}`)
		}
	})
	c, _ := newTestClient(t, f)
	ctx := context.Background()

	list, err := c.Templates.List(ctx, &ListTemplatesParams{Limit: Int(10), Cursor: String("abc")})
	if err != nil || len(list.Items) != 1 || list.NextCursor == nil || *list.NextCursor != "c2" {
		t.Fatalf("List: %+v %v", list, err)
	}

	created, err := c.Templates.Create(ctx, &CreateTemplateParams{
		Name:       "Invoice",
		Source:     String("Hello"),
		Files:      []TemplateFile{NewTemplateFile("logo.png", []byte{1, 2})},
		SampleData: map[string]any{"name": "Ada"},
	})
	if err != nil || created.Template.ID != "tpl_1" || created.Version.Version != 1 {
		t.Fatalf("Create: %+v %v", created, err)
	}
	if string(created.Version.SampleData) != "null" {
		t.Errorf("sample_data = %s", created.Version.SampleData)
	}

	got, err := c.Templates.Get(ctx, "tpl/1")
	if err != nil || got.Name != "Invoice" || got.LiveVersion == nil || *got.LiveVersion != 1 {
		t.Fatalf("Get: %+v %v", got, err)
	}
	if _, err := c.Templates.Rename(ctx, "tpl_1", "New name"); err != nil {
		t.Fatal(err)
	}
	if err := c.Templates.Delete(ctx, "tpl_1"); err != nil {
		t.Fatal(err)
	}
	if _, err := c.Templates.CreateVersion(ctx, "tpl_1", &CreateVersionParams{Source: String("v2"), Publish: Bool(false)}); err != nil {
		t.Fatal(err)
	}
	v, err := c.Templates.GetVersion(ctx, "tpl_1", 3)
	if err != nil || v.Source != "Hello" {
		t.Fatalf("GetVersion: %+v %v", v, err)
	}
	if _, err := c.Templates.Publish(ctx, "tpl_1", 2); err != nil {
		t.Fatal(err)
	}

	want := []struct{ method, path, query, body string }{
		{"GET", "/v1/templates", "cursor=abc&limit=10", ""},
		{"POST", "/v1/templates", "", `{"files":[{"content":"AQI=","encoding":"base64","path":"logo.png"}],"name":"Invoice","sample_data":{"name":"Ada"},"source":"Hello"}`},
		{"GET", "/v1/templates/tpl%2F1", "", ""},
		{"PATCH", "/v1/templates/tpl_1", "", `{"name":"New name"}`},
		{"DELETE", "/v1/templates/tpl_1", "", ""},
		{"POST", "/v1/templates/tpl_1/versions", "", `{"publish":false,"source":"v2"}`},
		{"GET", "/v1/templates/tpl_1/versions/3", "", ""},
		{"POST", "/v1/templates/tpl_1/publish", "", `{"version":2}`},
	}
	reqs := f.reqs()
	if len(reqs) != len(want) {
		t.Fatalf("got %d requests, want %d", len(reqs), len(want))
	}
	for i, w := range want {
		r := reqs[i]
		if r.Method != w.method || r.Path != w.path || r.Query != w.query {
			t.Errorf("request %d = %s %s?%s, want %s %s?%s", i, r.Method, r.Path, r.Query, w.method, w.path, w.query)
		}
		if w.body == "" {
			if len(r.Body) != 0 {
				t.Errorf("request %d: unexpected body %s", i, r.Body)
			}
			continue
		}
		// Re-encode to compare independent of key order.
		gotBody, _ := json.Marshal(jsonBody(t, r.Body))
		if string(gotBody) != w.body {
			t.Errorf("request %d body = %s, want %s", i, gotBody, w.body)
		}
		if r.Header.Get("Content-Type") != "application/json" {
			t.Errorf("request %d: Content-Type = %q", i, r.Header.Get("Content-Type"))
		}
	}
}

func TestEmptyIDRejected(t *testing.T) {
	c, _ := NewClient("k", WithBaseURL("http://127.0.0.1:1"))
	_, err := c.Templates.Get(context.Background(), "")
	var perr *Error
	if !errors.As(err, &perr) || perr.Code != CodeValidationFailed {
		t.Fatalf("err = %v", err)
	}
}

func TestListAll(t *testing.T) {
	f := newFakeAPI(t, func(w http.ResponseWriter, r *http.Request, n int) {
		switch r.URL.Query().Get("cursor") {
		case "":
			writeJSON(w, 200, map[string]any{
				"items":       []map[string]any{{"id": "a", "name": "A"}, {"id": "b", "name": "B"}},
				"next_cursor": "page2",
			})
		case "page2":
			writeJSON(w, 200, map[string]any{"items": []map[string]any{{"id": "c", "name": "C"}}, "next_cursor": nil})
		default:
			t.Errorf("unexpected cursor %q", r.URL.Query().Get("cursor"))
		}
	})
	c, _ := newTestClient(t, f)
	it := c.Templates.ListAll(context.Background(), &ListTemplatesParams{Limit: Int(2)})
	var ids []string
	for it.Next() {
		ids = append(ids, it.Template().ID)
	}
	if err := it.Err(); err != nil {
		t.Fatal(err)
	}
	if strings.Join(ids, ",") != "a,b,c" {
		t.Fatalf("ids = %v", ids)
	}
	if it.Next() {
		t.Fatal("Next after end returned true")
	}
	reqs := f.reqs()
	if len(reqs) != 2 || reqs[0].Query != "limit=2" || reqs[1].Query != "cursor=page2&limit=2" {
		t.Fatalf("requests = %+v", reqs)
	}
}

func TestListAllError(t *testing.T) {
	f := newFakeAPI(t, func(w http.ResponseWriter, r *http.Request, n int) {
		writeProblem(w, 401, CodeInvalidAPIKey, nil)
	})
	c, _ := newTestClient(t, f)
	it := c.Templates.ListAll(context.Background(), nil)
	if it.Next() {
		t.Fatal("Next returned true")
	}
	var perr *Error
	if !errors.As(it.Err(), &perr) || perr.Code != CodeInvalidAPIKey {
		t.Fatalf("Err = %v", it.Err())
	}
}

func TestRenderSync(t *testing.T) {
	f := newFakeAPI(t, func(w http.ResponseWriter, r *http.Request, n int) {
		h := w.Header()
		h.Set("Content-Type", "application/pdf")
		h.Set("Content-Disposition", `inline; filename="invoice 42.pdf"`)
		h.Set("X-Job-Id", "job_1")
		h.Set("X-Template-Version", "3")
		h.Set("X-Render-Engine", "typst-0.13.1")
		h.Set("X-Page-Count", "2")
		h.Set("X-Template-Warnings", "0")
		h.Set("X-Request-Id", "req_1")
		_, _ = io.WriteString(w, "%PDF-1.7 test")
	})
	c, _ := newTestClient(t, f)
	res, err := c.Templates.Render(context.Background(), "tpl_1", &RenderParams{
		Data:                json.RawMessage(`{"name":"Ada"}`),
		Version:             Int(3),
		Filename:            String("invoice 42.pdf"),
		Metadata:            map[string]string{"order": "42"},
		DeleteAfterDownload: Bool(true),
		IdempotencyKey:      "order-42",
	})
	if err != nil {
		t.Fatal(err)
	}
	if res.Queued || string(res.PDF) != "%PDF-1.7 test" || res.JobID != "job_1" || res.TemplateVersion != 3 ||
		res.Engine != "typst-0.13.1" || res.PageCount == nil || *res.PageCount != 2 ||
		res.Warnings == nil || *res.Warnings != 0 || res.RequestID != "req_1" || res.Filename != "invoice 42.pdf" {
		t.Fatalf("result = %+v", res)
	}
	r := f.reqs()[0]
	if r.Path != "/v1/templates/tpl_1/render" || r.Method != "POST" {
		t.Errorf("request = %s %s", r.Method, r.Path)
	}
	if r.Header.Get("Accept") != "application/pdf, application/json" {
		t.Errorf("Accept = %q", r.Header.Get("Accept"))
	}
	if r.Header.Get("Idempotency-Key") != "order-42" {
		t.Errorf("Idempotency-Key = %q", r.Header.Get("Idempotency-Key"))
	}
	if r.Header.Get("Prefer") != "" {
		t.Errorf("Prefer = %q", r.Header.Get("Prefer"))
	}
	gotBody, _ := json.Marshal(jsonBody(t, r.Body))
	want := `{"data":{"name":"Ada"},"delete_after_download":true,"filename":"invoice 42.pdf","metadata":{"order":"42"},"version":3}`
	if string(gotBody) != want {
		t.Errorf("body = %s", gotBody)
	}

	path := filepath.Join(t.TempDir(), "out.pdf")
	if err := res.WriteFile(path); err != nil {
		t.Fatal(err)
	}
	if b, _ := os.ReadFile(path); string(b) != "%PDF-1.7 test" {
		t.Fatalf("file = %q", b)
	}
}

func TestRenderSyncMissingOptionalHeaders(t *testing.T) {
	f := newFakeAPI(t, func(w http.ResponseWriter, r *http.Request, n int) {
		w.Header().Set("Content-Type", "application/pdf")
		w.Header().Set("X-Template-Version", "1")
		_, _ = io.WriteString(w, "%PDF")
	})
	c, _ := newTestClient(t, f)
	res, err := c.Templates.Render(context.Background(), "tpl_1", &RenderParams{Data: nil})
	if err != nil {
		t.Fatal(err)
	}
	if res.PageCount != nil || res.Warnings != nil || res.RequestID != "" || res.Filename != "" {
		t.Fatalf("result = %+v", res)
	}
	if string(f.reqs()[0].Body) != `{"data":null}` {
		t.Fatalf("body = %s", f.reqs()[0].Body)
	}
}

func TestRenderQueued(t *testing.T) {
	f := newFakeAPI(t, func(w http.ResponseWriter, r *http.Request, n int) {
		writeJSON(w, 202, map[string]any{
			"id": "job_9", "status": "queued", "mode": "async", "region": "ap-south",
			"template_id": "tpl_1", "template_version": 4, "metadata": map[string]string{"a": "b"},
			"download_url": nil, "delete_after_download": false, "created_at": "2026-01-01T00:00:00.000Z",
			"completed_at": nil, "delivery": map[string]any{"status": "pending", "attempts": 0, "error": nil},
		})
	})
	c, _ := newTestClient(t, f)
	res, err := c.Templates.Render(context.Background(), "tpl_1", &RenderParams{
		Data:        map[string]any{},
		CallbackURL: String("https://example.com/hook"),
		PreferAsync: true,
	})
	if err != nil {
		t.Fatal(err)
	}
	if !res.Queued || res.PDF != nil || res.Job == nil || res.JobID != "job_9" || res.TemplateVersion != 4 ||
		res.Engine != "" || res.PageCount != nil || res.Job.Delivery.Status != "pending" {
		t.Fatalf("result = %+v", res)
	}
	r := f.reqs()[0]
	if r.Header.Get("Prefer") != "respond-async" {
		t.Errorf("Prefer = %q", r.Header.Get("Prefer"))
	}
	if jsonBody(t, r.Body)["callback_url"] != "https://example.com/hook" {
		t.Errorf("body = %s", r.Body)
	}
	if err := res.WriteFile(filepath.Join(t.TempDir(), "x.pdf")); !errors.Is(err, ErrQueued) {
		t.Fatalf("WriteFile err = %v", err)
	}
}

var uuidV4 = regexp.MustCompile(`^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$`)

func TestRenderIdempotencyKeyCreatedAndReused(t *testing.T) {
	f := newFakeAPI(t, func(w http.ResponseWriter, r *http.Request, n int) {
		if n == 1 {
			writeProblem(w, 502, CodeInternal, nil)
			return
		}
		w.Header().Set("Content-Type", "application/pdf")
		_, _ = io.WriteString(w, "%PDF")
	})
	c, s := newTestClient(t, f)
	if _, err := c.Templates.Render(context.Background(), "tpl_1", &RenderParams{Data: 1}); err != nil {
		t.Fatal(err)
	}
	reqs := f.reqs()
	if len(reqs) != 2 {
		t.Fatalf("got %d requests", len(reqs))
	}
	k1, k2 := reqs[0].Header.Get("Idempotency-Key"), reqs[1].Header.Get("Idempotency-Key")
	if !uuidV4.MatchString(k1) || k1 != k2 {
		t.Fatalf("keys %q %q", k1, k2)
	}
	if len(s.delays) != 1 || s.delays[0] < 375*time.Millisecond || s.delays[0] > 625*time.Millisecond {
		t.Fatalf("delays = %v", s.delays)
	}
	// A second call gets a new key.
	if _, err := c.Templates.Render(context.Background(), "tpl_1", &RenderParams{Data: 1}); err != nil {
		t.Fatal(err)
	}
	if k3 := f.reqs()[2].Header.Get("Idempotency-Key"); k3 == k1 {
		t.Fatal("key reused across calls")
	}
}

func TestRetryOn503WithRetryAfter(t *testing.T) {
	f := newFakeAPI(t, func(w http.ResponseWriter, r *http.Request, n int) {
		if n == 1 {
			w.Header().Set("Retry-After", "7")
			writeProblem(w, 503, CodeRenderUnavailable, nil)
			return
		}
		if n == 2 {
			w.Header().Set("Retry-After", "120")
			writeProblem(w, 429, CodeRateLimited, nil)
			return
		}
		writeJSON(w, 201, map[string]any{"template": map[string]any{"id": "tpl_1"}, "version": map[string]any{"version": 1}})
	})
	c, s := newTestClient(t, f)
	// Create is an unsafe POST, but 503 and 429 are still retried.
	res, err := c.Templates.Create(context.Background(), &CreateTemplateParams{Name: "x", Source: String("y")})
	if err != nil || res.Template.ID != "tpl_1" {
		t.Fatalf("Create: %+v %v", res, err)
	}
	if len(f.reqs()) != 3 {
		t.Fatalf("got %d requests", len(f.reqs()))
	}
	if len(s.delays) != 2 || s.delays[0] != 7*time.Second || s.delays[1] != 30*time.Second {
		t.Fatalf("delays = %v", s.delays)
	}
}

func TestNoRetryOfCreateOn502(t *testing.T) {
	f := newFakeAPI(t, func(w http.ResponseWriter, r *http.Request, n int) {
		writeProblem(w, 502, CodeInternal, nil)
	})
	c, s := newTestClient(t, f)
	_, err := c.Templates.Create(context.Background(), &CreateTemplateParams{Name: "x"})
	var perr *Error
	if !errors.As(err, &perr) || perr.Status != 502 {
		t.Fatalf("err = %v", err)
	}
	if len(f.reqs()) != 1 || len(s.delays) != 0 {
		t.Fatalf("requests %d, delays %v", len(f.reqs()), s.delays)
	}
}

func TestNoRetryOfCreateOnConnectionError(t *testing.T) {
	ln, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	var mu sync.Mutex
	accepted := 0
	go func() {
		for {
			conn, err := ln.Accept()
			if err != nil {
				return
			}
			mu.Lock()
			accepted++
			mu.Unlock()
			conn.Close() // hang up without a response
		}
	}()
	defer ln.Close()

	c, _ := NewClient("k", WithBaseURL("http://"+ln.Addr().String()))
	s := &sleeps{}
	c.sleep = s.sleep
	_, err = c.Templates.Create(context.Background(), &CreateTemplateParams{Name: "x"})
	var perr *Error
	if !errors.As(err, &perr) || perr.Code != CodeConnectionError || perr.Status != 0 || perr.Unwrap() == nil {
		t.Fatalf("err = %#v", err)
	}
	if len(s.delays) != 0 {
		t.Fatalf("create was retried: %v", s.delays)
	}

	// GET is retried on connection errors.
	_, err = c.Templates.Get(context.Background(), "tpl_1")
	if !errors.As(err, &perr) || perr.Code != CodeConnectionError {
		t.Fatalf("err = %v", err)
	}
	if len(s.delays) != 2 {
		t.Fatalf("GET delays = %v", s.delays)
	}
}

func TestNoRetryAfterMaxRetries(t *testing.T) {
	f := newFakeAPI(t, func(w http.ResponseWriter, r *http.Request, n int) {
		writeProblem(w, 503, CodeRenderUnavailable, nil)
	})
	c, s := newTestClient(t, f)
	_, err := c.Templates.Get(context.Background(), "tpl_1")
	var perr *Error
	if !errors.As(err, &perr) || perr.Status != 503 {
		t.Fatalf("err = %v", err)
	}
	if len(f.reqs()) != 3 {
		t.Fatalf("got %d requests, want 3", len(f.reqs()))
	}
	// Backoff: 0.5 s then 1 s, each ±25 %.
	if len(s.delays) != 2 || s.delays[0] < 375*time.Millisecond || s.delays[0] > 625*time.Millisecond ||
		s.delays[1] < 750*time.Millisecond || s.delays[1] > 1250*time.Millisecond {
		t.Fatalf("delays = %v", s.delays)
	}

	c0, _ := newTestClient(t, f, WithMaxRetries(0))
	before := len(f.reqs())
	_, _ = c0.Templates.Get(context.Background(), "tpl_1")
	if len(f.reqs())-before != 1 {
		t.Fatal("WithMaxRetries(0) retried")
	}
}

func TestBackoffCap(t *testing.T) {
	for i := 0; i < 50; i++ {
		if d := backoff(10); d < 6*time.Second || d > 10*time.Second {
			t.Fatalf("backoff(10) = %v", d)
		}
	}
}

func TestNoRetryOn4xx(t *testing.T) {
	f := newFakeAPI(t, func(w http.ResponseWriter, r *http.Request, n int) {
		writeProblem(w, 404, CodeTemplateNotFound, nil)
	})
	c, _ := newTestClient(t, f)
	_, _ = c.Templates.Get(context.Background(), "x")
	if len(f.reqs()) != 1 {
		t.Fatal("404 was retried")
	}
}

func TestRetryStopsOnContextCancel(t *testing.T) {
	f := newFakeAPI(t, func(w http.ResponseWriter, r *http.Request, n int) {
		writeProblem(w, 503, CodeRenderUnavailable, nil)
	})
	c, err := NewClient("k", WithBaseURL(f.server.URL))
	if err != nil {
		t.Fatal(err)
	}
	ctx, cancel := context.WithCancel(context.Background())
	c.sleep = func(ctx context.Context, d time.Duration) error {
		cancel()
		return sleepContext(ctx, d)
	}
	_, err = c.Templates.Get(ctx, "tpl_1")
	var perr *Error
	// The 503 that caused the retry is reported, wrapping the context error.
	if !errors.As(err, &perr) || perr.Status != 503 || perr.Code != CodeRenderUnavailable || !errors.Is(err, context.Canceled) {
		t.Fatalf("err = %v", err)
	}
	if len(f.reqs()) != 1 {
		t.Fatalf("got %d requests", len(f.reqs()))
	}
}

func TestTimeoutError(t *testing.T) {
	release := make(chan struct{})
	f := newFakeAPI(t, func(w http.ResponseWriter, r *http.Request, n int) {
		select {
		case <-release:
		case <-r.Context().Done():
		}
	})
	defer close(release)
	c, s := newTestClient(t, f, WithTimeout(50*time.Millisecond), WithMaxRetries(1))
	_, err := c.Jobs.Get(context.Background(), "job_1")
	var perr *Error
	if !errors.As(err, &perr) || perr.Code != CodeTimeout || perr.Status != 0 || !errors.Is(err, context.DeadlineExceeded) {
		t.Fatalf("err = %v", err)
	}
	if len(s.delays) != 1 {
		t.Fatalf("timeout not retried once: %v", s.delays)
	}
}

func TestErrorMapping(t *testing.T) {
	t.Run("problem+json", func(t *testing.T) {
		f := newFakeAPI(t, func(w http.ResponseWriter, r *http.Request, n int) {
			w.Header().Set("X-Request-Id", "req_header")
			writeProblem(w, 422, CodeTemplateCompileFailed, map[string]any{
				"detail":     "Template failed to compile",
				"errors":     []map[string]string{{"path": "main.typ:3:5", "message": "unknown variable: foo"}},
				"request_id": "req_body",
			})
		})
		c, _ := newTestClient(t, f)
		_, err := c.Templates.Render(context.Background(), "tpl_1", &RenderParams{Data: 1})
		var perr *Error
		if !errors.As(err, &perr) {
			t.Fatalf("err = %v", err)
		}
		if perr.Status != 422 || perr.Code != CodeTemplateCompileFailed || perr.Title != "Unprocessable Entity" ||
			perr.Detail != "Template failed to compile" || len(perr.Errors) != 1 ||
			perr.Errors[0].Path != "main.typ:3:5" || perr.RequestID != "req_body" ||
			perr.Type != "https://pagerush.dev/docs#x" || perr.HasRetryAfter() {
			t.Fatalf("err = %+v", perr)
		}
		if perr.Error() != "TEMPLATE_COMPILE_FAILED: Template failed to compile" {
			t.Fatalf("message = %q", perr.Error())
		}
		if len(f.reqs()) != 1 {
			t.Fatal("422 was retried")
		}
	})
	t.Run("non-JSON body and request id from header", func(t *testing.T) {
		f := newFakeAPI(t, func(w http.ResponseWriter, r *http.Request, n int) {
			w.Header().Set("X-Request-Id", "req_header")
			w.Header().Set("Content-Type", "text/html")
			w.WriteHeader(500)
			_, _ = io.WriteString(w, "<html>oops</html>")
		})
		c, _ := newTestClient(t, f)
		_, err := c.Jobs.Get(context.Background(), "job_1")
		var perr *Error
		if !errors.As(err, &perr) {
			t.Fatalf("err = %v", err)
		}
		if perr.Status != 500 || perr.Code != CodeUnknown || perr.RequestID != "req_header" || len(perr.Errors) != 0 {
			t.Fatalf("err = %+v", perr)
		}
		if perr.Error() != "UNKNOWN: HTTP 500" {
			t.Fatalf("message = %q", perr.Error())
		}
	})
	t.Run("title fallback and retry-after", func(t *testing.T) {
		f := newFakeAPI(t, func(w http.ResponseWriter, r *http.Request, n int) {
			w.Header().Set("Retry-After", "5")
			writeJSON(w, 402, map[string]any{"type": "t", "title": "Payment Required", "status": 402, "code": CodeQuotaExceeded})
		})
		c, _ := newTestClient(t, f)
		_, err := c.Jobs.Get(context.Background(), "job_1")
		var perr *Error
		if !errors.As(err, &perr) || perr.Error() != "QUOTA_EXCEEDED: Payment Required" ||
			perr.RetryAfter != 5*time.Second || !perr.HasRetryAfter() {
			t.Fatalf("err = %v %+v", err, perr)
		}
	})
}

func jobJSON(status string, downloadURL any) map[string]any {
	return map[string]any{
		"id": "job_1", "status": status, "mode": "async", "region": "ap-south",
		"template_id": "tpl_1", "template_version": 1, "download_url": downloadURL,
		"delete_after_download": false, "created_at": "2026-01-01T00:00:00.000Z",
	}
}

func TestJobsWait(t *testing.T) {
	f := newFakeAPI(t, func(w http.ResponseWriter, r *http.Request, n int) {
		if r.URL.Path != "/v1/jobs/job_1" {
			t.Errorf("path = %s", r.URL.Path)
		}
		status := "queued"
		if n == 2 {
			status = "rendering"
		}
		if n >= 3 {
			status = "succeeded"
		}
		writeJSON(w, 200, jobJSON(status, nil))
	})
	c, s := newTestClient(t, f)
	job, err := c.Jobs.Wait(context.Background(), "job_1", &WaitOptions{Interval: 250 * time.Millisecond})
	if err != nil || job.Status != JobStatusSucceeded {
		t.Fatalf("Wait: %+v %v", job, err)
	}
	if len(f.reqs()) != 3 || len(s.delays) != 2 || s.delays[0] != 250*time.Millisecond {
		t.Fatalf("requests %d, delays %v", len(f.reqs()), s.delays)
	}
}

func TestJobsWaitTimeout(t *testing.T) {
	f := newFakeAPI(t, func(w http.ResponseWriter, r *http.Request, n int) {
		writeJSON(w, 200, jobJSON("rendering", nil))
	})
	c, _ := newTestClient(t, f)
	clock := time.Unix(1_000_000, 0)
	var mu sync.Mutex
	c.now = func() time.Time { mu.Lock(); defer mu.Unlock(); return clock }
	c.sleep = func(ctx context.Context, d time.Duration) error {
		mu.Lock()
		clock = clock.Add(d)
		mu.Unlock()
		return nil
	}
	_, err := c.Jobs.Wait(context.Background(), "job_1", &WaitOptions{Timeout: 5 * time.Second, Interval: 2 * time.Second})
	var perr *Error
	if !errors.As(err, &perr) || perr.Code != CodeWaitTimeout || perr.Status != 0 {
		t.Fatalf("err = %v", err)
	}
	// Polls at 0, 2, 4 and 5 s.
	if len(f.reqs()) != 4 {
		t.Fatalf("got %d polls", len(f.reqs()))
	}
}

func TestJobsWaitReturnsFailedJob(t *testing.T) {
	f := newFakeAPI(t, func(w http.ResponseWriter, r *http.Request, n int) {
		j := jobJSON("failed", nil)
		j["error"] = map[string]string{"code": "TEMPLATE_COMPILE_FAILED", "message": "boom"}
		writeJSON(w, 200, j)
	})
	c, _ := newTestClient(t, f)
	job, err := c.Jobs.Wait(context.Background(), "job_1", nil)
	if err != nil || job.Status != JobStatusFailed || job.Error == nil || job.Error.Code != CodeTemplateCompileFailed {
		t.Fatalf("Wait: %+v %v", job, err)
	}
}

func TestJobsDownloadWithoutAuth(t *testing.T) {
	var fileHeaders http.Header
	files := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		fileHeaders = r.Header.Clone()
		if r.URL.Query().Get("sig") != "abc" {
			w.WriteHeader(403)
			return
		}
		w.Header().Set("Content-Type", "application/pdf")
		_, _ = io.WriteString(w, "%PDF-stored")
	}))
	defer files.Close()
	f := newFakeAPI(t, func(w http.ResponseWriter, r *http.Request, n int) {
		writeJSON(w, 200, jobJSON("succeeded", files.URL+"/files/job_1?sig=abc"))
	})
	c, _ := newTestClient(t, f)
	pdf, err := c.Jobs.Download(context.Background(), "job_1")
	if err != nil || string(pdf) != "%PDF-stored" {
		t.Fatalf("Download: %q %v", pdf, err)
	}
	if fileHeaders.Get("Authorization") != "" {
		t.Fatal("Authorization sent to download_url")
	}
	if f.reqs()[0].Header.Get("Authorization") == "" {
		t.Fatal("no Authorization on jobs.get")
	}
}

func TestJobsDownloadExpired(t *testing.T) {
	f := newFakeAPI(t, func(w http.ResponseWriter, r *http.Request, n int) {
		writeJSON(w, 200, jobJSON("succeeded", nil))
	})
	c, _ := newTestClient(t, f)
	_, err := c.Jobs.Download(context.Background(), "job_1")
	var perr *Error
	if !errors.As(err, &perr) || perr.Code != CodeFileExpired {
		t.Fatalf("err = %v", err)
	}
}

func TestTemplateFiles(t *testing.T) {
	cases := map[string]string{
		"main/part.typ": EncodingUTF8, "data.CSV": EncodingUTF8, "logo.svg": EncodingUTF8, "refs.bib": EncodingUTF8,
		"logo.png": EncodingBase64, "font.ttf": EncodingBase64, "noext": EncodingBase64,
	}
	for path, enc := range cases {
		if got := NewTemplateFile(path, []byte("hi")); got.Encoding != enc || got.Path != path {
			t.Errorf("%s: %+v", path, got)
		}
	}
	if f := NewTemplateFile("x.png", []byte("hi")); f.Content != "aGk=" {
		t.Errorf("base64 content = %q", f.Content)
	}
	dir := t.TempDir()
	local := filepath.Join(dir, "header.typ")
	if err := os.WriteFile(local, []byte("= Header"), 0o644); err != nil {
		t.Fatal(err)
	}
	f, err := TemplateFileFromPath(local, "")
	if err != nil || f.Path != "header.typ" || f.Content != "= Header" || f.Encoding != EncodingUTF8 {
		t.Fatalf("%+v %v", f, err)
	}
	f, err = TemplateFileFromPath(local, "parts/header.typ")
	if err != nil || f.Path != "parts/header.typ" {
		t.Fatalf("%+v %v", f, err)
	}
	if _, err := TemplateFileFromPath(filepath.Join(dir, "missing"), ""); err == nil {
		t.Fatal("expected error")
	}
}

func TestVersionMatchesChangelog(t *testing.T) {
	b, err := os.ReadFile("CHANGELOG.md")
	if err != nil {
		t.Fatal(err)
	}
	re := regexp.MustCompile(`(?m)^## \[(\d+\.\d+\.\d+[^\]]*)\]`)
	m := re.FindSubmatch(b)
	if m == nil {
		t.Fatal("no released version heading in CHANGELOG.md")
	}
	if string(m[1]) != Version {
		t.Fatalf("CHANGELOG latest release %s != Version %s", m[1], Version)
	}
}
