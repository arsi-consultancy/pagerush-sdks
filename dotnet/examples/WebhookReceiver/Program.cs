// Accepts PageRush callback_url deliveries, verifies their signatures and saves each PDF once.
// It also logs batch.completed notices, which carry the finished batch instead of a PDF.
//
//     PAGERUSH_WEBHOOK_SECRET=whsec_... dotnet run --project examples/WebhookReceiver
//
// During a secret rotation, set PAGERUSH_WEBHOOK_SECRET_PREVIOUS as well.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using PageRush;

var secret = Environment.GetEnvironmentVariable("PAGERUSH_WEBHOOK_SECRET");
if (string.IsNullOrEmpty(secret))
{
    Console.Error.WriteLine("Set PAGERUSH_WEBHOOK_SECRET to your project's webhook secret.");
    return 1;
}

var secrets = new List<string> { secret };
if (Environment.GetEnvironmentVariable("PAGERUSH_WEBHOOK_SECRET_PREVIOUS") is { Length: > 0 } previous)
{
    secrets.Add(previous);
}

var safeId = new Regex("^[A-Za-z0-9_-]{1,64}$");

// Delivery is at-least-once: remember ids already handled, and only once the work for them
// succeeded, so a failed attempt is retried. Use a database in production; this is lost on restart.
var processed = new ConcurrentDictionary<string, bool>();

var app = WebApplication.CreateBuilder(args).Build();

app.MapPost("/webhooks/pagerush", async (HttpRequest request, ILogger<Program> log) =>
{
    // Verify the exact bytes received. Don't bind the body to a model or re-serialise JSON first:
    // the signature would no longer match.
    using var buffer = new MemoryStream();
    await request.Body.CopyToAsync(buffer);
    var body = buffer.ToArray();

    WebhookEvent ev;
    try
    {
        ev = Webhooks.ConstructEvent(body, request.Headers, secrets);
    }
    catch (WebhookVerificationException e)
    {
        log.LogWarning("Rejected delivery: {Reason}", e.Reason);
        return Results.BadRequest(e.Reason);
    }

    // A batch.completed notice carries a batch id instead of a job id. The id becomes part of a
    // file name: accept only the expected characters.
    var id = ev.JobId ?? ev.BatchId ?? "";
    if (!safeId.IsMatch(id))
    {
        return Results.BadRequest("unexpected id");
    }

    if (processed.ContainsKey(id))
    {
        return Results.Ok();
    }

    switch (ev.Event)
    {
        case WebhookEvents.PdfSucceeded:
            await File.WriteAllBytesAsync(id + ".pdf", ev.Pdf!);
            log.LogInformation("Saved {File}", id + ".pdf");
            break;
        case WebhookEvents.PdfFailed:
            log.LogError("Job {Id} failed: {Code}: {Message}", id, ev.Failure?.Error?.Code, ev.Failure?.Error?.Message);
            break;
        case WebhookEvents.BatchCompleted:
            // The notice has no PDFs. Download the ZIP with client.Batches.DownloadZipAsync(id),
            // for example from a background worker.
            log.LogInformation("Batch {Id} {Status}: {Succeeded} of {Count} rendered", id, ev.Batch?.Status, ev.Batch?.Succeeded, ev.Batch?.ItemCount);
            break;
    }

    processed[id] = true; // only after the work succeeded
    return Results.Ok();
});

app.Run();
return 0;
