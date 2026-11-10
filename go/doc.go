// Package pagerush is the official Go client for the PageRush API (v1), which
// renders PDFs from Typst templates and JSON data.
//
// Store a template once (its main.typ plus any files it uses), then render it
// with data. The template reads the render's data with
// #let data = json("data.json").
//
//	client, err := pagerush.NewClient("") // reads PAGERUSH_API_KEY
//	if err != nil {
//		log.Fatal(err)
//	}
//	ctx := context.Background()
//
//	created, err := client.Templates.Create(ctx, &pagerush.CreateTemplateParams{
//		Name:   "Invoice",
//		Source: pagerush.String("#let data = json(\"data.json\")\nInvoice for #data.name"),
//	})
//	if err != nil {
//		log.Fatal(err)
//	}
//	res, err := client.Templates.Render(ctx, created.Template.ID, &pagerush.RenderParams{
//		Data: map[string]any{"name": "Ada"},
//	})
//	if err != nil {
//		log.Fatal(err)
//	}
//	if err := res.WriteFile("invoice.pdf"); err != nil {
//		log.Fatal(err)
//	}
//
// # Errors
//
// API errors and connection failures are returned as *Error. Use errors.As
// and compare Code with the Code constants. Connection failures have Status 0
// and Code CodeConnectionError or CodeTimeout, and unwrap to their cause.
//
// # Retries
//
// GET, DELETE and render requests are retried on connection errors, timeouts,
// 429, 502, 503 and 504. Other POST and PATCH requests are retried only on 429
// and 503. Retries wait for Retry-After (capped at 30 s) or an exponential
// backoff, and stop when the context is done. Render requests always carry an
// Idempotency-Key (a random one if you do not set one), so a retry never renders
// or charges twice.
//
// # Webhooks
//
// VerifyWebhook and ConstructEvent check callback_url deliveries. The
// signature covers the body and the event, job ID and metadata headers. They
// need no Client, only the raw request body, its headers and your webhook
// secret(s).
package pagerush
