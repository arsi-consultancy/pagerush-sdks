<?php

declare(strict_types=1);

namespace PageRush\Tests;

use PageRush\Exception\InvalidArgumentException;
use PageRush\Exception\PageRushException;
use PageRush\TemplateFile;
use PageRush\Tests\Support\FakeTransport;
use PageRush\Tests\Support\TestCase;
use PageRush\Transport\Response;

final class TemplatesTest extends TestCase
{
    private const TEMPLATE = [
        'id' => 'tpl_1',
        'name' => 'Invoice',
        'live_version' => 1,
        'latest_version' => 1,
        'created_at' => '2026-01-01T00:00:00.000Z',
        'updated_at' => '2026-01-01T00:00:00.000Z',
    ];

    private const VERSION = [
        'version' => 2,
        'source' => 'Hello',
        'document' => null,
        'files' => [],
        'sample_data' => null,
        'typst_version' => '0.13.1',
        'created_at' => '2026-01-01T00:00:00.000Z',
    ];

    public function testListSendsLimitAndCursor(): void
    {
        $this->transport->push(FakeTransport::json(200, ['items' => [self::TEMPLATE], 'next_cursor' => null]));

        $page = $this->client()->templates->list(['limit' => 10, 'cursor' => '1767225600000_tpl_9']);

        self::assertSame('GET', $this->transport->last()->method);
        self::assertSame('https://api.example.test/v1/templates?limit=10&cursor=1767225600000_tpl_9', $this->transport->last()->url);
        self::assertSame('tpl_1', $page['items'][0]['id']);
        self::assertNull($page['next_cursor']);
    }

    public function testListWithoutParamsHasNoQueryString(): void
    {
        $this->transport->push(FakeTransport::json(200, ['items' => [], 'next_cursor' => null]));
        $this->client()->templates->list();
        self::assertSame('https://api.example.test/v1/templates', $this->transport->last()->url);
    }

    public function testListAllFollowsCursorsAcrossPages(): void
    {
        $this->transport->push(
            FakeTransport::json(200, ['items' => [self::TEMPLATE, ['id' => 'tpl_2'] + self::TEMPLATE], 'next_cursor' => 'c_2']),
            FakeTransport::json(200, ['items' => [['id' => 'tpl_3'] + self::TEMPLATE], 'next_cursor' => null]),
        );

        $ids = [];
        foreach ($this->client()->templates->listAll(['limit' => 2]) as $template) {
            $ids[] = $template['id'];
        }

        self::assertSame(['tpl_1', 'tpl_2', 'tpl_3'], $ids);
        self::assertCount(2, $this->transport->requests);
        self::assertSame('https://api.example.test/v1/templates?limit=2', $this->transport->requests[0]->url);
        self::assertSame('https://api.example.test/v1/templates?limit=2&cursor=c_2', $this->transport->requests[1]->url);
    }

    public function testListAllIsLazy(): void
    {
        $generator = $this->client()->templates->listAll();
        self::assertInstanceOf(\Generator::class, $generator);
        self::assertCount(0, $this->transport->requests);
    }

    public function testCreatePostsTheBody(): void
    {
        $this->transport->push(FakeTransport::json(201, ['template' => self::TEMPLATE, 'version' => self::VERSION]));
        $logo = TemplateFile::fromContents('logo.png', "\x89PNG");

        $result = $this->client()->templates->create([
            'name' => 'Invoice',
            'source' => 'Hello',
            'files' => [$logo],
            'sample_data' => ['name' => 'Ada'],
            'typst_version' => '0.13.1',
        ]);

        $request = $this->transport->last();
        self::assertSame('POST', $request->method);
        self::assertSame('https://api.example.test/v1/templates', $request->url);
        self::assertSame('application/json', $request->header('Content-Type'));
        self::assertSame([
            'name' => 'Invoice',
            'source' => 'Hello',
            'files' => [['path' => 'logo.png', 'content' => base64_encode("\x89PNG"), 'encoding' => 'base64']],
            'sample_data' => ['name' => 'Ada'],
            'typst_version' => '0.13.1',
        ], $this->transport->lastJsonBody());
        self::assertSame('tpl_1', $result['template']['id']);
        self::assertSame(2, $result['version']['version']);
    }

    public function testGetUnwrapsTheTemplateAndEncodesTheId(): void
    {
        $this->transport->push(FakeTransport::json(200, ['template' => self::TEMPLATE]));

        $template = $this->client()->templates->get('tpl/1 ?#');

        self::assertSame('GET', $this->transport->last()->method);
        self::assertSame('https://api.example.test/v1/templates/tpl%2F1%20%3F%23', $this->transport->last()->url);
        self::assertSame('Invoice', $template['name']);
    }

