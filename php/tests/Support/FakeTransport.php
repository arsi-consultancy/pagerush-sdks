<?php

declare(strict_types=1);

namespace PageRush\Tests\Support;

use PageRush\Transport\Request;
use PageRush\Transport\Response;
use PageRush\Transport\Transport;

/**
 * Returns queued responses (or throws queued exceptions) and records every request.
 */
final class FakeTransport implements Transport
{
    /** @var list<Request> */
    public array $requests = [];

    /** @var list<Response|\Throwable> */
    private array $queue;

    public function __construct(Response|\Throwable ...$queue)
    {
        $this->queue = array_values($queue);
    }

    public function push(Response|\Throwable ...$responses): void
    {
        foreach ($responses as $response) {
            $this->queue[] = $response;
        }
    }

    public function send(Request $request): Response
    {
        $this->requests[] = $request;
        $next = array_shift($this->queue);
        if ($next === null) {
            throw new \LogicException('FakeTransport has no response queued for ' . $request->method . ' ' . $request->url);
        }
        if ($next instanceof \Throwable) {
            throw $next;
        }

        return $next;
    }

    public function last(): Request
    {
        $last = end($this->requests);
        if ($last === false) {
            throw new \LogicException('No requests were sent');
        }

        return $last;
    }

    /**
     * @return array<mixed>
     */
    public function lastJsonBody(): array
    {
        $decoded = json_decode($this->last()->body ?? 'null', true, 512, JSON_THROW_ON_ERROR);
        if (!is_array($decoded)) {
            throw new \LogicException('The last request body is not a JSON object');
        }

        return $decoded;
    }

    /**
     * @param array<mixed> $body
     * @param array<string, string> $headers
     */
    public static function json(int $status, array $body, array $headers = []): Response
    {
        return new Response($status, ['Content-Type' => 'application/json'] + $headers, json_encode($body, JSON_THROW_ON_ERROR));
    }

    /**
     * @param array<string, string> $headers
     */
    public static function problem(int $status, string $code, array $headers = [], ?string $detail = 'Something went wrong'): Response
    {
        $body = ['type' => 'https://pagerush.dev/errors/' . strtolower($code), 'title' => 'Error', 'status' => $status, 'code' => $code];
        if ($detail !== null) {
            $body['detail'] = $detail;
        }

        return new Response($status, ['Content-Type' => 'application/problem+json'] + $headers, json_encode($body, JSON_THROW_ON_ERROR));
    }
}
