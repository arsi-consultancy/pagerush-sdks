<?php

declare(strict_types=1);

namespace PageRush\Transport;

use PageRush\Exception\ConnectionException;

/**
 * Sends one HTTP request. The client uses {@see CurlTransport} by default; pass your own
 * implementation in the `transport` option to use another HTTP stack or a fake in tests.
 *
 * A transport returns every HTTP response, including 4xx and 5xx, and does not retry or follow
 * redirects. The client handles retries and error mapping.
 */
interface Transport
{
    /**
     * @throws ConnectionException When no HTTP response arrives (code `CONNECTION_ERROR`, or `TIMEOUT`).
     */
    public function send(Request $request): Response;
}
