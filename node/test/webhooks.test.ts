import { createHmac } from "node:crypto";
import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";
import { constructEvent, verify, WebhookVerificationError, webhooks } from "../src";

interface Vector {
  name: string;
  body_base64: string;
  headers: Record<string, string>;
  secrets: string[];
  now: number;
  tolerance_seconds: number;
  valid: boolean;
  reason?: "header" | "timestamp" | "signature";
}

const vectors = (
  JSON.parse(readFileSync(new URL("./fixtures/webhooks.json", import.meta.url), "utf8")) as {
    cases: Vector[];
  }
).cases;

const fromBase64 = (s: string) => new Uint8Array(Buffer.from(s, "base64"));

describe("webhook test vectors", () => {
  it("loads the shared vectors", () => {
    expect(vectors.length).toBeGreaterThanOrEqual(24);
  });

  it.each(vectors.map((v) => [v.name, v] as const))("verify: %s", async (_name, v) => {
    const run = verify(fromBase64(v.body_base64), v.headers, v.secrets, {
      tolerance: v.tolerance_seconds,
      now: v.now,
    });
    if (v.valid) {
      await expect(run).resolves.toBeUndefined();
    } else {
      const err = await run.catch((e: unknown) => e);
      expect(err).toBeInstanceOf(WebhookVerificationError);
      expect((err as WebhookVerificationError).reason).toBe(v.reason);
    }
  });

  it.each(vectors.filter((v) => v.valid).map((v) => [v.name, v] as const))(
    "constructEvent: %s",
    async (_name, v) => {
      const body = fromBase64(v.body_base64);
      const event = await constructEvent(body, new Headers(v.headers), v.secrets, {
        tolerance: v.tolerance_seconds,
        now: v.now,
      });
      expect(event.event).toBe(v.headers["X-PageRush-Event"]);
      expect(event.job_id).toBe(v.headers["X-PageRush-Job-Id"]);
      expect(event.timestamp).toBe(Number(/t=(\d+)/.exec(v.headers["X-PageRush-Signature"]!)![1]));
      if (event.event === "pdf.succeeded") {
        expect(event.pdf).toEqual(body);
        expect(event.failure).toBeNull();
        const raw = v.headers["X-PageRush-Metadata"];
        expect(event.metadata).toEqual(raw ? JSON.parse(raw) : null);
      } else {
        expect(event.pdf).toBeNull();
        expect(event.failure).toEqual(JSON.parse(new TextDecoder().decode(body)));
      }
    },
  );

  it("parses \\u escapes in metadata back to the exact values", async () => {
    const v = vectors.find((c) => c.valid && c.headers["X-PageRush-Metadata"])!;
    const event = await constructEvent(fromBase64(v.body_base64), v.headers, v.secrets, { now: v.now });
    expect(event.metadata).toEqual({ order: "42", amount: "₹1,250" });
  });
});

const SECRET = "whsec_test_secret";
const T = 1_767_225_600;
const PDF = new TextEncoder().encode("%PDF-1.7\n%%EOF\n");

interface Delivery {
  event?: string;
  jobId?: string;
  metadata?: string;
  t?: number | string;
  secret?: string;
}

/** Headers for a delivery of `body`, signed the way the server signs them. */
function signed(body: Uint8Array | string, d: Delivery = {}): Record<string, string> {
  const event = d.event ?? "pdf.succeeded";
  const jobId = d.jobId ?? "job_123";
  const t = d.t ?? T;
  const hex = createHmac("sha256", d.secret ?? SECRET)
    .update(`${t}\n${event}\n${jobId}\n${d.metadata ?? ""}\n`)
    .update(body)
    .digest("hex");
  const headers: Record<string, string> = {
    "X-PageRush-Signature": `t=${t},v2=${hex}`,
    "X-PageRush-Event": event,
    "X-PageRush-Job-Id": jobId,
  };
  if (d.metadata !== undefined) headers["X-PageRush-Metadata"] = d.metadata;
  return headers;
}

const lowerCased = (h: Record<string, string>) =>
  Object.fromEntries(Object.entries(h).map(([k, v]) => [k.toLowerCase(), v]));

