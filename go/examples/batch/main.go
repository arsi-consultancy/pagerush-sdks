// Command batch renders one PDF per record with a single batch request, waits
// for the batch to finish and saves the PDFs as invoices.zip.
//
//	PAGERUSH_API_KEY=pk_live_... go run ./examples/batch tpl_123
//	PAGERUSH_API_KEY=pk_live_... go run ./examples/batch tpl_123 invoices.csv
//
// Without a CSV file it renders three built-in records. A CSV file needs a
// header row: each row becomes the data {"<column>": "<value>", ...}, and a
// "filename" column, if present, names that PDF in the ZIP.
package main

import (
	"context"
	"encoding/csv"
	"errors"
	"fmt"
	"io"
	"log"
	"os"
	"time"

	pagerush "github.com/arsi-consultancy/pagerush-sdks/go"
)

func main() {
	if len(os.Args) < 2 {
		log.Fatal("usage: batch <template-id> [records.csv]")
	}
	templateID := os.Args[1]

	var items []pagerush.BatchItemParams
	if len(os.Args) > 2 {
		var err error
		if items, err = itemsFromCSV(os.Args[2]); err != nil {
			log.Fatal(err)
		}
	} else {
		items = builtinItems()
	}

	client, err := pagerush.NewClient("") // reads PAGERUSH_API_KEY
	if err != nil {
		log.Fatal(err)
	}
	ctx, cancel := context.WithTimeout(context.Background(), 15*time.Minute)
	defer cancel()

	batch, err := client.Batches.Create(ctx, templateID, &pagerush.CreateBatchParams{
		Items:    items,
		Metadata: map[string]string{"run": "example"},
	})
	if err != nil {
		var perr *pagerush.Error
		if errors.As(err, &perr) {
			log.Fatalf("batch not accepted: %v (request id %s)", perr, perr.RequestID)
		}
		log.Fatal(err)
	}
	fmt.Printf("batch %s queued with %d items\n", batch.ID, batch.ItemCount)

	batch, err = client.Batches.Wait(ctx, batch.ID, &pagerush.WaitOptions{Timeout: 10 * time.Minute})
	if err != nil {
		log.Fatal(err)
	}
	fmt.Printf("batch %s: %d succeeded, %d failed\n", batch.Status, batch.Succeeded, batch.Failed)
	for _, item := range batch.Items {
		if item.Error != nil {
			fmt.Printf("  item %d: %s: %s\n", item.Index, item.Error.Code, item.Error.Message)
		}
	}
	if batch.Succeeded == 0 {
		log.Fatal("no PDFs to download")
	}

	zip, err := client.Batches.DownloadZip(ctx, batch.ID)
	if err != nil {
		log.Fatal(err)
	}
	if err := os.WriteFile("invoices.zip", zip, 0o644); err != nil {
		log.Fatal(err)
	}
	fmt.Println("Saved invoices.zip")
}

// builtinItems builds batch items from a slice of records.
func builtinItems() []pagerush.BatchItemParams {
	type invoice struct {
		Number   string `json:"number"`
		Customer string `json:"customer"`
		Items    []any  `json:"items"`
	}
	invoices := []invoice{
		{Number: "INV-101", Customer: "Example Ltd"},
		{Number: "INV-102", Customer: "Sample GmbH"},
		{Number: "INV-103", Customer: "Demo Inc"},
	}
	items := make([]pagerush.BatchItemParams, 0, len(invoices))
	for _, inv := range invoices {
		inv.Items = []any{}
		items = append(items, pagerush.BatchItemParams{
			Data:     inv,
			Filename: pagerush.String(inv.Number + ".pdf"),
		})
	}
	return items
}

// itemsFromCSV turns each row of a CSV file with a header row into one item.
func itemsFromCSV(path string) ([]pagerush.BatchItemParams, error) {
	f, err := os.Open(path)
	if err != nil {
		return nil, err
	}
	defer f.Close()
	r := csv.NewReader(f)
	header, err := r.Read()
	if err != nil {
		return nil, fmt.Errorf("%s: reading the header row: %w", path, err)
	}
	var items []pagerush.BatchItemParams
	for {
		row, err := r.Read()
		if err == io.EOF {
			break
		}
		if err != nil {
			return nil, fmt.Errorf("%s: %w", path, err)
		}
		data := make(map[string]any, len(header))
		for i, name := range header {
			data[name] = row[i]
		}
		item := pagerush.BatchItemParams{Data: data}
		if name, ok := data["filename"].(string); ok && name != "" {
			item.Filename = pagerush.String(name)
		}
		items = append(items, item)
	}
	if len(items) == 0 {
		return nil, fmt.Errorf("%s: no rows after the header", path)
	}
	return items, nil
}
