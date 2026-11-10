<?php

/*
 * Renders with prefer_async and handles both outcomes: the PDF returned directly (the usual case),
 * or a job queued because the API was briefly at capacity, which it waits for and downloads.
 *
 *     PAGERUSH_API_KEY=pk_live_... php examples/async-render.php
 */

declare(strict_types=1);

require __DIR__ . '/../vendor/autoload.php';

use PageRush\Exception\PageRushException;
use PageRush\PageRush;

$client = new PageRush();

try {
    $created = $client->templates->create([
        'name' => 'Async example',
        'source' => "#let data = json(\"data.json\")\nHello #data.name",
    ]);
    $templateId = $created['template']['id'];

    $result = $client->templates->render($templateId, [
        'data' => ['name' => 'Ada'],
        'metadata' => ['order' => '42'],
        'prefer_async' => true,
    ]);

    if (!$result->queued) {
        // The usual case: the API rendered straight away.
        $result->save('async.pdf');
    } else {
        echo "Queued job {$result->job_id}\n";
        $job = $client->jobs->wait((string) $result->job_id, ['timeout' => 120, 'interval' => 1]);
        if ($job['status'] === 'failed') {
            fwrite(STDERR, 'Render failed: ' . ($job['error']['message'] ?? 'unknown error') . PHP_EOL);
            exit(1);
        }
        file_put_contents('async.pdf', $client->jobs->download($job['id']));
    }
    echo "Saved async.pdf\n";

    $client->templates->delete($templateId);
} catch (PageRushException $e) {
    fwrite(STDERR, $e->getMessage() . PHP_EOL);
    exit(1);
}
