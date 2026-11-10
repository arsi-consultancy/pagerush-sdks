<?php

/*
 * Receives callback_url deliveries with plain PHP.
 *
 *     PAGERUSH_WEBHOOK_SECRET=whsec_... php -S 0.0.0.0:8000 examples/webhook-receiver.php
 *
 * Expose it over https (the API only calls https URLs) and pass that URL as callback_url.
 */

declare(strict_types=1);

require __DIR__ . '/../vendor/autoload.php';

use PageRush\Exception\WebhookVerificationException;
use PageRush\Webhook;
use PageRush\WebhookEvent;

$secret = getenv('PAGERUSH_WEBHOOK_SECRET');
if ($secret === false || $secret === '') {
    http_response_code(500);
    exit('PAGERUSH_WEBHOOK_SECRET is not set');
}

// The exact bytes received. Never json_decode and re-encode the body before verifying it.
$rawBody = (string) file_get_contents('php://input');

try {
    // $_SERVER holds every request header as HTTP_*; constructEvent() understands that form.
    $event = Webhook::constructEvent($rawBody, $_SERVER, $secret);
} catch (WebhookVerificationException $e) {
    http_response_code(400);
    exit('Invalid signature: ' . $e->reason);
}

// The job ID goes into file names below, so accept only the characters job IDs use.
if (preg_match('/^[A-Za-z0-9_-]{1,64}$/D', $event->job_id) !== 1) {
    http_response_code(400);
    exit('Unexpected job ID');
}

// Delivery is at-least-once: a retry carries the same job ID, so skip IDs you have handled.
$seenFile = sys_get_temp_dir() . '/pagerush-seen-' . $event->job_id;
if (file_exists($seenFile)) {
    http_response_code(200);
    exit('Already handled');
}

if ($event->event === WebhookEvent::SUCCEEDED && $event->pdf !== null) {
    if (file_put_contents(sys_get_temp_dir() . '/' . $event->job_id . '.pdf', $event->pdf) === false) {
        // Not recorded as handled, so the API's retry gets another chance.
        http_response_code(500);
        exit('Could not save the PDF');
    }
    error_log("PDF for job {$event->job_id} saved; metadata: " . json_encode($event->metadata));
} elseif ($event->event === WebhookEvent::FAILED) {
    error_log("Job {$event->job_id} failed: " . json_encode($event->failure['error'] ?? null));
}

// Record the job only after the work succeeded, so a failed attempt is retried.
touch($seenFile);
http_response_code(200);
echo 'OK';
