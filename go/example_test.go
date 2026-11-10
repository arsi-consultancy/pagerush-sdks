package pagerush_test

import (
	"context"
	"crypto/hmac"
	"crypto/sha256"
	"encoding/hex"
	"errors"
	"fmt"
	"io"
	"log"
	"net/http"
	"os"
	"regexp"
	"time"

	pagerush "github.com/arsi-consultancy/pagerush-sdks/go"
)

func ExampleNewClient() {
	// An empty key reads PAGERUSH_API_KEY.
	client, err := pagerush.NewClient("",
		pagerush.WithTimeout(30*time.Second),
		pagerush.WithMaxRetries(3),
	)
	if err != nil {
		log.Fatal(err)
	}
	_ = client
}

func ExampleTemplatesService_Create() {
	client, err := pagerush.NewClient("")
	if err != nil {
		log.Fatal(err)
	}
	logo, err := pagerush.TemplateFileFromPath("logo.png", "")
	if err != nil {
		log.Fatal(err)
	}
	created, err := client.Templates.Create(context.Background(), &pagerush.CreateTemplateParams{
		Name: "Invoice",
		Source: pagerush.String(`#let data = json("data.json")
#image("logo.png", width: 3cm)
= Invoice #data.number`),
		Files: []pagerush.TemplateFile{logo},
	})
	if err != nil {
		log.Fatal(err)
	}
	fmt.Println(created.Template.ID, created.Version.Version)
}

func ExampleTemplatesService_Render() {
	client, err := pagerush.NewClient("")
	if err != nil {
		log.Fatal(err)
	}
	res, err := client.Templates.Render(context.Background(), "tpl_123", &pagerush.RenderParams{
		Data:     map[string]any{"number": 42, "customer": "Ada"},
		Filename: pagerush.String("invoice-42.pdf"),
	})
	if err != nil {
		log.Fatal(err)
	}
	if err := res.WriteFile("invoice.pdf"); err != nil {
		log.Fatal(err)
	}
}

func ExampleTemplatesService_Render_async() {
	client, err := pagerush.NewClient("")
	if err != nil {
		log.Fatal(err)
	}
	ctx := context.Background()
	res, err := client.Templates.Render(ctx, "tpl_123", &pagerush.RenderParams{
		Data:        map[string]any{"number": 42},
		PreferAsync: true,
	})
	if err != nil {
		log.Fatal(err)
	}
	pdf := res.PDF
	if res.Queued {
		job, err := client.Jobs.Wait(ctx, res.JobID, nil)
		if err != nil {
			log.Fatal(err)
		}
		if job.Status == pagerush.JobStatusFailed {
			log.Fatalf("render failed: %s", job.Error.Message)
		}
		if pdf, err = client.Jobs.Download(ctx, res.JobID); err != nil {
			log.Fatal(err)
		}
	}
	if err := os.WriteFile("invoice.pdf", pdf, 0o644); err != nil {
		log.Fatal(err)
	}
}

func ExampleTemplatesService_ListAll() {
	client, err := pagerush.NewClient("")
	if err != nil {
		log.Fatal(err)
	}
	it := client.Templates.ListAll(context.Background(), nil)
	for it.Next() {
		t := it.Template()
		fmt.Println(t.ID, t.Name)
	}
	if err := it.Err(); err != nil {
		log.Fatal(err)
	}
}

func ExampleError() {
	client, err := pagerush.NewClient("")
	if err != nil {
		log.Fatal(err)
	}
	_, err = client.Templates.Render(context.Background(), "tpl_123", &pagerush.RenderParams{Data: map[string]any{}})
	var perr *pagerush.Error
	if errors.As(err, &perr) {
		switch perr.Code {
		case pagerush.CodeTemplateCompileFailed:
			for _, d := range perr.Errors {
				fmt.Printf("%s: %s\n", d.Path, d.Message)
			}
		case pagerush.CodeQuotaExceeded:
			fmt.Println("out of renders")
		default:
			fmt.Println(perr, "request id:", perr.RequestID)
		}
	}
}

func ExampleVerifyWebhook() {
	secret := "whsec_example"
	body := []byte("%PDF-1.7 ...")
	now := time.Unix(1767225600, 0)

	// The headers PageRush sends with a delivery. In a handler, pass r.Header.
	h := http.Header{}
	h.Set("X-PageRush-Event", "pdf.succeeded")
	h.Set("X-PageRush-Job-Id", "job_123")
	mac := hmac.New(sha256.New, []byte(secret))
	mac.Write([]byte("1767225600\npdf.succeeded\njob_123\n\n"))
	mac.Write(body)
	h.Set("X-PageRush-Signature", "t=1767225600,v2="+hex.EncodeToString(mac.Sum(nil)))

	err := pagerush.VerifyWebhook(body, h, []string{secret}, pagerush.WithWebhookNow(now))
	fmt.Println(err)

	h.Set("X-PageRush-Job-Id", "job_999") // a changed header no longer matches
	err = pagerush.VerifyWebhook(body, h, []string{secret}, pagerush.WithWebhookNow(now))
	var verr *pagerush.WebhookVerificationError
	if errors.As(err, &verr) {
		fmt.Println(verr.Reason)
	}
	// Output:
	// <nil>
	// signature
}

func ExampleConstructEvent() {
	secret := os.Getenv("PAGERUSH_WEBHOOK_SECRET")
	if secret == "" {
		log.Fatal("set PAGERUSH_WEBHOOK_SECRET")
	}
	safeJobID := regexp.MustCompile(`^[A-Za-z0-9_-]{1,64}$`)

	http.HandleFunc("/pagerush", func(w http.ResponseWriter, r *http.Request) {
		body, err := io.ReadAll(r.Body) // the raw bytes, never re-encoded JSON
		if err != nil {
			http.Error(w, "read error", http.StatusBadRequest)
			return
		}
		event, err := pagerush.ConstructEvent(body, r.Header, []string{secret})
		if err != nil {
			http.Error(w, "invalid signature", http.StatusBadRequest)
			return
		}
		if !safeJobID.MatchString(event.JobID) {
			http.Error(w, "unexpected job id", http.StatusBadRequest)
			return
		}
		switch event.Event {
		case pagerush.EventPDFSucceeded:
			if err := os.WriteFile(event.JobID+".pdf", event.PDF, 0o644); err != nil {
				http.Error(w, "cannot save", http.StatusInternalServerError)
				return
			}
		case pagerush.EventPDFFailed:
			log.Printf("job %s failed: %s", event.JobID, event.Failure.Error.Message)
		}
		w.WriteHeader(http.StatusNoContent)
	})
}
