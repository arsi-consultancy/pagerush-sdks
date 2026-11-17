// Renders with PreferAsync and handles both outcomes: the PDF in the response, or a queued job
// that it waits for and downloads.
//
//     PAGERUSH_API_KEY=pk_live_... dotnet run --project examples/AsyncRender -- tpl_123
using System;
using System.IO;
using PageRush;

if (args.Length != 1)
{
    Console.Error.WriteLine("usage: AsyncRender <template id>");
    return 2;
}

var client = new PageRushClient(); // reads PAGERUSH_API_KEY

try
{
    var result = await client.Templates.RenderAsync(args[0], new RenderOptions
    {
        Data = new { number = "INV-002", customer = "Example Corp", items = Array.Empty<object>() },
        PreferAsync = true,
        // Reuse a key of your own to make the render safe to repeat, for example after a crash.
        IdempotencyKey = "async-render-example-INV-002",
    });

    byte[] pdf;
    if (result.Queued)
    {
        Console.WriteLine($"Queued as job {result.JobId}; waiting...");
        var job = await client.Jobs.WaitAsync(result.JobId!, new WaitOptions { Timeout = TimeSpan.FromMinutes(2) });
        if (job.Status == JobStatus.Failed)
        {
            Console.Error.WriteLine($"Render failed: {job.Error?.Code}: {job.Error?.Message}");
            return 1;
        }

        pdf = await client.Jobs.DownloadAsync(job.Id);
    }
    else
    {
        pdf = result.Pdf!;
    }

    await File.WriteAllBytesAsync("invoice-async.pdf", pdf);
    Console.WriteLine($"Saved invoice-async.pdf ({pdf.Length} bytes)");
    return 0;
}
catch (PageRushException e)
{
    Console.Error.WriteLine($"{e.Message} (request {e.RequestId})");
    foreach (var d in e.Errors)
    {
        Console.Error.WriteLine($"  {d.Path}: {d.Message}");
    }

    return 1;
}
