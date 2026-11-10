<?php

declare(strict_types=1);

namespace PageRush\Internal;

use PageRush\ErrorCode;
use PageRush\Exception\ConnectionException;
use PageRush\Exception\InvalidArgumentException;
use PageRush\Exception\LogicException;
use PageRush\Exception\PageRushException;
use PageRush\Transport\Request;
use PageRush\Transport\Response;
use PageRush\Transport\Transport;
use PageRush\Version;

/**
 * Sends API requests with auth, retries and error mapping. Not part of the public API.
 *
 * @internal
 */
final class HttpClient
{
    private const MAX_RETRY_AFTER = 30.0;
    /** Upper bound for a parsed Retry-After, so huge or infinite values stay finite. */
    private const RETRY_AFTER_LIMIT = 1_000_000_000.0;

    /**
     * Returns the Authorization header value. A closure rather than a string property, so that
     * var_export() of the client cannot print the key (it ignores __debugInfo()).
     *
     * @var \Closure(): string
     */
    private readonly \Closure $authorization;
    /** @var \Closure(float): void */
    private readonly \Closure $sleep;
    /** @var \Closure(): float */
    private readonly \Closure $clock;

    /**
     * @param callable(float): void $sleep Sleeps for the given seconds.
     * @param callable(): float     $clock Returns the current time in seconds.
     */
    public function __construct(
        #[\SensitiveParameter]
        string $apiKey,
        public readonly string $baseUrl,
        private readonly float $timeout,
        private readonly int $maxRetries,
        private readonly Transport $transport,
        callable $sleep,
        callable $clock,
    ) {
        $this->authorization = static fn (): string => 'Bearer ' . $apiKey;
        $this->sleep = \Closure::fromCallable($sleep);
        $this->clock = \Closure::fromCallable($clock);
    }

    /**
     * Keeps the API key out of var_dump(), print_r() and var_export() output.
     *
     * @return array<string, mixed>
     */
    public function __debugInfo(): array
    {
        return [
            'apiKey' => '[redacted]',
            'baseUrl' => $this->baseUrl,
            'timeout' => $this->timeout,
            'maxRetries' => $this->maxRetries,
            'transport' => $this->transport::class,
        ];
    }

    /**
     * The client holds the API key, so it must not be serialised.
     *
     * @return array<string, mixed>
     */
    public function __serialize(): array
    {
        throw new LogicException('A PageRush client cannot be serialized');
    }

    public function sleep(float $seconds): void
    {
        ($this->sleep)($seconds);
    }

    public function now(): float
    {
        return ($this->clock)();
    }

    /**
     * Sends a JSON API request and returns the decoded JSON object, or one object inside it.
     *
     * @param non-empty-string          $method
     * @param non-empty-string          $path
     * @param array<string, mixed>|null $body
     * @param string|null               $unwrap   Return this key's object instead, as the API wraps
     *                                            single resources (`{"template": {...}}`).
     * @param array<string, bool>       $required Keys the object must have; true means the value must be an array.
     *
     * @return array<string, mixed>
     *
     * @throws PageRushException Including code `INVALID_RESPONSE`, with the response's status, when a 2xx body is
     *                           not a JSON object or lacks a required key.
     */
    public function json(
        string $method,
        string $path,
        ?array $body = null,
        RetryPolicy $retry = RetryPolicy::Idempotent,
        ?string $unwrap = null,
        array $required = [],
    ): array {
        $response = $this->send($method, $path, $body === null ? null : self::encode($body), $retry);
        $data = self::decodeObject($response);

        if ($unwrap !== null) {
            $required[$unwrap] = true;
        }
        foreach ($required as $key => $mustBeArray) {
            if (!array_key_exists($key, $data) || ($mustBeArray && !is_array($data[$key]))) {
                throw self::invalidResponse($response, sprintf('has no "%s" %s', $key, $mustBeArray ? 'object' : 'field'));
            }
        }
        if ($unwrap === null) {
            return $data;
        }

        /** @var array<string, mixed> $value */
        $value = $data[$unwrap];

        return $value;
    }

    /**
     * Decodes a 2xx response body that must be a JSON object.
     *
     * @return array<string, mixed>
     *
     * @throws PageRushException Code `INVALID_RESPONSE` when the body is not a JSON object.
     */
    public static function decodeObject(Response $response): array
    {
        try {
            $decoded = json_decode($response->body, true, 512, JSON_THROW_ON_ERROR);
        } catch (\JsonException $e) {
            throw self::invalidResponse($response, 'is not valid JSON', $e);
        }
        if (!is_array($decoded) || array_is_list($decoded) && $decoded !== []) {
            throw self::invalidResponse($response, 'is not a JSON object');
        }

        /** @var array<string, mixed> $decoded */
        return $decoded;
    }

    public static function invalidResponse(Response $response, string $problem, ?\Throwable $previous = null): PageRushException
    {
        return new PageRushException(
            $response->status,
            ErrorCode::INVALID_RESPONSE,
            null,
            sprintf('The API returned HTTP %d with a body that %s', $response->status, $problem),
            [],
            $response->header('x-request-id'),
            null,
            null,
            $previous,
        );
    }

