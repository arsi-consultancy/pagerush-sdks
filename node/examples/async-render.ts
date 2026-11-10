/**
 * Renders with `preferAsync` and handles both outcomes. Usually the PDF comes back at once. If the
 * server had no capacity, it queues the render instead (202): then this waits for the job and
 * downloads the PDF.
 *
 *   PAGERUSH_API_KEY=pk_live_... npx tsx examples/async-render.ts <template-id>
 */
import { writeFile } from "node:fs/promises";
import { PageRush, PageRushError } from "pagerush";

const templateId = process.argv[2];
if (!templateId) {
  console.error("Usage: async-render.ts <template-id>");
  process.exit(1);
}

const client = new PageRush();

try {
  const result = await client.templates.render(templateId, {
    data: { name: "Ada" },
    preferAsync: true,
    metadata: { order: "42" },
  });

  let pdf: Uint8Array;
  if (result.queued) {
    console.log(`Queued as job ${result.job_id}; waiting...`);
    const job = await client.jobs.wait(result.job_id, { timeout: 120_000 });
    if (job.status === "failed") {
      console.error(`Render failed: ${job.error?.code}: ${job.error?.message}`);
      process.exit(1);
    }
    pdf = await client.jobs.download(job.id);
  } else {
    console.log(`Rendered straight away as job ${result.job_id}`);
    pdf = result.pdf!;
  }

  await writeFile("async.pdf", pdf);
  console.log(`Saved async.pdf (${pdf.byteLength} bytes)`);
} catch (err) {
  if (err instanceof PageRushError) {
    console.error(err.message, err.request_id ? `(request ${err.request_id})` : "");
    for (const e of err.errors) console.error(`  ${e.path}: ${e.message}`);
    process.exit(1);
  }
  throw err;
}
