<?php

declare(strict_types=1);

namespace PageRush\Transport;

use PageRush\Exception\InvalidArgumentException;

/**
 * One HTTP request, as handed to a {@see Transport}.
 */
final class Request
{
    /**
     * @param non-empty-string      $method  Upper-case HTTP method, e.g. `GET`.
     * @param non-empty-string      $url     Absolute URL.
     * @param array<string, string> $headers Header name => value.
     * @param string|null           $body    Raw request body, or null for none.
     * @param float                 $timeout Seconds allowed for the whole attempt.
     *
     * @throws InvalidArgumentException When a header name or value contains a line break or other control character.
     */
    public function __construct(
        public readonly string $method,
        public readonly string $url,
        #[\SensitiveParameter]
        public readonly array $headers,
        public readonly ?string $body,
        public readonly float $timeout,
    ) {
        foreach ($headers as $name => $value) {
            if (preg_match('/^[!#$%&\'*+.^_`|~0-9A-Za-z-]+$/D', $name) !== 1 || preg_match('/[\x00-\x08\x0a-\x1f\x7f]/', $value) === 1) {
                throw new InvalidArgumentException('Invalid HTTP header: ' . $name);
            }
        }
    }

    /**
     * Keeps the Authorization header out of var_dump(), print_r() and var_export() output.
     *
     * @return array<string, mixed>
     */
    public function __debugInfo(): array
    {
        $headers = $this->headers;
        foreach ($headers as $name => $value) {
            if (strcasecmp($name, 'Authorization') === 0) {
                $headers[$name] = '[redacted]';
            }
        }

        return ['method' => $this->method, 'url' => $this->url, 'headers' => $headers, 'body' => $this->body, 'timeout' => $this->timeout];
    }

    /**
     * Returns a header value by case-insensitive name, or null when it is absent.
     */
    public function header(string $name): ?string
    {
        foreach ($this->headers as $key => $value) {
            if (strcasecmp($key, $name) === 0) {
                return $value;
            }
        }

        return null;
    }
}