    public function testRenamePatchesTheName(): void
    {
        $this->transport->push(FakeTransport::json(200, ['template' => ['name' => 'Receipt'] + self::TEMPLATE]));

        $template = $this->client()->templates->rename('tpl_1', 'Receipt');

        self::assertSame('PATCH', $this->transport->last()->method);
        self::assertSame('https://api.example.test/v1/templates/tpl_1', $this->transport->last()->url);
        self::assertSame(['name' => 'Receipt'], $this->transport->lastJsonBody());
        self::assertSame('Receipt', $template['name']);
    }

    public function testDeleteSendsDeleteAndAccepts204(): void
    {
        $this->transport->push(new Response(204));

        $this->client()->templates->delete('tpl_1');

        self::assertSame('DELETE', $this->transport->last()->method);
        self::assertSame('https://api.example.test/v1/templates/tpl_1', $this->transport->last()->url);
        self::assertNull($this->transport->last()->body);
    }

    public function testCreateVersionPostsToVersions(): void
    {
        $this->transport->push(FakeTransport::json(201, ['template' => self::TEMPLATE, 'version' => self::VERSION]));

        $result = $this->client()->templates->createVersion('tpl_1', ['source' => 'Hello v2', 'publish' => false]);

        self::assertSame('POST', $this->transport->last()->method);
        self::assertSame('https://api.example.test/v1/templates/tpl_1/versions', $this->transport->last()->url);
        self::assertSame(['source' => 'Hello v2', 'publish' => false], $this->transport->lastJsonBody());
        self::assertSame(2, $result['version']['version']);
    }

    public function testGetVersionUnwrapsTheVersion(): void
    {
        $this->transport->push(FakeTransport::json(200, ['version' => self::VERSION]));

        $version = $this->client()->templates->getVersion('tpl_1', 2);

        self::assertSame('GET', $this->transport->last()->method);
        self::assertSame('https://api.example.test/v1/templates/tpl_1/versions/2', $this->transport->last()->url);
        self::assertSame('Hello', $version['source']);
    }

    public function testPublishPostsTheVersion(): void
    {
        $this->transport->push(FakeTransport::json(200, ['template' => ['live_version' => 1] + self::TEMPLATE]));

        $template = $this->client()->templates->publish('tpl_1', 1);

        self::assertSame('POST', $this->transport->last()->method);
        self::assertSame('https://api.example.test/v1/templates/tpl_1/publish', $this->transport->last()->url);
        self::assertSame(['version' => 1], $this->transport->lastJsonBody());
        self::assertSame(1, $template['live_version']);
    }

    public function testMissingWrapperKeyIsAnInvalidResponse(): void
    {
        $this->transport->push(FakeTransport::json(200, ['id' => 'tpl_1']));

        try {
            $this->client()->templates->get('tpl_1');
            self::fail('Expected an exception');
        } catch (PageRushException $e) {
            self::assertSame('INVALID_RESPONSE', $e->errorCode);
            self::assertSame(200, $e->status);
            self::assertStringContainsString('has no "template" object', $e->getMessage());
        }
    }

    public function testNonJsonSuccessBodyIsAnInvalidResponse(): void
    {
        $this->transport->push(new Response(200, ['Content-Type' => 'text/html', 'X-Request-Id' => 'req_html'], '<html>proxy</html>'));

        try {
            $this->client()->templates->get('tpl_1');
            self::fail('Expected an exception');
        } catch (PageRushException $e) {
            self::assertSame('INVALID_RESPONSE', $e->errorCode);
            self::assertSame(200, $e->status);
            self::assertSame('req_html', $e->requestId);
            self::assertInstanceOf(\JsonException::class, $e->getPrevious());
        }
    }

    public function testJsonListSuccessBodyIsAnInvalidResponse(): void
    {
        $this->transport->push(new Response(200, ['Content-Type' => 'application/json'], '[1,2]'));

        $this->expectException(PageRushException::class);
        $this->expectExceptionMessage('is not a JSON object');
        $this->client()->templates->list();
    }

