using System;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace PageRush;

/// <summary>
/// Calls the PageRush HTTP API (v1). Create one and reuse it; it is safe for concurrent use.
/// </summary>
/// <example>
/// <code>
/// using PageRush;
///
/// var client = new PageRushClient(); // reads PAGERUSH_API_KEY
/// var result = await client.Templates.RenderAsync("tpl_123", new RenderOptions
/// {
///     Data = new { name = "Ada" },
/// });
/// await result.SaveAsync("invoice.pdf");
/// </code>
/// </example>
public sealed partial class PageRushClient
{
    /// <summary>The API host used when neither <see cref="PageRushClientOptions.BaseUrl"/> nor <c>PAGERUSH_BASE_URL</c> is set.</summary>
    public const string DefaultBaseUrl = "https://pagerush.dev";

    private static readonly Lazy<HttpClient> _sharedHttpClient = new(CreateDefaultHttpClient, LazyThreadSafetyMode.ExecutionAndPublication);

    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    private readonly string _apiKey;

    /// <summary>Creates a client that reads its API key from the <c>PAGERUSH_API_KEY</c> environment variable.</summary>
    /// <exception cref="ArgumentException">There is no API key, or it contains spaces, control characters or non-ASCII characters.</exception>
    public PageRushClient()
        : this(new PageRushClientOptions())
    {
    }

    /// <summary>Creates a client for the given project API key.</summary>
    /// <param name="apiKey">The project API key (<c>pk_live_...</c>). When null or empty, <c>PAGERUSH_API_KEY</c> is used.</param>
    /// <exception cref="ArgumentException">There is no API key, or it contains spaces, control characters or non-ASCII characters.</exception>
    public PageRushClient(string? apiKey)
        : this(new PageRushClientOptions { ApiKey = apiKey })
    {
    }

    /// <summary>Creates a client with the given options.</summary>
    /// <param name="options">The client settings.</param>
    /// <exception cref="ArgumentException">
    /// There is no API key, the key contains spaces, control characters or non-ASCII characters, or the base URL
    /// is not an absolute http or https URL. Messages never include the key.
    /// </exception>
    public PageRushClient(PageRushClientOptions options)
    {
        Guard.NotNull(options, nameof(options));

        var key = options.ApiKey?.Trim();
        if (string.IsNullOrEmpty(key))
        {
            key = Environment.GetEnvironmentVariable("PAGERUSH_API_KEY")?.Trim();
        }

        if (string.IsNullOrEmpty(key))
        {
            throw new ArgumentException("Missing API key: set PageRushClientOptions.ApiKey or the PAGERUSH_API_KEY environment variable.", nameof(options));
        }

        foreach (var c in key!)
        {
            if (c <= ' ' || c >= '\x7f')
            {
                throw new ArgumentException("Invalid API key: it contains spaces, control characters or non-ASCII characters.", nameof(options));
            }
        }

        var baseUrl = options.BaseUrl;
        if (string.IsNullOrEmpty(baseUrl))
        {
            baseUrl = Environment.GetEnvironmentVariable("PAGERUSH_BASE_URL");
        }

        if (string.IsNullOrEmpty(baseUrl))
        {
            baseUrl = DefaultBaseUrl;
        }

        baseUrl = baseUrl!.TrimEnd('/');
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var parsed)
            || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrEmpty(parsed.Host))
        {
            throw new ArgumentException($"Invalid base URL \"{baseUrl}\": want an absolute http or https URL.", nameof(options));
        }

        _apiKey = key;
        BaseUrl = baseUrl;
        var timeout = options.Timeout <= TimeSpan.Zero ? System.Threading.Timeout.InfiniteTimeSpan : options.Timeout;
        Transport = new Transport(
            options.HttpClient ?? _sharedHttpClient.Value,
            key,
            baseUrl,
            timeout,
            Math.Max(0, options.MaxRetries),
            options.Sleep ?? DefaultSleep);
        Templates = new TemplatesResource(Transport);
        Jobs = new JobsResource(Transport);
        Batches = new BatchesResource(Transport);
    }

    /// <summary>The API base URL the client sends requests to, without a trailing slash.</summary>
    public string BaseUrl { get; }

    /// <summary>Manages templates and renders them to PDF.</summary>
    public TemplatesResource Templates { get; }

    /// <summary>Reads render jobs, waits for them and downloads their PDFs.</summary>
    public JobsResource Jobs { get; }

    /// <summary>Renders many PDFs from one template and downloads them as a ZIP.</summary>
    public BatchesResource Batches { get; }

    internal Transport Transport { get; }

    /// <summary>Describes the client without its API key.</summary>
    /// <returns>A description such as <c>PageRushClient { BaseUrl = https://pagerush.dev, ApiKey = [redacted] }</c>.</returns>
    public override string ToString() => $"PageRushClient {{ BaseUrl = {BaseUrl}, ApiKey = [redacted] }}";

    private static Task DefaultSleep(TimeSpan delay, CancellationToken cancellationToken) =>
        delay <= TimeSpan.Zero ? Task.CompletedTask : Task.Delay(delay, cancellationToken);

    private static HttpClient CreateDefaultHttpClient()
    {
#if NET
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
#else
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        };
#endif
        return new HttpClient(handler, disposeHandler: true) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
    }
}
