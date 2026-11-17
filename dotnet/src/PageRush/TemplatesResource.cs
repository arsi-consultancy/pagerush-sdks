using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace PageRush;

/// <summary>Manages templates and renders them. Use it as <see cref="PageRushClient.Templates"/>.</summary>
public sealed class TemplatesResource
{
    private static readonly HttpMethod _patch = new("PATCH");
    private static readonly Regex _filenamePattern = new(
        @"(?:^|;)\s*filename\s*=\s*(?:""(?<q>[^""]*)""|(?<t>[^;\s]+))",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly Transport _transport;

    internal TemplatesResource(Transport transport) => _transport = transport;

    /// <summary>Returns one page of the project's templates, newest first.</summary>
    /// <param name="options">Page size and cursor, or null.</param>
    /// <param name="cancellationToken">Cancels the call, including any retry wait.</param>
    /// <returns>The page, with <see cref="TemplateList.NextCursor"/> for the next one.</returns>
    /// <exception cref="PageRushException">The API returned an error or the request failed.</exception>
    public Task<TemplateList> ListAsync(ListTemplatesOptions? options = null, CancellationToken cancellationToken = default)
    {
        var query = new List<string>();
        if (options?.Limit is { } limit)
        {
            query.Add("limit=" + limit.ToString(CultureInfo.InvariantCulture));
        }

        if (options?.Cursor is { } cursor)
        {
            query.Add("cursor=" + Uri.EscapeDataString(cursor));
        }

        var url = _transport.Url("/v1/templates") + (query.Count > 0 ? "?" + string.Join("&", query) : "");
        return _transport.SendJsonAsync<TemplateList>(new ApiRequest { Url = url, Policy = RetryPolicy.Safe }, _ => true, cancellationToken);
    }

    /// <summary>Iterates over every template, fetching pages as needed.</summary>
    /// <param name="options"><see cref="ListTemplatesOptions.Limit"/> sets the page size; <see cref="ListTemplatesOptions.Cursor"/> a starting point. May be null.</param>
    /// <param name="cancellationToken">Cancels the iteration.</param>
    /// <returns>An async sequence of templates.</returns>
    /// <example>
    /// <code>
    /// await foreach (var t in client.Templates.ListAllAsync())
    /// {
    ///     Console.WriteLine($"{t.Id} {t.Name}");
    /// }
    /// </code>
    /// </example>
    public async IAsyncEnumerable<Template> ListAllAsync(ListTemplatesOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var cursor = options?.Cursor;
        while (true)
        {
            var page = await ListAsync(new ListTemplatesOptions { Limit = options?.Limit, Cursor = cursor }, cancellationToken).ConfigureAwait(false);
            foreach (var template in page.Items)
            {
                yield return template;
            }

            if (page.NextCursor is null)
            {
                yield break;
            }

            cursor = page.NextCursor;
        }
    }

    /// <summary>Creates a template. Its version 1 goes live. Retried only on 429 and 503.</summary>
    /// <param name="options">The template's name and source or document, files and sample data.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The template and its first version.</returns>
    /// <exception cref="PageRushException">The API returned an error (for example <see cref="ErrorCodes.TemplateCompileFailed"/>) or the request failed.</exception>
    public Task<TemplateWithVersion> CreateAsync(CreateTemplateOptions options, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(options, nameof(options));

        return _transport.SendJsonAsync<TemplateWithVersion>(
            new ApiRequest { Method = HttpMethod.Post, Url = _transport.Url("/v1/templates"), Body = Transport.Encode(options), Policy = RetryPolicy.Unsafe },
            v => v.Template != null && v.Version != null,
            cancellationToken);
    }

    /// <summary>Returns a template with its version history (<see cref="Template.Versions"/>).</summary>
    /// <param name="id">The template id.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The template.</returns>
    /// <exception cref="PageRushException">For example <see cref="ErrorCodes.TemplateNotFound"/>.</exception>
    public async Task<Template> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        var path = TemplatePath(id);
        var envelope = await _transport.SendJsonAsync<TemplateEnvelope>(
            new ApiRequest { Url = _transport.Url(path), Policy = RetryPolicy.Safe }, e => e.Template != null, cancellationToken).ConfigureAwait(false);
        return envelope.Template!;
    }

