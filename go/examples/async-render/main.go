// Command async-render renders a template with PreferAsync and handles both
// outcomes: the PDF comes back directly (the usual case), or, if rendering was
// temporarily at capacity, the API queues the job and the program waits for it
// and downloads the PDF.
//
//	PAGERUSH_API_KEY=pk_live_... go run ./examples/async-render tpl_123
//
// Set CALLBACK_URL to an https URL to have the PDF POSTed there as well (see
// examples/webhook-receiver); the render is then always queued.
package main

import (
	"context"
	"errors"
	"fmt"
	"log"
	"os"
	"time"

	pagerush "github.com/arsi-consultancy/pagerush-sdks/go"
)

func main() {
	if len(os.Args) < 2 {
		log.Fatal("usage: async-render <template-id>")
	}
	templateID := os.Args[1]

	client, err := pagerush.NewClient("")
	if err != nil {
		log.Fatal(err)
	}
	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Minute)
	defer cancel()

	params := &pagerush.RenderParams{
		Data:        map[string]any{"number": "INV-002", "customer": "Example Ltd", "items": []any{}},
		Metadata:    map[string]string{"order_id": "1234"},
		PreferAsync: true,
	}
	if url := os.Getenv("CALLBACK_URL"); url != "" {
		params.CallbackURL = pagerush.String(url)
	}

	res, err := client.Templates.Render(ctx, templateID, params)
	if err != nil {
		var perr *pagerush.Error
		if errors.As(err, &perr) {
			log.Fatalf("render failed: %v (request id %s)", perr, perr.RequestID)
		}
		log.Fatal(err)
	}

	pdf := res.PDF
	if res.Queued {
		fmt.Println("queued as job", res.JobID)
		job, err := client.Jobs.Wait(ctx, res.JobID, &pagerush.WaitOptions{Interval: 2 * time.Second})
		if err != nil {
			log.Fatal(err)
		}
		if job.Status == pagerush.JobStatusFailed {
			log.Fatalf("job failed: %s: %s", job.Error.Code, job.Error.Message)
		}
		if pdf, err = client.Jobs.Download(ctx, res.JobID); err != nil {
			log.Fatal(err)
		}
	}
	if err := os.WriteFile("async.pdf", pdf, 0o644); err != nil {
		log.Fatal(err)
	}
	fmt.Println("Saved async.pdf")
}