describe("webhooks.verify", () => {
  it("accepts string, Uint8Array, Buffer and ArrayBuffer bodies", async () => {
    const body = '{"hello":"world"}';
    const headers = signed(body, { event: "pdf.failed" });
    const bytes = new TextEncoder().encode(body);
    for (const raw of [body, bytes, Buffer.from(body), bytes.buffer]) {
      await expect(verify(raw, headers, SECRET, { now: T })).resolves.toBeUndefined();
    }
  });

  it("verifies a Uint8Array view into a larger buffer", async () => {
    const padded = new Uint8Array([0, 0, ...PDF, 0, 0]);
    await expect(
      verify(padded.subarray(2, 2 + PDF.length), signed(PDF), SECRET, { now: T }),
    ).resolves.toBeUndefined();
  });

  it("reads Node-style lower-case headers and a Fetch Headers object", async () => {
    const headers = signed(PDF, { metadata: '{"order":"42"}' });
    await expect(verify(PDF, lowerCased(headers), SECRET, { now: T })).resolves.toBeUndefined();
    await expect(verify(PDF, new Headers(headers), SECRET, { now: T })).resolves.toBeUndefined();
  });

  it("uses the system clock by default", async () => {
    const now = Math.floor(Date.now() / 1000);
    await expect(verify(PDF, signed(PDF, { t: now }), SECRET)).resolves.toBeUndefined();
    await expect(verify(PDF, signed(PDF, { t: now - 301 }), SECRET)).rejects.toMatchObject({
      reason: "timestamp",
    });
  });

  it("honours a custom tolerance", async () => {
    await expect(verify(PDF, signed(PDF), SECRET, { now: T + 10, tolerance: 5 })).rejects.toMatchObject({
      reason: "timestamp",
    });
  });

  it.each([
    ["the event header", "X-PageRush-Event"],
    ["the job id header", "X-PageRush-Job-Id"],
    ["the signature header", "X-PageRush-Signature"],
  ])("rejects a delivery without %s with reason header", async (_name, missing) => {
    const headers = signed(PDF);
    delete headers[missing];
    await expect(verify(PDF, headers, SECRET, { now: T })).rejects.toMatchObject({ reason: "header" });
  });

  it.each([
    ["an empty event header", { "X-PageRush-Event": "" }],
    ["an empty job id header", { "X-PageRush-Job-Id": "" }],
    ["a negative timestamp", { "X-PageRush-Signature": `t=-${T},v2=00` }],
    ["a non-numeric timestamp", { "X-PageRush-Signature": "t=abc,v2=00" }],
    ["only garbage", { "X-PageRush-Signature": "garbage" }],
  ])("rejects %s with reason header", async (_name, override) => {
    const headers = { ...signed(PDF), ...override };
    const err = await verify(PDF, headers, SECRET, { now: T }).catch((e: unknown) => e);
    expect(err).toBeInstanceOf(WebhookVerificationError);
    expect(err).toMatchObject({ name: "WebhookVerificationError", reason: "header" });
  });

  it("accepts a 15-digit timestamp", async () => {
    const t = 999_999_999_999_999;
    await expect(verify(PDF, signed(PDF, { t }), SECRET, { now: t })).resolves.toBeUndefined();
  });

  it("ignores pairs without a key", async () => {
    const headers = signed(PDF);
    headers["X-PageRush-Signature"] = `=x,junk,${headers["X-PageRush-Signature"]}`;
    await expect(verify(PDF, headers, SECRET, { now: T })).resolves.toBeUndefined();
  });

  it("signs an absent metadata header as empty, not as a missing line", async () => {
    const headers = signed(PDF);
    headers["X-PageRush-Metadata"] = "";
    await expect(verify(PDF, headers, SECRET, { now: T })).resolves.toBeUndefined();
  });

  it("rejects a body re-serialised from parsed JSON", async () => {
    const original = '{ "event": "pdf.failed" }';
    const reserialised = JSON.stringify(JSON.parse(original));
    await expect(
      verify(reserialised, signed(original, { event: "pdf.failed" }), SECRET, { now: T }),
    ).rejects.toMatchObject({ reason: "signature" });
  });

  it("rejects a repeated job id header", async () => {
    const headers: Record<string, string | string[]> = lowerCased(signed(PDF));
    headers["x-pagerush-job-id"] = ["job_123", "job_123"];
    await expect(verify(PDF, headers, SECRET, { now: T })).rejects.toMatchObject({ reason: "signature" });
  });

  it("is exposed as webhooks.verify", () => {
    expect(webhooks.verify).toBe(verify);
    expect(webhooks.constructEvent).toBe(constructEvent);
  });
});

