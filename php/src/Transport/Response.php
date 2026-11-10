<?php

declare(strict_types=1);

namespace PageRush\Transport;

/**
 * One HTTP response, as returned by a {@see Transport}.
 */
final class Response
{
    /** @var array<string, string> Header names in lower case. */
    public readonly array $headers;

    /**
     * @param int                   $status  HTTP status code.
     * @param array<string, string> $headers Header name => value (any case; stored lower-cased).
     * @param string                $body    Raw response body.
     */
    public function __construct(
        public readonly int $status,
        array $headers = [],
        public readonly string $body = '',
    ) {
        $normalized = [];
        foreach ($headers as $name => $value) {
            $normalized[strtolower($name)] = $value;
        }
        $this->headers = $normalized;
    }

    /**
     * Returns a header value by case-insensitive name, or null when it is absent.
     */
    public function header(string $name): ?string
    {
        return $this->headers[strtolower($name)] ?? null;
    }
}
