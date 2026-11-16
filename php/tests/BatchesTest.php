<?php

declare(strict_types=1);

namespace PageRush\Tests;

use PageRush\Exception\ConnectionException;
use PageRush\Exception\InvalidArgumentException;
use PageRush\Exception\PageRushException;
use PageRush\Tests\Support\FakeTransport;
use PageRush\Tests\Support\TestCase;
use PageRush\Transport\Response;

final class BatchesTest extends TestCase
{
    /**
     * @param array<string, mixed> $fields
     *
     * @return array<string, mixed>
     */
    private static function batch(string $status, array $fields = []): array
    {
        return $fields + [
            'id' => 'bat_1',
            'status' => $status,
            'template_id' => 'tpl_1',
            'template_version' => 3,
            'item_count' => 2,
            'succeeded' => 0,
            'failed' => 0,
            'zip_url' => null,
            'expires_at' => null,
            'metadata' => null,
            'delete_after_download' => false,
            'delivery' => null,
            'created_at' => '2026-11-16T00:00:00.000Z',
            'finished_at' => null,
        ];
    }

    /**
     * @return list<array<string, mixed>>
     */
    private static function items(): array
    {
        return [
            ['data' => ['customer' => 'Acme'], 'filename' => 'acme.pdf'],
            ['data' => ['customer' => 'Globex']],
        ];
    }

    private static function zip(): Response
    {
        return new Response(200, ['Content-Type' => 'application/zip'], "PK\x03\x04zip-bytes");
    }

    public function testCreatePostsTheItemsAndOptions(): void
    {
        $this->transport->push(FakeTransport::json(202, self::batch('queued')));

        $batch = $this->client()->batches->create('tpl/1', [
            'items' => self::items(),
            'version' => 3,
            'metadata' => ['run' => 'nov'],
            'delete_after_download' => true,
            'callback_url' => 'https://example.com/hook',
        ]);

        self::assertSame('bat_1', $batch['id']);
        self::assertSame('queued', $batch['status']);
        $request = $this->transport->last();
        self::assertSame('POST', $request->method);
        self::assertSame('https://api.example.test/v1/templates/tpl%2F1/batches', $request->url);
        self::assertSame('application/json', $request->header('Content-Type'));
        self::assertSame('application/json', $request->header('Accept'));
        self::assertSame('Bearer pk_test_123', $request->header('Authorization'));
        self::assertSame([
            'items' => self::items(),
            'version' => 3,
            'metadata' => ['run' => 'nov'],
            'delete_after_download' => true,
            'callback_url' => 'https://example.com/hook',
        ], $this->transport->lastJsonBody());
        self::assertMatchesRegularExpression('/^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/', (string) $request->header('Idempotency-Key'));
    }

    public function testCreateOmitsNullOptionsAndNullFilenames(): void
    {
        $this->transport->push(FakeTransport::json(202, self::batch('queued')));

        $this->client()->batches->create('tpl_1', [
            'items' => [['data' => ['a' => 1], 'filename' => null]],
            'version' => null,
            'metadata' => [],
        ]);

        self::assertSame('{"items":[{"data":{"a":1}}],"metadata":{}}', $this->transport->last()->body);
    }

    public function testCreateAcceptsAnIterableOfItems(): void
    {
        $this->transport->push(FakeTransport::json(202, self::batch('queued')));
        $rows = (static function (): \Generator {
            yield ['data' => ['n' => 1]];
            yield ['data' => ['n' => 2]];
        })();

        $this->client()->batches->create('tpl_1', ['items' => $rows]);

        self::assertSame(['items' => [['data' => ['n' => 1]], ['data' => ['n' => 2]]]], $this->transport->lastJsonBody());
    }

    public function testCreateReusesTheGeneratedIdempotencyKeyAcrossRetries(): void
    {
        $this->transport->push(
            new ConnectionException('TIMEOUT', 'timed out'),
            FakeTransport::problem(503, 'RENDER_UNAVAILABLE', ['Retry-After' => '1']),
            FakeTransport::json(202, self::batch('queued')),
        );

        $this->client()->batches->create('tpl_1', ['items' => self::items()]);

        self::assertCount(3, $this->transport->requests);
        $keys = array_map(static fn ($r) => $r->header('Idempotency-Key'), $this->transport->requests);
        self::assertNotNull($keys[0]);
        self::assertSame([$keys[0], $keys[0], $keys[0]], $keys);
        self::assertSame($this->transport->requests[0]->body, $this->transport->requests[2]->body);
    }

    public function testCreateRetriesOn502And504(): void
    {
        $this->transport->push(
            FakeTransport::problem(502, 'INTERNAL'),
            FakeTransport::problem(504, 'INTERNAL'),
            FakeTransport::json(202, self::batch('queued')),
        );

        $this->client()->batches->create('tpl_1', ['items' => self::items()]);

        self::assertCount(3, $this->transport->requests);
    }

