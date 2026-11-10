<?php

declare(strict_types=1);

namespace PageRush;

/**
 * The SDK's version. This constant is the single source of truth: the User-Agent header uses it,
 * and a test checks it against the latest released heading in CHANGELOG.md.
 */
final class Version
{
    /** Semantic version of this SDK (not the HTTP API version, which is `v1`). */
    public const VERSION = '0.1.0';

    private function __construct()
    {
    }
}
