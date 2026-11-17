using System;
using System.Net.Http;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace PageRush;

/// <summary>Settings for <see cref="PageRushClient"/>. Every property is optional.</summary>
public sealed class PageRushClientOptions
{
    /// <summary>
    /// The project API key (<c>pk_live_...</c>). When null or empty, the <c>PAGERUSH_API_KEY</c> environment
    /// variable is used. Surrounding whitespace is trimmed. Never serialised or printed.
    /// </summary>
    [JsonIgnore]
    public string? ApiKey { get; set; }

    /// <summary>
    /// The API base URL. When null or empty, the <c>PAGERUSH_BASE_URL</c> environment variable is used, else
    /// <c>https://pagerush.dev</c>. A trailing slash is removed.
    /// </summary>
    public string? BaseUrl { get; set; }

    /// <summary>
    /// The limit for each HTTP attempt, including reading the response body (default 60 s). Each retry gets a
    /// fresh timeout. <see cref="System.Threading.Timeout.InfiniteTimeSpan"/> or a value of zero or less turns
    /// the limit off.
    /// </summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>How many times a failed request is retried after the first attempt (default 2, so up to 3 attempts). Negative values mean 0.</summary>
    public int MaxRetries { get; set; } = 2;

    /// <summary>
    /// The <see cref="System.Net.Http.HttpClient"/> to send requests with, for a proxy, custom handler or tests.
    /// When null, the SDK uses its own shared client. The SDK follows redirects itself so it can drop the
    /// Authorization header; give your client a handler with <c>AllowAutoRedirect = false</c> so that rule
    /// applies. Its <see cref="System.Net.Http.HttpClient.Timeout"/> also limits each attempt.
    /// </summary>
    [JsonIgnore]
    public HttpClient? HttpClient { get; set; }

    /// <summary>
    /// Waits before a retry and between polls. The default is <see cref="Task.Delay(TimeSpan, CancellationToken)"/>.
    /// Replace it in tests to run without real delays. It must stop when its token is cancelled.
    /// </summary>
    [JsonIgnore]
    public Func<TimeSpan, CancellationToken, Task>? Sleep { get; set; }

    /// <summary>Describes the options without the API key.</summary>
    /// <returns>A description such as <c>PageRushClientOptions { BaseUrl = ..., ApiKey = [redacted] }</c>.</returns>
    public override string ToString() =>
        $"PageRushClientOptions {{ BaseUrl = {BaseUrl ?? "(default)"}, ApiKey = {(string.IsNullOrEmpty(ApiKey) ? "(not set)" : "[redacted]")}, Timeout = {Timeout}, MaxRetries = {MaxRetries} }}";
}
