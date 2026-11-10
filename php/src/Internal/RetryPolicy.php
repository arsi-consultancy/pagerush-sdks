<?php

declare(strict_types=1);

namespace PageRush\Internal;

use PageRush\ErrorCode;

/**
 * Which failures a request may be retried after.
 *
 * @internal
 */
enum RetryPolicy
{
    /** GET and DELETE: connection errors, 429, 502, 503, 504. */
    case Idempotent;

    /**
     * Render, which always carries an idempotency key: as {@see self::Idempotent}, plus 409 `CONFLICT`
     * (the API is still rendering an earlier attempt with the same key). Never `IDEMPOTENCY_CONFLICT`.
     */
    case Render;

    /** Other POST and PATCH requests: only 429 and 503, which the API returns before doing any work. */
    case NonIdempotent;

    public function allowsConnectionRetry(): bool
    {
        return $this !== self::NonIdempotent;
    }

    public function allowsStatusRetry(int $status, string $errorCode): bool
    {
        return match ($this) {
            self::Idempotent => in_array($status, [429, 502, 503, 504], true),
            self::Render => in_array($status, [429, 502, 503, 504], true)
                || ($status === 409 && $errorCode === ErrorCode::CONFLICT),
            self::NonIdempotent => $status === 429 || $status === 503,
        };
    }
}
