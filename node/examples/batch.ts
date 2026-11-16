/**
 * Renders one PDF per row of a CSV file as a batch, waits for it and saves the ZIP.
 *
 *   PAGERUSH_API_KEY=pk_live_... npx tsx examples/batch.ts <template-id> customers.csv
 *
 * The CSV needs a header row; each row becomes the `data` of one render (e.g. `{ id, name }`), and
 * the template reads it with `#let data = json("data.json")`. This reader splits on commas and
 * doesn't handle quoted fields; use a CSV library for real files.
 */
import { readFile, writeFile } from "node:fs/promises";
import { type BatchItemInput, PageRush, PageRushError } from "pagerush";

const [templateId, csvPath] = process.argv.slice(2);
if (!templateId || !csvPath) {
  console.error("Usage: batch.ts <template-id> <file.csv>");
  process.exit(1);
}

/** Rows of a simple CSV (header row, no quoted fields) as objects. */
function parseCsv(text: string): Record<string, string>[] {
  const [header, ...rows] = text.split(/\r?\n/).filter((line) => line.trim() !== "");
  if (!header) return [];
  const keys = header.split(",").map((k) => k.trim());
  return rows.map((row) => {
    const cells = row.split(",");
    return Object.fromEntries(keys.map((k, i) => [k, (cells[i] ?? "").trim()]));
  });
}

const client = new PageRush();

try {
  const records = parseCsv(await readFile(csvPath, "utf8"));
  // Up to 10,000 items per batch. File names may use letters, numbers, spaces, "-", "." and "_".
  const items: BatchItemInput[] = records.map((record, i) => ({
    data: record,
    filename: `${(record.id ?? String(i + 1)).replace(/[^A-Za-z0-9 ._-]/g, "_")}.pdf`,
  }));

  const created = await client.batches.create(templateId, { items, metadata: { source: "csv" } });
  console.log(`Batch ${created.id} queued with ${created.item_count} items; waiting...`);

  const batch = await client.batches.wait(created.id, { timeout: 600_000, interval: 2_000 });
  console.log(`Batch ${batch.status}: ${batch.succeeded} succeeded, ${batch.failed} failed`);
  for (const item of batch.items ?? []) {
    if (item.error) console.error(`  item ${item.index}: ${item.error.code}: ${item.error.message}`);
  }

  if (batch.succeeded > 0) {
    const zip = await client.batches.downloadZip(batch.id);
    await writeFile("batch.zip", zip);
    console.log(`Saved batch.zip (${zip.byteLength} bytes)`);
  }
  if (batch.status !== "succeeded") process.exit(1);
} catch (err) {
  if (err instanceof PageRushError) {
    console.error(err.message, err.request_id ? `(request ${err.request_id})` : "");
    for (const e of err.errors) console.error(`  ${e.path}: ${e.message}`);
    process.exit(1);
  }
  throw err;
}
