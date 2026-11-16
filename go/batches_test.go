package pagerush

import (
	"context"
	"errors"
	"io"
	"net/http"
	"sync"
	"testing"
	"time"
)

func batchJSON(status string, withItems bool) map[string]any {
	b := map[string]any{
		"id": "bat_1", "status": status, "template_id": "tpl_1", "template_version": 3,
		"item_count": 2, "succeeded": 0, "failed": 0,
		"zip_url":    "https://pagerush.dev/v1/batches/bat_1/zip",
		"expires_at": "2026-01-02T00:00:00.000Z", "metadata": map[string]string{"run": "nov"},
		"delete_after_download": false, "delivery": nil,
		"created_at": "2026-01-01T00:00:00.000Z", "finished_at": nil,
	}
	if withItems {
		b["items"] = []any{
			map[string]any{"index": 0, "job_id": "job_a", "status": "succeeded", "filename": "ada.pdf", "bytes": 1234, "renders": 1, "error": nil},
			map[string]any{"index": 1, "job_id": "job_b", "status": "failed", "filename": "grace.pdf", "bytes": nil, "renders": 1,
				"error": map[string]string{"code": "INVALID_DATA", "message": "bad"}},
		}
	}
	return b
}

func TestBatchesCreate(t *testing.T) {
	f := newFakeAPI(t, func(w http.ResponseWriter, r *http.Request, n int) {
		writeJSON(w, 202, batchJSON("queued", false))
	})
	c, _ := newTestClient(t, f)
	batch, err := c.Batches.Create(context.Background(), "tpl/1", &CreateBatchParams{
		Items: []BatchItemParams{
			{Data: map[string]any{"name": "Ada"}, Filename: String("ada.pdf")},
			{Data: map[string]any{"name": "Grace"}},
		},
		Version:             Int(3),
		Metadata:            map[string]string{"run": "nov"},
		DeleteAfterDownload: Bool(true),
		CallbackURL:         String("https://example.com/hook"),
	})
	if err != nil {
		t.Fatal(err)
	}
	if batch.ID != "bat_1" || batch.Status != BatchStatusQueued || batch.TemplateVersion != 3 || batch.ItemCount != 2 ||
		batch.ZipURL == nil || batch.Metadata["run"] != "nov" || batch.ExpiresAt == nil || batch.Done() || batch.Items != nil {
		t.Fatalf("batch = %+v", batch)
	}
	req := f.reqs()[0]
	if req.Method != "POST" || req.Path != "/v1/templates/tpl%2F1/batches" {
		t.Fatalf("request = %s %s", req.Method, req.Path)
	}
	if req.Header.Get("Content-Type") != "application/json" || req.Header.Get("Accept") != "application/json" ||
		req.Header.Get("Authorization") != "Bearer pk_test_123" {
		t.Fatalf("headers = %v", req.Header)
	}
	if !uuidV4.MatchString(req.Header.Get("Idempotency-Key")) {
		t.Fatalf("Idempotency-Key = %q", req.Header.Get("Idempotency-Key"))
	}
	body := jsonBody(t, req.Body)
	items, _ := body["items"].([]any)
	if len(items) != 2 || body["version"] != float64(3) || body["delete_after_download"] != true ||
		body["callback_url"] != "https://example.com/hook" || body["metadata"].(map[string]any)["run"] != "nov" {
		t.Fatalf("body = %s", req.Body)
	}
	first, second := items[0].(map[string]any), items[1].(map[string]any)
	if first["filename"] != "ada.pdf" || first["data"].(map[string]any)["name"] != "Ada" {
		t.Fatalf("item 0 = %v", first)
	}
	if _, ok := second["filename"]; ok {
		t.Fatalf("item 1 = %v", second)
	}
	if _, ok := body["idempotency_key"]; ok {
		t.Fatal("idempotency key sent in the body")
	}
}

