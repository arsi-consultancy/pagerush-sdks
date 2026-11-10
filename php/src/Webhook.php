<?php

declare(strict_types=1);

namespace PageRush;

use PageRush\Exception\InvalidArgumentException;
use PageRush\Exception\WebhookVerificationException;

/**
 * Verifies `callback_url` deliveries. Static, so a receiver needs only the webhook secret, not an
 * API key or client.
 *
 * ```php
 * $event = PageRush\Webhook::constructEvent(
 *     file_get_contents('php://input'),   // the raw body: never re-encode parsed JSON
 *     getallheaders(),
 *     getenv('PAGERUSH_WEBHOOK_SECRET'),
 * );
 * ```
 *
 * The X-PageRush-Signature header signs the timestamp, the X-PageRush-Event, X-PageRush-Job-Id and
 * X-PageRush-Metadata headers, and the raw body, so every field of a verified event can be trusted.
 *
 * `$headers` is any map of header name to value (or to a list of values), matched without regard to
 * case: `getallheaders()`, `$_SERVER` (`HTTP_X_PAGERUSH_SIGNATURE`), Laravel/Symfony's
 * `$request->headers->all()` and PSR-7's `getHeaders()` all work.
 */
final class Webhook
{
    /** Default tolerance between the signed timestamp and now, in seconds. */
    public const DEFAULT_TOLERANCE = 300;

    private const OPTIONS = ['tolerance', 'now'];

    private function __construct()
    {
    }

    /**
     * Checks a delivery's signature. Returns normally when it is valid.
     *
     * @param string                            $rawBody The exact bytes received.
     * @param array<mixed>                      $headers The request headers.
     * @param string|list<string>               $secrets Your webhook secret, or several (any may match).
     * @param array{tolerance?: int, now?: int} $options `tolerance` in seconds (default 300); `now` as a Unix timestamp (default: time()).
     *
     * @throws WebhookVerificationException With reason `header` (a missing or malformed signature, event or job ID
     *                                      header), `timestamp` (outside the tolerance) or `signature` (no match).
     * @throws InvalidArgumentException    When no secret is given, a secret is empty, or an option is unknown.
     */
    public static function verify(string $rawBody, array $headers, string|array $secrets, array $options = []): void
    {
        self::verified($rawBody, self::normalizeHeaders($headers), $secrets, $options);
    }

    /**
     * Verifies a delivery and returns it as a {@see WebhookEvent}. Every field comes from signed data.
     *
     * @param string                            $rawBody The exact bytes received.
     * @param array<mixed>                      $headers The request headers.
     * @param string|list<string>               $secrets Your webhook secret, or several (any may match).
     * @param array{tolerance?: int, now?: int} $options See {@see verify()}.
     *
     * @throws WebhookVerificationException As {@see verify()}, and with reason `header` when a `pdf.failed` body
     *                                      is not a JSON object.
     * @throws InvalidArgumentException    When no secret is given, a secret is empty, or an option is unknown.
     */
    public static function constructEvent(string $rawBody, array $headers, string|array $secrets, array $options = []): WebhookEvent
    {
        $headers = self::normalizeHeaders($headers);
        [$timestamp, $event, $jobId] = self::verified($rawBody, $headers, $secrets, $options);

        $failure = null;
        $metadata = null;
        if ($event === WebhookEvent::FAILED) {
            $failure = self::decodeFailure($rawBody);
            $metadata = self::stringMap($failure['metadata'] ?? null);
        } elseif ($event === WebhookEvent::SUCCEEDED && isset($headers['x-pagerush-metadata'])) {
            $metadata = self::stringMap(json_decode($headers['x-pagerush-metadata'], true));
        }

        return new WebhookEvent(
            event: $event,
            job_id: $jobId,
            timestamp: $timestamp,
            pdf: $event === WebhookEvent::SUCCEEDED ? $rawBody : null,
            failure: $failure,
            metadata: $metadata,
        );
    }

