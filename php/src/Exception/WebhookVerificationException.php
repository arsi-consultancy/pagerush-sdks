<?php

declare(strict_types=1);

namespace PageRush\Exception;

/**
 * Raised when a webhook delivery fails signature verification. Respond with 400 and do not trust
 * the body.
 *
 * {@see $reason} says why:
 * - `header`: the X-PageRush-Signature, X-PageRush-Event or X-PageRush-Job-Id header is missing or
 *   malformed, or a `pdf.failed` body is not a JSON object.
 * - `timestamp`: the signed timestamp is outside the tolerance (a replay, or a wrong clock).
 * - `signature`: no v2 signature matches any of your secrets (wrong secret, or the delivery changed).
 */
final class WebhookVerificationException extends \RuntimeException implements PageRushExceptionInterface
{
    public const REASON_HEADER = 'header';
    public const REASON_TIMESTAMP = 'timestamp';
    public const REASON_SIGNATURE = 'signature';

    /**
     * @param string $reason One of `header`, `timestamp`, `signature`.
     */
    public function __construct(public readonly string $reason, string $message)
    {
        parent::__construct($message);
    }
}
