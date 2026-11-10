<?php

declare(strict_types=1);

namespace PageRush\Tests;

use PageRush\Exception\PageRushException;
use PageRush\Exception\PageRushExceptionInterface;
use PageRush\Tests\Support\TestCase;
use PageRush\Transport\Response;

final class ErrorTest extends TestCase
{
    private function catch(Response $response): PageRushException
    {
        $this->transport->push($response);
        try {
            $this->client(['max_retries' => 0])->templates->get('tpl_1');
        } catch (PageRushException $e) {
            return $e;
        }
        self::fail('Expected a PageRushException');
    }

    public function testMapsProblemJson(): void
    {
        $e = $this->catch(new Response(422, ['Content-Type' => 'application/problem+json', 'X-Request-Id' => 'req_header'], (string) json_encode([
            'type' => 'https://pagerush.dev/errors/template-compile-failed',
            'title' => 'Template failed to compile',
            'status' => 422,
            'code' => 'TEMPLATE_COMPILE_FAILED',
            'detail' => 'unknown variable: foo',
            'errors' => [['path' => 'main.typ:3:5', 'message' => 'unknown variable: foo'], 'junk', ['message' => 'no path']],
            'request_id' => 'req_body',
        ])));

        self::assertInstanceOf(PageRushExceptionInterface::class, $e);
        self::assertSame(422, $e->status);
        self::assertSame(422, $e->getCode());
        self::assertSame('TEMPLATE_COMPILE_FAILED', $e->errorCode);
        self::assertSame('Template failed to compile', $e->title);
        self::assertSame('unknown variable: foo', $e->detail);
        self::assertSame('https://pagerush.dev/errors/template-compile-failed', $e->type);
        self::assertSame([
            ['path' => 'main.typ:3:5', 'message' => 'unknown variable: foo'],
            ['path' => '', 'message' => 'no path'],
        ], $e->errors);
        self::assertSame('req_body', $e->requestId, 'body request_id wins over the header');
        self::assertNull($e->retryAfter);
        self::assertSame('TEMPLATE_COMPILE_FAILED: unknown variable: foo', $e->getMessage());
    }

    public function testMessageFallsBackToTitle(): void
    {
        $e = $this->catch(new Response(404, ['Content-Type' => 'application/problem+json'], '{"type":"about:blank","title":"Template not found","status":404,"code":"TEMPLATE_NOT_FOUND"}'));

        self::assertSame('TEMPLATE_NOT_FOUND: Template not found', $e->getMessage());
        self::assertNull($e->detail);
        self::assertSame([], $e->errors);
    }

    public function testNonJsonBodyIsUnknownWithRequestIdFromHeader(): void
    {
        $e = $this->catch(new Response(500, ['Content-Type' => 'text/html', 'X-Request-Id' => 'req_1'], '<html>Bad gateway</html>'));

        self::assertSame(500, $e->status);
        self::assertSame('UNKNOWN', $e->errorCode);
        self::assertNull($e->title);
        self::assertNull($e->detail);
        self::assertSame('req_1', $e->requestId);
        self::assertSame('UNKNOWN: HTTP 500', $e->getMessage());
    }

    public function testJsonWithoutCodeIsUnknown(): void
    {
        $e = $this->catch(new Response(400, ['Content-Type' => 'application/json'], '{"error":"bad"}'));
        self::assertSame('UNKNOWN', $e->errorCode);
        self::assertNull($e->requestId);
    }

    public function testEmptyBodyIsUnknown(): void
    {
        $e = $this->catch(new Response(401));
        self::assertSame('UNKNOWN: HTTP 401', $e->getMessage());
    }

    public function testParsesRetryAfter(): void
    {
        $e = $this->catch(new Response(429, ['Content-Type' => 'application/problem+json', 'Retry-After' => '7'], '{"code":"RATE_LIMITED","title":"Too many requests","status":429}'));
        self::assertSame(7, $e->retryAfter);
        self::assertSame('RATE_LIMITED', $e->errorCode);
    }

    public function testRequestIdHeaderLookupIsCaseInsensitive(): void
    {
        $e = $this->catch(new Response(500, ['x-REQUEST-id' => 'req_mixed'], ''));
        self::assertSame('req_mixed', $e->requestId);
    }
}