    /**
     * @param array<string, string>             $headers Normalised headers.
     * @param string|array<mixed>               $secrets Typed loosely so a wrong value gets a clear error.
     * @param array{tolerance?: int, now?: int} $options
     *
     * @return array{int, string, string} The signed timestamp, event and job ID.
     */
    private static function verified(string $rawBody, array $headers, string|array $secrets, array $options): array
    {
        $unknown = array_diff(array_keys($options), self::OPTIONS);
        if ($unknown !== []) {
            throw new InvalidArgumentException('Unknown webhook option(s): ' . implode(', ', $unknown));
        }
        $secrets = is_string($secrets) ? [$secrets] : $secrets;
        if ($secrets === []) {
            throw new InvalidArgumentException('Pass at least one webhook secret');
        }
        foreach ($secrets as $secret) {
            if (!is_string($secret) || $secret === '') {
                throw new InvalidArgumentException('Webhook secrets must be non-empty strings; is PAGERUSH_WEBHOOK_SECRET set?');
            }
        }
        $tolerance = $options['tolerance'] ?? self::DEFAULT_TOLERANCE;
        $now = $options['now'] ?? time();

        $timestamps = [];
        $signatures = [];
        foreach (explode(',', $headers['x-pagerush-signature'] ?? '') as $pair) {
            $eq = strpos($pair, '=');
            if ($eq === false) {
                continue;
            }
            $key = trim(substr($pair, 0, $eq));
            $value = trim(substr($pair, $eq + 1));
            if ($key === 't') {
                $timestamps[] = $value;
            } elseif ($key === 'v2') {
                $signatures[] = $value;
            }
        }
        if (count($timestamps) !== 1 || preg_match('/^\d{1,15}$/D', $timestamps[0]) !== 1 || $signatures === []) {
            throw self::headerError('X-PageRush-Signature must have one t of 1-15 digits and at least one v2');
        }
        $event = $headers['x-pagerush-event'] ?? null;
        $jobId = $headers['x-pagerush-job-id'] ?? null;
        if ($event === null || $jobId === null) {
            throw self::headerError('The delivery needs X-PageRush-Event and X-PageRush-Job-Id headers');
        }
        $timestamp = (int) $timestamps[0];

        if (abs($now - $timestamp) > $tolerance) {
            throw new WebhookVerificationException(
                WebhookVerificationException::REASON_TIMESTAMP,
                sprintf('Webhook timestamp %d is more than %d seconds from now (%d)', $timestamp, $tolerance, $now),
            );
        }

        $signed = $timestamp . "\n" . $event . "\n" . $jobId . "\n" . ($headers['x-pagerush-metadata'] ?? '') . "\n" . $rawBody;
        $matched = false;
        foreach ($secrets as $secret) {
            $expected = hash_hmac('sha256', $signed, $secret);
            foreach ($signatures as $signature) {
                // No early exit, so timing does not reveal which comparison matched.
                $matched = hash_equals($expected, $signature) || $matched;
            }
        }
        if (!$matched) {
            throw new WebhookVerificationException(
                WebhookVerificationException::REASON_SIGNATURE,
                'No X-PageRush-Signature v2 value matches the webhook secret',
            );
        }

        return [$timestamp, $event, $jobId];
    }

    /**
     * @return array<string, mixed>
     *
     * @throws WebhookVerificationException With reason `header` when the body is not a JSON object.
     */
    private static function decodeFailure(string $rawBody): array
    {
        try {
            $decoded = json_decode($rawBody, true, 512, JSON_THROW_ON_ERROR);
        } catch (\JsonException) {
            $decoded = null;
        }
        if (!is_array($decoded) || ($decoded !== [] && array_is_list($decoded))) {
            throw self::headerError('The pdf.failed body is not a JSON object');
        }

        /** @var array<string, mixed> $decoded */
        return $decoded;
    }

    private static function headerError(string $message): WebhookVerificationException
    {
        return new WebhookVerificationException(WebhookVerificationException::REASON_HEADER, $message);
    }

    /**
     * Lower-cases names, maps `$_SERVER` keys (`HTTP_X_FOO`) to `x-foo`, and takes the first of several values.
     *
     * @param array<mixed> $headers
     *
     * @return array<string, string>
     */
    private static function normalizeHeaders(array $headers): array
    {
        $normalized = [];
        foreach ($headers as $name => $value) {
            if (!is_string($name)) {
                continue;
            }
            $name = str_replace('_', '-', strtolower($name));
            if (str_starts_with($name, 'http-')) {
                $name = substr($name, 5);
            }
            if (is_array($value)) {
                $value = reset($value);
            }
            if (is_string($value) && !isset($normalized[$name])) {
                $normalized[$name] = $value;
            }
        }

        return $normalized;
    }

    /**
     * Returns a JSON object of strings as a map, or null for anything else.
     *
     * @return array<string, string>|null
     */
    private static function stringMap(mixed $value): ?array
    {
        if (!is_array($value) || ($value !== [] && array_is_list($value))) {
            return null;
        }
        $map = [];
        foreach ($value as $key => $item) {
            if (!is_string($item)) {
                return null;
            }
            $map[(string) $key] = $item;
        }

        return $map;
    }
}
