<?php

declare(strict_types=1);

namespace PageRush\Transport;

use PageRush\ErrorCode;
use PageRush\Exception\ConnectionException;
use PageRush\Exception\InvalidArgumentException;

/**
 * The default {@see Transport}, built on ext-curl. It does not follow redirects.
 */
final class CurlTransport implements Transport
{
    public function send(Request $request): Response
    {
        $handle = curl_init();
        if ($handle === false) {
            throw new ConnectionException(ErrorCode::CONNECTION_ERROR, 'Could not initialise curl');
        }

        $headerLines = [];
        foreach ($request->headers as $name => $value) {
            $headerLines[] = $name . ': ' . $value;
        }
        // Stop curl adding "Expect: 100-continue" to larger bodies.
        $headerLines[] = 'Expect:';

        /** @var array<string, string> $responseHeaders */
        $responseHeaders = [];
        $lastHeader = null;
        $options = [
            CURLOPT_URL => $request->url,
            CURLOPT_CUSTOMREQUEST => $request->method,
            CURLOPT_HTTPHEADER => $headerLines,
            CURLOPT_RETURNTRANSFER => true,
            CURLOPT_FOLLOWLOCATION => false,
            CURLOPT_TIMEOUT_MS => max(1, (int) round($request->timeout * 1000)),
            CURLOPT_PROTOCOLS => CURLPROTO_HTTP | CURLPROTO_HTTPS,
            CURLOPT_HEADERFUNCTION => static function ($curl, string $line) use (&$responseHeaders, &$lastHeader): int {
                if (str_starts_with($line, 'HTTP/')) {
                    // A new status line (e.g. after "100 Continue") starts a new header block.
                    $responseHeaders = [];
                    $lastHeader = null;
                } elseif (($line[0] ?? '') === ' ' || ($line[0] ?? '') === "\t") {
                    // Obsolete line folding: the line continues the previous header's value.
                    if ($lastHeader !== null) {
                        $responseHeaders[$lastHeader] = trim($responseHeaders[$lastHeader] . ' ' . trim($line));
                    }
                } elseif (str_contains($line, ':')) {
                    [$name, $value] = explode(':', $line, 2);
                    $name = strtolower(trim($name));
                    $value = trim($value);
                    // Repeated headers combine into one comma-separated value (RFC 9110).
                    $responseHeaders[$name] = isset($responseHeaders[$name]) ? $responseHeaders[$name] . ', ' . $value : $value;
                    $lastHeader = $name;
                }

                return strlen($line);
            },
        ];
        if ($request->body !== null) {
            $options[CURLOPT_POSTFIELDS] = $request->body;
        }
        curl_setopt_array($handle, $options);

        $body = curl_exec($handle);
        if ($body === false || !is_string($body)) {
            $errno = curl_errno($handle);
            // Signed download URLs carry their signature in the query string: keep it out of messages.
            $message = sprintf('%s %s failed: %s', $request->method, self::withoutQuery($request->url), curl_error($handle));
            if (in_array($errno, [CURLE_UNSUPPORTED_PROTOCOL, CURLE_URL_MALFORMAT], true)) {
                // The request never left the machine, so this is not a connection error.
                throw new InvalidArgumentException($message);
            }

            throw new ConnectionException($errno === CURLE_OPERATION_TIMEDOUT ? ErrorCode::TIMEOUT : ErrorCode::CONNECTION_ERROR, $message);
        }

        $status = (int) curl_getinfo($handle, CURLINFO_RESPONSE_CODE);

        return new Response($status, $responseHeaders, $body);
    }

    private static function withoutQuery(string $url): string
    {
        return (string) preg_replace('/[?#].*$/s', '', $url);
    }
}