    public function testCreateSendsAGivenIdempotencyKey(): void
    {
        $this->transport->push(FakeTransport::json(202, self::batch('queued')));

        $this->client()->batches->create('tpl_1', ['items' => self::items(), 'idempotency_key' => 'run:2026-11.a']);

        self::assertSame('run:2026-11.a', $this->transport->last()->header('Idempotency-Key'));
    }

    public function testCreateRejectsABadIdempotencyKeyBeforeSending(): void
    {
        try {
            $this->client()->batches->create('tpl_1', ['items' => self::items(), 'idempotency_key' => 'has space']);
            self::fail('Expected an exception');
        } catch (InvalidArgumentException $e) {
            self::assertSame('VALIDATION_FAILED', $e->errorCode);
            self::assertSame(0, $e->status);
        }
        self::assertSame([], $this->transport->requests);
    }

    /**
     * @return iterable<string, array{array<string, mixed>}>
     */
    public static function invalidOptions(): iterable
    {
        yield 'no items' => [[]];
        yield 'empty items' => [['items' => []]];
        yield 'items not a list' => [['items' => 'rows']];
        yield 'item without data' => [['items' => [['filename' => 'a.pdf']]]];
        yield 'item not an array' => [['items' => ['a']]];
        yield 'unknown option' => [['items' => [['data' => 1]], 'filenames' => []]];
        yield 'body that cannot be encoded' => [['items' => [['data' => NAN]]]];
    }

    /**
     * @param array<string, mixed> $options
     */
    #[\PHPUnit\Framework\Attributes\DataProvider('invalidOptions')]
    public function testCreateRejectsInvalidOptionsBeforeSending(array $options): void
    {
        try {
            $this->client()->batches->create('tpl_1', $options);
            self::fail('Expected an exception');
        } catch (InvalidArgumentException $e) {
            self::assertSame('VALIDATION_FAILED', $e->errorCode);
        }
        self::assertSame([], $this->transport->requests);
    }

    public function testCreateNeverRetriesAConflict(): void
    {
        foreach (['IDEMPOTENCY_CONFLICT', 'CONFLICT'] as $code) {
            $this->transport = new FakeTransport(FakeTransport::problem(409, $code, ['Retry-After' => '2']));
            try {
                $this->client()->batches->create('tpl_1', ['items' => self::items()]);
                self::fail('Expected an exception');
            } catch (PageRushException $e) {
                self::assertSame($code, $e->errorCode);
            }
            self::assertCount(1, $this->transport->requests);
        }
    }

    public function testCreateMapsQuotaAndConcurrencyErrorsWithoutRetrying(): void
    {
        $this->transport->push(FakeTransport::problem(402, 'QUOTA_EXCEEDED'));

        try {
            $this->client()->batches->create('tpl_1', ['items' => self::items()]);
            self::fail('Expected an exception');
        } catch (PageRushException $e) {
            self::assertSame('QUOTA_EXCEEDED', $e->errorCode);
            self::assertSame(402, $e->status);
        }
        self::assertCount(1, $this->transport->requests);
    }

    public function testCreateWithoutIdIsAnInvalidResponse(): void
    {
        $this->transport->push(FakeTransport::json(202, ['status' => 'queued']));

        try {
            $this->client()->batches->create('tpl_1', ['items' => self::items()]);
            self::fail('Expected an exception');
        } catch (PageRushException $e) {
            self::assertSame('INVALID_RESPONSE', $e->errorCode);
            self::assertSame(202, $e->status);
        }
    }

    public function testGetFetchesTheBatchWithItems(): void
    {
        $item = ['index' => 0, 'job_id' => 'job_1', 'status' => 'succeeded', 'filename' => 'acme.pdf', 'bytes' => 1234, 'renders' => 1, 'error' => null];
        $this->transport->push(FakeTransport::json(200, self::batch('rendering', ['items' => [$item]])));

        $batch = $this->client()->batches->get('bat/1');

        $request = $this->transport->last();
        self::assertSame('GET', $request->method);
        self::assertSame('https://api.example.test/v1/batches/bat%2F1', $request->url);
        self::assertNull($request->body);
        self::assertSame('rendering', $batch['status']);
        self::assertSame([$item], $batch['items'] ?? null);
    }

    public function testWaitPollsUntilFinished(): void
    {
        $this->transport->push(
            FakeTransport::json(200, self::batch('queued')),
            FakeTransport::json(200, self::batch('rendering')),
            FakeTransport::json(200, self::batch('partial', ['succeeded' => 1, 'failed' => 1])),
        );

        $batch = $this->client()->batches->wait('bat_1');

        self::assertSame('partial', $batch['status']);
        self::assertCount(3, $this->transport->requests);
        self::assertSame([2.0, 2.0], $this->sleeps, 'polls every 2 seconds by default');
    }

