import { describe, expect, it } from "vitest";
import { PageRush } from "../src";

/** Runs against the real API only when PAGERUSH_API_KEY is set. Creates a template and deletes it. */
describe.skipIf(!process.env.PAGERUSH_API_KEY)("live API smoke test", () => {
  it("creates, renders and deletes a template", { timeout: 120_000 }, async () => {
    const client = new PageRush();
    const { template } = await client.templates.create({
      name: "SDK smoke test (node)",
      source: '#let data = json("data.json")\nHello #data.name',
    });
    try {
      const result = await client.templates.render(template.id, { data: { name: "Node" } });
      expect(result.queued).toBe(false);
      expect(new TextDecoder().decode(result.pdf!.subarray(0, 4))).toBe("%PDF");
    } finally {
      await client.templates.delete(template.id);
    }
  });
});
