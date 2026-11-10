<?php

declare(strict_types=1);

namespace PageRush\Tests;

use PageRush\Exception\InvalidArgumentException;
use PageRush\Exception\PageRushExceptionInterface;
use PageRush\PageRush;
use PageRush\Tests\Support\FakeTransport;
use PageRush\Tests\Support\TestCase;
use PageRush\Version;
use PHPUnit\Framework\Attributes\DataProvider;

final class ClientTest extends TestCase
{
    protected function tearDown(): void
    {
        putenv('PAGERUSH_API_KEY');
        putenv('PAGERUSH_BASE_URL');
    }

    public function testSendsAuthUserAgentAndAcceptHeaders(): void
    {
        $this->transport->push(FakeTransport::json(200, ['id' => 'job_1', 'status' => 'queued']));
        $this->client()->jobs->get('job_1');

        $request = $this->transport->last();
        self::assertSame('GET', $request->method);
        self::assertSame('https://api.example.test/v1/jobs/job_1', $request->url);
        self::assertSame('Bearer pk_test_123', $request->header('authorization'));
        self::assertSame('pagerush-php/' . Version::VERSION, $request->header('User-Agent'));
        self::assertSame('application/json', $request->header('Accept'));
        self::assertNull($request->header('Content-Type'), 'no Content-Type without a body');
        self::assertNull($request->body);
        self::assertSame(60.0, $request->timeout);
    }

    public function testAcceptsTheKeyAsAString(): void
    {
        $client = new PageRush('pk_test_456');
        self::assertInstanceOf(PageRush::class, $client);
    }

    public function testReadsKeyAndBaseUrlFromTheEnvironment(): void
    {
        putenv('PAGERUSH_API_KEY=pk_test_env');
        putenv('PAGERUSH_BASE_URL=https://env.example.test/');
        $transport = new FakeTransport(FakeTransport::json(200, ['id' => 'job_1', 'status' => 'queued']));

        (new PageRush(['transport' => $transport]))->jobs->get('job_1');

        self::assertSame('Bearer pk_test_env', $transport->last()->header('Authorization'));
        self::assertSame('https://env.example.test/v1/jobs/job_1', $transport->last()->url);
    }

    public function testOptionsOverrideTheEnvironmentAndTrailingSlashesAreStripped(): void
    {
        putenv('PAGERUSH_BASE_URL=https://env.example.test');
        $this->transport->push(FakeTransport::json(200, ['id' => 'job_1', 'status' => 'queued']));

        $this->client(['base_url' => 'https://option.example.test///', 'timeout' => 5])->jobs->get('job_1');

        self::assertSame('https://option.example.test/v1/jobs/job_1', $this->transport->last()->url);
        self::assertSame(5.0, $this->transport->last()->timeout);
    }

    public function testDefaultsToTheProductionBaseUrl(): void
    {
        $transport = new FakeTransport(FakeTransport::json(200, ['id' => 'job_1', 'status' => 'queued']));
        (new PageRush(['api_key' => 'pk_test_123', 'transport' => $transport]))->jobs->get('job_1');

        self::assertSame('https://pagerush.dev/v1/jobs/job_1', $transport->last()->url);
    }

    public function testMissingKeyFailsAtConstruction(): void
    {
        putenv('PAGERUSH_API_KEY');
        $this->expectException(InvalidArgumentException::class);
        $this->expectExceptionMessage('Missing API key');
        new PageRush();
    }

    /**
     * @return iterable<string, array{array<string, mixed>, string}>
     */
    public static function invalidOptions(): iterable
    {
        yield 'unknown option' => [['api_key' => 'pk_test_123', 'apiKey' => 'x'], 'Unknown PageRush option(s): apiKey'];
        yield 'empty key' => [['api_key' => ''], 'Missing API key'];
        yield 'bad base url' => [['api_key' => 'pk_test_123', 'base_url' => 'pagerush.dev'], 'base_url must be an http(s) URL'];
        yield 'zero timeout' => [['api_key' => 'pk_test_123', 'timeout' => 0], 'timeout must be a positive number'];
        yield 'negative retries' => [['api_key' => 'pk_test_123', 'max_retries' => -1], 'max_retries must be an integer, 0 or more'];
        yield 'string retries' => [['api_key' => 'pk_test_123', 'max_retries' => '2'], 'max_retries must be an integer'];
        yield 'string timeout' => [['api_key' => 'pk_test_123', 'timeout' => '5'], 'timeout must be a positive number'];
        yield 'infinite timeout' => [['api_key' => 'pk_test_123', 'timeout' => INF], 'timeout must be a positive number'];
        yield 'non-string base url' => [['api_key' => 'pk_test_123', 'base_url' => 42], 'base_url must be an http(s) URL'];
        yield 'base url with spaces' => [['api_key' => 'pk_test_123', 'base_url' => 'https://pagerush.dev/a b'], 'base_url must be an http(s) URL'];
        yield 'bad transport' => [['api_key' => 'pk_test_123', 'transport' => new \stdClass()], 'transport must implement'];
        yield 'bad sleep' => [['api_key' => 'pk_test_123', 'sleep' => 'not a function'], 'sleep and clock must be callables'];
        yield 'non-string key' => [['api_key' => 123], 'Missing API key'];
        yield 'whitespace key' => [['api_key' => "  \t"], 'Missing API key'];
    }