    public function testRenderSyncParsesHeaders(): void
    {
        $this->transport->push(new Response(200, [
            'Content-Type' => 'application/pdf',
            'Content-Disposition' => 'inline; filename="invoice-42.pdf"',
            'X-Job-Id' => 'job_1',
            'X-Template-Version' => '3',
            'X-Render-Engine' => 'typst',
            'X-Page-Count' => '2',
            'X-Template-Warnings' => '0',
            'X-Request-Id' => 'req_1',
        ], "%PDF-1.7\n"));

        $result = $this->client()->templates->render('tpl_1', [
            'data' => ['customer' => 'Acme'],
            'version' => 3,
            'filename' => 'invoice-42.pdf',
            'metadata' => ['order' => '42'],
            'delete_after_download' => true,
        ]);

        $request = $this->transport->last();
        self::assertSame('POST', $request->method);
        self::assertSame('https://api.example.test/v1/templates/tpl_1/render', $request->url);
        self::assertSame('application/pdf, application/json', $request->header('Accept'));
        self::assertNull($request->header('Prefer'));
        self::assertSame([
            'data' => ['customer' => 'Acme'],
            'version' => 3,
            'filename' => 'invoice-42.pdf',
            'metadata' => ['order' => '42'],
            'delete_after_download' => true,
        ], $this->transport->lastJsonBody());

        self::assertFalse($result->queued);
        self::assertSame("%PDF-1.7\n", $result->pdf);
        self::assertSame('job_1', $result->job_id);
        self::assertSame(3, $result->template_version);
        self::assertSame('typst', $result->engine);
        self::assertSame(2, $result->page_count);
        self::assertSame(0, $result->warnings);
        self::assertSame('req_1', $result->request_id);
        self::assertSame('invoice-42.pdf', $result->filename);
        self::assertNull($result->job);
    }

    public function testRenderSyncWithoutOptionalHeaders(): void
    {
        $this->transport->push(new Response(200, ['Content-Type' => 'application/pdf', 'X-Job-Id' => 'job_1', 'X-Template-Version' => '1', 'X-Render-Engine' => 'typst'], '%PDF'));

        $result = $this->client()->templates->render('tpl_1', ['data' => null]);

        self::assertSame(['data' => null], $this->transport->lastJsonBody());
        self::assertNull($result->page_count);
        self::assertNull($result->warnings);
        self::assertNull($result->request_id);
        self::assertNull($result->filename);
    }

    public function testRenderQueuedReturnsTheJob(): void
    {
        $job = ['id' => 'job_9', 'status' => 'queued', 'template_id' => 'tpl_1', 'template_version' => 4, 'download_url' => null];
        $this->transport->push(FakeTransport::json(202, $job, ['Location' => '/v1/jobs/job_9']));

        $result = $this->client()->templates->render('tpl_1', [
            'data' => ['a' => 1],
            'callback_url' => 'https://example.test/hook',
            'prefer_async' => true,
        ]);

        self::assertSame('respond-async', $this->transport->last()->header('Prefer'));
        self::assertSame('https://example.test/hook', $this->transport->lastJsonBody()['callback_url']);
        self::assertTrue($result->queued);
        self::assertNull($result->pdf);
        self::assertSame('job_9', $result->job_id);
        self::assertSame(4, $result->template_version);
        self::assertNull($result->engine);
        self::assertSame($job, $result->job);
    }

    public function testRenderGeneratesAUuidIdempotencyKey(): void
    {
        $this->transport->push(new Response(200, ['Content-Type' => 'application/pdf'], '%PDF'));
        $this->client()->templates->render('tpl_1', ['data' => []]);

        self::assertMatchesRegularExpression(
            '/^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/',
            (string) $this->transport->last()->header('Idempotency-Key'),
        );
    }

    public function testRenderKeysDifferPerCall(): void
    {
        $this->transport->push(new Response(200, ['Content-Type' => 'application/pdf'], '%PDF'), new Response(200, ['Content-Type' => 'application/pdf'], '%PDF'));
        $client = $this->client();
        $client->templates->render('tpl_1', ['data' => []]);
        $client->templates->render('tpl_1', ['data' => []]);

        self::assertNotSame($this->transport->requests[0]->header('Idempotency-Key'), $this->transport->requests[1]->header('Idempotency-Key'));
    }

    public function testRenderUsesTheGivenIdempotencyKey(): void
    {
        $this->transport->push(new Response(200, ['Content-Type' => 'application/pdf'], '%PDF'));
        $this->client()->templates->render('tpl_1', ['data' => [], 'idempotency_key' => 'order-42']);

        self::assertSame('order-42', $this->transport->last()->header('Idempotency-Key'));
        self::assertArrayNotHasKey('idempotency_key', $this->transport->lastJsonBody());
    }

    public function testRenderSendsEmptyMetadataAsAnObjectAndDropsNulls(): void
    {
        $this->transport->push(new Response(200, ['Content-Type' => 'application/pdf'], '%PDF'));
        $this->client()->templates->render('tpl_1', ['data' => new \stdClass(), 'metadata' => [], 'version' => null]);

        self::assertSame('{"data":{},"metadata":{}}', $this->transport->last()->body);
    }

    public function testRenderRequiresData(): void
    {
        $this->expectException(InvalidArgumentException::class);
        $this->expectExceptionMessage('"data"');
        $this->client()->templates->render('tpl_1', ['filename' => 'a.pdf']);
    }

    public function testRenderRejectsUnknownOptions(): void
    {
        $this->expectException(InvalidArgumentException::class);
        $this->expectExceptionMessage('Unknown render option(s): idempotencyKey');
        $this->client()->templates->render('tpl_1', ['data' => [], 'idempotencyKey' => 'x']);
    }

