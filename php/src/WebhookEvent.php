<?php

declare(strict_types=1);

namespace PageRush;

/**
 * A verified webhook delivery, from {@see Webhook::constructEvent()}. Every field comes from signed
 * data. Property names follow the API's snake_case fields.
 */
final class WebhookEvent
{
    public const SUCCEEDED = 'pdf.succeeded';
    public const FAILED = 'pdf.failed';
    public const BATCH_COMPLETED = 'batch.completed';

    /**
     * @param string                     $event     `pdf.succeeded`, `pdf.failed` or `batch.completed` (`X-PageRush-Event`).
     *                                              Other values may appear in future; they have `pdf`, `failure` and
     *                                              `batch` set to null.
     * @param string|null                $job_id    The job ID (`X-PageRush-Job-Id`); null on `batch.completed`. Retries
     *                                              reuse it: dedupe on it. Check it against `^[A-Za-z0-9_-]{1,64}$`
     *                                              before using it in a file name.
     * @param int                        $timestamp The signed Unix timestamp.
     * @param string|null                $pdf       The PDF bytes on `pdf.succeeded`, else null.
     * @param array<string, mixed>|null  $failure   The parsed JSON body on `pdf.failed`
     *                                              (`event, job_id, template_id, error{code,message}, metadata, created_at`), else null.
     * @param array<string, string>|null $metadata  The job's or batch's metadata (from X-PageRush-Metadata on
     *                                              `pdf.succeeded` and `batch.completed`, from the body on `pdf.failed`),
     *                                              or null when absent or not an object of strings.
     * @param string|null                $batch_id  The batch ID (`X-PageRush-Batch-Id`) on `batch.completed`, else null.
     *                                              Retries reuse it: dedupe on it.
     * @param array<string, mixed>|null  $batch     The body's `batch` object on `batch.completed` (the batch without
     *                                              `items`: `id, status, item_count, succeeded, failed, zip_url, ...`), else null.
     */
    public function __construct(
        public readonly string $event,
        public readonly ?string $job_id,
        public readonly int $timestamp,
        public readonly ?string $pdf,
        public readonly ?array $failure,
        public readonly ?array $metadata,
        public readonly ?string $batch_id = null,
        public readonly ?array $batch = null,
    ) {
    }
}