describe("webhook secrets", () => {
  it.each([
    ["an empty string", ""],
    ["an empty list", []],
    ["a list with an empty string", [SECRET, ""]],
    ["undefined", undefined as unknown as string],
  ])("throws a TypeError for %s", async (_name, secrets) => {
    const err = await verify(PDF, signed(PDF), secrets, { now: T }).catch((e: unknown) => e);
    expect(err).toBeInstanceOf(TypeError);
    expect(err).not.toBeInstanceOf(WebhookVerificationError);
    expect((err as Error).message).toMatch(/secret is missing or empty/);
  });

  it("checks the secret before the headers", async () => {
    await expect(verify(PDF, {}, "", { now: T })).rejects.toBeInstanceOf(TypeError);
  });

  it("accepts any of several secrets", async () => {
    await expect(
      verify(PDF, signed(PDF, { secret: "whsec_old" }), ["whsec_new", "whsec_old"], { now: T }),
    ).resolves.toBeUndefined();
  });
});

describe("webhooks.constructEvent", () => {
  it("parses a pdf.succeeded delivery", async () => {
    const event = await constructEvent(PDF, lowerCased(signed(PDF, { metadata: '{"order":"42"}' })), SECRET, {
      now: T,
    });
    expect(event).toEqual({
      event: "pdf.succeeded",
      job_id: "job_123",
      timestamp: T,
      pdf: PDF,
      failure: null,
      metadata: { order: "42" },
    });
  });

  it("parses a pdf.failed delivery", async () => {
    const failure = {
      event: "pdf.failed",
      job_id: "job_123",
      template_id: "tpl_1",
      error: { code: "TEMPLATE_COMPILE_FAILED", message: "unknown variable: foo" },
      metadata: { order: "42" },
      created_at: "2026-01-01T00:00:00.000Z",
    };
    const body = JSON.stringify(failure);
    const event = await constructEvent(body, signed(body, { event: "pdf.failed" }), [SECRET], { now: T });
    expect(event).toEqual({
      event: "pdf.failed",
      job_id: "job_123",
      timestamp: T,
      pdf: null,
      failure,
      metadata: { order: "42" },
    });
  });

  it("verifies before parsing", async () => {
    await expect(constructEvent(PDF, signed(PDF), "whsec_wrong", { now: T })).rejects.toMatchObject({
      reason: "signature",
    });
  });

  it("passes unknown events through with neither pdf nor failure", async () => {
    const event = await constructEvent(PDF, signed(PDF, { event: "pdf.future" }), SECRET, { now: T });
    expect(event).toMatchObject({
      event: "pdf.future",
      job_id: "job_123",
      pdf: null,
      failure: null,
      metadata: null,
    });
  });

  it.each([
    ["not JSON", "{oops"],
    ["a JSON array", '["a"]'],
    ["JSON null", "null"],
    ["a non-string value", '{"order":42}'],
    ["a nested object", '{"order":{"id":"42"}}'],
  ])("sets metadata to null when the header is %s", async (_name, metadata) => {
    await expect(constructEvent(PDF, signed(PDF, { metadata }), SECRET, { now: T })).resolves.toMatchObject({
      metadata: null,
      job_id: "job_123",
    });
  });

  it.each([
    ["null", "null"],
    ["an array", "[1,2]"],
    ["a string", '"pdf.failed"'],
    ["not JSON", "<html>"],
    ["invalid UTF-8", new Uint8Array([0x7b, 0xff, 0x7d])],
  ])("rejects a pdf.failed body that is %s with reason header", async (_name, body) => {
    const err = await constructEvent(body, signed(body, { event: "pdf.failed" }), SECRET, { now: T }).catch(
      (e: unknown) => e,
    );
    expect(err).toBeInstanceOf(WebhookVerificationError);
    expect(err).toMatchObject({ reason: "header" });
  });

  it("sets metadata to null when the pdf.failed body's metadata is not a string map", async () => {
    const body = JSON.stringify({ event: "pdf.failed", job_id: "job_123", error: {}, metadata: { n: 1 } });
    const event = await constructEvent(body, signed(body, { event: "pdf.failed" }), SECRET, { now: T });
    expect(event.metadata).toBeNull();
  });
});
