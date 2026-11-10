<?php

/*
 * Creates a template, renders it with data, saves invoice.pdf, and deletes the template.
 *
 *     PAGERUSH_API_KEY=pk_live_... php examples/quickstart.php
 */

declare(strict_types=1);

require __DIR__ . '/../vendor/autoload.php';

use PageRush\Exception\PageRushException;
use PageRush\PageRush;

$client = new PageRush(); // reads PAGERUSH_API_KEY

try {
    $created = $client->templates->create([
        'name' => 'Quickstart invoice',
        'source' => <<<'TYP'
            #let data = json("data.json")
            = Invoice #data.number

            Bill to: #data.customer

            #table(
              columns: (1fr, auto),
              ..data.items.map(item => (item.name, item.price)).flatten(),
            )
            TYP,
    ]);
    $templateId = $created['template']['id'];

    $result = $client->templates->render($templateId, [
        'data' => [
            'number' => 'INV-001',
            'customer' => 'Acme Ltd',
            'items' => [
                ['name' => 'Design', 'price' => '$400'],
                ['name' => 'Hosting', 'price' => '$20'],
            ],
        ],
    ]);
    $bytes = $result->save('invoice.pdf');
    printf("Saved invoice.pdf (%d bytes, job %s)\n", $bytes, $result->job_id);

    $client->templates->delete($templateId);
} catch (PageRushException $e) {
    fwrite(STDERR, $e->getMessage() . PHP_EOL);
    foreach ($e->errors as $error) {
        fwrite(STDERR, "  {$error['path']}: {$error['message']}\n");
    }
    exit(1);
}
