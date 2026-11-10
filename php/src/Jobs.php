<?php

declare(strict_types=1);

namespace PageRush;

use PageRush\Exception\PageRushException;
use PageRush\Internal\HttpClient;
use PageRush\Internal\RetryPolicy;

/**
 * Render job endpoints: `$client->jobs`.
 *
 * @phpstan-import-type Job from Types
 */
final class Jobs
{
    /**
     * @internal Use `$client->jobs`.
     */
    public function __construct(private readonly HttpClient $http)
    {
    }

    /**
     * Fetches a job. `download_url` is set while a succeeded job's PDF is still stored.
     *
     * @return Job
     *
     * @throws PageRushException `NOT_FOUND`, ...
     */
    public function get(string $id): array
    {
        /** @var Job $job */
        $job = $this->http->json('GET', '/v1/jobs/' . HttpClient::segment($id), required: ['id' => false, 'status' => false]);

        return $job;
    }

    /**
     * Polls a job until its status is `succeeded` or `failed`, and returns it. A failed job is
     * returned, not thrown: check `$job['status']` and `$job['error']`.
     *
     * @param array{timeout?: int|float, interval?: int|float} $options Seconds; defaults 120 and 1.
     *
     * @return Job
     *
     * @throws PageRushException Code `WAIT_TIMEOUT` (status 0) when the job is still running after `timeout`.
     */
    public function wait(string $id, array $options = []): array
    {
        $timeout = (float) ($options['timeout'] ?? 120);
        $interval = (float) ($options['interval'] ?? 1);
        $deadline = $this->http->now() + $timeout;

        while (true) {
            $job = $this->get($id);
            if ($job['status'] === 'succeeded' || $job['status'] === 'failed') {
                return $job;
            }
            $remaining = $deadline - $this->http->now();
            if ($remaining <= 0) {
                throw new PageRushException(0, ErrorCode::WAIT_TIMEOUT, null, sprintf('Job %s is still %s after %s seconds', $id, $job['status'], $timeout));
            }
            $this->http->sleep(min($interval, $remaining));
        }
    }

    /**
     * Downloads a succeeded job's PDF and returns its bytes. The API key is not sent to the
     * signed `download_url`.
     *
     * @throws PageRushException Code `FILE_EXPIRED` when the job has no `download_url` (not finished,
     *                           failed, deleted after download, or past the 24-hour retention).
     */
    public function download(string $id): string
    {
        $job = $this->get($id);
        $url = $job['download_url'];
        if ($url === null || $url === '') {
            throw new PageRushException(0, ErrorCode::FILE_EXPIRED, null, sprintf('Job %s has no PDF to download (status: %s)', $id, $job['status']));
        }

        return $this->http->send('GET', $url, null, RetryPolicy::Idempotent, ['Accept' => 'application/pdf'], false)->body;
    }
}
