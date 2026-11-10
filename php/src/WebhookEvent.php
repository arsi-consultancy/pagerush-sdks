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

    /**
     * @param string                     $event     `pdf.succeeded` or `pdf.failed` (`X-PageRush-Event`). Other values may
     *                                              appear in future; they have `pdf` and `failure` set to null.
     * @param string                     $job_id    The job ID (`X-PageRush-Job-Id`). Retries reuse it: dedupe on it. Check
     *                                              it against `^[A-Za-z0-9_-]{1,64}$` before using it in a file name.
     * @param int                        $timestamp The signed Unix timestamp.
     * @param string|null                $pdf       The PDF bytes on `pdf.succeeded`, else null.
     * @param array<string, mixed>|null  $failure   The parsed JSON body on `pdf.failed`
     *                                              (`event, job_id, template_id, error{code,message}, metadata, created_at`), else null.
     * @param array<string, string>|null $metadata  The job's metadata (from X-PageRush-Metadata on `pdf.succeeded`, from
     *                                              the body on `pdf.failed`), or null when absent or not an object of strings.
     */
    public function __construct(
        public readonly string $event,
        public readonly string $job_id,
        public readonly int $timestamp,
        public readonly ?string $pdf,
        public readonly ?array $failure,
        public readonly ?array $metadata,
    ) {
    }
}
