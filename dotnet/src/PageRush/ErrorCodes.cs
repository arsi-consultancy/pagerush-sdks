namespace PageRush;

/// <summary>
/// Error codes found in <see cref="PageRushException.Code"/>: the API's codes plus the SDK's own
/// (<see cref="Unknown"/>, <see cref="ConnectionError"/>, <see cref="Timeout"/>, <see cref="WaitTimeout"/>
/// and <see cref="InvalidResponse"/>). Codes are plain strings and the API may add new ones, so compare
/// against these constants rather than assuming the list is complete.
/// </summary>
public static class ErrorCodes
{
    /// <summary>The request was invalid. Also raised by the SDK, with status 0, before sending a request it can't send.</summary>
    public const string ValidationFailed = "VALIDATION_FAILED";
    /// <summary>No API key was sent.</summary>
    public const string Unauthenticated = "UNAUTHENTICATED";
    /// <summary>The API key is not valid.</summary>
    public const string InvalidApiKey = "INVALID_API_KEY";
    /// <summary>The key may not do this.</summary>
    public const string Forbidden = "FORBIDDEN";
    /// <summary>The project is disabled.</summary>
    public const string ProjectDisabled = "PROJECT_DISABLED";
    /// <summary>The project's quota and credits can't cover the request.</summary>
    public const string QuotaExceeded = "QUOTA_EXCEEDED";
    /// <summary>Too many unfinished requests of this kind, such as a fourth unfinished batch.</summary>
    public const string ConcurrencyLimit = "CONCURRENCY_LIMIT";
    /// <summary>Too many requests; see <see cref="PageRushException.RetryAfter"/>.</summary>
    public const string RateLimited = "RATE_LIMITED";
    /// <summary>
    /// On a render, the API is still rendering an earlier attempt with the same idempotency key (retried
    /// automatically). On <see cref="BatchesResource.DownloadZipAsync"/>, the batch is still rendering.
    /// </summary>
    public const string Conflict = "CONFLICT";
    /// <summary>The idempotency key was used before with a different request. Never retried.</summary>
    public const string IdempotencyConflict = "IDEMPOTENCY_CONFLICT";
    /// <summary>The request body is too large.</summary>
    public const string RequestTooLarge = "REQUEST_TOO_LARGE";
    /// <summary>The callback URL is not allowed.</summary>
    public const string UrlNotAllowed = "URL_NOT_ALLOWED";
    /// <summary>The template does not exist.</summary>
    public const string TemplateNotFound = "TEMPLATE_NOT_FOUND";
    /// <summary>The template has no live version.</summary>
    public const string TemplateNotPublished = "TEMPLATE_NOT_PUBLISHED";
    /// <summary>The render data is not valid.</summary>
    public const string InvalidData = "INVALID_DATA";
    /// <summary>Typst could not compile the template; <see cref="PageRushException.Errors"/> holds its diagnostics.</summary>
    public const string TemplateCompileFailed = "TEMPLATE_COMPILE_FAILED";
    /// <summary>The render took too long.</summary>
    public const string RenderTimeout = "RENDER_TIMEOUT";
    /// <summary>The render failed.</summary>
    public const string RenderFailed = "RENDER_FAILED";
    /// <summary>No render capacity right now.</summary>
    public const string RenderUnavailable = "RENDER_UNAVAILABLE";
    /// <summary>The output was not valid.</summary>
    public const string InvalidOutput = "INVALID_OUTPUT";
    /// <summary>The file is no longer available (expired, or deleted after download).</summary>
    public const string FileExpired = "FILE_EXPIRED";
    /// <summary>The resource does not exist.</summary>
    public const string NotFound = "NOT_FOUND";
    /// <summary>An error on the server.</summary>
    public const string Internal = "INTERNAL";

    /// <summary>An error response had no problem+json code.</summary>
    public const string Unknown = "UNKNOWN";
    /// <summary>The request could not be sent or the response could not be read (status 0).</summary>
    public const string ConnectionError = "CONNECTION_ERROR";
    /// <summary>An HTTP attempt took longer than the configured timeout (status 0).</summary>
    public const string Timeout = "TIMEOUT";
    /// <summary>A job or batch had not finished when its wait timeout passed (status 0).</summary>
    public const string WaitTimeout = "WAIT_TIMEOUT";
    /// <summary>A 2xx response could not be used, for example a body that is not the expected JSON (status is the real status).</summary>
    public const string InvalidResponse = "INVALID_RESPONSE";
}
