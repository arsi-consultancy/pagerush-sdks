package pagerush

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"mime"
	"net/http"
	"os"
	"regexp"
	"strconv"
)

// RenderParams are the options for Templates.Render.
type RenderParams struct {
	// Data is any JSON value. The template reads it with
	// #let data = json("data.json"). Pass a map, a struct or json.RawMessage.
	Data any `json:"data"`
	// Version renders this version instead of the live one.
	Version *int `json:"version,omitempty"`
	// Filename is used in the response's Content-Disposition header.
	Filename *string `json:"filename,omitempty"`
	// Metadata is up to 20 string pairs (at most 4 KB as JSON), echoed on the job and the callback.
	Metadata map[string]string `json:"metadata,omitempty"`
	// DeleteAfterDownload deletes the PDF as soon as you have it instead of
	// keeping it for 24 hours. Nil uses the project's setting.
	DeleteAfterDownload *bool `json:"delete_after_download,omitempty"`
	// CallbackURL queues the render and POSTs the signed PDF to this public
	// https URL when it is ready. The call then returns a queued result.
	CallbackURL *string `json:"callback_url,omitempty"`

	// IdempotencyKey is sent as the Idempotency-Key header: 1 to 255 letters,
	// digits, '_', '-', ':' or '.'. Render returns a CodeValidationFailed
	// error, without sending anything, for any other value. If empty, the SDK
	// uses a random UUID v4 and reuses it on every retry of this call, so a
	// retry never renders or charges twice.
	IdempotencyKey string `json:"-"`
	// PreferAsync sends "Prefer: respond-async". It does not ask for an async
	// render: the API still renders right away, and only if that attempt hits a
	// temporary capacity problem does it queue the job (202) instead of
	// returning 503. Code that sets it must handle both a PDF and a queued
	// result.
	PreferAsync bool `json:"-"`
}

// RenderResult is the outcome of Templates.Render. If Queued is false, PDF
// holds the document. If Queued is true, the render was accepted as a job: use
// Jobs.Wait and Jobs.Download, or wait for the callback.
type RenderResult struct {
	Queued bool
	// PDF is the document (nil when queued).
	PDF []byte
	// Job is the queued job (nil when not queued).
	Job *Job
	// JobID is the X-Job-Id header, or the queued job's ID.
	JobID string
	// TemplateVersion is the version that was (or will be) rendered.
	TemplateVersion int
	// Engine is the X-Render-Engine header ("" when queued).
	Engine string
	// PageCount is the X-Page-Count header, or nil.
	PageCount *int
	// Warnings is the X-Template-Warnings header (Typst warnings), or nil.
	Warnings *int
	// RequestID is the X-Request-Id header ("" when absent).
	RequestID string
	// Filename is the filename from Content-Disposition ("" when absent).
	Filename string
}

// ErrQueued is returned by RenderResult.WriteFile when the render was queued
// and the result has no PDF.
var ErrQueued = errors.New("pagerush: render was queued; no PDF in this result (use Jobs.Wait and Jobs.Download)")

// WriteFile writes the PDF to path with mode 0644. It returns ErrQueued if the
// render was queued.
func (r *RenderResult) WriteFile(path string) error {
	if r.Queued || r.PDF == nil {
		return ErrQueued
	}
	return os.WriteFile(path, r.PDF, 0o644)
}

// Render renders a template with data. Without CallbackURL the PDF comes back
// in the result. With CallbackURL, or with PreferAsync when rendering is
// overloaded, the result is queued.
//
// Render is retried on connection errors, timeouts, 429, 502, 503, 504 and
// 409 CONFLICT (an earlier attempt with the same key is still rendering),
// always with the same idempotency key.
func (s *TemplatesService) Render(ctx context.Context, id string, params *RenderParams) (*RenderResult, error) {
	if err := requireID("template", id); err != nil {
		return nil, err
	}
	if params == nil {
		params = &RenderParams{}
	}
	key := params.IdempotencyKey
	if key == "" {
		key = newIdempotencyKey()
	} else if !idempotencyKeyPattern.MatchString(key) {
		return nil, clientError(CodeValidationFailed, "IdempotencyKey must be 1 to 255 characters: letters, digits, '_', '-', ':' or '.'")
	}
	headers := map[string]string{"Idempotency-Key": key}
	if params.PreferAsync {
		headers["Prefer"] = "respond-async"
	}
	resp, err := s.client.do(ctx, request{
		method:  http.MethodPost,
		url:     s.client.apiURL(templatePath(id, "render"), nil),
		body:    params,
		headers: headers,
		accept:  "application/pdf, application/json",
		policy:  retryRender,
	})
	if err != nil {
		return nil, err
	}
	if resp.status == http.StatusAccepted {
		var job Job
		if err := json.Unmarshal(resp.body, &job); err != nil {
			return nil, invalidResponse(resp.status, "202 response body is not a job", err)
		}
		if !job.valid() {
			return nil, invalidResponse(resp.status, "202 response body is missing the job id", nil)
		}
		res := &RenderResult{Queued: true, Job: &job, JobID: job.ID}
		if job.TemplateVersion != nil {
			res.TemplateVersion = *job.TemplateVersion
		}
		return res, nil
	}
	h := resp.header
	if mt, _, _ := mime.ParseMediaType(h.Get("Content-Type")); mt != "application/pdf" {
		return nil, invalidResponse(resp.status, fmt.Sprintf("render response has Content-Type %q, want application/pdf", h.Get("Content-Type")), nil)
	}
	res := &RenderResult{
		PDF:       resp.body,
		JobID:     h.Get("X-Job-Id"),
		Engine:    h.Get("X-Render-Engine"),
		PageCount: headerInt(h, "X-Page-Count"),
		Warnings:  headerInt(h, "X-Template-Warnings"),
		RequestID: h.Get("X-Request-Id"),
		Filename:  contentDispositionFilename(h.Get("Content-Disposition")),
	}
	if v := headerInt(h, "X-Template-Version"); v != nil {
		res.TemplateVersion = *v
	}
	return res, nil
}

func headerInt(h http.Header, name string) *int {
	v := h.Get(name)
	if v == "" {
		return nil
	}
	n, err := strconv.Atoi(v)
	if err != nil {
		return nil
	}
	return &n
}

var idempotencyKeyPattern = regexp.MustCompile(`^[A-Za-z0-9_\-:.]{1,255}$`)

var filenameRe = regexp.MustCompile(`filename="([^"]*)"`)

func contentDispositionFilename(cd string) string {
	if cd == "" {
		return ""
	}
	if _, params, err := mime.ParseMediaType(cd); err == nil {
		if f, ok := params["filename"]; ok {
			return f
		}
	}
	if m := filenameRe.FindStringSubmatch(cd); m != nil {
		return m[1]
	}
	return ""
}
