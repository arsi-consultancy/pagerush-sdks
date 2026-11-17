// Creates a template, renders it with data and saves invoice.pdf.
//
//     PAGERUSH_API_KEY=pk_live_... dotnet run --project examples/Quickstart
using System;
using PageRush;

var client = new PageRushClient(); // reads PAGERUSH_API_KEY

var created = await client.Templates.CreateAsync(new CreateTemplateOptions
{
    Name = "Quickstart invoice",
    Source = string.Join("\n",
        "#let data = json(\"data.json\")",
        "= Invoice #data.number",
        "Bill to: #data.customer",
        "",
        "#table(columns: 2, ..data.items.map(i => (i.name, str(i.price))).flatten())"),
});

var result = await client.Templates.RenderAsync(created.Template.Id, new RenderOptions
{
    Data = new
    {
        number = "INV-001",
        customer = "Example Corp",
        items = new[]
        {
            new { name = "Design", price = 400 },
            new { name = "Build", price = 1200 },
        },
    },
});

await result.SaveAsync("invoice.pdf");
Console.WriteLine($"Saved invoice.pdf (job {result.JobId})");
