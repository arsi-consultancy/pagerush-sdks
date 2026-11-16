<?php

/*
 * Renders one PDF per CSV row in a single batch, waits for it, and saves the ZIP file.
 *
 *     PAGERUSH_API_KEY=pk_live_... php examples/batch.php [customers.csv]
 *
 * The CSV needs a header row with `number` and `customer` columns. Without a file, the example uses
 * three built-in rows.
 */

declare(strict_types=1);

require __DIR__ . '/../vendor/autoload.php';

use PageRush\Exception\PageRushException;
use PageRush\PageRush;

$csvPath = $argv[1] ?? null;
$csv = $csvPath !== null
    ? fopen($csvPath, 'r')
    : fopen('data://text/plain,' . rawurlencode("number,customer\nINV-001,Acme Ltd\nINV-002,Globex\nINV-003,Initech\n"), 'r');
if ($csv === false) {
    fwrite(STDERR, "Could not open the CSV file\n");
    exit(1);
}

// One batch item per row: the row (keyed by the header) is the render data.
$header = fgetcsv($csv, null, ',', '"', '');
if ($header === false) {
    fwrite(STDERR, "The CSV file is empty\n");
    exit(1);
}
$items = [];
while (($row = fgetcsv($csv, null, ',', '"', '')) !== false) {
    if ($row === [null]) {
        continue; // a blank line
    }
    $record = [];
    foreach ($header as $i => $column) {
        $record[(string) $column] = $row[$i] ?? '';
    }
    $number = preg_replace('/[^A-Za-z0-9_-]/', '-', (string) ($record['number'] ?? ''));
    $items[] = ['data' => $record, 'filename' => $number . '.pdf'];
}
fclose($csv);

$client = new PageRush();

try {
    $created = $client->templates->create([
        'name' => 'Batch example',
        'source' => "#let data = json(\"data.json\")\n= Invoice #data.number\n\nBill to: #data.customer",
    ]);
    $templateId = $created['template']['id'];

    $batch = $client->batches->create($templateId, [
        'items' => $items,
        'metadata' => ['run' => 'example'],
    ]);
    echo "Batch {$batch['id']} queued with {$batch['item_count']} items\n";

    $batch = $client->batches->wait($batch['id'], ['timeout' => 600, 'interval' => 2]);
    echo "Batch {$batch['status']}: {$batch['succeeded']} succeeded, {$batch['failed']} failed\n";
    foreach ($batch['items'] ?? [] as $item) {
        if ($item['error'] !== null) {
            fwrite(STDERR, "Item {$item['index']} failed: " . ($item['error']['message'] ?? 'unknown error') . PHP_EOL);
        }
    }

    if ($batch['succeeded'] > 0) {
        file_put_contents('batch.zip', $client->batches->downloadZip($batch['id']));
        echo "Saved batch.zip\n";
    }

    $client->templates->delete($templateId);
} catch (PageRushException $e) {
    fwrite(STDERR, $e->getMessage() . PHP_EOL);
    exit(1);
}