func TestBatchesCreateMinimalBody(t *testing.T) {
	f := newFakeAPI(t, func(w http.ResponseWriter, r *http.Request, n int) {
		writeJSON(w, 202, batchJSON("queued", false))
	})
	c, _ := newTestClient(t, f)
	if _, err := c.Batches.Create(context.Background(), "tpl_1", &CreateBatchParams{
		Items:          []BatchItemParams{{Data: 1}},
		IdempotencyKey: "run:nov.1",
	}); err != nil {
		t.Fatal(err)
	}
	req := f.reqs()[0]
	if string(req.Body) != `{"items":[{"data":1}]}` {
		t.Fatalf("body = %s", req.Body)
	}
	if req.Header.Get("Idempotency-Key") != "run:nov.1" {
		t.Fatalf("Idempotency-Key = %q", req.Header.Get("Idempotency-Key"))
	}
}

func TestBatchesCreateIdempotencyKeyReused(t *testing.T) {
	f := newFakeAPI(t, func(w http.ResponseWriter, r *http.Request, n int) {
		switch n {
		case 1:
			writeProblem(w, 502, CodeInternal, nil)
		case 2:
			w.Header().Set("Retry-After", "3")
			writeProblem(w, 503, CodeRenderUnavailable, nil)
		default:
			writeJSON(w, 202, batchJSON("queued", false))
		}
	})
	c, s := newTestClient(t, f)
	if _, err := c.Batches.Create(context.Background(), "tpl_1", &CreateBatchParams{Items: []BatchItemParams{{Data: 1}}}); err != nil {
		t.Fatal(err)
	}
	reqs := f.reqs()
	if len(reqs) != 3 {
		t.Fatalf("got %d requests", len(reqs))
	}
	k1, k2, k3 := reqs[0].Header.Get("Idempotency-Key"), reqs[1].Header.Get("Idempotency-Key"), reqs[2].Header.Get("Idempotency-Key")
	if !uuidV4.MatchString(k1) || k1 != k2 || k2 != k3 {
		t.Fatalf("keys %q %q %q", k1, k2, k3)
	}
	if len(s.delays) != 2 || s.delays[1] != 3*time.Second {
		t.Fatalf("delays = %v", s.delays)
	}
	// A second call gets a new key.
	if _, err := c.Batches.Create(context.Background(), "tpl_1", &CreateBatchParams{Items: []BatchItemParams{{Data: 1}}}); err != nil {
		t.Fatal(err)
	}
	if k4 := f.reqs()[3].Header.Get("Idempotency-Key"); k4 == k1 {
		t.Fatal("key reused across calls")
	}
}

func TestBatchesCreateRetryPolicy(t *testing.T) {
	cases := []struct {
		name      string
		status    int
		code      string
		wantCalls int
	}{
		{"retried on 504", 504, CodeInternal, 3},
		{"retried on 429", 429, CodeRateLimited, 3},
		{"not retried on 409 CONFLICT", 409, CodeConflict, 1},
		{"not retried on IDEMPOTENCY_CONFLICT", 409, CodeIdempotencyConflict, 1},
		{"not retried on 402", 402, CodeQuotaExceeded, 1},
		{"429 CONCURRENCY_LIMIT retried like any 429", 429, CodeConcurrencyLimit, 3},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			f := newFakeAPI(t, func(w http.ResponseWriter, r *http.Request, n int) {
				writeProblem(w, tc.status, tc.code, nil)
			})
			c, _ := newTestClient(t, f)
			_, err := c.Batches.Create(context.Background(), "tpl_1", &CreateBatchParams{Items: []BatchItemParams{{Data: 1}}})
			var perr *Error
			if !errors.As(err, &perr) || perr.Status != tc.status || perr.Code != tc.code {
				t.Fatalf("err = %v", err)
			}
			if got := len(f.reqs()); got != tc.wantCalls {
				t.Fatalf("calls = %d, want %d", got, tc.wantCalls)
			}
		})
	}
}

