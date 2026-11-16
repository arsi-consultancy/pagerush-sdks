<?php

declare(strict_types=1);

namespace PageRush;

use PageRush\Exception\InvalidArgumentException;
use PageRush\Exception\PageRushException;
use PageRush\Internal\HttpClient;
use PageRush\Internal\RetryPolicy;

/**
 * Batch render endpoints: `$client->batches`. A batch renders one template with many data records
 * and collects the PDFs in a ZIP file.
 *
 * ```php
 * $batch = $client->batches->create($templateId, [
 *     'items' => [
 *         ['data' => ['customer' => 'Acme'], 'filename' => 'acme.pdf'],
 *         ['data' => ['customer' => 'Globex'], 'filename' => 'globex.pdf'],
 *     ],
 * ]);
 * $batch = $client->batches->wait($batch['id']);
 * file_put_contents('invoices.zip', $client->batches->downloadZip($batch['id']));
 * ```
 *
 * Request and response arrays use the API's snake_case field names.
 *
 * @phpstan-import-type Batch from Types
 * @phpstan-import-type BatchItemInput from Types
 */
final class Batches
{
    /** Batch options sent in the JSON body. */
    private const BODY_FIELDS = ['items', 'version', 'metadata', 'delete_after_download', 'callback_url'];
    /** Batch options handled by the SDK (sent as headers). */
    private const SDK_OPTIONS = ['idempotency_key'];
    /** Statuses after which a batch no longer changes. */
    private const FINISHED = ['succeeded', 'partial', 'failed'];

    /**
     * @internal Use `$client->batches`.
     */
    public function __construct(private readonly HttpClient $http)
    {
    }

    /**
     * Starts a batch render and returns the queued batch (HTTP 202). The PDFs render in the
     * background: poll with {@see wait()}, or pass `callback_url` to get a `batch.completed` webhook.
     *
     * Body options (API names): `items` (required: 1-1,000 entries of `['data' => mixed, 'filename' => ?string]`;
     * an array or any iterable, such as a generator over CSV rows), `version`, `metadata` (string => string),
     * `delete_after_download`, `callback_url`.
     *
     * SDK option: `idempotency_key` (sent as `Idempotency-Key`; when omitted the SDK generates a UUID v4
     * and reuses it across retries, so a retry never starts or bills a batch twice).
     *
     * PHP encodes an empty array as `[]`. Pass `new \stdClass()` for an empty JSON object.
     *
     * @param array<string, mixed> $options
     *
     * @return Batch
     *
     * @throws InvalidArgumentException When `items` is missing or empty, an item has no `data`, or an option is unknown.
     * @throws PageRushException         `TEMPLATE_NOT_FOUND`, `VALIDATION_FAILED`, `QUOTA_EXCEEDED`, `CONCURRENCY_LIMIT`, ...
     */
    public function create(string $templateId, array $options): array
    {
        $unknown = array_diff(array_keys($options), self::BODY_FIELDS, self::SDK_OPTIONS);
        if ($unknown !== []) {
            throw new InvalidArgumentException('Unknown batch option(s): ' . implode(', ', $unknown));
        }

        $body = [];
        foreach (self::BODY_FIELDS as $field) {
            if (array_key_exists($field, $options) && $options[$field] !== null) {
                $body[$field] = $options[$field];
            }
        }
        $body['items'] = self::items($options['items'] ?? null);
        if (isset($body['metadata']) && $body['metadata'] === []) {
            $body['metadata'] = new \stdClass();
        }

        $key = HttpClient::idempotencyKey($options['idempotency_key'] ?? null);

        /** @var Batch $batch */
        $batch = $this->http->json(
            'POST',
            '/v1/templates/' . HttpClient::segment($templateId) . '/batches',
            $body,
            RetryPolicy::BatchCreate,
            required: ['id' => false, 'status' => false],
            headers: ['Idempotency-Key' => $key],
        );

        return $batch;
    }

    /**
     * Fetches a batch with its `items`: one entry per input record, in order, with the item's `job_id`,
     * `status`, `filename`, `bytes`, `renders` and `error`.
     *
     * @return Batch
     *
     * @throws PageRushException `NOT_FOUND`, ...
     */
    public function get(string $id): array
    {
        /** @var Batch $batch */
        $batch = $this->http->json('GET', '/v1/batches/' . HttpClient::segment($id), required: ['id' => false, 'status' => false]);

        return $batch;
    }

    /**
     * Polls a batch until its status is `succeeded`, `partial` or `failed`, and returns it. A partial or
     * failed batch is returned, not thrown: check `$batch['status']`, `$batch['failed']` and each item's `error`.
     *
     * @param array{timeout?: int|float, interval?: int|float} $options Seconds; defaults 600 and 2.
     *
     * @return Batch
     *
     * @throws PageRushException Code `WAIT_TIMEOUT` (status 0) when the batch is still running after `timeout`.
     */
    public function wait(string $id, array $options = []): array
    {
        $timeout = (float) ($options['timeout'] ?? 600);
        $interval = (float) ($options['interval'] ?? 2);
        $deadline = $this->http->now() + $timeout;

        while (true) {
            $batch = $this->get($id);
            if (in_array($batch['status'], self::FINISHED, true)) {
                return $batch;
            }
            $remaining = $deadline - $this->http->now();
            if ($remaining <= 0) {
                throw new PageRushException(0, ErrorCode::WAIT_TIMEOUT, null, sprintf('Batch %s is still %s after %s seconds', $id, $batch['status'], $timeout));
            }
            $this->http->sleep(min($interval, $remaining));
        }
    }

    /**
     * Downloads a finished batch's ZIP file (one PDF per succeeded item) and returns its bytes. Unlike a
     * job's signed `download_url`, the ZIP endpoint needs the API key, so pass the batch ID rather than
     * `zip_url`.
     *
     * @throws PageRushException `CONFLICT` (409, with `retryAfter`) while the batch is still rendering,
     *                           `FILE_EXPIRED` (410) when no PDFs are left, and `INVALID_RESPONSE` when a
     *                           200 response is not `application/zip`.
     */
    public function downloadZip(string $id): string
    {
        $response = $this->http->send(
            'GET',
            '/v1/batches/' . HttpClient::segment($id) . '/zip',
            null,
            RetryPolicy::Idempotent,
            ['Accept' => 'application/zip'],
        );

        $contentType = strtolower(trim($response->header('content-type') ?? ''));
        if (!str_starts_with($contentType, 'application/zip')) {
            throw HttpClient::invalidResponse(
                $response,
                $contentType === '' ? 'has no Content-Type instead of a ZIP file' : 'is ' . $contentType . ' instead of a ZIP file',
            );
        }

        return $response->body;
    }

    /**
     * Checks the `items` option and returns it as a list.
     *
     * @return list<BatchItemInput>
     *
     * @throws InvalidArgumentException
     */
    private static function items(mixed $items): array
    {
        if (!is_iterable($items)) {
            throw new InvalidArgumentException('create() needs an "items" option: a list of [\'data\' => ..., \'filename\' => ...] entries');
        }
        $list = [];
        foreach ($items as $item) {
            if (!is_array($item) || !array_key_exists('data', $item)) {
                throw new InvalidArgumentException(sprintf('Batch item %d must be an array with a "data" key', count($list)));
            }
            if (array_key_exists('filename', $item) && $item['filename'] === null) {
                unset($item['filename']);
            }
            /** @var BatchItemInput $item */
            $list[] = $item;
        }
        if ($list === []) {
            throw new InvalidArgumentException('create() needs at least one item');
        }

        return $list;
    }
}
