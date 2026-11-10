// Command webhook-receiver accepts PageRush callback_url deliveries, verifies
// their signatures and saves each PDF once.
//
//	PAGERUSH_WEBHOOK_SECRET=whsec_... go run ./examples/webhook-receiver
//
// During a secret rotation, set PAGERUSH_WEBHOOK_SECRET_PREVIOUS as well.
package main

import (
	"io"
	"log"
	"net/http"
	"os"
	"regexp"
	"sync"
	"time"

	pagerush "github.com/arsi-consultancy/pagerush-sdks/go"
)

var safeJobID = regexp.MustCompile(`^[A-Za-z0-9_-]{1,64}$`)

func main() {
	secret := os.Getenv("PAGERUSH_WEBHOOK_SECRET")
	if secret == "" {
		log.Fatal("set PAGERUSH_WEBHOOK_SECRET")
	}
	secrets := []string{secret}
	if prev := os.Getenv("PAGERUSH_WEBHOOK_SECRET_PREVIOUS"); prev != "" {
		secrets = append(secrets, prev)
	}

	// Delivery is at-least-once: remember job IDs already handled, and only
	// once the work for them succeeded, so a failed attempt is retried. Use
	// a database in production; this map is lost on restart.
	var mu sync.Mutex
	seen := map[string]bool{}

	mux := http.NewServeMux()
	mux.HandleFunc("/webhooks/pagerush", func(w http.ResponseWriter, r *http.Request) {
		if r.Method != http.MethodPost {
			http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
			return
		}
		// Verify the exact bytes received. Do not decode and re-encode JSON
		// first: the signature would no longer match.
		body, err := io.ReadAll(http.MaxBytesReader(w, r.Body, 64<<20))
		if err != nil {
			http.Error(w, "cannot read body", http.StatusBadRequest)
			return
		}
		event, err := pagerush.ConstructEvent(body, r.Header, secrets)
		if err != nil {
			log.Printf("rejected delivery: %v", err)
			http.Error(w, "invalid signature", http.StatusBadRequest)
			return
		}

		// The job ID becomes part of a file name: accept only the expected
		// characters.
		if !safeJobID.MatchString(event.JobID) {
			http.Error(w, "unexpected job id", http.StatusBadRequest)
			return
		}

		mu.Lock()
		dup := seen[event.JobID]
		mu.Unlock()
		if dup {
			w.WriteHeader(http.StatusOK)
			return
		}

		switch event.Event {
		case pagerush.EventPDFSucceeded:
			name := event.JobID + ".pdf"
			if err := os.WriteFile(name, event.PDF, 0o644); err != nil {
				http.Error(w, "cannot save", http.StatusInternalServerError)
				return
			}
			log.Printf("saved %s (metadata %v)", name, event.Metadata)
		case pagerush.EventPDFFailed:
			log.Printf("job %s failed: %s: %s", event.JobID, event.Failure.Error.Code, event.Failure.Error.Message)
		}

		mu.Lock()
		seen[event.JobID] = true
		mu.Unlock()
		w.WriteHeader(http.StatusOK)
	})

	addr := ":8080"
	if port := os.Getenv("PORT"); port != "" {
		addr = ":" + port
	}
	log.Printf("listening on %s", addr)
	srv := &http.Server{Addr: addr, Handler: mux, ReadHeaderTimeout: 10 * time.Second}
	log.Fatal(srv.ListenAndServe())
}
