<?php

declare(strict_types=1);

namespace PageRush\Tests\Support;

use PageRush\PageRush;

abstract class TestCase extends \PHPUnit\Framework\TestCase
{
    protected FakeTransport $transport;

    /** @var list<float> Seconds passed to the injected sleep. */
    protected array $sleeps = [];

    protected float $clock = 1_000_000.0;

    /**
     * @param array<string, mixed> $options
     */
    protected function client(array $options = []): PageRush
    {
        /** @var array{api_key?: string, base_url?: string, timeout?: int|float, max_retries?: int} $options */
        return new PageRush($options + [
            'api_key' => 'pk_test_123',
            'base_url' => 'https://api.example.test',
            'transport' => $this->transport,
            'sleep' => function (float $seconds): void {
                $this->sleeps[] = $seconds;
                $this->clock += $seconds;
            },
            'clock' => fn (): float => $this->clock,
        ]);
    }

    protected function setUp(): void
    {
        $this->transport = new FakeTransport();
        $this->sleeps = [];
    }
}
