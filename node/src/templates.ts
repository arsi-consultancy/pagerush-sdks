/** Template endpoints. @module */

import {
  type HttpClient,
  invalidResponse,
  isObject,
  parseJsonBody,
  type RequestOptions,
  validationError,
} from "./http";
import { uuidv4, writeLocalFile } from "./internal";
import type {
  CreateTemplateParams,
  CreateVersionParams,
  Job,
  ListTemplatesParams,
  RenderOptions,
  RenderResult,
  Template,
  TemplatePage,
  TemplateVersion,
  TemplateWithVersion,
} from "./types";

const enc = encodeURIComponent;

/** The server's rule for Idempotency-Key values. */
const IDEMPOTENCY_KEY = /^[A-Za-z0-9_\-:.]{1,255}$/;

const intOrNull = (v: string | null): number | null => {
  if (v === null || v.trim() === "") return null;
  const n = Number(v);
  return Number.isInteger(n) ? n : null;
};

/** `filename="..."` from a Content-Disposition header, or null. */
export function filenameFromDisposition(value: string | null): string | null {
  if (!value) return null;
  const match = /filename="([^"]*)"/i.exec(value) ?? /filename=([^;]+)/i.exec(value);
  return match?.[1]?.trim() || null;
}

function makeResult(fields: Omit<RenderResult, "save">): RenderResult {
  return {
    ...fields,
    async save(path: string) {
      if (!fields.pdf) {
        throw new Error(
          `Render was queued as job ${fields.job_id}; there is no PDF to save yet. Use jobs.wait() and jobs.download().`,
        );
      }
      await writeLocalFile(path, fields.pdf);
    },
  };
}

/** `client.templates`: create, version, publish and render Typst templates. */
export class Templates {
  /** @internal */
  constructor(private readonly http: HttpClient) {}

  /**
   * Lists one page of templates.
   *
   * @returns `{ items, next_cursor }`. Pass `next_cursor` as `cursor` for the next page; it is null on the last page.
   */
  list(params: ListTemplatesParams = {}, options?: RequestOptions): Promise<TemplatePage> {
    return this.http.object<TemplatePage>(
      {
        method: "GET",
        path: "/v1/templates",
        query: { limit: params.limit, cursor: params.cursor },
        retry: "safe",
        options,
      },
      ["items", "next_cursor"],
    );
  }

  /**
   * Iterates over every template, fetching pages as needed.
   *
   * @example
   * ```ts
   * for await (const template of client.templates.listAll()) console.log(template.id, template.name);
   * ```
   */
  async *listAll(
    params: Omit<ListTemplatesParams, "cursor"> = {},
    options?: RequestOptions,
  ): AsyncGenerator<Template, void, undefined> {
    let cursor: string | undefined;
    do {
      const page = await this.list({ limit: params.limit, cursor }, options);
      yield* page.items;
      cursor = page.next_cursor ?? undefined;
    } while (cursor);
  }

  /**
   * Creates a template with its first version. Send `source` (the `main.typ`) or `document`.
   *
   * @returns The template and its version 1.
   */
  create(params: CreateTemplateParams, options?: RequestOptions): Promise<TemplateWithVersion> {
    return this.http.object<TemplateWithVersion>(
      { method: "POST", path: "/v1/templates", body: params, retry: "unsafe", options },
      ["template", "version"],
    );
  }

  /** Gets a template, including its version history in `versions`. Throws TEMPLATE_NOT_FOUND if missing. */
  async get(id: string, options?: RequestOptions): Promise<Template> {
    return this.http.unwrap<Template>(
      {
        method: "GET",
        path: `/v1/templates/${enc(id)}`,
        retry: "safe",
        options,
      },
      "template",
    );
  }

  /** Renames a template. */
  async rename(id: string, name: string, options?: RequestOptions): Promise<Template> {
    return this.http.unwrap<Template>(
      {
        method: "PATCH",
        path: `/v1/templates/${enc(id)}`,
        body: { name },
        retry: "unsafe",
        options,
      },
      "template",
    );
  }

  /** Deletes a template and all its versions. */
  async delete(id: string, options?: RequestOptions): Promise<void> {
    await this.http.request({ method: "DELETE", path: `/v1/templates/${enc(id)}`, retry: "safe", options });
  }

