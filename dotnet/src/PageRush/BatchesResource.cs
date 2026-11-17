using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace PageRush;

/// <summary>
/// Renders many PDFs from one template in a single request and downloads them as a ZIP. Use it as
/// <see cref="PageRushClient.Batches"/>.
/// </summary>
public sealed class BatchesResource
{
    /// <summary>The default total wait of <see cref="WaitAsync"/>: 600 seconds.</summary>
    public static readonly TimeSpan DefaultWaitTimeout = TimeSpan.FromSeconds(600);

    /// <summary>The default pause between polls of <see cref="WaitAsync"/>: 2 seconds.</summary>
    public static readonly TimeSpan DefaultWaitInterval = TimeSpan.FromSeconds(2);

    private readonly Transport _transport;

    internal BatchesResource(Transport transport) => _transport = transport;

    /// <summary>
    /// Starts a batch render of the template, one PDF per item, and returns the queued batch. Wait for it with
    /// <see cref="WaitAsync"/>, or set <see cref="CreateBatchOptions.CallbackUrl"/>.
    /// </summary>
    /// <remarks>
    /// Always sends an <c>Idempotency-Key</c> and reuses it on every retry. Retried on connection errors,
    /// timeouts, 429, 502, 503 and 504; never on <c>IDEMPOTENCY_CONFLICT</c>. The SDK doesn't split large lists.
    /// </remarks>
    /// <param name="templateId">The template id.</param>
    /// <param name="options">The items and batch options.</param>
    /// <param name="cancellationToken">Cancels the call, including any retry wait.</param>
    /// <returns>The queued batch.</returns>
    /// <exception cref="PageRushException">For example <see cref="ErrorCodes.QuotaExceeded"/> (402) or <see cref="ErrorCodes.ConcurrencyLimit"/> (429, a fourth unfinished batch).</exception>
    public Task<Batch> CreateAsync(string templateId, CreateBatchOptions options, CancellationToken cancellationToken = default)
    {
        var path = "/v1/templates/" + Transport.Segment(templateId, nameof(templateId)) + "/batches";
        Guard.NotNull(options, nameof(options));

        var key = Transport.ResolveIdempotencyKey(options.IdempotencyKey);
        return _transport.SendJsonAsync<Batch>(
            new ApiRequest { Method = HttpMethod.Post, Url = _transport.Url(path), Body = Transport.Encode(options), IdempotencyKey = key, Policy = RetryPolicy.Safe },
            b => !string.IsNullOrEmpty(b.Id),
            cancellationToken);
    }

    /// <summary>Returns a batch with the state of each item (<see cref="Batch.Items"/>).</summary>
    /// <param name="id">The batch id.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The batch.</returns>
    /// <exception cref="PageRushException">For example <see cref="ErrorCodes.NotFound"/>.</exception>
    public Task<Batch> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        var path = "/v1/batches/" + Transport.Segment(id, nameof(id));
        return _transport.SendJsonAsync<Batch>(new ApiRequest { Url = _transport.Url(path), Policy = RetryPolicy.Safe }, b => !string.IsNullOrEmpty(b.Id), cancellationToken);
    }

    /// <summary>
    /// Polls the batch until its status is <c>succeeded</c>, <c>partial</c> or <c>failed</c> and returns it.
    /// A partial or failed batch is returned, not thrown: check <see cref="Batch.Status"/> and each item's error.
    /// </summary>
    /// <param name="id">The batch id.</param>
    /// <param name="options">Total timeout (default 600 s) and poll interval (default 2 s), or null.</param>
    /// <param name="cancellationToken">Cancels the call, including the pause between polls.</param>
    /// <returns>The finished batch, with its items.</returns>
    /// <exception cref="PageRushException">Code <see cref="ErrorCodes.WaitTimeout"/> (status 0) if the batch is still running after the timeout, or any error from <see cref="GetAsync"/>.</exception>
    public Task<Batch> WaitAsync(string id, WaitOptions? options = null, CancellationToken cancellationToken = default)
    {
        Transport.Segment(id, nameof(id));
        return _transport.PollAsync(
            ct => GetAsync(id, ct),
            b => b.IsDone,
            options,
            DefaultWaitTimeout,
            DefaultWaitInterval,
            (b, timeout) => new PageRushException(0, ErrorCodes.WaitTimeout, detail: $"Batch {id} did not finish within {JobsResource.Seconds(timeout)} (status {b.Status})."),
            cancellationToken);
    }

    /// <summary>
    /// Downloads the ZIP of the batch's rendered PDFs. Unlike a job's <c>download_url</c>, this sends your API key.
    /// </summary>
    /// <param name="id">The batch id.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The ZIP bytes.</returns>
    /// <exception cref="PageRushException">
    /// 409 <see cref="ErrorCodes.Conflict"/> while the batch is still rendering; 410 <see cref="ErrorCodes.FileExpired"/>
    /// once no PDFs are left; 429 <see cref="ErrorCodes.RateLimited"/> once the batch's ZIP downloads are used up
    /// (single PDFs stay available through <see cref="JobsResource.DownloadAsync"/>); <see cref="ErrorCodes.InvalidResponse"/>
    /// for a 200 that is not <c>application/zip</c>.
    /// </exception>
    public async Task<byte[]> DownloadZipAsync(string id, CancellationToken cancellationToken = default)
    {
        var path = "/v1/batches/" + Transport.Segment(id, nameof(id)) + "/zip";
        var response = await _transport.SendAsync(
            new ApiRequest { Url = _transport.Url(path), Accept = "application/zip", Policy = RetryPolicy.Safe },
            cancellationToken).ConfigureAwait(false);
        if (response.MediaType() != "application/zip")
        {
            throw Transport.InvalidResponse(response.Status, $"The batch ZIP response has Content-Type \"{response.Header("Content-Type")}\", want application/zip.");
        }

        return response.Body;
    }
}
