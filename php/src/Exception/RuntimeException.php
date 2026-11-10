<?php

declare(strict_types=1);

namespace PageRush\Exception;

/**
 * Thrown when a local operation fails, e.g. reading or writing a file.
 */
final class RuntimeException extends \RuntimeException implements PageRushExceptionInterface
{
}