    /// <summary>Changes a template's name. Retried only on 429 and 503.</summary>
    /// <param name="id">The template id.</param>
    /// <param name="name">The new name.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The updated template.</returns>
    /// <exception cref="PageRushException">The API returned an error or the request failed.</exception>
    public async Task<Template> RenameAsync(string id, string name, CancellationToken cancellationToken = default)
    {
        var path = TemplatePath(id);
        Guard.NotNull(name, nameof(name));

        var envelope = await _transport.SendJsonAsync<TemplateEnvelope>(
            new ApiRequest { Method = _patch, Url = _transport.Url(path), Body = Transport.Encode(new Dictionary<string, string> { ["name"] = name }), Policy = RetryPolicy.Unsafe },
            e => e.Template != null,
            cancellationToken).ConfigureAwait(false);
        return envelope.Template!;
    }

    /// <summary>Deletes a template and all its versions.</summary>
    /// <param name="id">The template id.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes when the template is deleted.</returns>
    /// <exception cref="PageRushException">The API returned an error or the request failed.</exception>
    public Task DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        var path = TemplatePath(id);
        return _transport.SendAsync(new ApiRequest { Method = HttpMethod.Delete, Url = _transport.Url(path), Policy = RetryPolicy.Safe }, cancellationToken);
    }

    /// <summary>Adds a version to a template. It goes live unless <see cref="CreateVersionOptions.Publish"/> is false. Retried only on 429 and 503.</summary>
    /// <param name="id">The template id.</param>
    /// <param name="options">The version's source or document, files and sample data.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The template and the new version.</returns>
    /// <exception cref="PageRushException">The API returned an error or the request failed.</exception>
    public Task<TemplateWithVersion> CreateVersionAsync(string id, CreateVersionOptions options, CancellationToken cancellationToken = default)
    {
        var path = TemplatePath(id) + "/versions";
        Guard.NotNull(options, nameof(options));

        return _transport.SendJsonAsync<TemplateWithVersion>(
            new ApiRequest { Method = HttpMethod.Post, Url = _transport.Url(path), Body = Transport.Encode(options), Policy = RetryPolicy.Unsafe },
            v => v.Template != null && v.Version != null,
            cancellationToken);
    }

    /// <summary>Returns one version's source, files and sample data.</summary>
    /// <param name="id">The template id.</param>
    /// <param name="version">The version number.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The version.</returns>
    /// <exception cref="PageRushException">The API returned an error or the request failed.</exception>
    public async Task<TemplateVersion> GetVersionAsync(string id, int version, CancellationToken cancellationToken = default)
    {
        var path = TemplatePath(id) + "/versions/" + version.ToString(CultureInfo.InvariantCulture);
        var envelope = await _transport.SendJsonAsync<VersionEnvelope>(
            new ApiRequest { Url = _transport.Url(path), Policy = RetryPolicy.Safe }, e => e.Version != null, cancellationToken).ConfigureAwait(false);
        return envelope.Version!;
    }

    /// <summary>Makes a version the live one. Publishing an older version is how you roll back. Retried only on 429 and 503.</summary>
    /// <param name="id">The template id.</param>
    /// <param name="version">The version to make live.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The updated template.</returns>
    /// <exception cref="PageRushException">The API returned an error or the request failed.</exception>
    public async Task<Template> PublishAsync(string id, int version, CancellationToken cancellationToken = default)
    {
        var path = TemplatePath(id) + "/publish";
        var envelope = await _transport.SendJsonAsync<TemplateEnvelope>(
            new ApiRequest { Method = HttpMethod.Post, Url = _transport.Url(path), Body = Transport.Encode(new Dictionary<string, int> { ["version"] = version }), Policy = RetryPolicy.Unsafe },
            e => e.Template != null,
            cancellationToken).ConfigureAwait(false);
        return envelope.Template!;
    }

    /// <summary>
    /// Renders a template with data. Without <see cref="RenderOptions.CallbackUrl"/> the PDF comes back in the
    /// result. With a callback URL, or with <see cref="RenderOptions.PreferAsync"/> when rendering is
    /// overloaded, the result is queued.
    /// </summary>
    /// <remarks>
    /// Retried on connection errors, timeouts, 429, 502, 503, 504 and 409 <c>CONFLICT</c> (an earlier attempt
    /// with the same key is still rendering), always with the same idempotency key. Never retried on
    /// <c>IDEMPOTENCY_CONFLICT</c>.
    /// </remarks>
    /// <param name="id">The template id.</param>
    /// <param name="options">The data and render options.</param>
    /// <param name="cancellationToken">Cancels the call, including any retry wait.</param>
    /// <returns>The PDF and its response headers, or the queued job.</returns>
    /// <exception cref="PageRushException">The API returned an error, the request failed, or the idempotency key or body was invalid (<see cref="ErrorCodes.ValidationFailed"/>, status 0).</exception>
    public async Task<RenderResult> RenderAsync(string id, RenderOptions options, CancellationToken cancellationToken = default)
    {
        var path = TemplatePath(id) + "/render";
        Guard.NotNull(options, nameof(options));

        var key = Transport.ResolveIdempotencyKey(options.IdempotencyKey);
        var response = await _transport.SendAsync(
            new ApiRequest
            {
                Method = HttpMethod.Post,
                Url = _transport.Url(path),
                Body = Transport.Encode(options),
                Accept = "application/pdf, application/json",
                IdempotencyKey = key,
                PreferAsync = options.PreferAsync,
                Policy = RetryPolicy.Render,
            },
            cancellationToken).ConfigureAwait(false);

        if (response.Status == 202)
        {
            var job = Transport.ReadJson<Job>(response, j => !string.IsNullOrEmpty(j.Id));
            return new RenderResult { Queued = true, Job = job, JobId = job.Id, TemplateVersion = job.TemplateVersion };
        }

        if (response.MediaType() != "application/pdf")
        {
            throw Transport.InvalidResponse(response.Status, $"The render response has Content-Type \"{response.Header("Content-Type")}\", want application/pdf.");
        }

        return new RenderResult
        {
            Pdf = response.Body,
            JobId = response.Header("X-Job-Id"),
            TemplateVersion = HeaderInt(response, "X-Template-Version"),
            Engine = response.Header("X-Render-Engine"),
            PageCount = HeaderInt(response, "X-Page-Count"),
            Warnings = HeaderInt(response, "X-Template-Warnings"),
            RequestId = response.Header("X-Request-Id"),
            Filename = ContentDispositionFilename(response.Header("Content-Disposition")),
        };
    }

    internal static string TemplatePath(string id) => "/v1/templates/" + Transport.Segment(id, nameof(id));

    internal static string? ContentDispositionFilename(string? header)
    {
        if (string.IsNullOrEmpty(header))
        {
            return null;
        }

        var m = _filenamePattern.Match(header);
        if (!m.Success)
        {
            return null;
        }

        return m.Groups["q"].Success ? m.Groups["q"].Value : m.Groups["t"].Value;
    }

    private static int? HeaderInt(ApiResponse response, string name) =>
        int.TryParse(response.Header(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;

    private sealed class TemplateEnvelope
    {
        [JsonPropertyName("template")]
        public Template? Template { get; init; }
    }

    private sealed class VersionEnvelope
    {
        [JsonPropertyName("version")]
        public TemplateVersion? Version { get; init; }
    }
}