func TestBatchesCreateValidation(t *testing.T) {
	c, _ := NewClient("k", WithBaseURL("http://127.0.0.1:1"))
	var perr *Error
	_, err := c.Batches.Create(context.Background(), "tpl_1", &CreateBatchParams{IdempotencyKey: "has space"})
	if !errors.As(err, &perr) || perr.Code != CodeValidationFailed || perr.Status != 0 {
		t.Fatalf("bad key: err = %v", err)
	}
	_, err = c.Batches.Create(context.Background(), "", nil)
	if !errors.As(err, &perr) || perr.Code != CodeValidationFailed {
		t.Fatalf("empty id: err = %v", err)
	}
	for _, call := range []func() error{
		func() error { _, err := c.Batches.Get(context.Background(), ""); return err },
		func() error { _, err := c.Batches.Wait(context.Background(), "", nil); return err },
		func() error { _, err := c.Batches.DownloadZip(context.Background(), ""); return err },
	} {
		if err := call(); !errors.As(err, &perr) || perr.Code != CodeValidationFailed {
			t.Fatalf("empty id: err = %v", err)
		}
	}
}

func TestBatchesCreateMissingID(t *testing.T) {
	f := newFakeAPI(t, func(w http.ResponseWriter, r *http.Request, n int) {
		writeJSON(w, 202, map[string]any{"status": "queued"})
	})
	c, _ := newTestClient(t, f)
	_, err := c.Batches.Create(context.Background(), "tpl_1", &CreateBatchParams{Items: []BatchItemParams{{Data: 1}}})
	var perr *Error
	if !errors.As(err, &perr) || perr.Code != CodeInvalidResponse || perr.Status != 202 {
		t.Fatalf("err = %v", err)
	}
}

func TestBatchesGet(t *testing.T) {
	f := newFakeAPI(t, func(w http.ResponseWriter, r *http.Request, n int) {
		writeJSON(w, 200, batchJSON("partial", true))
	})
	c, _ := newTestClient(t, f)
	batch, err := c.Batches.Get(context.Background(), "bat 1")
	if err != nil {
		t.Fatal(err)
	}
	req := f.reqs()[0]
	if req.Method != "GET" || req.Path != "/v1/batches/bat%201" {
		t.Fatalf("request = %s %s", req.Method, req.Path)
	}
	if !batch.Done() || len(batch.Items) != 2 {
		t.Fatalf("batch = %+v", batch)
	}
	a, b := batch.Items[0], batch.Items[1]
	if a.Index != 0 || a.JobID != "job_a" || a.Status != JobStatusSucceeded || *a.Filename != "ada.pdf" ||
		*a.Bytes != 1234 || a.Renders != 1 || a.Error != nil {
		t.Fatalf("item 0 = %+v", a)
	}
	if b.Index != 1 || b.Bytes != nil || b.Error == nil || b.Error.Code != CodeInvalidData {
		t.Fatalf("item 1 = %+v", b)
	}
}

func TestBatchesWait(t *testing.T) {
	f := newFakeAPI(t, func(w http.ResponseWriter, r *http.Request, n int) {
		if r.URL.Path != "/v1/batches/bat_1" {
			t.Errorf("path = %s", r.URL.Path)
		}
		status := "queued"
		if n == 2 {
			status = "rendering"
		}
		if n >= 3 {
			status = "partial"
		}
		writeJSON(w, 200, batchJSON(status, true))
	})
	c, s := newTestClient(t, f)
	batch, err := c.Batches.Wait(context.Background(), "bat_1", nil)
	if err != nil || batch.Status != BatchStatusPartial || len(batch.Items) != 2 {
		t.Fatalf("Wait: %+v %v", batch, err)
	}
	if len(f.reqs()) != 3 || len(s.delays) != 2 || s.delays[0] != DefaultBatchWaitInterval {
		t.Fatalf("requests %d, delays %v", len(f.reqs()), s.delays)
	}
}

func TestBatchesWaitReturnsFinishedBatch(t *testing.T) {
	for _, status := range []string{BatchStatusSucceeded, BatchStatusFailed} {
		f := newFakeAPI(t, func(w http.ResponseWriter, r *http.Request, n int) {
			writeJSON(w, 200, batchJSON(status, true))
		})
		c, s := newTestClient(t, f)
		batch, err := c.Batches.Wait(context.Background(), "bat_1", nil)
		if err != nil || batch.Status != status || len(s.delays) != 0 {
			t.Fatalf("%s: %+v %v", status, batch, err)
		}
	}
}