    public function testRenderRejectsAJsonSuccessBody(): void
    {
        $this->transport->push(FakeTransport::json(200, ['id' => 'job_1']));

        try {
            $this->client()->templates->render('tpl_1', ['data' => []]);
            self::fail('Expected an exception');
        } catch (PageRushException $e) {
            self::assertSame('INVALID_RESPONSE', $e->errorCode);
            self::assertSame(200, $e->status);
        }
    }

    public function testRenderQueuedWithInvalidJsonIsAnInvalidResponse(): void
    {
        $this->transport->push(new Response(202, ['Content-Type' => 'application/json'], 'not json'));

        $this->expectException(PageRushException::class);
        $this->expectExceptionMessage('INVALID_RESPONSE');
        $this->client()->templates->render('tpl_1', ['data' => []]);
    }

    public function testRenderRejectsAnInvalidIdempotencyKey(): void
    {
        foreach (['', "a\r\nX-Injected: 1", 'with space', 'café', 'a/b', str_repeat('k', 256), 42] as $key) {
            try {
                $this->client()->templates->render('tpl_1', ['data' => [], 'idempotency_key' => $key]);
                self::fail('Expected an exception for ' . var_export($key, true));
            } catch (InvalidArgumentException $e) {
                self::assertStringContainsString('idempotency_key', $e->getMessage());
            }
        }
        self::assertCount(0, $this->transport->requests);
    }

    public function testRenderRejectsANonBooleanPreferAsync(): void
    {
        $this->expectException(InvalidArgumentException::class);
        $this->expectExceptionMessage('prefer_async');
        $this->client()->templates->render('tpl_1', ['data' => [], 'prefer_async' => 1]);
    }

    public function testRenderQueuedWithoutAJobIdIsAnInvalidResponse(): void
    {
        $this->transport->push(FakeTransport::json(202, ['status' => 'queued']));

        try {
            $this->client()->templates->render('tpl_1', ['data' => []]);
            self::fail('Expected an exception');
        } catch (PageRushException $e) {
            self::assertSame('INVALID_RESPONSE', $e->errorCode);
            self::assertSame(202, $e->status);
            self::assertStringContainsString('has no job ID', $e->getMessage());
        }
    }

    public function testListWithoutItemsIsAnInvalidResponse(): void
    {
        $this->transport->push(FakeTransport::json(200, ['data' => []]));

        try {
            iterator_to_array($this->client()->templates->listAll());
            self::fail('Expected an exception');
        } catch (PageRushException $e) {
            self::assertSame('INVALID_RESPONSE', $e->errorCode);
            self::assertSame(200, $e->status);
        }
    }

    public function testCreateWithoutVersionIsAnInvalidResponse(): void
    {
        $this->transport->push(FakeTransport::json(201, ['template' => self::TEMPLATE]));

        try {
            $this->client()->templates->create(['name' => 'A', 'source' => 'B']);
            self::fail('Expected an exception');
        } catch (PageRushException $e) {
            self::assertSame('INVALID_RESPONSE', $e->errorCode);
            self::assertSame(201, $e->status);
            self::assertStringContainsString('has no "version" object', $e->getMessage());
        }
    }

    public function testPreferAsyncStillReturnsAPdfWhenTheApiRendersStraightAway(): void
    {
        $this->transport->push(new Response(200, ['Content-Type' => 'application/pdf', 'X-Job-Id' => 'job_1'], '%PDF'));

        $result = $this->client()->templates->render('tpl_1', ['data' => [], 'prefer_async' => true]);

        self::assertSame('respond-async', $this->transport->last()->header('Prefer'));
        self::assertFalse($result->queued);
        self::assertSame('%PDF', $result->pdf);
    }

    public function testRenderAcceptsAnyKeyTheApiAllows(): void
    {
        $key = 'Order_42-v1:retry.' . str_repeat('k', 237);
        self::assertSame(255, strlen($key));
        $this->transport->push(new Response(200, ['Content-Type' => 'application/pdf'], '%PDF'));

        $this->client()->templates->render('tpl_1', ['data' => [], 'idempotency_key' => $key]);

        self::assertSame($key, $this->transport->last()->header('Idempotency-Key'));
    }

    public function testRenderWithoutContentTypeIsAnInvalidResponse(): void
    {
        $this->transport->push(new Response(200, ['X-Job-Id' => 'job_1'], '%PDF'));

        try {
            $this->client()->templates->render('tpl_1', ['data' => []]);
            self::fail('Expected an exception');
        } catch (PageRushException $e) {
            self::assertSame('INVALID_RESPONSE', $e->errorCode);
            self::assertSame(200, $e->status);
            self::assertStringContainsString('no Content-Type', $e->getMessage());
        }
    }
}
