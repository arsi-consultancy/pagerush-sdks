<?php

declare(strict_types=1);

namespace PageRush;

use PageRush\Exception\WebhookVerificationException;

/**
 * `$client->webhooks`: the same checks as the static {@see Webhook} class, for code that already
 * holds a client. Receivers that have no API key should call {@see Webhook} directly.
 */
final class Webhooks
{
    /**
     * @internal Use `$client->webhooks`.
     */
    public function __construct()
    {
    }

    /**
     * See {@see Webhook::verify()}.
     *
     * @param array<mixed>                      $headers
     * @param string|list<string>               $secrets
     * @param array{tolerance?: int, now?: int} $options
     *
     * @throws WebhookVerificationException
     */
    public function verify(string $rawBody, array $headers, string|array $secrets, array $options = []): void
    {
        Webhook::verify($rawBody, $headers, $secrets, $options);
    }

    /**
     * See {@see Webhook::constructEvent()}.
     *
     * @param array<mixed>                      $headers
     * @param string|list<string>               $secrets
     * @param array{tolerance?: int, now?: int} $options
     *
     * @throws WebhookVerificationException
     */
    public function constructEvent(string $rawBody, array $headers, string|array $secrets, array $options = []): WebhookEvent
    {
        return Webhook::constructEvent($rawBody, $headers, $secrets, $options);
    }
}