    /**
     * @param array<string, mixed> $options
     */
    #[DataProvider('invalidOptions')]
    public function testRejectsInvalidOptions(array $options, string $message): void
    {
        putenv('PAGERUSH_API_KEY');
        try {
            new PageRush($options);
            self::fail('Expected an exception');
        } catch (InvalidArgumentException $e) {
            self::assertInstanceOf(PageRushExceptionInterface::class, $e);
            self::assertInstanceOf(\InvalidArgumentException::class, $e);
            self::assertStringContainsString($message, $e->getMessage());
        }
    }

    public function testValidationErrorsHaveCodeValidationFailed(): void
    {
        try {
            new PageRush(['api_key' => 'pk_test_123', 'max_retries' => '2']);
            self::fail('Expected an exception');
        } catch (InvalidArgumentException $e) {
            self::assertSame('VALIDATION_FAILED', $e->errorCode);
            self::assertSame(0, $e->status);
        }
    }

    public function testTrimsTheApiKey(): void
    {
        $this->transport->push(FakeTransport::json(200, ['id' => 'job_1', 'status' => 'queued']));
        $this->client(['api_key' => "  pk_test_123\n"])->jobs->get('job_1');

        self::assertSame('Bearer pk_test_123', $this->transport->last()->header('Authorization'));
    }

    /**
     * @return iterable<string, array{string}>
     */
    public static function invalidKeys(): iterable
    {
        yield 'inner space' => ['pk_test_secret value'];
        yield 'newline' => ["pk_test_secret\nX-Injected: 1"];
        yield 'nul byte' => ["pk_test_secret\0"];
        yield 'delete' => ["pk_test_secret\x7f"];
    }

    #[DataProvider('invalidKeys')]
    public function testRejectsKeysWithSpacesOrControlCharactersWithoutEchoingThem(string $key): void
    {
        try {
            new PageRush(['api_key' => $key]);
            self::fail('Expected an exception');
        } catch (InvalidArgumentException $e) {
            self::assertStringNotContainsString('pk_test_secret', $e->getMessage());
        }
    }

    public function testDebugOutputDoesNotContainTheApiKey(): void
    {
        $client = new PageRush(['api_key' => 'pk_test_supersecret', 'transport' => $this->transport]);

        ob_start();
        var_dump($client);
        $dump = (string) ob_get_clean();
        $printed = print_r($client, true);
        $exported = var_export($client, true);
        $json = (string) json_encode($client);

        foreach ([$dump, $printed, $exported, $json] as $output) {
            self::assertStringNotContainsString('pk_test_supersecret', $output);
        }
        self::assertStringContainsString('[redacted]', $dump);
        self::assertStringContainsString('https://pagerush.dev', $printed);
    }

    public function testRequestDebugOutputRedactsAuthorization(): void
    {
        $this->transport->push(FakeTransport::json(200, ['id' => 'job_1', 'status' => 'queued']));
        $this->client(['api_key' => 'pk_test_supersecret'])->jobs->get('job_1');

        self::assertStringNotContainsString('pk_test_supersecret', print_r($this->transport->last(), true));
    }

    public function testTheClientCannotBeSerialized(): void
    {
        $client = new PageRush(['api_key' => 'pk_test_supersecret', 'transport' => $this->transport]);

        try {
            serialize($client);
            self::fail('Expected an exception');
        } catch (\Throwable $e) {
            self::assertStringNotContainsString('pk_test_supersecret', $e->getMessage());
        }
    }

    public function testBodiesThatCannotBeEncodedAreValidationErrors(): void
    {
        foreach ([['x' => NAN], ['x' => INF], ['x' => "caf\xe9"]] as $data) {
            try {
                $this->client()->templates->render('tpl_1', ['data' => $data]);
                self::fail('Expected an exception');
            } catch (InvalidArgumentException $e) {
                self::assertSame('VALIDATION_FAILED', $e->errorCode);
                self::assertSame(0, $e->status);
                self::assertInstanceOf(\JsonException::class, $e->getPrevious());
            }
        }
        try {
            $this->client()->templates->create(['name' => "bad\xff"]);
            self::fail('Expected an exception');
        } catch (InvalidArgumentException $e) {
            self::assertStringContainsString('cannot be encoded as JSON', $e->getMessage());
        }
        self::assertCount(0, $this->transport->requests);
    }

    public function testRejectsHeaderValuesWithLineBreaks(): void
    {
        $this->expectException(InvalidArgumentException::class);
        new \PageRush\Transport\Request('GET', 'https://pagerush.dev/', ['X-Test' => "a\r\nb"], null, 1);
    }
}
