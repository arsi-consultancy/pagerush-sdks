package pagerush

import (
	"context"
	"net/http"
	"net/url"
	"strings"
	"time"
)

// Defaults for Jobs.Wait.
const (
	DefaultWaitTimeout  = 120 * time.Second
	DefaultWaitInterval = 1 * time.Second
)

// JobsService reads render jobs. Use it as client.Jobs.
type JobsService struct {
	client *Client
}

// WaitOptions configure Jobs.Wait. Zero values use the defaults.
type WaitOptions struct {
	// Timeout is how long to wait in total (default 120 s).
	Timeout time.Duration
	// Interval is the pause between polls (default 1 s).
	Interval time.Duration
}

// Get returns a job's status and, once it has succeeded, a download link.
func (s *JobsService) Get(ctx context.Context, id string) (*Job, error) {
	if err := requireID("job", id); err != nil {
		return nil, err
	}
	var job Job
	if err := s.client.getJSON(ctx, "/v1/jobs/"+url.PathEscape(id), nil, &job); err != nil {
		return nil, err
	}
	return &job, nil
}

// Wait polls the job until its status is succeeded or failed and returns it.
// A failed job is returned with a nil error; check Job.Status and Job.Error.
// If the job is still running after opts.Timeout, Wait returns an *Error with
// Status 0 and Code CodeWaitTimeout. opts may be nil.
func (s *JobsService) Wait(ctx context.Context, id string, opts *WaitOptions) (*Job, error) {
	timeout, interval := DefaultWaitTimeout, DefaultWaitInterval
	if opts != nil {
		if opts.Timeout > 0 {
			timeout = opts.Timeout
		}
		if opts.Interval > 0 {
			interval = opts.Interval
		}
	}
	deadline := s.client.now().Add(timeout)
	for {
		job, err := s.Get(ctx, id)
		if err != nil {
			return nil, err
		}
		if job.Done() {
			return job, nil
		}
		remaining := deadline.Sub(s.client.now())
		if remaining <= 0 {
			return nil, clientError(CodeWaitTimeout, "job %s did not finish within %s (status %s)", id, timeout, job.Status)
		}
		pause := interval
		if pause > remaining {
			pause = remaining
		}
		if err := s.client.sleep(ctx, pause); err != nil {
			return nil, connectionError(ctx, err)
		}
	}
}

// Download fetches the job, then downloads its PDF from the signed
// download_url (without the Authorization header). If the job has no
// download_url (not finished, failed, expired, or already deleted after
// download), it returns an *Error with Code CodeFileExpired.
func (s *JobsService) Download(ctx context.Context, id string) ([]byte, error) {
	job, err := s.Get(ctx, id)
	if err != nil {
		return nil, err
	}
	if job.DownloadURL == nil || *job.DownloadURL == "" {
		return nil, clientError(CodeFileExpired, "job %s has no download_url (status %s): the PDF is not available", id, job.Status)
	}
	target := *job.DownloadURL
	u, perr := url.Parse(target)
	if perr == nil && !u.IsAbs() {
		target = s.client.baseURL + "/" + strings.TrimLeft(target, "/")
	} else if perr != nil || (u.Scheme != "http" && u.Scheme != "https") {
		return nil, invalidResponse(http.StatusOK, "job download_url is not an http or https URL", nil)
	}
	resp, err := s.client.do(ctx, request{
		method: http.MethodGet,
		url:    target,
		accept: "application/pdf",
		noAuth: true,
		policy: retrySafe,
	})
	if err != nil {
		return nil, err
	}
	return resp.body, nil
}
