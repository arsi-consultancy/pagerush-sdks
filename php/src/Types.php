<?php

declare(strict_types=1);

namespace PageRush;

/**
 * PHPStan/Psalm array shapes for the API's JSON objects. Responses are plain PHP arrays that keep
 * the API's own snake_case field names, so they match the API reference exactly. Import a shape
 * with `@phpstan-import-type Template from \PageRush\Types`.
 *
 * @phpstan-type TemplateFileEntry array{path: string, content: string, encoding: 'utf8'|'base64'}
 * @phpstan-type VersionSummary array{version: int, live: bool, bytes: int, file_count: int, has_sample_data: bool, typst_version: string, created_at: string}
 * @phpstan-type Template array{id: string, name: string, live_version: int|null, latest_version: int|null, created_at: string, updated_at: string, versions?: list<VersionSummary>}
 * @phpstan-type TemplateVersion array{version: int, source: string|null, document: mixed, files: list<TemplateFileEntry>, sample_data: mixed, typst_version: string, created_at: string}
 * @phpstan-type TemplateWithVersion array{template: Template, version: TemplateVersion}
 * @phpstan-type TemplatePage array{items: list<Template>, next_cursor: string|null}
 * @phpstan-type JobError array{code: string|null, message: string|null}
 * @phpstan-type JobDelivery array{status: string|null, attempts: int, error: string|null}
 * @phpstan-type Job array{id: string, status: string, mode: string, region: string, template_id: string|null, template_version: int|null, engine: string|null, bytes: int|null, duration_ms: int|null, error: JobError|null, delivery: JobDelivery|null, metadata: array<string, string>|null, download_url: string|null, delete_after_download: bool, created_at: string, completed_at: string|null}
 */
final class Types
{
    private function __construct()
    {
    }
}
