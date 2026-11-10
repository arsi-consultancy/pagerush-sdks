<?php

declare(strict_types=1);

namespace PageRush\Tests;

use PageRush\Exception\RuntimeException;
use PageRush\TemplateFile;
use PHPUnit\Framework\Attributes\DataProvider;
use PHPUnit\Framework\TestCase;

final class TemplateFileTest extends TestCase
{
    /**
     * @return iterable<string, array{string}>
     */
    public static function textPaths(): iterable
    {
        foreach (['typ', 'txt', 'csv', 'json', 'svg', 'xml', 'md', 'yml', 'yaml', 'bib'] as $extension) {
            yield $extension => ['parts/file.' . $extension];
        }
        yield 'upper-case extension' => ['NOTES.TXT'];
    }

    #[DataProvider('textPaths')]
    public function testTextFilesAreUtf8(string $path): void
    {
        self::assertSame(['path' => $path, 'content' => 'Grüße', 'encoding' => 'utf8'], TemplateFile::fromContents($path, 'Grüße'));
    }

    /**
     * @return iterable<string, array{string}>
     */
    public static function binaryPaths(): iterable
    {
        yield 'png' => ['logo.png'];
        yield 'font' => ['fonts/Inter.ttf'];
        yield 'no extension' => ['LICENSE'];
    }

    #[DataProvider('binaryPaths')]
    public function testOtherFilesAreBase64(string $path): void
    {
        self::assertSame(['path' => $path, 'content' => base64_encode("\x00\xff"), 'encoding' => 'base64'], TemplateFile::fromContents($path, "\x00\xff"));
    }

    public function testInvalidUtf8TextFallsBackToBase64(): void
    {
        self::assertSame('base64', TemplateFile::fromContents('latin1.csv', "caf\xe9")['encoding']);
    }

    public function testFromPathReadsTheFileAndDefaultsToItsBaseName(): void
    {
        $dir = sys_get_temp_dir() . '/pagerush-php-' . bin2hex(random_bytes(4));
        mkdir($dir);
        $file = $dir . '/header.typ';
        file_put_contents($file, '= Header');
        try {
            self::assertSame(['path' => 'header.typ', 'content' => '= Header', 'encoding' => 'utf8'], TemplateFile::fromPath($file));
            self::assertSame('parts/header.typ', TemplateFile::fromPath($file, 'parts/header.typ')['path']);
        } finally {
            unlink($file);
            rmdir($dir);
        }
    }

    public function testFromPathFailsForAMissingFile(): void
    {
        $this->expectException(RuntimeException::class);
        TemplateFile::fromPath(__DIR__ . '/does-not-exist.png');
    }

    public function testFromPathFailsForADirectory(): void
    {
        $this->expectException(RuntimeException::class);
        TemplateFile::fromPath(__DIR__);
    }
}