  /**
   * Adds a version to a template. It becomes the live version unless `publish: false`.
   *
   * @returns The updated template and the new version.
   */
  createVersion(
    id: string,
    params: CreateVersionParams,
    options?: RequestOptions,
  ): Promise<TemplateWithVersion> {
    return this.http.object<TemplateWithVersion>(
      { method: "POST", path: `/v1/templates/${enc(id)}/versions`, body: params, retry: "unsafe", options },
      ["template", "version"],
    );
  }

  /** Gets one version's source and files. */
  async getVersion(id: string, version: number, options?: RequestOptions): Promise<TemplateVersion> {
    return this.http.unwrap<TemplateVersion>(
      {
        method: "GET",
        path: `/v1/templates/${enc(id)}/versions/${enc(String(version))}`,
        retry: "safe",
        options,
      },
      "version",
    );
  }

  /** Makes `version` the live version. Publish an older version to roll back. */
  async publish(id: string, version: number, options?: RequestOptions): Promise<Template> {
    return this.http.unwrap<Template>(
      {
        method: "POST",
        path: `/v1/templates/${enc(id)}/publish`,
        body: { version },
        retry: "unsafe",
        options,
      },
      "template",
    );
  }

  /**
   * Renders a template to PDF.
   *
   * The server answers with the PDF (`queued: false`), or with a queued job (`queued: true`) for
   * `callback_url` renders and for `preferAsync` renders it could not finish straight away. An
   * `Idempotency-Key` is always sent (a random UUID if you don't pass one) and reused across retries.
   *
   * @example
   * ```ts
   * const result = await client.templates.render("tpl_123", { data: { name: "Ada" } });
   * await result.save("out.pdf");
   * ```
   */
  async render(id: string, params: RenderOptions, options?: RequestOptions): Promise<RenderResult> {
    const { idempotencyKey, preferAsync, ...rest } = params;
    const body: Record<string, unknown> = { data: rest.data };
    if (rest.version !== undefined) body.version = rest.version;
    if (rest.filename !== undefined) body.filename = rest.filename;
    if (rest.metadata !== undefined) body.metadata = rest.metadata;
    if (rest.delete_after_download !== undefined) body.delete_after_download = rest.delete_after_download;
    if (rest.callback_url !== undefined) body.callback_url = rest.callback_url;

    if (idempotencyKey !== undefined && !IDEMPOTENCY_KEY.test(idempotencyKey)) {
      throw validationError(
        "idempotencyKey must be 1-255 characters from A-Z, a-z, 0-9, '_', '-', ':' and '.'",
      );
    }
    const headers: Record<string, string> = { "Idempotency-Key": idempotencyKey ?? (await uuidv4()) };
    if (preferAsync) headers.Prefer = "respond-async";

    const res = await this.http.request({
      method: "POST",
      path: `/v1/templates/${enc(id)}/render`,
      body,
      headers,
      accept: "application/pdf, application/json",
      retry: "render",
      options,
    });

    if (res.status === 202) {
      const body = parseJsonBody<unknown>(res);
      if (!isObject(body) || typeof body.id !== "string") {
        throw invalidResponse(res, "Expected a job object in the 202 response");
      }
      const job = body as unknown as Job;
      return makeResult({
        queued: true,
        pdf: null,
        job,
        job_id: job.id,
        template_version: job.template_version,
        engine: null,
        page_count: null,
        warnings: null,
        request_id: null,
        filename: null,
      });
    }
    const contentType = res.headers.get("Content-Type") ?? "";
    if (!/^application\/pdf\b/i.test(contentType.trim())) {
      throw invalidResponse(res, `Expected application/pdf but got ${contentType || "no content type"}`);
    }
    const jobId = res.headers.get("X-Job-Id");
    if (!jobId) throw invalidResponse(res, "Render response had no X-Job-Id header");
    return makeResult({
      queued: false,
      pdf: res.body,
      job: null,
      job_id: jobId,
      template_version: intOrNull(res.headers.get("X-Template-Version")),
      engine: res.headers.get("X-Render-Engine"),
      page_count: intOrNull(res.headers.get("X-Page-Count")),
      warnings: intOrNull(res.headers.get("X-Template-Warnings")),
      request_id: res.headers.get("X-Request-Id"),
      filename: filenameFromDisposition(res.headers.get("Content-Disposition")),
    });
  }
}
