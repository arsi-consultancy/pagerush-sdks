<?php

declare(strict_types=1);

namespace PageRush;

use PageRush\Exception\LogicException;
use PageRush\Exception\RuntimeException;
use PageRush\Transport\Response;

/**
 * The outcome of {@see Templates::render()}.
 *
 * - A synchronous render (HTTP 200) has `queued === false` and the PDF bytes in {@see $pdf}.
 * - A queued render (HTTP 202: always with a `callback_url`, and with `prefer_async` when the API is
 *   briefly at capacity) has `queued === true`,
 *   `pdf === null` and the {@see $job} to poll with {@see Jobs::wait()}.
 *
 * Property names follow the API's snake_case fields.
 *
 * @phpstan-import-type Job from Types
 */
final class RenderResult
{
    /**
     * @param bool        $queued           True when the API queued the render (HTTP 202).
     * @param string|null $pdf              The PDF bytes, or null when queued.
     * @param string|null $job_id           The job ID (`X-Job-Id`, or the queued job's `id`).
     * @param int|null    $template_version The template version rendered (`X-Template-Version`).
     * @param string|null $engine           The render engine (`X-Render-Engine`); null when queued.
     * @param int|null    $page_count       Pages in the PDF (`X-Page-Count`), when the server reports it.
     * @param int|null    $warnings         Typst warnings (`X-Template-Warnings`), when the server reports it.
     * @param string|null $request_id       `X-Request-Id`, for support requests.
     * @param string|null $filename         The file name from `Content-Disposition`.
     * @param Job|null    $job              The queued job; null for a synchronous render.
     */
    public function __construct(
        public readonly bool $queued,
        public readonly ?string $pdf,
        public readonly ?string $job_id,
        public readonly ?int $template_version,
        public readonly ?string $engine = null,
        public readonly ?int $page_count = null,
        public readonly ?int $warnings = null,
        public readonly ?string $request_id = null,
        public readonly ?string $filename = null,
        public readonly ?array $job = null,
    ) {
    }

    /**
     * Writes the PDF to a file and returns the number of bytes written.
     *
     * @throws LogicException   When the render was queued, so there is no PDF yet. Use
     *                           `$client->jobs->wait($result->job_id)` then `$client->jobs->download(...)`.
     * @throws RuntimeException When the file cannot be written.
     */
    public function save(string $path): int
    {
        if ($this->queued || $this->pdf === null) {
            throw new LogicException('This render was queued, so it has no PDF yet. Wait for job ' . ($this->job_id ?? '') . ' and download it instead.');
        }
        $written = @file_put_contents($path, $this->pdf);
        if ($written === false) {
            $error = error_get_last();
            throw new RuntimeException('Could not write ' . $path . ($error !== null ? ': ' . $error['message'] : ''));
        }

        return $written;
    }

    /**
     * @internal Builds a result from a 200 PDF response.
     */
    public static function fromPdfResponse(Response $response): self
    {
        $filename = null;
        $disposition = $response->header('content-disposition');
        if ($disposition !== null) {
            if (preg_match('/filename="([^"]*)"/i', $disposition, $m) === 1) {
                $filename = $m[1];
            } elseif (preg_match('/filename=([^;\s]+)/i', $disposition, $m) === 1) {
                $filename = $m[1];
            }
        }

        return new self(
            queued: false,
            pdf: $response->body,
            job_id: $response->header('x-job-id'),
            template_version: self::intOrNull($response->header('x-template-version')),
            engine: $response->header('x-render-engine'),
            page_count: self::intOrNull($response->header('x-page-count')),
            warnings: self::intOrNull($response->header('x-template-warnings')),
            request_id: $response->header('x-request-id'),
            filename: $filename,
        );
    }

    /**
     * @internal Builds a result from a 202 queued job.
     *
     * @param Job $job
     */
    public static function fromJob(array $job): self
    {
        return new self(
            queued: true,
            pdf: null,
            job_id: $job['id'],
            template_version: $job['template_version'],
            job: $job,
        );
    }

    private static function intOrNull(?string $value): ?int
    {
        if ($value === null || preg_match('/^\s*-?\d+\s*$/', $value) !== 1) {
            return null;
        }

        return (int) $value;
    }
}
