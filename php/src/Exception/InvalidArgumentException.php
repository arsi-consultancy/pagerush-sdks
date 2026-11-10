<?php

declare(strict_types=1);

namespace PageRush\Exception;

use PageRush\ErrorCode;

/**
 * Thrown when an argument or option is invalid, before any request is sent: a missing or malformed
 * API key, an unknown option, a bad idempotency key, or a body that cannot be encoded as JSON.
 * {@see $errorCode} is always `VALIDATION_FAILED` and {@see $status} is 0. It is never retried.
 */
final class InvalidArgumentException extends \InvalidArgumentException implements PageRushExceptionInterface
{
    /** Always 0: no request was sent. */
    public readonly int $status;

    /** Always `VALIDATION_FAILED`. */
    public readonly string $errorCode;

    public function __construct(string $message, ?\Throwable $previous = null)
    {
        parent::__construct($message, 0, $previous);
        $this->status = 0;
        $this->errorCode = ErrorCode::VALIDATION_FAILED;
    }
}
