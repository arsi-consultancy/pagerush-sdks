<?php

declare(strict_types=1);

namespace PageRush;

use PageRush\Exception\InvalidArgumentException;
use PageRush\Exception\PageRushException;
use PageRush\Internal\HttpClient;
use PageRush\Internal\RetryPolicy;

/**
 * Template endpoints: `$client->templates`.
 *
 * Request and response arrays use the API's snake_case field names.
 *
 * @phpstan-import-type Template from Types
 * @phpstan-import-type TemplateVersion from Types
 * @phpstan-import-type TemplateWithVersion from Types
 * @phpstan-import-type TemplatePage from Types
 * @phpstan-import-type Job from Types
 */
final class Templates
{
    /** Render options sent in the JSON body. */
    private const RENDER_BODY_FIELDS = ['data', 'version', 'filename', 'metadata', 'delete_after_download', 'callback_url'];
    /** Render options handled by the SDK (sent as headers). */
    private const RENDER_SDK_OPTIONS = ['idempotency_key', 'prefer_async'];
    /** The API's rule for Idempotency-Key values. */
    private const IDEMPOTENCY_KEY = '/^[A-Za-z0-9_\-:.]{1,255}$/D';

    /**
     * @internal Use `$client->templates`.
     */
    public function __construct(private readonly HttpClient $http)
    {
    }

    /**
     * Lists one page of templates, newest first.
     *
     * @param array{limit?: int|null, cursor?: string|null} $params `limit` 1-100 (server default 50);
     *                                                         `cursor` is a previous page's `next_cursor`.
     *
     * @return TemplatePage `['items' => Template[], 'next_cursor' => string|null]`.
     *
     * @throws PageRushException
     */
    public function list(array $params = []): array
    {
        $query = [];
        if (isset($params['limit'])) {
            $query['limit'] = (string) $params['limit'];
        }
        if (isset($params['cursor'])) {
            $query['cursor'] = $params['cursor'];
        }
        $path = '/v1/templates' . ($query === [] ? '' : '?' . http_build_query($query, '', '&', PHP_QUERY_RFC3986));

        $page = $this->http->json('GET', $path, required: ['items' => true, 'next_cursor' => false]);

        /** @var TemplatePage $page */
        return $page;
    }

    /**
     * Iterates over every template, fetching further pages as needed.
     *
     * ```php
     * foreach ($client->templates->listAll() as $template) {
     *     echo $template['id'], ' ', $template['name'], PHP_EOL;
     * }
     * ```
     *
     * @param array{limit?: int|null} $params `limit` is the page size (1-100).
     *
     * @return \Generator<int, Template>
     *
     * @throws PageRushException
     */
    public function listAll(array $params = []): \Generator
    {
        $cursor = null;
        do {
            $page = $this->list(['limit' => $params['limit'] ?? null, 'cursor' => $cursor]);
            foreach ($page['items'] as $template) {
                yield $template;
            }
            $cursor = $page['next_cursor'];
        } while ($cursor !== null);
    }

    /**
     * Creates a template and its first version (which is live).
     *
     * ```php
     * $created = $client->templates->create([
     *     'name' => 'Invoice',
     *     'source' => "#let data = json(\"data.json\")\nInvoice for #data.customer",
     *     'files' => [PageRush\TemplateFile::fromPath('logo.png')],
     * ]);
     * $templateId = $created['template']['id'];
     * ```
     *
     * @param array<string, mixed> $params `name` (required), and either `source` (Typst) or `document`
     *                                     (visual editor JSON); optional `files`, `sample_data`, `typst_version`.
     *
     * @return TemplateWithVersion `['template' => Template, 'version' => TemplateVersion]`.
     *
     * @throws PageRushException `VALIDATION_FAILED`, `TEMPLATE_COMPILE_FAILED`, ...
     */
    public function create(array $params): array
    {
        /** @var TemplateWithVersion $result */
        $result = $this->http->json('POST', '/v1/templates', $params, RetryPolicy::NonIdempotent, required: ['template' => true, 'version' => true]);

        return $result;
    }

    /**
     * Fetches a template, including its version history in `versions`.
     *
     * @return Template
     *
     * @throws PageRushException `TEMPLATE_NOT_FOUND`, ...
     */
    public function get(string $id): array
    {
        /** @var Template $template */
        $template = $this->http->json('GET', '/v1/templates/' . HttpClient::segment($id), unwrap: 'template');

        return $template;
    }

    /**
     * Renames a template.
     *
     * @return Template
     *
     * @throws PageRushException
     */
    public function rename(string $id, string $name): array
    {
        /** @var Template $template */
        $template = $this->http->json('PATCH', '/v1/templates/' . HttpClient::segment($id), ['name' => $name], RetryPolicy::NonIdempotent, unwrap: 'template');

        return $template;
    }

    /**
     * Deletes a template and all its versions.
     *
     * @throws PageRushException
     */
    public function delete(string $id): void
    {
        $this->http->send('DELETE', '/v1/templates/' . HttpClient::segment($id), null, RetryPolicy::Idempotent);
    }

