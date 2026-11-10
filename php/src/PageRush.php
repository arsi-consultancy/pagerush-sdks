<?php

declare(strict_types=1);

namespace PageRush;

use PageRush\Exception\InvalidArgumentException;
use PageRush\Internal\HttpClient;
use PageRush\Transport\CurlTransport;
use PageRush\Transport\Transport;

/**
 * The PageRush API client.
 *
 * ```php
 * $client = new PageRush\PageRush(getenv('PAGERUSH_API_KEY'));
 *
 * $created = $client->templates->create([
 *     'name' => 'Invoice',
 *     'source' => "#let data = json(\"data.json\")\nInvoice for #data.customer",
 * ]);
 * $client->templates
 *     ->render($created['template']['id'], ['data' => ['customer' => 'Acme']])
 *     ->save('invoice.pdf');
 * ```
 *
 * Options (all optional when `PAGERUSH_API_KEY` is set):
 * - `api_key` (string): project key (`pk_live_...`). Defaults to env `PAGERUSH_API_KEY`.
 * - `base_url` (string): defaults to env `PAGERUSH_BASE_URL`, else `https://pagerush.dev`.
 * - `timeout` (int|float): seconds per HTTP attempt. Default 60.
 * - `max_retries` (int): retries after the first attempt. Default 2.
 * - `transport` ({@see Transport}): sends HTTP requests. Default {@see CurlTransport}.
 * - `sleep` (callable(float): void): waits between retries and polls. Default `usleep`.
 * - `clock` (callable(): float): current time in seconds, used by {@see Jobs::wait()}. Default `microtime(true)`.
 */
final class PageRush
{
    public const DEFAULT_BASE_URL = 'https://pagerush.dev';
    public const DEFAULT_TIMEOUT = 60;
    public const DEFAULT_MAX_RETRIES = 2;

    /** Whitespace trimmed from the API key (trim()'s default set without NUL). */
    private const WHITESPACE = " \t\n\r\v\f";

    private const OPTIONS = ['api_key', 'base_url', 'timeout', 'max_retries', 'transport', 'sleep', 'clock'];

    /** Template endpoints. */
    public readonly Templates $templates;
    /** Render job endpoints. */
    public readonly Jobs $jobs;
    /** Webhook verification helpers (also available statically on {@see Webhook}). */
    public readonly Webhooks $webhooks;

    /**
     * @param string|array<string, mixed> $options The API key, or an options array (see the class description).
     *
     * @throws InvalidArgumentException When no valid API key is given or found in the environment, or an option is
     *                                  unknown or has the wrong type.
     */
    public function __construct(#[\SensitiveParameter] string|array $options = [])
    {
        if (is_string($options)) {
            $options = ['api_key' => $options];
        }
        $unknown = array_diff(array_keys($options), self::OPTIONS);
        if ($unknown !== []) {
            throw new InvalidArgumentException('Unknown PageRush option(s): ' . implode(', ', $unknown));
        }

        $apiKey = $options['api_key'] ?? self::env('PAGERUSH_API_KEY');
        $apiKey = is_string($apiKey) ? trim($apiKey, self::WHITESPACE) : '';
        if ($apiKey === '') {
            throw new InvalidArgumentException('Missing API key: pass it to new PageRush(...) or set PAGERUSH_API_KEY');
        }
        // The message must not include the key.
        if (preg_match('/[\x00-\x20\x7f]/', $apiKey) === 1) {
            throw new InvalidArgumentException('The API key contains spaces or control characters');
        }

        $baseUrl = $options['base_url'] ?? self::env('PAGERUSH_BASE_URL') ?? self::DEFAULT_BASE_URL;
        if (!is_string($baseUrl) || preg_match('#^https?://[^/\s]+(/\S*)?$#iD', $baseUrl) !== 1) {
            throw new InvalidArgumentException('base_url must be an http(s) URL');
        }
        $baseUrl = rtrim($baseUrl, '/');

        $timeout = $options['timeout'] ?? self::DEFAULT_TIMEOUT;
        if (!is_int($timeout) && !is_float($timeout) || !is_finite((float) $timeout) || $timeout <= 0) {
            throw new InvalidArgumentException('timeout must be a positive number of seconds');
        }
        $maxRetries = $options['max_retries'] ?? self::DEFAULT_MAX_RETRIES;
        if (!is_int($maxRetries) || $maxRetries < 0) {
            throw new InvalidArgumentException('max_retries must be an integer, 0 or more');
        }
        $transport = $options['transport'] ?? new CurlTransport();
        if (!$transport instanceof Transport) {
            throw new InvalidArgumentException('transport must implement ' . Transport::class);
        }
        $sleep = $options['sleep'] ?? static function (float $seconds): void {
            usleep((int) round($seconds * 1_000_000));
        };
        $clock = $options['clock'] ?? static fn (): float => microtime(true);
        if (!is_callable($sleep) || !is_callable($clock)) {
            throw new InvalidArgumentException('sleep and clock must be callables');
        }

        $http = new HttpClient($apiKey, $baseUrl, (float) $timeout, $maxRetries, $transport, $sleep, $clock);
        $this->templates = new Templates($http);
        $this->jobs = new Jobs($http);
        $this->webhooks = new Webhooks();
    }

    /**
     * Shows the configuration without the API key in var_dump() and print_r().
     *
     * @return array<string, mixed>
     */
    public function __debugInfo(): array
    {
        return ['templates' => $this->templates, 'jobs' => $this->jobs, 'webhooks' => $this->webhooks];
    }

    private static function env(string $name): ?string
    {
        $value = getenv($name);

        return is_string($value) && $value !== '' ? $value : null;
    }
}
