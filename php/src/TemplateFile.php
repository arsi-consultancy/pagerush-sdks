<?php

declare(strict_types=1);

namespace PageRush;

use PageRush\Exception\RuntimeException;

/**
 * Builds entries for the `files` array of {@see Templates::create()} and {@see Templates::createVersion()}.
 *
 * Text files (`.typ .txt .csv .json .svg .xml .md .yml .yaml .bib`) are sent as utf8; everything
 * else (images, fonts) as base64.
 *
 * ```php
 * $files = [
 *     PageRush\TemplateFile::fromPath(__DIR__ . '/logo.png'),             // path "logo.png", base64
 *     PageRush\TemplateFile::fromPath(__DIR__ . '/header.typ', 'parts/header.typ'),
 *     PageRush\TemplateFile::fromContents('terms.txt', "Pay within 30 days."),
 * ];
 * ```
 *
 * @phpstan-import-type TemplateFileEntry from Types
 */
final class TemplateFile
{
    /** Extensions sent as utf8 text. */
    public const TEXT_EXTENSIONS = ['typ', 'txt', 'csv', 'json', 'svg', 'xml', 'md', 'yml', 'yaml', 'bib'];

    private function __construct()
    {
    }

    /**
     * Builds a file entry from bytes in memory.
     *
     * @param string $path     Path inside the template, relative to main.typ (e.g. `images/logo.png`).
     * @param string $contents The file's bytes.
     *
     * @return TemplateFileEntry
     */
    public static function fromContents(string $path, string $contents): array
    {
        $extension = strtolower(pathinfo($path, PATHINFO_EXTENSION));
        // A "text" file that is not valid UTF-8 cannot be sent as a JSON string, so send it as base64.
        if (in_array($extension, self::TEXT_EXTENSIONS, true) && preg_match('//u', $contents) === 1) {
            return ['path' => $path, 'content' => $contents, 'encoding' => 'utf8'];
        }

        return ['path' => $path, 'content' => base64_encode($contents), 'encoding' => 'base64'];
    }

    /**
     * Reads a local file and builds a file entry.
     *
     * @param string      $localPath  The file on disk.
     * @param string|null $targetPath Path inside the template; defaults to the file's base name.
     *
     * @return TemplateFileEntry
     *
     * @throws RuntimeException When the file cannot be read.
     */
    public static function fromPath(string $localPath, ?string $targetPath = null): array
    {
        $contents = is_file($localPath) ? @file_get_contents($localPath) : false;
        if ($contents === false) {
            throw new RuntimeException('Could not read ' . $localPath);
        }

        return self::fromContents($targetPath ?? basename($localPath), $contents);
    }
}
