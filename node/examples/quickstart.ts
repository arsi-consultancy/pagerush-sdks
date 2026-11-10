/**
 * Creates a template, renders it with data, saves invoice.pdf, then deletes the template.
 *
 *   PAGERUSH_API_KEY=pk_live_... npx tsx examples/quickstart.ts
 */
import { PageRush } from "pagerush";

const client = new PageRush(); // reads PAGERUSH_API_KEY

const { template } = await client.templates.create({
  name: "Quickstart invoice",
  source: [
    '#let data = json("data.json")',
    "= Invoice #data.number",
    "Bill to: #data.customer",
    "",
    "#table(columns: 2, ..data.items.map(i => (i.name, str(i.price))).flatten())",
  ].join("\n"),
});

const result = await client.templates.render(template.id, {
  data: {
    number: "INV-001",
    customer: "Example Corp",
    items: [
      { name: "Design", price: 400 },
      { name: "Build", price: 1200 },
    ],
  },
});

await result.save("invoice.pdf");
console.log(`Saved invoice.pdf (${result.page_count ?? "?"} pages, job ${result.job_id})`);

await client.templates.delete(template.id);
