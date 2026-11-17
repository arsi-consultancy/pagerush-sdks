using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json.Serialization;

namespace PageRush;

/// <summary>
/// Thrown for every non-2xx API response, for 2xx responses the SDK can't use
/// (<see cref="ErrorCodes.InvalidResponse"/>), for problems found before a request is sent
/// (<see cref="ErrorCodes.ValidationFailed"/>, status 0) and for wait timeouts
/// (<see cref="ErrorCodes.WaitTimeout"/>, status 0). Connection failures throw the subclass
/// <see cref="PageRushConnectionException"/>.
/// </summary>
/// <example>
/// <code>
/// try
/// {
///     await client.Templates.RenderAsync(templateId, new RenderOptions { Data = data });
/// }
/// catch (PageRushException e) when (e.Code == ErrorCodes.TemplateCompileFailed)
/// {
///     foreach (var d in e.Errors) Console.Error.WriteLine($"{d.Path}: {d.Message}");
/// }
/// </code>
/// </example>
public class PageRushException : Exception
{
    /// <summary>Creates an error with the given code and fields. The message is <c>"&lt;code&gt;: &lt;detail or title&gt;"</c>, or <c>"&lt;code&gt;: HTTP &lt;status&gt;"</c>.</summary>
    /// <param name="status">The HTTP status, or 0 when there was no response.</param>
    /// <param name="code">The error code, such as <see cref="ErrorCodes.TemplateNotFound"/>.</param>
    /// <param name="title">Short summary, or null.</param>
    /// <param name="detail">Explanation of this occurrence, or null.</param>
    /// <param name="errors">Field problems or Typst diagnostics; null means none.</param>
    /// <param name="requestId">The request id for support, or null.</param>
    /// <param name="retryAfter">The Retry-After delay, or null.</param>
    /// <param name="type">The problem type URI, or null.</param>
    /// <param name="innerException">The underlying cause, or null.</param>
    public PageRushException(
        int status,
        string code,
        string? title = null,
        string? detail = null,
        IReadOnlyList<FieldError>? errors = null,
        string? requestId = null,
        TimeSpan? retryAfter = null,
        string? type = null,
        Exception? innerException = null)
        : base(FormatMessage(status, code, title, detail), innerException)
    {
        Status = status;
        Code = code ?? ErrorCodes.Unknown;
        Title = title;
        Detail = detail;
        Errors = errors ?? Array.Empty<FieldError>();
        RequestId = requestId;
        RetryAfter = retryAfter;
        Type = type;
    }

    /// <summary>The HTTP status, or 0 for connection errors, timeouts and errors raised before sending.</summary>
    public int Status { get; }

    /// <summary>The machine-readable error code, such as <c>TEMPLATE_NOT_FOUND</c>. See <see cref="ErrorCodes"/>.</summary>
    public string Code { get; }

    /// <summary>The short, human-readable summary from the response, or null.</summary>
    public string? Title { get; }

    /// <summary>The explanation of this occurrence of the error, or null.</summary>
    public string? Detail { get; }

    /// <summary>Field problems or, for <see cref="ErrorCodes.TemplateCompileFailed"/>, Typst diagnostics. May be empty.</summary>
    public IReadOnlyList<FieldError> Errors { get; }

    /// <summary>Identifies the request for PageRush support: the body's <c>request_id</c>, else the <c>X-Request-Id</c> header, else null.</summary>
    public string? RequestId { get; }

    /// <summary>The <c>Retry-After</c> header as a delay, or null when the response had none (or it could not be parsed).</summary>
    public TimeSpan? RetryAfter { get; }

    /// <summary>The problem type URI from the response, or null.</summary>
    public string? Type { get; }

    private static string FormatMessage(int status, string? code, string? title, string? detail)
    {
        var text = !string.IsNullOrEmpty(detail) ? detail
            : !string.IsNullOrEmpty(title) ? title
            : "HTTP " + status.ToString(CultureInfo.InvariantCulture);
        return (code ?? ErrorCodes.Unknown) + ": " + text;
    }
}

/// <summary>
/// Thrown when a request got no usable response: the connection failed (<see cref="ErrorCodes.ConnectionError"/>)
/// or an attempt took longer than the timeout (<see cref="ErrorCodes.Timeout"/>). <see cref="PageRushException.Status"/> is 0.
/// The original exception is in <see cref="Exception.InnerException"/>.
/// </summary>
public class PageRushConnectionException : PageRushException
{
    /// <summary>Creates a connection error.</summary>
    /// <param name="code"><see cref="ErrorCodes.ConnectionError"/> or <see cref="ErrorCodes.Timeout"/>.</param>
    /// <param name="detail">What went wrong.</param>
    /// <param name="innerException">The transport's exception, or null.</param>
    public PageRushConnectionException(string code, string detail, Exception? innerException = null)
        : base(0, code, detail: detail, innerException: innerException)
    {
    }
}

/// <summary>One entry of <see cref="PageRushException.Errors"/>: a problem with the field at <see cref="Path"/>, or a Typst diagnostic.</summary>
public sealed class FieldError
{
    /// <summary>The field, or for Typst diagnostics the file and position.</summary>
    [JsonPropertyName("path")]
    public string? Path { get; init; }

    /// <summary>What is wrong.</summary>
    [JsonPropertyName("message")]
    public string? Message { get; init; }
}
