<?php

declare(strict_types=1);

namespace PageRush;

/**
 * Known values of {@see Exception\PageRushException::$errorCode}. Codes are plain strings and the API
 * may add new ones, so compare against these constants but don't treat the list as complete.
 */
final class ErrorCode
{
    // Returned by the API.
    public const VALIDATION_FAILED = 'VALIDATION_FAILED';
    public const UNAUTHENTICATED = 'UNAUTHENTICATED';
    public const INVALID_API_KEY = 'INVALID_API_KEY';
    public const FORBIDDEN = 'FORBIDDEN';
    public const PROJECT_DISABLED = 'PROJECT_DISABLED';
    public const QUOTA_EXCEEDED = 'QUOTA_EXCEEDED';
    public const CONCURRENCY_LIMIT = 'CONCURRENCY_LIMIT';
    public const RATE_LIMITED = 'RATE_LIMITED';
    public const CONFLICT = 'CONFLICT';
    public const IDEMPOTENCY_CONFLICT = 'IDEMPOTENCY_CONFLICT';
    public const REQUEST_TOO_LARGE = 'REQUEST_TOO_LARGE';
    public const URL_NOT_ALLOWED = 'URL_NOT_ALLOWED';
    public const TEMPLATE_NOT_FOUND = 'TEMPLATE_NOT_FOUND';
    public const TEMPLATE_NOT_PUBLISHED = 'TEMPLATE_NOT_PUBLISHED';
    public const INVALID_DATA = 'INVALID_DATA';
    public const TEMPLATE_COMPILE_FAILED = 'TEMPLATE_COMPILE_FAILED';
    public const INVALID_OUTPUT = 'INVALID_OUTPUT';
    public const RENDER_TIMEOUT = 'RENDER_TIMEOUT';
    public const RENDER_FAILED = 'RENDER_FAILED';
    public const RENDER_UNAVAILABLE = 'RENDER_UNAVAILABLE';
    public const FILE_EXPIRED = 'FILE_EXPIRED';
    public const NOT_FOUND = 'NOT_FOUND';
    public const INTERNAL = 'INTERNAL';

    // Set by the SDK.
    /** An error response whose body is not problem+json. */
    public const UNKNOWN = 'UNKNOWN';
    /** A 2xx response the SDK cannot use (not JSON, missing fields, or a render that is not a PDF). */
    public const INVALID_RESPONSE = 'INVALID_RESPONSE';
    /** No response arrived ({@see Exception\ConnectionException}). */
    public const CONNECTION_ERROR = 'CONNECTION_ERROR';
    /** An HTTP attempt took longer than the `timeout` option ({@see Exception\ConnectionException}). */
    public const TIMEOUT = 'TIMEOUT';
    /** {@see Jobs::wait()} gave up before the job finished. */
    public const WAIT_TIMEOUT = 'WAIT_TIMEOUT';

    private function __construct()
    {
    }
}
