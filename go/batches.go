package pagerush

import (
	"context"
	"fmt"
	"mime"
	"net/http"
	"net/url"
	"time"
)

// Defaults for Batches.Wait.
const (
	DefaultBatchWaitTimeout  = 600 * time.Second
	DefaultBatchWaitInterval = 2 * time.Second
)

// BatchesService renders many PDFs from one template in a single request and
// downloads them as a ZIP. Use it as client.Batches.
type BatchesService struct {
	client *Client
}

// BatchItemParams is one document of a batch: the data to render it with and
// an optional file name for it in the ZIP.
type BatchItemParams struct {
	// Data is any JSON value, as for RenderParams.Data. The template reads it
	// with #let data = json("data.json").
	Data any `json:"data"`
	// Filename is the name of this PDF in the ZIP. The API picks one when nil.
	Filename *string `json:"filename,omitempty"`
}

// CreateBatchParams are the options for Batches.Create.
type CreateBatchParams struct {
	// Items are the documents to render: 1 to 10,000.
	Items []BatchItemParams `json:"items"`
	// Version renders this version instead of the live one.
	Version *int `json:"version,omitempty"`
	// Metadata is up to 20 string pairs (at most 4 KB as JSON), echoed on the
	// batch and its batch.completed callback.
	Metadata map[string]string `json:"metadata,omitempty"`
	// DeleteAfterDownload deletes the PDFs once the ZIP has been downloaded
	// instead of keeping them for 24 hours. Nil uses the project's setting.
	DeleteAfterDownload *bool `json:"delete_after_download,omitempty"`
	// CallbackURL receives a signed batch.completed notice (JSON, without the
	// PDFs) when the batch has finished. It must be a public https URL.
	CallbackURL *string `json:"callback_url,omitempty"`

	// IdempotencyKey is sent as the Idempotency-Key header, with the same
	// rules as RenderParams.IdempotencyKey. If empty, the SDK uses a random
	// UUID v4 and reuses it on every retry of this call, so a retry never
	// creates a second batch.
	IdempotencyKey string `json:"-"`
}

// Batch statuses. A batch is done once it is succeeded (every item
// rendered), partial (some items failed) or failed (every item failed).
const (
	BatchStatusQueued    = "queued"
	BatchStatusRendering = "rendering"
	BatchStatusSucceeded = "succeeded"
	BatchStatusPartial   = "partial"
	BatchStatusFailed    = "failed"
)

// Batch is a batch render. Field names follow the API.
type Batch struct {
	ID string `json:"id"`
	// Status is one of the BatchStatus constants.
	Status string `json:"status"`
	// TemplateID is nil once the template has been deleted.
	TemplateID      *string `json:"template_id"`
	TemplateVersion int     `json:"template_version"`
	// ItemCount is the number of items in the batch.
	ItemCount int `json:"item_count"`
	// Succeeded and Failed count the items rendered and failed so far.
	Succeeded int `json:"succeeded"`
	Failed    int `json:"failed"`
	// ZipURL is the API URL of the ZIP of the rendered PDFs. It needs your API
	// key; download it with Batches.DownloadZip.
	ZipURL *string `json:"zip_url"`
	// ExpiresAt is when the PDFs are deleted, or nil.
	ExpiresAt           *time.Time        `json:"expires_at"`
	Metadata            map[string]string `json:"metadata"`
	DeleteAfterDownload bool              `json:"delete_after_download"`
	// Delivery is the state of the batch.completed callback, or nil without a
	// CallbackURL.
	Delivery   *JobDelivery `json:"delivery"`
	CreatedAt  time.Time    `json:"created_at"`
	FinishedAt *time.Time   `json:"finished_at"`
	// Items is the state of each item, in request order. Only Batches.Get
	// and Batches.Wait fill it.
	Items []BatchItem `json:"items,omitempty"`
}

func (b *Batch) valid() bool { return b.ID != "" }

// Done reports whether the batch has finished (succeeded, partial or failed).
func (b *Batch) Done() bool {
	return b.Status == BatchStatusSucceeded || b.Status == BatchStatusPartial || b.Status == BatchStatusFailed
}

