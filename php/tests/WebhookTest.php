<?php

declare(strict_types=1);

namespace PageRush\Tests;

use PageRush\Exception\InvalidArgumentException;
use PageRush\Exception\WebhookVerificationException;
use PageRush\PageRush;
use PageRush\Tests\Support\FakeTransport;
use PageRush\Webhook;
use PageRush\WebhookEvent;
use PHPUnit\Framework\Attributes\DataProvider;
use PHPUnit\Framework\TestCase;

final class WebhookTest extends TestCase
{
    private const SECRET = 'whsec_test_secret';
    private const NOW = 1767225600;

    /**
     * The shared vectors every SDK must pass.
     *
     * @return iterable<string, array{array<string, mixed>}>
     */
    public static function vectors(): iterable
    {
        $raw = file_get_contents(__DIR__ . '/fixtures/webhooks.json');
        self::assertIsString($raw, 'tests/fixtures/webhooks.json is missing');
        $data = json_decode($raw, true, 512, JSON_THROW_ON_ERROR);
        self::assertIsArray($data);
        self::assertIsArray($data['cases']);
        foreach ($data['cases'] as $case) {
            self::assertIsArray($case);
            yield (string) $case['name'] => [$case];
        }
    }

    /**
     * @param array<string, mixed> $case
     */
    #[DataProvider('vectors')]
    public function testSharedVector(array $case): void
    {
        $body = base64_decode((string) $case['body_base64'], true);
        self::assertIsString($body);
        self::assertIsArray($case['headers']);
        /** @var list<string> $secrets */
        $secrets = $case['secrets'];
        $options = ['tolerance' => (int) $case['tolerance_seconds'], 'now' => (int) $case['now']];

        if ($case['valid'] !== true) {
            try {
                Webhook::verify($body, $case['headers'], $secrets, $options);
                self::fail('Expected verification to fail');
            } catch (WebhookVerificationException $e) {
                self::assertSame($case['reason'], $e->reason);
            }

            return;
        }

        Webhook::verify($body, $case['headers'], $secrets, $options);

        $event = Webhook::constructEvent($body, $case['headers'], $secrets, $options);
        self::assertSame($case['headers']['X-PageRush-Event'], $event->event);
        self::assertSame($case['headers']['X-PageRush-Job-Id'], $event->job_id);
        self::assertSame(1767225600, $event->timestamp);
        if ($event->event === WebhookEvent::SUCCEEDED) {
            self::assertSame($body, $event->pdf);
            self::assertNull($event->failure);
            if (isset($case['headers']['X-PageRush-Metadata'])) {
                self::assertSame(json_decode((string) $case['headers']['X-PageRush-Metadata'], true), $event->metadata);
            } else {
                self::assertNull($event->metadata);
            }
        } else {
            self::assertNull($event->pdf);
            self::assertSame(json_decode($body, true), $event->failure);
        }
    }

    public function testVectorsFileHasAllReasonsAndSignsNonAsciiMetadata(): void
    {
        $reasons = [];
        $metadata = [];
        foreach (self::vectors() as [$case]) {
            $reasons[] = $case['reason'] ?? 'valid';
            $metadata[] = $case['headers']['X-PageRush-Metadata'] ?? null;
        }
        self::assertEqualsCanonicalizing(['valid', 'header', 'timestamp', 'signature'], array_values(array_unique($reasons)));
        self::assertContains('{"order":"42","amount":"\\u20b91,250"}', $metadata);
    }

    public function testDecodesEscapedMetadataToTheOriginalValues(): void
    {
        foreach (self::vectors() as [$case]) {
            if ($case['name'] === 'pdf body, current secret') {
                $body = (string) base64_decode((string) $case['body_base64'], true);
                /** @var list<string> $secrets */
                $secrets = $case['secrets'];
                $event = Webhook::constructEvent($body, $case['headers'], $secrets, ['now' => (int) $case['now']]);
                self::assertSame(['order' => '42', 'amount' => "\u{20b9}1,250"], $event->metadata);

                return;
            }
        }
        self::fail('Vector not found');
    }

    /**
     * Signs a delivery the way the API does.
     *
     * @return array<string, string>
     */
    private static function delivery(string $event, string $jobId, string $body, ?string $metadata = null, int $t = self::NOW, string $secret = self::SECRET): array
    {
        $signed = $t . "\n" . $event . "\n" . $jobId . "\n" . ($metadata ?? '') . "\n" . $body;
        $headers = [
            'X-PageRush-Signature' => 't=' . $t . ',v2=' . hash_hmac('sha256', $signed, $secret),
            'X-PageRush-Event' => $event,
            'X-PageRush-Job-Id' => $jobId,
            'X-PageRush-Timestamp' => (string) $t,
        ];
        if ($metadata !== null) {
            $headers['X-PageRush-Metadata'] = $metadata;
        }

        return $headers;
    }