    /**
     * Sends a request with retries and returns the 2xx response.
     *
     * @param non-empty-string      $method    HTTP method.
     * @param non-empty-string      $pathOrUrl A path under the base URL (starting with `/`), or an absolute URL.
     * @param array<string, string> $headers   Extra headers.
     * @param bool                  $auth      Whether to send the API key.
     *
     * @throws PageRushException
     */
    public function send(string $method, string $pathOrUrl, ?string $body, RetryPolicy $retry, array $headers = [], bool $auth = true): Response
    {
        $url = preg_match('#^https?://#i', $pathOrUrl) === 1 ? $pathOrUrl : $this->baseUrl . $pathOrUrl;
        $allHeaders = ['User-Agent' => 'pagerush-php/' . Version::VERSION, 'Accept' => 'application/json'];
        if ($auth) {
            $allHeaders['Authorization'] = ($this->authorization)();
        }
        if ($body !== null) {
            $allHeaders['Content-Type'] = 'application/json';
        }
        $allHeaders = array_merge($allHeaders, $headers);
        $request = new Request($method, $url, $allHeaders, $body, $this->timeout);

        for ($attempt = 1; ; $attempt++) {
            $canRetry = $attempt <= $this->maxRetries;
            try {
                $response = $this->transport->send($request);
            } catch (ConnectionException $e) {
                if ($canRetry && $retry->allowsConnectionRetry()) {
                    $this->sleep($this->backoff($attempt, null));
                    continue;
                }
                throw $e;
            }

            if ($response->status >= 200 && $response->status < 300) {
                return $response;
            }
            $error = self::errorFrom($response);
            if ($canRetry && $retry->allowsStatusRetry($response->status, $error->errorCode)) {
                $this->sleep($this->backoff($attempt, $response->header('retry-after')));
                continue;
            }

            throw $error;
        }
    }

    /**
     * Delay before retry $n: Retry-After (capped at 30 s) if present, else min(8, 0.5 * 2^(n-1)) ± 25 %.
     */
    private function backoff(int $n, ?string $retryAfter): float
    {
        $seconds = self::parseRetryAfter($retryAfter, $this->now());
        if ($seconds !== null) {
            return min(self::MAX_RETRY_AFTER, $seconds);
        }
        $base = min(8.0, 0.5 * 2 ** ($n - 1));
        $jitter = 0.75 + (mt_rand() / mt_getrandmax()) * 0.5;

        return $base * $jitter;
    }

    /**
     * Seconds from a Retry-After header (delta-seconds or HTTP date), or null.
     */
    public static function parseRetryAfter(?string $value, ?float $now = null): ?float
    {
        if ($value === null) {
            return null;
        }
        $value = trim($value);
        if ($value === '') {
            return null;
        }
        if (is_numeric($value)) {
            $seconds = (float) $value;
        } else {
            $time = strtotime($value);
            if ($time === false) {
                return null;
            }
            $seconds = $time - ($now ?? microtime(true));
        }

        return max(0.0, min(self::RETRY_AFTER_LIMIT, $seconds));
    }

    /**
     * Builds the exception for a non-2xx response.
     */
    public static function errorFrom(Response $response): PageRushException
    {
        $problem = null;
        try {
            $decoded = $response->body === '' ? null : json_decode($response->body, true, 512, JSON_THROW_ON_ERROR);
            if (is_array($decoded) && isset($decoded['code']) && is_string($decoded['code'])) {
                $problem = $decoded;
            }
        } catch (\JsonException) {
            // Not JSON: fall through to UNKNOWN.
        }

        $errors = [];
        if ($problem !== null && isset($problem['errors']) && is_array($problem['errors'])) {
            foreach ($problem['errors'] as $item) {
                if (is_array($item)) {
                    $errors[] = [
                        'path' => self::stringOf($item['path'] ?? null) ?? '',
                        'message' => self::stringOf($item['message'] ?? null) ?? '',
                    ];
                }
            }
        }

        $retryAfter = self::parseRetryAfter($response->header('retry-after'));

        return new PageRushException(
            $response->status,
            $problem !== null ? (string) $problem['code'] : ErrorCode::UNKNOWN,
            $problem !== null ? self::stringOf($problem['title'] ?? null) : null,
            $problem !== null ? self::stringOf($problem['detail'] ?? null) : null,
            $errors,
            ($problem !== null ? self::stringOf($problem['request_id'] ?? null) : null) ?? $response->header('x-request-id'),
            $retryAfter === null ? null : (int) ceil($retryAfter),
            $problem !== null ? self::stringOf($problem['type'] ?? null) : null,
        );
    }

    private static function stringOf(mixed $value): ?string
    {
        if (is_string($value)) {
            return $value;
        }
        if (is_int($value) || is_float($value)) {
            return (string) $value;
        }

        return null;
    }

    /**
     * @param array<string, mixed> $body
     *
     * @throws InvalidArgumentException When the body cannot be encoded (invalid UTF-8, NAN/INF, resources, ...).
     */
    public static function encode(array $body): string
    {
        try {
            return json_encode($body, JSON_THROW_ON_ERROR | JSON_UNESCAPED_SLASHES | JSON_UNESCAPED_UNICODE | JSON_PRESERVE_ZERO_FRACTION);
        } catch (\JsonException $e) {
            throw new InvalidArgumentException('The request body cannot be encoded as JSON: ' . $e->getMessage(), $e);
        }
    }

    /**
     * URL-encodes one path segment.
     */
    public static function segment(string|int $value): string
    {
        return rawurlencode((string) $value);
    }

    /**
     * A random UUID v4.
     */
    public static function uuid4(): string
    {
        $bytes = random_bytes(16);
        $bytes[6] = chr((ord($bytes[6]) & 0x0f) | 0x40);
        $bytes[8] = chr((ord($bytes[8]) & 0x3f) | 0x80);
        $hex = bin2hex($bytes);

        return sprintf('%s-%s-%s-%s-%s', substr($hex, 0, 8), substr($hex, 8, 4), substr($hex, 12, 4), substr($hex, 16, 4), substr($hex, 20));
    }
}
