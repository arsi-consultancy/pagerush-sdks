<?php

declare(strict_types=1);

namespace PageRush\Tests;

use PageRush\PageRush;
use PHPUnit\Framework\Attributes\Group;
use PHPUnit\Framework\TestCase;

/**
 * Talks to the real API. Runs only when PAGERUSH_API_KEY is set.
 */
#[Group('live')]
final class LiveTest extends TestCase
{
    public function testCreateRenderDelete(): void
    {
        $key = getenv('PAGERUSH_API_KEY');
        if ($key === false || $key === '') {
            self::markTestSkipped('Set PAGERUSH_API_KEY to run the live smoke test');
        }

        $client = new PageRush($key);
        $created = $client->templates->create([
            'name' => 'pagerush-php smoke test',
            'source' => "#let data = json(\"data.json\")\nHello #data.name",
        ]);
        $id = $created['template']['id'];
        try {
            $result = $client->templates->render($id, ['data' => ['name' => 'PHP']]);
            self::assertFalse($result->queued);
            self::assertStringStartsWith('%PDF', (string) $result->pdf);
            self::assertNotNull($result->job_id);
        } finally {
            $client->templates->delete($id);
        }
    }
}