    private static function assertReason(string $reason, callable $call): void
    {
        try {
            $call();
            self::fail('Expected a WebhookVerificationException');
        } catch (WebhookVerificationException $e) {
            self::assertSame($reason, $e->reason);
        }
    }

    public function testDefaultsToTheCurrentTimeAndA300SecondTolerance(): void
    {
        $body = '%PDF-1.7';
        Webhook::verify($body, self::delivery('pdf.succeeded', 'job_1', $body, null, time() - 299), self::SECRET);
        $this->addToAssertionCount(1);

        self::assertReason('timestamp', static fn () => Webhook::verify($body, self::delivery('pdf.succeeded', 'job_1', $body, null, time() - 400), self::SECRET));
    }

    public function testHeaderNamesAreCaseInsensitiveInEveryContainer(): void
    {
        $body = '%PDF-1.7';
        $headers = self::delivery('pdf.succeeded', 'job_1', $body, '{"order":"42"}');

        $lower = array_change_key_case($headers, CASE_LOWER);
        $server = [];
        $symfony = [];
        foreach ($headers as $name => $value) {
            $server['HTTP_' . strtoupper(str_replace('-', '_', $name))] = $value;
            $symfony[strtolower($name)] = [$value];
        }

        foreach ([$headers, $lower, $server, $symfony] as $container) {
            $event = Webhook::constructEvent($body, $container, self::SECRET, ['now' => self::NOW]);
            self::assertSame('job_1', $event->job_id);
            self::assertSame(['order' => '42'], $event->metadata);
        }
    }

    public function testMissingSignatureHeaderIsReasonHeader(): void
    {
        $headers = self::delivery('pdf.succeeded', 'job_1', '%PDF-');
        unset($headers['X-PageRush-Signature']);
        self::assertReason('header', static fn () => Webhook::verify('%PDF-', $headers, self::SECRET, ['now' => self::NOW]));
    }

    public function testMissingEventOrJobIdIsReasonHeader(): void
    {
        foreach (['X-PageRush-Event', 'X-PageRush-Job-Id'] as $name) {
            $headers = self::delivery('pdf.succeeded', 'job_1', '%PDF-');
            unset($headers[$name]);
            self::assertReason('header', static fn () => Webhook::verify('%PDF-', $headers, self::SECRET, ['now' => self::NOW]));
            self::assertReason('header', static fn () => Webhook::constructEvent('%PDF-', $headers, self::SECRET, ['now' => self::NOW]));
        }
    }

    /**
     * @return iterable<string, array{string}>
     */
    public static function malformedSignatures(): iterable
    {
        yield 'non-integer t' => ['t=12.5,v2=abc'];
        yield 'negative t' => ['t=-5,v2=abc'];
        yield 'empty t' => ['t=,v2=abc'];
        yield 'garbage' => ['not a signature'];
    }

    #[DataProvider('malformedSignatures')]
    public function testMalformedSignaturesAreReasonHeader(string $signature): void
    {
        $headers = ['X-PageRush-Signature' => $signature] + self::delivery('pdf.succeeded', 'job_1', '%PDF-');
        self::assertReason('header', static fn () => Webhook::verify('%PDF-', $headers, self::SECRET, ['now' => self::NOW]));
    }

    public function testRejectsNoSecrets(): void
    {
        $this->expectException(InvalidArgumentException::class);
        Webhook::verify('%PDF-', self::delivery('pdf.succeeded', 'job_1', '%PDF-'), [], ['now' => self::NOW]);
    }

    public function testRejectsAnEmptySecret(): void
    {
        $this->expectException(InvalidArgumentException::class);
        Webhook::verify('%PDF-', self::delivery('pdf.succeeded', 'job_1', '%PDF-'), '', ['now' => self::NOW]);
    }

    public function testRejectsAnEmptySecretInAList(): void
    {
        try {
            Webhook::verify('%PDF-', self::delivery('pdf.succeeded', 'job_1', '%PDF-'), [self::SECRET, ''], ['now' => self::NOW]);
            self::fail('Expected an exception');
        } catch (InvalidArgumentException $e) {
            self::assertSame('VALIDATION_FAILED', $e->errorCode);
            self::assertStringContainsString('non-empty strings', $e->getMessage());
        }
    }

    public function testRejectsANonStringSecret(): void
    {
        $this->expectException(InvalidArgumentException::class);
        Webhook::verify('%PDF-', self::delivery('pdf.succeeded', 'job_1', '%PDF-'), [false], ['now' => self::NOW]);
    }

    public function testRejectsUnknownOptions(): void
    {
        $this->expectException(InvalidArgumentException::class);
        Webhook::verify('%PDF-', self::delivery('pdf.succeeded', 'job_1', '%PDF-'), self::SECRET, ['tolerence' => 5]);
    }

