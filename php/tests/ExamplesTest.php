<?php

declare(strict_types=1);

namespace PageRush\Tests;

use PHPUnit\Framework\Attributes\DataProvider;
use PHPUnit\Framework\TestCase;

final class ExamplesTest extends TestCase
{
    /**
     * @return iterable<string, array{string}>
     */
    public static function examples(): iterable
    {
        foreach (glob(__DIR__ . '/../examples/*.php') ?: [] as $file) {
            yield basename($file) => [$file];
        }
    }

    public function testTheExpectedExamplesExist(): void
    {
        self::assertSame(
            ['async-render.php', 'quickstart.php', 'webhook-receiver.php'],
            array_keys(iterator_to_array(self::examples())),
        );
    }

    #[DataProvider('examples')]
    public function testExampleHasValidSyntax(string $file): void
    {
        exec(escapeshellarg(PHP_BINARY) . ' -l ' . escapeshellarg($file) . ' 2>&1', $output, $status);
        self::assertSame(0, $status, implode("\n", $output));
    }

    public function testWebhookReceiverRejectsAnUnsignedRequest(): void
    {
        $command = 'PAGERUSH_WEBHOOK_SECRET=whsec_test_secret ' . escapeshellarg(PHP_BINARY) . ' '
            . escapeshellarg(__DIR__ . '/../examples/webhook-receiver.php') . ' 2>&1';
        exec($command, $output, $status);

        self::assertSame(0, $status);
        self::assertSame('Invalid signature: header', implode("\n", $output));
    }

    public function testWebhookReceiverRequiresTheSecret(): void
    {
        $command = 'PAGERUSH_WEBHOOK_SECRET= ' . escapeshellarg(PHP_BINARY) . ' '
            . escapeshellarg(__DIR__ . '/../examples/webhook-receiver.php') . ' 2>&1';
        exec($command, $output, $status);

        self::assertSame('PAGERUSH_WEBHOOK_SECRET is not set', implode("\n", $output));
    }
}
