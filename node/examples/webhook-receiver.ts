/**
 * Receives callback_url deliveries with plain node:http, verifies the signature and saves PDFs.
 *
 *   PAGERUSH_WEBHOOK_SECRET=whsec_... npx tsx examples/webhook-receiver.ts
 *
 * Expose it over https (the API only calls https URLs) and pass that URL as `callback_url`.
 */
import { writeFile } from "node:fs/promises";
import { createServer, type IncomingMessage } from "node:http";
import { constructEvent, WebhookVerificationError } from "pagerush";

const secret = process.env.PAGERUSH_WEBHOOK_SECRET;
if (!secret) {
  console.error("Set PAGERUSH_WEBHOOK_SECRET to your project's webhook secret.");
  process.exit(1);
}

// Delivery is at-least-once. Use a database in production; a Set is enough for a demo.
const processed = new Set<string>();

/** Job ids are short and URL-safe; check before using one in a file name. */
const JOB_ID = /^[A-Za-z0-9_-]{1,64}$/;

/** The exact bytes received. Never verify a re-serialised JSON.parse result. */
async function rawBody(req: IncomingMessage): Promise<Uint8Array> {
  const chunks: Uint8Array[] = [];
  for await (const chunk of req) chunks.push(chunk as Uint8Array);
  return Buffer.concat(chunks);
}

const server = createServer(async (req, res) => {
  if (req.method !== "POST" || req.url !== "/pagerush") {
    res.writeHead(404).end();
    return;
  }
  try {
    const event = await constructEvent(await rawBody(req), req.headers, secret);
    if (!JOB_ID.test(event.job_id)) {
      res.writeHead(400).end("unexpected job id");
      return;
    }
    if (processed.has(event.job_id)) {
      res.writeHead(200).end("duplicate");
      return;
    }

    if (event.event === "pdf.succeeded" && event.pdf) {
      const file = `${event.job_id}.pdf`;
      await writeFile(file, event.pdf);
      console.log(`Saved ${file}`);
    } else if (event.event === "pdf.failed") {
      console.error(
        `Job ${event.job_id} failed: ${event.failure?.error.code}: ${event.failure?.error.message}`,
      );
    }
    // Only now: if the work above threw, PageRush retries the delivery and we try again.
    processed.add(event.job_id);
    res.writeHead(200).end("ok");
  } catch (err) {
    if (err instanceof WebhookVerificationError) {
      res.writeHead(400).end(`invalid signature (${err.reason})`);
      return;
    }
    console.error(err);
    res.writeHead(500).end();
  }
});

const port = Number(process.env.PORT ?? 3000);
server.listen(port, () => console.log(`Listening on http://localhost:${port}/pagerush`));
