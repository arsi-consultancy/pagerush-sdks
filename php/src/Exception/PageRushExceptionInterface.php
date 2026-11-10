<?php

declare(strict_types=1);

namespace PageRush\Exception;

/**
 * Marker interface implemented by every exception this SDK throws, so
 * `catch (PageRushExceptionInterface $e)` catches them all.
 *
 * - {@see PageRushException}: the API returned an error (non-2xx), or a job could not be waited
 *   for or downloaded.
 * - {@see ConnectionException}: no HTTP response arrived (a subclass of PageRushException).
 * - {@see WebhookVerificationException}: a webhook signature did not verify.
 * - {@see InvalidArgumentException} (code `VALIDATION_FAILED`), {@see LogicException} and
 *   {@see RuntimeException}: SPL-compatible errors for bad input, misuse and file I/O.
 */
interface PageRushExceptionInterface extends \Throwable
{
}
