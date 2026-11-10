<?php

declare(strict_types=1);

namespace PageRush\Tests;

use PageRush\Version;
use PHPUnit\Framework\TestCase;

final class VersionTest extends TestCase
{
    public function testVersionMatchesTheLatestReleasedChangelogHeading(): void
    {
        $changelog = file_get_contents(__DIR__ . '/../CHANGELOG.md');
        self::assertIsString($changelog);
        self::assertSame(1, preg_match('/^## \[(\d+\.\d+\.\d+[^\]]*)\] - \d{4}-\d{2}-\d{2}$/m', $changelog, $match), 'CHANGELOG.md has no released version heading');
        self::assertSame($match[1], Version::VERSION);
    }

    public function testComposerJsonHasNoVersionField(): void
    {
        $composer = json_decode((string) file_get_contents(__DIR__ . '/../composer.json'), true, 512, JSON_THROW_ON_ERROR);
        self::assertIsArray($composer);
        self::assertArrayNotHasKey('version', $composer, 'Composer takes versions from git tags');
    }
}
