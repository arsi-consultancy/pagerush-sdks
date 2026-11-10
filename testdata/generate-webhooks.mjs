// Generates webhooks.json: callback signature test vectors shared by every SDK.
// Run: node testdata/generate-webhooks.mjs > testdata/webhooks.json && make sync-testdata
import { createHmac } from "node:crypto";

const T = 1767225600; // 2026-01-01T00:00:00Z
const CURRENT = "whsec_current_secret";
const PREVIOUS = "whsec_previous_secret";

const pdf = Buffer.from("%PDF-1.7\n% test body\n%%EOF\n");
const failure = Buffer.from(
  JSON.stringify({
    event: "pdf.failed",
    job_id: "job_123",
    template_id: "tpl_1",
    error: { code: "TEMPLATE_COMPILE_FAILED", message: "unknown variable: foo" },
    metadata: { order: "42" },
    created_at: "2026-01-01T00:00:00.000Z",
  }),
);
// Metadata header as the server sends it: JSON with non-ASCII characters escaped.
const metadata = '{"order":"42","amount":"\\u20b91,250"}';

function sign(secret, { t = T, event, job_id, metadata_header }, body) {
  const prefix = `${t}\n${event}\n${job_id}\n${metadata_header ?? ""}\n`;
  return createHmac("sha256", secret).update(Buffer.concat([Buffer.from(prefix), body])).digest("hex");
}

const succeeded = { event: "pdf.succeeded", job_id: "job_123", metadata_header: metadata };
const failed = { event: "pdf.failed", job_id: "job_123", metadata_header: null };

const cases = [];
function add(name, { fields = succeeded, body = pdf, header, secrets = [CURRENT], now = T, valid, reason }) {
  cases.push({
    name,
    body_base64: body.toString("base64"),
    headers: {
      "X-PageRush-Signature": header,
      "X-PageRush-Event": fields.event,
      "X-PageRush-Job-Id": fields.job_id,
      ...(fields.metadata_header === null ? {} : { "X-PageRush-Metadata": fields.metadata_header }),
    },
    secrets,
    now,
    tolerance_seconds: 300,
    valid,
    ...(reason ? { reason } : {}),
  });
}

const good = sign(CURRENT, succeeded, pdf);
add("pdf body, current secret", { header: `t=${T},v2=${good}`, now: T + 10, valid: true });
add("failure body, no metadata header", {
  fields: failed,
  body: failure,
  header: `t=${T},v2=${sign(CURRENT, failed, failure)}`,
  valid: true,
});
add("succeeded without metadata header", {
  fields: { ...succeeded, metadata_header: null },
  header: `t=${T},v2=${sign(CURRENT, { ...succeeded, metadata_header: null }, pdf)}`,
  valid: true,
});
const both = `t=${T},v2=${good},v2=${sign(PREVIOUS, succeeded, pdf)}`;
add("rotation: two v2, receiver has old secret", { header: both, secrets: [PREVIOUS], valid: true });
add("rotation: two v2, receiver has new secret", { header: both, valid: true });
add("receiver passes several secrets", { header: `t=${T},v2=${good}`, secrets: ["whsec_wrong", CURRENT], valid: true });
add("wrong secret", { header: `t=${T},v2=${good}`, secrets: ["whsec_wrong"], valid: false, reason: "signature" });
add("body tampered", {
  body: Buffer.from("%PDF-1.7 tampered"),
  header: `t=${T},v2=${good}`,
  valid: false,
  reason: "signature",
});
add("job id header changed", {
  fields: { ...succeeded, job_id: "job_999" },
  header: `t=${T},v2=${good}`,
  valid: false,
  reason: "signature",
});
add("event header changed", {
  fields: { ...succeeded, event: "pdf.failed" },
  header: `t=${T},v2=${good}`,
  valid: false,
  reason: "signature",
});
add("metadata header changed", {
  fields: { ...succeeded, metadata_header: '{"order":"43","amount":"\\u20b91,250"}' },
  header: `t=${T},v2=${good}`,
  valid: false,
  reason: "signature",
});
add("metadata header removed", {
  fields: { ...succeeded, metadata_header: null },
  header: `t=${T},v2=${good}`,
  valid: false,
  reason: "signature",
});
add("too old", { header: `t=${T},v2=${good}`, now: T + 301, valid: false, reason: "timestamp" });
add("too far in future", { header: `t=${T},v2=${good}`, now: T - 301, valid: false, reason: "timestamp" });
add("exactly at tolerance", { header: `t=${T},v2=${good}`, now: T + 300, valid: true });
add("missing t", { header: `v2=${good}`, valid: false, reason: "header" });
add("t too long", { header: `t=0${T}00000000000,v2=${good}`, valid: false, reason: "header" });
add("duplicate t", { header: `t=${T},t=${T},v2=${good}`, valid: false, reason: "header" });
add("missing v2", { header: `t=${T}`, valid: false, reason: "header" });
add("old v1 scheme only", {
  header: `t=${T},v1=${createHmac("sha256", CURRENT).update(Buffer.concat([Buffer.from(`${T}.`), pdf])).digest("hex")}`,
  valid: false,
  reason: "header",
});
add("empty signature header", { header: "", valid: false, reason: "header" });
add("spaces after commas", { header: `t=${T}, v2=${good}`, valid: true });
add("unknown scheme ignored", { header: `t=${T},v0=deadbeef,v2=${good}`, valid: true });
add("uppercase hex signature", { header: `t=${T},v2=${good.toUpperCase()}`, valid: false, reason: "signature" });

process.stdout.write(
  `${JSON.stringify(
    {
      description:
        "X-PageRush-Signature (v2) verification vectors. Every SDK must pass all of them. " +
        "'reason' is the failure category: header (malformed), timestamp (outside tolerance), " +
        "signature (no v2 matches). A missing X-PageRush-Metadata header is signed as an empty string.",
      cases,
    },
    null,
    2,
  )}\n`,
);
