<?php

declare(strict_types=1);

namespace PageRush\Tests;

use PageRush\Exception\ConnectionException;
use PageRush\Exception\PageRushException;
use PageRush\Tests\Support\FakeTransport;
use PageRush\Tests\Support\TestCase;
use PageRush\Transport\Response;

final class RetryTest extends TestCase
{
    private static function pdf(): Response
    {
        return new Response(200, ['Content-Type' => 'application/pdf', 'X-Job-Id' => 'job_1'], '%PDF');
    }

    public function testRetriesA503AfterRetryAfterSeconds(): void
    {
        $this->transport->push(FakeTransport::problem(503, 'RENDER_UNAVAILABLE', ['Retry-After' => '2']), self::pdf());

        $result = $this->client()->templates->render('tpl_1', ['data' => []]);

        self::assertSame('job_1', $result->job_id);
        self::assertCount(2, $this->transport->requests);
        self::assertSame([2.0], $this->sleeps);
    }

    public function testReusesTheIdempotencyKeyAcrossRetries(): void
    {
        $this->transport->push(
            new ConnectionException('TIMEOUT', 'timed out'),
            FakeTransport::problem(502, 'INTERNAL'),
            self::pdf(),
        );

        $this->client()->templates->render('tpl_1', ['data' => ['a' => 1]]);

        self::assertCount(3, $this->transport->requests);
        $keys = array_map(static fn ($r) => $r->header('Idempotency-Key'), $this->transport->requests);
        self::assertNotNull($keys[0]);
        self::assertSame([$keys[0], $keys[0], $keys[0]], $keys);
        self::assertSame($this->transport->requests[0]->body, $this->transport->requests[2]->body);
    }

    public function testCapsRetryAfterAtThirtySeconds(): void
    {
        $this->transport->push(FakeTransport::problem(429, 'RATE_LIMITED', ['Retry-After' => '120']), FakeTransport::json(200, ['id' => 'job_1', 'status' => 'queued']));
        $this->client()->jobs->get('job_1');
        self::assertSame([30.0], $this->sleeps);
    }

    public function testRetryAfterAsAnHttpDate(): void
    {
        $this->clock = (float) strtotime('2026-01-01 00:00:00 UTC');
        $this->transport->push(FakeTransport::problem(503, 'RENDER_UNAVAILABLE', ['Retry-After' => 'Thu, 01 Jan 2026 00:00:05 GMT']), FakeTransport::json(200, ['id' => 'job_1', 'status' => 'queued']));
        $this->client()->jobs->get('job_1');
        self::assertSame([5.0], $this->sleeps);
    }

    public function testEmptyOrInvalidRetryAfterFallsBackToBackoff(): void
    {
        $this->transport->push(
            FakeTransport::problem(503, 'RENDER_UNAVAILABLE', ['Retry-After' => '']),
            FakeTransport::problem(503, 'RENDER_UNAVAILABLE', ['Retry-After' => 'soon']),
            FakeTransport::json(200, ['id' => 'job_1', 'status' => 'queued']),
        );
        $this->client()->jobs->get('job_1');

        self::assertCount(2, $this->sleeps);
        self::assertGreaterThanOrEqual(0.375, $this->sleeps[0]);
        self::assertLessThanOrEqual(0.625, $this->sleeps[0]);
        self::assertGreaterThanOrEqual(0.75, $this->sleeps[1]);
        self::assertLessThanOrEqual(1.25, $this->sleeps[1]);
    }

    public function testExponentialBackoffIsCappedAtEightSeconds(): void
    {
        for ($i = 0; $i < 6; $i++) {
            $this->transport->push(FakeTransport::problem(502, 'INTERNAL'));
        }
        $this->transport->push(FakeTransport::json(200, ['id' => 'job_1', 'status' => 'queued']));

        $this->client(['max_retries' => 6])->jobs->get('job_1');

        $bases = [0.5, 1, 2, 4, 8, 8];
        self::assertCount(6, $this->sleeps);
        foreach ($bases as $n => $base) {
            self::assertGreaterThanOrEqual($base * 0.75, $this->sleeps[$n], "retry $n");
            self::assertLessThanOrEqual($base * 1.25, $this->sleeps[$n], "retry $n");
        }
    }

    public function testStopsAfterMaxRetries(): void
    {
        for ($i = 0; $i < 4; $i++) {
            $this->transport->push(FakeTransport::problem(503, 'RENDER_UNAVAILABLE', ['Retry-After' => '1']));
        }

        try {
            $this->client()->jobs->get('job_1');
            self::fail('Expected an exception');
        } catch (PageRushException $e) {
            self::assertSame(503, $e->status);
            self::assertSame('RENDER_UNAVAILABLE', $e->errorCode);
        }
        self::assertCount(3, $this->transport->requests, 'first attempt + 2 retries');
        self::assertSame([1.0, 1.0], $this->sleeps);
    }

    public function testMaxRetriesZeroMeansOneAttempt(): void
    {
        $this->transport->push(FakeTransport::problem(503, 'RENDER_UNAVAILABLE'));

        $this->expectException(PageRushException::class);
        try {
            $this->client(['max_retries' => 0])->jobs->get('job_1');
        } finally {
            self::assertCount(1, $this->transport->requests);
            self::assertSame([], $this->sleeps);
        }
    }