// BatchItem is the state of one item of a batch.
type BatchItem struct {
	// Index is the item's position in CreateBatchParams.Items, from 0.
	Index int `json:"index"`
	// JobID is the render job for this item.
	JobID string `json:"job_id"`
	// Status is one of the JobStatus constants.
	Status string `json:"status"`
	// Filename is the PDF's name in the ZIP.
	Filename *string `json:"filename"`
	// Bytes is the PDF's size, or nil until it has rendered.
	Bytes *int64 `json:"bytes"`
	// Renders is the number of renders this item used.
	Renders int `json:"renders"`
	// Error is why the item failed, or nil.
	Error *JobError `json:"error"`
}

// Create starts a batch render of the template, one PDF per item, and
// returns the queued batch. Wait for it with Batches.Wait, or pass a
// CallbackURL. The API refuses a batch the project's quota and credits can't
// cover (CodeQuotaExceeded) and a fourth unfinished batch
// (CodeConcurrencyLimit).
//
// Create is retried on connection errors, timeouts, 429, 502, 503 and 504,
// always with the same idempotency key.
func (s *BatchesService) Create(ctx context.Context, templateID string, params *CreateBatchParams) (*Batch, error) {
	if err := requireID("template", templateID); err != nil {
		return nil, err
	}
	if params == nil {
		params = &CreateBatchParams{}
	}
	key := params.IdempotencyKey
	if key == "" {
		key = newIdempotencyKey()
	} else if !idempotencyKeyPattern.MatchString(key) {
		return nil, clientError(CodeValidationFailed, "IdempotencyKey must be 1 to 255 characters: letters, digits, '_', '-', ':' or '.'")
	}
	var batch Batch
	err := s.client.sendJSON(ctx, request{
		method:  http.MethodPost,
		url:     s.client.apiURL(templatePath(templateID, "batches"), nil),
		body:    params,
		headers: map[string]string{"Idempotency-Key": key},
		policy:  retrySafe,
	}, &batch)
	if err != nil {
		return nil, err
	}
	return &batch, nil
}

// Get returns a batch with the state of each item.
func (s *BatchesService) Get(ctx context.Context, id string) (*Batch, error) {
	if err := requireID("batch", id); err != nil {
		return nil, err
	}
	var batch Batch
	if err := s.client.getJSON(ctx, batchPath(id), nil, &batch); err != nil {
		return nil, err
	}
	return &batch, nil
}

// Wait polls the batch until its status is succeeded, partial or failed and
// returns it. A partial or failed batch is returned with a nil error; check
// Batch.Status and the items' Error. If the batch is still running after
// opts.Timeout (default 600 s; polls every 2 s by default), Wait returns an
// *Error with Status 0 and Code CodeWaitTimeout. opts may be nil.
func (s *BatchesService) Wait(ctx context.Context, id string, opts *WaitOptions) (*Batch, error) {
	var batch *Batch
	err := s.client.poll(ctx, opts, DefaultBatchWaitTimeout, DefaultBatchWaitInterval, func() (bool, error) {
		b, err := s.Get(ctx, id)
		if err != nil {
			return false, err
		}
		batch = b
		return b.Done(), nil
	}, func(timeout time.Duration) *Error {
		return clientError(CodeWaitTimeout, "batch %s did not finish within %s (status %s)", id, timeout, batch.Status)
	})
	if err != nil {
		return nil, err
	}
	return batch, nil
}

// DownloadZip downloads the ZIP of the batch's rendered PDFs (sent with your
// API key). While the batch is still rendering, the API answers 409 with
// CodeConflict and a Retry-After; once no PDFs are left (expired or deleted
// after download), 410 with CodeFileExpired. Neither is retried. A 200
// response that is not application/zip is an *Error with
// CodeInvalidResponse.
func (s *BatchesService) DownloadZip(ctx context.Context, id string) ([]byte, error) {
	if err := requireID("batch", id); err != nil {
		return nil, err
	}
	resp, err := s.client.do(ctx, request{
		method: http.MethodGet,
		url:    s.client.apiURL(batchPath(id)+"/zip", nil),
		accept: "application/zip",
		policy: retrySafe,
	})
	if err != nil {
		return nil, err
	}
	ct := resp.header.Get("Content-Type")
	if mt, _, _ := mime.ParseMediaType(ct); mt != "application/zip" {
		return nil, invalidResponse(resp.status, fmt.Sprintf("batch ZIP response has Content-Type %q, want application/zip", ct), nil)
	}
	return resp.body, nil
}

func batchPath(id string) string {
	return "/v1/batches/" + url.PathEscape(id)
}
