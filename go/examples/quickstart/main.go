// Command quickstart creates a template, renders it with data and saves
// invoice.pdf. Run it with PAGERUSH_API_KEY set.
package main

import (
	"context"
	"fmt"
	"log"

	pagerush "github.com/arsi-consultancy/pagerush-sdks/go"
)

const source = `#let data = json("data.json")
= Invoice #data.number

Bill to: #data.customer

#table(
  columns: (1fr, auto),
  [*Item*], [*Amount*],
  ..data.items.map(i => ([#i.name], [#i.amount])).flatten(),
)`

func main() {
	client, err := pagerush.NewClient("") // reads PAGERUSH_API_KEY
	if err != nil {
		log.Fatal(err)
	}
	ctx := context.Background()

	created, err := client.Templates.Create(ctx, &pagerush.CreateTemplateParams{
		Name:   "Invoice",
		Source: pagerush.String(source),
	})
	if err != nil {
		log.Fatal(err)
	}

	res, err := client.Templates.Render(ctx, created.Template.ID, &pagerush.RenderParams{
		Data: map[string]any{
			"number":   "INV-001",
			"customer": "Example Ltd",
			"items": []map[string]string{
				{"name": "Design", "amount": "$400"},
				{"name": "Hosting", "amount": "$20"},
			},
		},
	})
	if err != nil {
		log.Fatal(err)
	}
	if err := res.WriteFile("invoice.pdf"); err != nil {
		log.Fatal(err)
	}
	fmt.Printf("Saved invoice.pdf (template %s, job %s)\n", created.Template.ID, res.JobID)
}