    public function testRetriesGetOnConnectionErrors(): void
    {
        $this->transport->push(new ConnectionException('CONNECTION_ERROR', 'refused'), FakeTransport::json(200, ['id' => 'job_1', 'status' => 'queued']));
        $this->client()->jobs->get('job_1');
        self::assertCount(2, $this->transport->requests);
    }

    public function testRetriesDeleteOn504(): void
    {
        $this->transport->push(FakeTransport::problem(504, 'INTERNAL'), new Response(204));
        $this->client()->templates->delete('tpl_1');
        self::assertCount(2, $this->transport->requests);
    }

    public function testThrowsTheLastConnectionErrorAfterMaxRetries(): void
    {
        $this->transport->push(
            new ConnectionException('TIMEOUT', 'one'),
            new ConnectionException('TIMEOUT', 'two'),
            new ConnectionException('TIMEOUT', 'three'),
        );

        try {
            $this->client()->jobs->get('job_1');
            self::fail('Expected an exception');
        } catch (ConnectionException $e) {
            self::assertSame('TIMEOUT', $e->errorCode);
            self::assertSame(0, $e->status);
            self::assertSame('TIMEOUT: three', $e->getMessage());
        }
        self::assertCount(3, $this->transport->requests);
    }

    public function testDoesNotRetryCreateOn502(): void
    {
        $this->transport->push(FakeTransport::problem(502, 'INTERNAL'));

        $this->expectException(PageRushException::class);
        try {
            $this->client()->templates->create(['name' => 'A', 'source' => 'B']);
        } finally {
            self::assertCount(1, $this->transport->requests);
        }
    }

    public function testDoesNotRetryCreateOn504(): void
    {
        $this->transport->push(FakeTransport::problem(504, 'INTERNAL'));

        $this->expectException(PageRushException::class);
        try {
            $this->client()->templates->createVersion('tpl_1', ['source' => 'B']);
        } finally {
            self::assertCount(1, $this->transport->requests);
        }
    }

    public function testDoesNotRetryUnsafeMethodsOnConnectionErrors(): void
    {
        $this->transport->push(new ConnectionException('TIMEOUT', 'timed out'));

        $this->expectException(ConnectionException::class);
        try {
            $this->client()->templates->publish('tpl_1', 2);
        } finally {
            self::assertCount(1, $this->transport->requests);
        }
    }

    public function testRetriesUnsafeMethodsOn429And503(): void
    {
        $this->transport->push(
            FakeTransport::problem(429, 'RATE_LIMITED', ['Retry-After' => '1']),
            FakeTransport::problem(503, 'RENDER_UNAVAILABLE', ['Retry-After' => '1']),
            FakeTransport::json(200, ['template' => ['id' => 'tpl_1']]),
        );

        $this->client()->templates->rename('tpl_1', 'New');

        self::assertCount(3, $this->transport->requests);
    }

    public function testDoesNotRetryClientErrors(): void
    {
        $this->transport->push(FakeTransport::problem(400, 'INVALID_DATA'));

        $this->expectException(PageRushException::class);
        try {
            $this->client()->templates->render('tpl_1', ['data' => []]);
        } finally {
            self::assertCount(1, $this->transport->requests);
        }
    }

    public function testRetriesARenderOn409ConflictAfterRetryAfter(): void
    {
        $this->transport->push(FakeTransport::problem(409, 'CONFLICT', ['Retry-After' => '2']), self::pdf());

        $result = $this->client()->templates->render('tpl_1', ['data' => []]);

        self::assertSame('job_1', $result->job_id);
        self::assertSame([2.0], $this->sleeps);
        self::assertSame(
            $this->transport->requests[0]->header('Idempotency-Key'),
            $this->transport->requests[1]->header('Idempotency-Key'),
        );
    }

    public function testNeverRetriesIdempotencyConflict(): void
    {
        $this->transport->push(FakeTransport::problem(409, 'IDEMPOTENCY_CONFLICT', ['Retry-After' => '2']));

        try {
            $this->client()->templates->render('tpl_1', ['data' => []]);
            self::fail('Expected an exception');
        } catch (PageRushException $e) {
            self::assertSame('IDEMPOTENCY_CONFLICT', $e->errorCode);
        }
        self::assertCount(1, $this->transport->requests);
    }

    public function testDoesNotRetryAConflictOutsideRender(): void
    {
        $this->transport->push(FakeTransport::problem(409, 'CONFLICT'));

        $this->expectException(PageRushException::class);
        try {
            $this->client()->templates->delete('tpl_1');
        } finally {
            self::assertCount(1, $this->transport->requests);
        }
    }

    /**
     * @return iterable<string, array{string}>
     */
    public static function hugeRetryAfter(): iterable
    {
        yield 'huge' => ['99999999999999999999999'];
        yield 'overflows to infinity' => ['1e999'];
    }

    #[\PHPUnit\Framework\Attributes\DataProvider('hugeRetryAfter')]
    public function testHugeRetryAfterIsCapped(string $value): void
    {
        $this->transport->push(FakeTransport::problem(503, 'RENDER_UNAVAILABLE', ['Retry-After' => $value]), FakeTransport::problem(503, 'RENDER_UNAVAILABLE', ['Retry-After' => $value]));

        try {
            $this->client(['max_retries' => 1])->jobs->get('job_1');
            self::fail('Expected an exception');
        } catch (PageRushException $e) {
            self::assertSame(1_000_000_000, $e->retryAfter);
        }
        self::assertSame([30.0], $this->sleeps);
    }
}