    public function testConstructEventForASucceededDelivery(): void
    {
        $body = "%PDF-1.7\nbinary\x00\xff";
        $event = Webhook::constructEvent($body, self::delivery('pdf.succeeded', 'job_1', $body, '{"order":"42"}'), self::SECRET, ['now' => self::NOW]);

        self::assertSame(WebhookEvent::SUCCEEDED, $event->event);
        self::assertSame('job_1', $event->job_id);
        self::assertSame(self::NOW, $event->timestamp);
        self::assertSame($body, $event->pdf);
        self::assertNull($event->failure);
        self::assertSame(['order' => '42'], $event->metadata);
    }

    public function testConstructEventForAFailedDelivery(): void
    {
        $failure = [
            'event' => 'pdf.failed',
            'job_id' => 'job_2',
            'template_id' => 'tpl_1',
            'error' => ['code' => 'TEMPLATE_COMPILE_FAILED', 'message' => 'unknown variable: foo'],
            'metadata' => ['order' => '42'],
            'created_at' => '2026-01-01T00:00:00.000Z',
        ];
        $body = (string) json_encode($failure);

        $event = Webhook::constructEvent($body, self::delivery('pdf.failed', 'job_2', $body), [self::SECRET], ['now' => self::NOW]);

        self::assertSame(WebhookEvent::FAILED, $event->event);
        self::assertSame('job_2', $event->job_id);
        self::assertNull($event->pdf);
        self::assertSame($failure, $event->failure);
        self::assertSame(['order' => '42'], $event->metadata);
    }

    public function testConstructEventReturnsAnUnknownEventWithoutPayload(): void
    {
        $body = '{"something":"new"}';
        $event = Webhook::constructEvent($body, self::delivery('pdf.archived', 'job_3', $body, '{"a":"b"}'), self::SECRET, ['now' => self::NOW]);

        self::assertSame('pdf.archived', $event->event);
        self::assertNull($event->pdf);
        self::assertNull($event->failure);
        self::assertNull($event->metadata);
    }

    public function testConstructEventVerifiesFirst(): void
    {
        $body = '%PDF-';
        $headers = self::delivery('pdf.succeeded', 'job_1', $body, null, self::NOW, 'whsec_other');
        self::assertReason('signature', static fn () => Webhook::constructEvent($body, $headers, self::SECRET, ['now' => self::NOW]));
    }

    /**
     * @return iterable<string, array{string}>
     */
    public static function invalidFailureBodies(): iterable
    {
        yield 'not json' => ['{not json'];
        yield 'json list' => ['[1,2]'];
        yield 'json string' => ['"failed"'];
    }

    #[DataProvider('invalidFailureBodies')]
    public function testConstructEventRejectsAFailureBodyThatIsNotAnObject(string $body): void
    {
        $headers = self::delivery('pdf.failed', 'job_1', $body);
        self::assertReason('header', static fn () => Webhook::constructEvent($body, $headers, self::SECRET, ['now' => self::NOW]));
    }

    /**
     * @return iterable<string, array{string}>
     */
    public static function metadataThatIsNotAMapOfStrings(): iterable
    {
        yield 'invalid json' => ['{bad'];
        yield 'non-string value' => ['{"order":42}'];
        yield 'json list' => ['["a"]'];
        yield 'json string' => ['"order"'];
    }

    #[DataProvider('metadataThatIsNotAMapOfStrings')]
    public function testSignedMetadataThatIsNotAMapOfStringsBecomesNull(string $metadata): void
    {
        $body = '%PDF-';
        $event = Webhook::constructEvent($body, self::delivery('pdf.succeeded', 'job_1', $body, $metadata), self::SECRET, ['now' => self::NOW]);

        self::assertNull($event->metadata);
        self::assertSame($body, $event->pdf);
    }

    public function testFailureMetadataThatIsNotAMapOfStringsBecomesNull(): void
    {
        $body = '{"event":"pdf.failed","job_id":"job_4","metadata":{"order":42}}';
        $event = Webhook::constructEvent($body, self::delivery('pdf.failed', 'job_4', $body), self::SECRET, ['now' => self::NOW]);

        self::assertNull($event->metadata);
    }

    public function testClientWebhooksDelegatesToTheStaticHelpers(): void
    {
        $client = new PageRush(['api_key' => 'pk_test_123', 'transport' => new FakeTransport()]);
        $body = '%PDF-';
        $headers = self::delivery('pdf.succeeded', 'job_1', $body);

        $client->webhooks->verify($body, $headers, self::SECRET, ['now' => self::NOW]);
        $event = $client->webhooks->constructEvent($body, $headers, self::SECRET, ['now' => self::NOW]);

        self::assertSame('job_1', $event->job_id);
    }
}