func TestBatchesWaitTimeout(t *testing.T) {
	f := newFakeAPI(t, func(w http.ResponseWriter, r *http.Request, n int) {
		writeJSON(w, 200, batchJSON("rendering", false))
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
	_, err := c.Batches.Wait(context.Background(), "bat_1", &WaitOptions{Timeout: 5 * time.Second})
	var perr *Error
	if !errors.As(err, &perr) || perr.Code != CodeWaitTimeout || perr.Status != 0 {
		t.Fatalf("err = %v", err)
	}
	// Polls at 0, 2, 4 and 5 s (default interval 2 s).
	if len(f.reqs()) != 4 {
		t.Fatalf("got %d polls", len(f.reqs()))
	}
}

func TestBatchesWaitContextCanceled(t *testing.T) {
	f := newFakeAPI(t, func(w http.ResponseWriter, r *http.Request, n int) {
		writeJSON(w, 200, batchJSON("rendering", false))
	})
	c, _ := newTestClient(t, f)
	ctx, cancel := context.WithCancel(context.Background())
	c.sleep = func(ctx context.Context, d time.Duration) error {
		cancel()
		return sleepContext(ctx, d)
	}
	_, err := c.Batches.Wait(ctx, "bat_1", nil)
	if !errors.Is(err, context.Canceled) {
		t.Fatalf("err = %v", err)
	}
}

func TestBatchesDownloadZip(t *testing.T) {
	f := newFakeAPI(t, func(w http.ResponseWriter, r *http.Request, n int) {
		w.Header().Set("Content-Type", "application/zip")
		_, _ = io.WriteString(w, "PK\x03\x04zip")
	})
	c, _ := newTestClient(t, f)
	zip, err := c.Batches.DownloadZip(context.Background(), "bat/1")
	if err != nil || string(zip) != "PK\x03\x04zip" {
		t.Fatalf("DownloadZip: %q %v", zip, err)
	}
	req := f.reqs()[0]
	if req.Method != "GET" || req.Path != "/v1/batches/bat%2F1/zip" {
		t.Fatalf("request = %s %s", req.Method, req.Path)
	}
	if req.Header.Get("Authorization") != "Bearer pk_test_123" || req.Header.Get("Accept") != "application/zip" {
		t.Fatalf("headers = %v", req.Header)
	}
}

func TestBatchesDownloadZipRejectsNonZip(t *testing.T) {
	f := newFakeAPI(t, func(w http.ResponseWriter, r *http.Request, n int) {
		w.Header().Set("Content-Type", "text/html")
		_, _ = io.WriteString(w, "<html>proxy</html>")
	})
	c, _ := newTestClient(t, f)
	_, err := c.Batches.DownloadZip(context.Background(), "bat_1")
	var perr *Error
	if !errors.As(err, &perr) || perr.Code != CodeInvalidResponse || perr.Status != 200 || !errors.Is(err, ErrUnexpectedResponse) {
		t.Fatalf("err = %v", err)
	}
}

func TestBatchesDownloadZipErrors(t *testing.T) {
	cases := []struct {
		status int
		code   string
	}{
		{409, CodeConflict},
		{410, CodeFileExpired},
	}
	for _, tc := range cases {
		t.Run(tc.code, func(t *testing.T) {
			f := newFakeAPI(t, func(w http.ResponseWriter, r *http.Request, n int) {
				w.Header().Set("Retry-After", "5")
				writeProblem(w, tc.status, tc.code, nil)
			})
			c, s := newTestClient(t, f)
			_, err := c.Batches.DownloadZip(context.Background(), "bat_1")
			var perr *Error
			if !errors.As(err, &perr) || perr.Status != tc.status || perr.Code != tc.code || perr.RetryAfter != 5*time.Second {
				t.Fatalf("err = %v", err)
			}
			if len(f.reqs()) != 1 || len(s.delays) != 0 {
				t.Fatalf("requests %d, delays %v", len(f.reqs()), s.delays)
			}
		})
	}
}
