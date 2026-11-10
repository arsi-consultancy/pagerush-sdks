<?php

declare(strict_types=1);

namespace PageRush\Tests;

use PageRush\Exception\LogicException;
use PageRush\Exception\RuntimeException;
use PageRush\RenderResult;
use PageRush\Transport\Response;
use PHPUnit\Framework\Attributes\DataProvider;
use PHPUnit\Framework\TestCase;

final class RenderResultTest extends TestCase
{
    public function testSaveWritesThePdf(): void
    {
        $path = tempnam(sys_get_temp_dir(), 'pagerush');
        self::assertIsString($path);
        try {
            $result = RenderResult::fromPdfResponse(new Response(200, ['Content-Type' => 'application/pdf'], '%PDF-1.7'));
            self::assertSame(8, $result->save($path));
            self::assertSame('%PDF-1.7', file_get_contents($path));
        } finally {
            unlink($path);
        }
    }

    public function testSaveFailsWhenQueued(): void
    {
        $result = RenderResult::fromJob(['id' => 'job_1', 'template_version' => 1] + self::emptyJob());

        $this->expectException(LogicException::class);
        $this->expectExceptionMessage('job_1');
        $result->save(sys_get_temp_dir() . '/never.pdf');
    }

    public function testSaveFailsWhenTheFileCannotBeWritten(): void
    {
        $result = RenderResult::fromPdfResponse(new Response(200, [], '%PDF'));

        $this->expectException(RuntimeException::class);
        $this->expectExceptionMessage('Could not write');
        $result->save(sys_get_temp_dir() . '/missing-dir-' . bin2hex(random_bytes(4)) . '/a.pdf');
    }

    /**
     * @return iterable<string, array{string, ?string}>
     */
    public static function dispositions(): iterable
    {
        yield 'quoted' => ['inline; filename="my invoice.pdf"', 'my invoice.pdf'];
        yield 'unquoted' => ['attachment; filename=report.pdf', 'report.pdf'];
        yield 'case-insensitive' => ['inline; FILENAME="A.pdf"', 'A.pdf'];
        yield 'none' => ['inline', null];
    }

    #[DataProvider('dispositions')]
    public function testParsesTheFilename(string $header, ?string $expected): void
    {
        $result = RenderResult::fromPdfResponse(new Response(200, ['Content-Disposition' => $header], '%PDF'));
        self::assertSame($expected, $result->filename);
    }

    public function testIgnoresNonIntegerHeaders(): void
    {
        $result = RenderResult::fromPdfResponse(new Response(200, ['X-Page-Count' => 'many', 'X-Template-Version' => '2'], '%PDF'));
        self::assertNull($result->page_count);
        self::assertSame(2, $result->template_version);
    }

    /**
     * @return array{id: string, status: string, mode: string, region: string, template_id: null, template_version: null, engine: null, bytes: null, duration_ms: null, error: null, delivery: null, metadata: null, download_url: null, delete_after_download: false, created_at: string, completed_at: null}
     */
    private static function emptyJob(): array
    {
        return [
            'id' => '', 'status' => 'queued', 'mode' => 'async', 'region' => 'ap-south-1', 'template_id' => null,
            'template_version' => null, 'engine' => null, 'bytes' => null, 'duration_ms' => null, 'error' => null,
            'delivery' => null, 'metadata' => null, 'download_url' => null, 'delete_after_download' => false,
            'created_at' => '2026-01-01T00:00:00.000Z', 'completed_at' => null,
        ];
    }
}
