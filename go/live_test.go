package pagerush

import (
	"bytes"
	"context"
	"os"
	"testing"
	"time"
)

// TestLiveSmoke runs against the real API only when PAGERUSH_API_KEY is set.
// It creates a template, renders it, checks the PDF and deletes the template.
func TestLiveSmoke(t *testing.T) {
	if os.Getenv("PAGERUSH_API_KEY") == "" {
		t.Skip("PAGERUSH_API_KEY not set")
	}
	client, err := NewClient("")
	if err != nil {
		t.Fatal(err)
	}
	ctx, cancel := context.WithTimeout(context.Background(), 2*time.Minute)
	defer cancel()

	created, err := client.Templates.Create(ctx, &CreateTemplateParams{
		Name:   "pagerush-go smoke test",
		Source: String("#let data = json(\"data.json\")\nHello #data.name"),
	})
	if err != nil {
		t.Fatal(err)
	}
	id := created.Template.ID
	t.Cleanup(func() {
		if err := client.Templates.Delete(context.Background(), id); err != nil {
			t.Errorf("delete: %v", err)
		}
	})

	res, err := client.Templates.Render(ctx, id, &RenderParams{Data: map[string]string{"name": "Go"}})
	if err != nil {
		t.Fatal(err)
	}
	if res.Queued {
		job, err := client.Jobs.Wait(ctx, res.JobID, nil)
		if err != nil {
			t.Fatal(err)
		}
		if job.Status != JobStatusSucceeded {
			t.Fatalf("job %s: %+v", job.Status, job.Error)
		}
		if res.PDF, err = client.Jobs.Download(ctx, res.JobID); err != nil {
			t.Fatal(err)
		}
	}
	if !bytes.HasPrefix(res.PDF, []byte("%PDF")) {
		t.Fatalf("not a PDF: %q", res.PDF[:min(len(res.PDF), 16)])
	}
}
