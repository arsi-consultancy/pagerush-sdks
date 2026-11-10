<?php

/*
 * A tiny HTTP server for CurlTransportTest. It prints its port, then for each connection saves the
 * raw request to argv[2] and replies with the raw bytes in argv[1]. A reply that starts with
 * "DELAY <seconds>\n" waits that long first.
 */

declare(strict_types=1);

[, $responseFile, $requestFile] = $argv;

$server = stream_socket_server('tcp://127.0.0.1:0', $errno, $error);
if ($server === false) {
    fwrite(STDERR, "listen failed: $error\n");
    exit(1);
}
$name = (string) stream_socket_get_name($server, false);
echo substr($name, strrpos($name, ':') + 1), "\n";

while (true) {
    $connection = @stream_socket_accept($server, 60);
    if ($connection === false) {
        exit(0);
    }
    $request = '';
    while (!str_contains($request, "\r\n\r\n") && !feof($connection)) {
        $request .= (string) fread($connection, 8192);
    }
    [$head, $body] = explode("\r\n\r\n", $request, 2) + [1 => ''];
    if (preg_match('/^content-length:\s*(\d+)/mi', $head, $m) === 1) {
        while (strlen($body) < (int) $m[1] && !feof($connection)) {
            $body .= (string) fread($connection, 8192);
        }
    }
    file_put_contents($requestFile, $head . "\r\n\r\n" . $body);

    $response = (string) file_get_contents($responseFile);
    if (preg_match('/^DELAY ([\d.]+)\n/', $response, $m) === 1) {
        usleep((int) ((float) $m[1] * 1_000_000));
        $response = substr($response, strlen($m[0]));
    }
    @fwrite($connection, $response);
    fclose($connection);
}
