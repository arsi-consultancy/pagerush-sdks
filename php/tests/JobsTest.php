<?php

declare(strict_types=1);

namespace PageRush\Tests;

use PageRush\Exception\PageRushException;
use PageRush\Tests\Support\FakeTransport;
use PageRush\Tests\Support\TestCase;
use PageRush\Transport\Response;

final class JobsTest extends TestCase
{
    /**
     * @param array<string, mixed> $fields
     *
     * @return array<string, mixed>
     */
    private static function job(string $status, array $fields = []): array
    {
        return $fields + [
            'id' => 'job_1',
            'status' => $status,
            'mode' => 'async',
            'region' => 'ap-south-1',
            'template_id' => 'tpl_1',
            'template_version' => 1,
            'error' => null,
            'download_url' => null,
        ];
    }

    public function testGetFetchesTheJob(): void
    {
        $this->transport->push(FakeTransport::json(200, self::job('running')));

        $job = $this->client()->jobs->get('job/1');

        self::assertSame('https://api.example.test/v1/jobs/job%2F1', $this->transport->last()->url);
        self::assertSame('running', $job['status']);
    }

    public function testWaitPollsUntilSucceeded(): void
    {
        $this->transport->push(
            FakeTransport::json(200, self::job('queued')),
            FakeTransport::json(200, self::job('running')),
            FakeTransport::json(200, self::job('succeeded', ['download_url' => 'https://files.example.test/job_1.pdf?sig=abc'])),
        );

        $job = $this->client()->jobs->wait('job_1', ['interval' => 0.5]);

        self::assertSame('succeeded', $job['status']);
        self::assertCount(3, $this->transport->requests);
        self::assertSame([0.5, 0.5], $this->sleeps);
    }

    public function testWaitReturnsAFailedJob(): void
    {
        $this->transport->push(FakeTransport::json(200, self::job('failed', ['error' => ['code' => 'RENDER_FAILED', 'message' => 'boom']])));

        $job = $this->client()->jobs->wait('job_1');

        self::assertSame('failed', $job['status']);
        self::assertSame(['code' => 'RENDER_FAILED', 'message' => 'boom'], $job['error']);
        self::assertSame([], $this->sleeps);
    }

    public function testWaitTimesOut(): void
    {
        for ($i = 0; $i < 10; $i++) {
            $this->transport->push(FakeTransport::json(200, self::job('running')));
        }

        try {
            $this->client()->jobs->wait('job_1', ['timeout' => 2.5, 'interval' => 1]);
            self::fail('Expected an exception');
        } catch (PageRushException $e) {
            self::assertSame('WAIT_TIMEOUT', $e->errorCode);
            self::assertSame(0, $e->status);
            self::assertStringContainsString('job_1 is still running after 2.5 seconds', $e->getMessage());
        }
        self::assertSame([1.0, 1.0, 0.5], $this->sleeps, 'never sleeps past the deadline');
        self::assertCount(4, $this->transport->requests);
    }

    public function testWaitPropagatesApiErrors(): void
    {
        $this->transport->push(FakeTransport::problem(404, 'NOT_FOUND'));

        $this->expectException(PageRushException::class);
        $this->expectExceptionMessage('NOT_FOUND');
        $this->client()->jobs->wait('job_missing');
    }

    public function testDownloadFetchesTheSignedUrlWithoutAuth(): void
    {
        $this->transport->push(
            FakeTransport::json(200, self::job('succeeded', ['download_url' => 'https://files.example.test/job_1.pdf?sig=abc'])),
            new Response(200, ['Content-Type' => 'application/pdf'], '%PDF-bytes'),
        );

        $pdf = $this->client()->jobs->download('job_1');

        self::assertSame('%PDF-bytes', $pdf);
        self::assertSame('Bearer pk_test_123', $this->transport->requests[0]->header('Authorization'));
        $download = $this->transport->requests[1];
        self::assertSame('GET', $download->method);
        self::assertSame('https://files.example.test/job_1.pdf?sig=abc', $download->url);
        self::assertNull($download->header('Authorization'));
        self::assertNotNull($download->header('User-Agent'));
    }

    public function testDownloadResolvesARelativeUrlAgainstTheBaseUrl(): void
    {
        $this->transport->push(
            FakeTransport::json(200, self::job('succeeded', ['download_url' => '/files/job_1?sig=abc'])),
            new Response(200, ['Content-Type' => 'application/pdf'], '%PDF'),
        );

        $this->client()->jobs->download('job_1');

        self::assertSame('https://api.example.test/files/job_1?sig=abc', $this->transport->last()->url);
        self::assertNull($this->transport->last()->header('Authorization'));
    }

    public function testDownloadWithoutUrlIsFileExpired(): void
    {
        $this->transport->push(FakeTransport::json(200, self::job('succeeded')));

        try {
            $this->client()->jobs->download('job_1');
            self::fail('Expected an exception');
        } catch (PageRushException $e) {
            self::assertSame('FILE_EXPIRED', $e->errorCode);
            self::assertStringContainsString('status: succeeded', $e->getMessage());
        }
        self::assertCount(1, $this->transport->requests);
    }

    public function testDownloadErrorsAreMapped(): void
    {
        $this->transport->push(
            FakeTransport::json(200, self::job('succeeded', ['download_url' => 'https://files.example.test/x'])),
            FakeTransport::problem(410, 'FILE_EXPIRED'),
        );

        $this->expectException(PageRushException::class);
        $this->expectExceptionMessage('FILE_EXPIRED');
        $this->client()->jobs->download('job_1');
    }

    public function testGetWithoutStatusIsAnInvalidResponse(): void
    {
        $this->transport->push(FakeTransport::json(200, ['id' => 'job_1']));

        try {
            $this->client()->jobs->get('job_1');
            self::fail('Expected an exception');
        } catch (PageRushException $e) {
            self::assertSame('INVALID_RESPONSE', $e->errorCode);
            self::assertSame(200, $e->status);
        }
    }
}
