<?php

declare(strict_types=1);

namespace PageRush\Exception;

/**
 * Thrown when a method is called in a state where it cannot work, e.g. saving a queued render.
 */
final class LogicException extends \LogicException implements PageRushExceptionInterface
{
}
