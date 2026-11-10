<?php

declare(strict_types=1);

namespace PageRush\Exception;

/**
 * Raised when no HTTP response arrived: DNS failure, refused connection, TLS error, or timeout.
 * {@see $status} is 0. {@see $errorCode} is `TIMEOUT` for a timeout and `CONNECTION_ERROR` otherwise.
 */
final class ConnectionException extends PageRushException
{
    /**
     * @param string $errorCode `CONNECTION_ERROR` or `TIMEOUT`.
     * @param string $detail    What went wrong.
     */
    public function __construct(string $errorCode, string $detail, ?\Throwable $previous = null)
    {
        parent::__construct(0, $errorCode, null, $detail, [], null, null, null, $previous);
    }
}