    /**
     * @return iterable<string, array{string}>
     */
    public static function finishedStatuses(): iterable
    {
        yield 'succeeded' => ['succeeded'];
        yield 'partial' => ['partial'];
        yield 'failed' => ['failed'];
    }

    #[\PHPUnit\Framework\Attributes\DataProvider('finishedStatuses')]
    public function testWaitReturnsAFinishedBatchWithoutThrowing(string $status): void
    {
        $this->transport->push(FakeTransport::json(200, self::batch($status)));

        self::assertSame($status, $this->client()->batches->wait('bat_1')['status']);
        self::assertSame([], $this->sleeps);
    }

    public function testWaitTimesOut(): void
    {
        for ($i = 0; $i < 10; $i++) {
            $this->transport->push(FakeTransport::json(200, self::batch('rendering')));
        }

        try {
            $this->client()->batches->wait('bat_1', ['timeout' => 5, 'interval' => 2]);
            self::fail('Expected an exception');
        } catch (PageRushException $e) {
            self::assertSame('WAIT_TIMEOUT', $e->errorCode);
            self::assertSame(0, $e->status);
            self::assertStringContainsString('bat_1 is still rendering after 5 seconds', $e->getMessage());
        }
        self::assertSame([2.0, 2.0, 1.0], $this->sleeps, 'never sleeps past the deadline');
        self::assertCount(4, $this->transport->requests);
    }

    public function testWaitDefaultsToTenMinutes(): void
    {
        for ($i = 0; $i < 302; $i++) {
            $this->transport->push(FakeTransport::json(200, self::batch('rendering')));
        }

        try {
            $this->client()->batches->wait('bat_1');
            self::fail('Expected an exception');
        } catch (PageRushException $e) {
            self::assertSame('WAIT_TIMEOUT', $e->errorCode);
        }
        self::assertSame(600.0, array_sum($this->sleeps));
    }

    public function testDownloadZipSendsTheApiKeyAndReturnsTheBytes(): void
    {
        $this->transport->push(self::zip());

        $zip = $this->client()->batches->downloadZip('bat/1');

        self::assertSame("PK\x03\x04zip-bytes", $zip);
        $request = $this->transport->last();
        self::assertSame('GET', $request->method);
        self::assertSame('https://api.example.test/v1/batches/bat%2F1/zip', $request->url);
        self::assertSame('Bearer pk_test_123', $request->header('Authorization'));
        self::assertSame('application/zip', $request->header('Accept'));
    }

    /**
     * @return iterable<string, array{array<string, string>}>
     */
    public static function nonZipHeaders(): iterable
    {
        yield 'json' => [['Content-Type' => 'application/json']];
        yield 'pdf' => [['Content-Type' => 'application/pdf']];
        yield 'no content type' => [[]];
    }

    /**
     * @param array<string, string> $headers
     */
    #[\PHPUnit\Framework\Attributes\DataProvider('nonZipHeaders')]
    public function testDownloadZipRejectsANonZip200(array $headers): void
    {
        $this->transport->push(new Response(200, $headers, '{"id":"bat_1"}'));

        try {
            $this->client()->batches->downloadZip('bat_1');
            self::fail('Expected an exception');
        } catch (PageRushException $e) {
            self::assertSame('INVALID_RESPONSE', $e->errorCode);
            self::assertSame(200, $e->status);
            self::assertStringContainsString('ZIP', $e->getMessage());
        }
    }

    public function testDownloadZipRaisesAConflictWhileRendering(): void
    {
        $this->transport->push(FakeTransport::problem(409, 'CONFLICT', ['Retry-After' => '5']));

        try {
            $this->client()->batches->downloadZip('bat_1');
            self::fail('Expected an exception');
        } catch (PageRushException $e) {
            self::assertSame('CONFLICT', $e->errorCode);
            self::assertSame(409, $e->status);
            self::assertSame(5, $e->retryAfter);
        }
        self::assertCount(1, $this->transport->requests);
    }

    public function testDownloadZipRaisesFileExpired(): void
    {
        $this->transport->push(FakeTransport::problem(410, 'FILE_EXPIRED'));

        $this->expectException(PageRushException::class);
        $this->expectExceptionMessage('FILE_EXPIRED');
        $this->client()->batches->downloadZip('bat_1');
    }

    public function testDownloadZipRetriesA503(): void
    {
        $this->transport->push(FakeTransport::problem(503, 'RENDER_UNAVAILABLE', ['Retry-After' => '1']), self::zip());

        $this->client()->batches->downloadZip('bat_1');

        self::assertCount(2, $this->transport->requests);
    }
}