    /**
     * Adds a version to a template. It goes live straight away unless you pass `'publish' => false`.
     *
     * @param array<string, mixed> $params Either `source` or `document`; optional `files`, `sample_data`,
     *                                     `typst_version`, `publish` (default true).
     *
     * @return TemplateWithVersion `['template' => Template, 'version' => TemplateVersion]`.
     *
     * @throws PageRushException
     */
    public function createVersion(string $id, array $params): array
    {
        /** @var TemplateWithVersion $result */
        $result = $this->http->json(
            'POST',
            '/v1/templates/' . HttpClient::segment($id) . '/versions',
            $params,
            RetryPolicy::NonIdempotent,
            required: ['template' => true, 'version' => true],
        );

        return $result;
    }

    /**
     * Fetches one version of a template, with its source and files.
     *
     * @return TemplateVersion
     *
     * @throws PageRushException
     */
    public function getVersion(string $id, int $version): array
    {
        /** @var TemplateVersion $templateVersion */
        $templateVersion = $this->http->json('GET', '/v1/templates/' . HttpClient::segment($id) . '/versions/' . HttpClient::segment($version), unwrap: 'version');

        return $templateVersion;
    }

    /**
     * Makes a version the live one that renders use by default. Publish an older version to roll back.
     *
     * @return Template
     *
     * @throws PageRushException
     */
    public function publish(string $id, int $version): array
    {
        /** @var Template $template */
        $template = $this->http->json('POST', '/v1/templates/' . HttpClient::segment($id) . '/publish', ['version' => $version], RetryPolicy::NonIdempotent, unwrap: 'template');

        return $template;
    }

    /**
     * Renders a template to PDF.
     *
     * ```php
     * $result = $client->templates->render($templateId, [
     *     'data' => ['customer' => 'Acme'],
     *     'filename' => 'invoice-42.pdf',
     * ]);
     * $result->save('invoice.pdf');
     * ```
     *
     * Body options (API names): `data` (required, any JSON value), `version`, `filename`, `metadata`
     * (string => string), `delete_after_download`, `callback_url`.
     *
     * SDK options: `idempotency_key` (sent as `Idempotency-Key`; when omitted the SDK generates a
     * UUID v4 and reuses it across retries, so a retry never renders or bills twice) and
     * `prefer_async` (sends `Prefer: respond-async`). The API still renders straight away; only when that
     * attempt hits a temporary capacity problem does it queue the job and return 202 instead of a 503.
     * Code that sets it must handle both a PDF and a queued result. Only `callback_url` always queues.
     *
     * PHP encodes an empty array as `[]`. Pass `new \stdClass()` for an empty JSON object.
     *
     * @param array<string, mixed> $options
     *
     * @throws InvalidArgumentException When `data` is missing or an option is unknown.
     * @throws PageRushException         `TEMPLATE_NOT_FOUND`, `INVALID_DATA`, `TEMPLATE_COMPILE_FAILED`, `QUOTA_EXCEEDED`, ...
     */
    public function render(string $id, array $options): RenderResult
    {
        if (!array_key_exists('data', $options)) {
            throw new InvalidArgumentException('render() needs a "data" option (any JSON value)');
        }
        $unknown = array_diff(array_keys($options), self::RENDER_BODY_FIELDS, self::RENDER_SDK_OPTIONS);
        if ($unknown !== []) {
            throw new InvalidArgumentException('Unknown render option(s): ' . implode(', ', $unknown));
        }

        $body = [];
        foreach (self::RENDER_BODY_FIELDS as $field) {
            if (array_key_exists($field, $options) && ($field === 'data' || $options[$field] !== null)) {
                $body[$field] = $options[$field];
            }
        }
        if (isset($body['metadata']) && $body['metadata'] === []) {
            $body['metadata'] = new \stdClass();
        }

        $key = $options['idempotency_key'] ?? HttpClient::uuid4();
        if (!is_string($key) || preg_match(self::IDEMPOTENCY_KEY, $key) !== 1) {
            throw new InvalidArgumentException('idempotency_key must be 1-255 characters from A-Z, a-z, 0-9, _ - : .');
        }
        $preferAsync = $options['prefer_async'] ?? false;
        if (!is_bool($preferAsync)) {
            throw new InvalidArgumentException('prefer_async must be a boolean');
        }
        $headers = ['Accept' => 'application/pdf, application/json', 'Idempotency-Key' => $key];
        if ($preferAsync) {
            $headers['Prefer'] = 'respond-async';
        }

        $response = $this->http->send(
            'POST',
            '/v1/templates/' . HttpClient::segment($id) . '/render',
            HttpClient::encode($body),
            RetryPolicy::Render,
            $headers,
        );

        if ($response->status === 202) {
            $job = HttpClient::decodeObject($response);
            if (!is_string($job['id'] ?? null)) {
                throw HttpClient::invalidResponse($response, 'has no job ID');
            }

            /** @var Job $job */
            return RenderResult::fromJob($job);
        }

        $contentType = strtolower(trim($response->header('content-type') ?? ''));
        if (!str_starts_with($contentType, 'application/pdf')) {
            throw HttpClient::invalidResponse(
                $response,
                $contentType === '' ? 'has no Content-Type instead of a PDF' : 'is ' . $contentType . ' instead of a PDF',
            );
        }

        return RenderResult::fromPdfResponse($response);
    }
}
