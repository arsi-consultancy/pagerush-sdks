<?php

declare(strict_types=1);

namespace PageRush\Tests;

use PageRush\Exception\ConnectionException;
use PageRush\Exception\InvalidArgumentException;
use PageRush\Transport\CurlTransport;
use PageRush\Transport\Request;
use PHPUnit\Framework\Attributes\RequiresPhpExtension;
use PHPUnit\Framework\TestCase;

/**
 * Exercises the real curl transport against a local raw-socket HTTP server.
 */
#[RequiresPhpExtension('curl')]
final class CurlTransportTest extends TestCase
{
    /** @var resource|null */
    private static $process;
    private static string $dir;
    private static int $port;

    public static function setUpBeforeClass(): void
    {
        self::$dir = sys_get_temp_dir() . '/pagerush-curl-' . bin2hex(random_bytes(4));
        mkdir(self::$dir);
        file_put_contents(self::$dir . '/response', "HTTP/1.1 204 No Content\r\n\r\n");

        $command = [PHP_BINARY, __DIR__ . '/Support/raw-http-server.php', self::$dir . '/response', self::$dir . '/request'];
        $process = proc_open($command, [1 => ['pipe', 'w'], 2 => ['pipe', 'w']], $pipes);
        self::assertIsResource($process);
        self::$process = $process;
        self::$port = (int) fgets($pipes[1]);
        self::assertGreaterThan(0, self::$port, 'test server did not start');
    }

    public static function tearDownAfterClass(): void
    {
        if (self::$process !== null) {
            proc_terminate(self::$process);
            proc_close(self::$process);
        }
        foreach (glob(self::$dir . '/*') ?: [] as $file) {
            unlink($file);
        }
        rmdir(self::$dir);
    }

    private function respond(string $raw): void
    {
        file_put_contents(self::$dir . '/response', $raw);
    }

    private function url(string $path = '/v1/templates'): string
    {
        return 'http://127.0.0.1:' . self::$port . $path;
    }

    public function testSendsTheRequestAndParsesTheResponse(): void
    {
        $this->respond(
            "HTTP/1.1 201 Created\r\n"
            . "Content-Type: application/json\r\n"
            . "X-Request-Id: req_1\r\n"
            . "Vary: Accept\r\n"
            . "vary: Origin\r\n"
            . "X-Folded: first\r\n"
            . "  second\r\n"
            . "Content-Length: 11\r\n"
            . "Connection: close\r\n"
            . "\r\n"
            . '{"ok":true}',
        );

        $response = (new CurlTransport())->send(new Request(
            'POST',
            $this->url(),
            ['Authorization' => 'Bearer pk_test_123', 'Content-Type' => 'application/json'],
            '{"name":"A"}',
            5,
        ));

        self::assertSame(201, $response->status);
        self::assertSame('{"ok":true}', $response->body);
        self::assertSame('req_1', $response->header('X-REQUEST-ID'));
        self::assertSame('Accept, Origin', $response->header('vary'));
        self::assertSame('first second', $response->header('x-folded'));

        $request = (string) file_get_contents(self::$dir . '/request');
        self::assertStringStartsWith("POST /v1/templates HTTP/1.1\r\n", $request);
        self::assertStringContainsString("Authorization: Bearer pk_test_123\r\n", $request);
        self::assertStringNotContainsStringIgnoringCase('Expect:', $request);
        self::assertStringEndsWith("\r\n\r\n{\"name\":\"A\"}", $request);
    }

    public function testReturnsErrorResponsesWithoutThrowing(): void
    {
        $this->respond("HTTP/1.1 503 Service Unavailable\r\nRetry-After: 2\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");

        $response = (new CurlTransport())->send(new Request('GET', $this->url(), [], null, 5));

        self::assertSame(503, $response->status);
        self::assertSame('2', $response->header('Retry-After'));
        self::assertSame('', $response->body);
    }

    public function testDoesNotFollowRedirects(): void
    {
        $this->respond("HTTP/1.1 302 Found\r\nLocation: http://127.0.0.1:1/elsewhere\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");

        $response = (new CurlTransport())->send(new Request('GET', $this->url(), [], null, 5));

        self::assertSame(302, $response->status);
    }

    public function testTimeoutsAreReportedAsTimeout(): void
    {
        $this->respond("DELAY 1\nHTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");

        try {
            (new CurlTransport())->send(new Request('GET', $this->url(), [], null, 0.2));
            self::fail('Expected a timeout');
        } catch (ConnectionException $e) {
            self::assertSame('TIMEOUT', $e->errorCode);
            self::assertSame(0, $e->status);
        }
    }

    public function testRefusedConnectionsAreConnectionErrors(): void
    {
        // Port 1 on localhost is almost never listening.
        try {
            (new CurlTransport())->send(new Request('GET', 'http://127.0.0.1:1/', [], null, 5));
            self::fail('Expected a connection error');
        } catch (ConnectionException $e) {
            self::assertSame('CONNECTION_ERROR', $e->errorCode);
            self::assertStringContainsString('GET http://127.0.0.1:1/ failed', $e->getMessage());
        }
    }

    public function testUnsupportedSchemesAreNotConnectionErrors(): void
    {
        $this->expectException(InvalidArgumentException::class);
        (new CurlTransport())->send(new Request('GET', 'file:///etc/hosts', [], null, 5));
    }

    public function testErrorMessagesOmitTheQueryString(): void
    {
        try {
            (new CurlTransport())->send(new Request('GET', 'http://127.0.0.1:1/files/job_1.pdf?sig=secret-signature#frag', [], null, 5));
            self::fail('Expected a connection error');
        } catch (ConnectionException $e) {
            self::assertStringContainsString('GET http://127.0.0.1:1/files/job_1.pdf failed', $e->getMessage());
            self::assertStringNotContainsString('secret-signature', $e->getMessage());
        }
    }
}
