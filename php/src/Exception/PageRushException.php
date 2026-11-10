<?php

declare(strict_types=1);

namespace PageRush\Exception;

/**
 * Raised for every non-2xx API response, and (as {@see ConnectionException}) when no response
 * arrives at all.
 *
 * The message reads `"<code>: <detail or title>"`. Catch it and branch on {@see $errorCode}:
 *
 * ```php
 * try {
 *     $client->templates->render($id, ['data' => $data]);
 * } catch (PageRush\Exception\PageRushException $e) {
 *     if ($e->errorCode === 'TEMPLATE_COMPILE_FAILED') {
 *         foreach ($e->errors as $diagnostic) {
 *             echo $diagnostic['path'], ': ', $diagnostic['message'], PHP_EOL;
 *         }
 *     }
 *     error_log("PageRush request failed ({$e->requestId}): {$e->getMessage()}");
 * }
 * ```
 *
 * `getCode()` returns the HTTP status (0 when there was no response). The API's string error code
 * is in {@see $errorCode}, because PHP reserves `$code` on exceptions for an integer.
 */
class PageRushException extends \RuntimeException implements PageRushExceptionInterface
{
    /**
     * @param int                                      $status     HTTP status, or 0 when no response arrived.
     * @param string                                   $errorCode  The body's `code`, e.g. `TEMPLATE_NOT_FOUND`; `UNKNOWN` when the body was not problem+json.
     * @param string|null                              $title      The body's `title`.
     * @param string|null                              $detail     The body's `detail`.
     * @param list<array{path: string, message: string}> $errors   Field errors, or Typst diagnostics on `TEMPLATE_COMPILE_FAILED`. May be empty.
     * @param string|null                              $requestId  The body's `request_id`, else the `X-Request-Id` header. Quote it to support.
     * @param int|null                                 $retryAfter Seconds from the `Retry-After` header, or null.
     * @param string|null                              $type       The body's `type` URI.
     */
    public function __construct(
        public readonly int $status,
        public readonly string $errorCode,
        public readonly ?string $title = null,
        public readonly ?string $detail = null,
        public readonly array $errors = [],
        public readonly ?string $requestId = null,
        public readonly ?int $retryAfter = null,
        public readonly ?string $type = null,
        ?\Throwable $previous = null,
    ) {
        $text = $detail ?? $title ?? ('HTTP ' . $status);
        parent::__construct($errorCode . ': ' . $text, $status, $previous);
    }
}
