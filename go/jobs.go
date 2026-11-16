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

// WaitOptions configure Jobs.Wait and Batches.Wait. Zero values use the
// defaults.
type WaitOptions struct {
	// Timeout is how long to wait in total (default 120 s for Jobs.Wait,
	// 600 s for Batches.Wait).
	Timeout time.Duration
	// Interval is the pause between polls (default 1 s for Jobs.Wait, 2 s
	// for Batches.Wait).
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
	var job *Job
	err := s.client.poll(ctx, opts, DefaultWaitTimeout, DefaultWaitInterval, func() (bool, error) {
		j, err := s.Get(ctx, id)
		if err != nil {
			return false, err
		}
		job = j
		return j.Done(), nil
	}, func(timeout time.Duration) *Error {
		return clientError(CodeWaitTimeout, "job %s did not finish within %s (status %s)", id, timeout, job.Status)
	})
	if err != nil {
		return nil, err
	}
	return job, nil
}

// poll calls check until it reports done or fails, pausing between calls,
// and returns onTimeout's error once the wait timeout has passed. Zero
// values in opts use the given defaults. The pause stops when ctx is done.
func (c *Client) poll(ctx context.Context, opts *WaitOptions, timeout, interval time.Duration, check func() (bool, error), onTimeout func(time.Duration) *Error) error {
	if opts != nil {
		if opts.Timeout > 0 {
			timeout = opts.Timeout
		}
		if opts.Interval > 0 {
			interval = opts.Interval
		}
	}
	deadline := c.now().Add(timeout)
	for {
		done, err := check()
		if err != nil {
			return err
		}
		if done {
			return nil
		}
		remaining := deadline.Sub(c.now())
		if remaining <= 0 {
			return onTimeout(timeout)
		}
		pause := interval
		if pause > remaining {
			pause = remaining
		}
		if err := c.sleep(ctx, pause); err != nil {
			return connectionError(ctx, err)
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
