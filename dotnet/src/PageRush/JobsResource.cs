using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace PageRush;

/// <summary>Reads render jobs, waits for them and downloads their PDFs. Use it as <see cref="PageRushClient.Jobs"/>.</summary>
public sealed class JobsResource
{
    /// <summary>The default total wait of <see cref="WaitAsync"/>: 120 seconds.</summary>
    public static readonly TimeSpan DefaultWaitTimeout = TimeSpan.FromSeconds(120);

    /// <summary>The default pause between polls of <see cref="WaitAsync"/>: 1 second.</summary>
    public static readonly TimeSpan DefaultWaitInterval = TimeSpan.FromSeconds(1);

    private readonly Transport _transport;

    internal JobsResource(Transport transport) => _transport = transport;

    /// <summary>Returns a job's status and, once it has succeeded, a download link.</summary>
    /// <param name="id">The job id.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The job.</returns>
    /// <exception cref="PageRushException">For example <see cref="ErrorCodes.NotFound"/>.</exception>
    public Task<Job> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        var path = "/v1/jobs/" + Transport.Segment(id, nameof(id));
        return _transport.SendJsonAsync<Job>(new ApiRequest { Url = _transport.Url(path), Policy = RetryPolicy.Safe }, j => !string.IsNullOrEmpty(j.Id), cancellationToken);
    }

    /// <summary>
    /// Polls the job until its status is <c>succeeded</c> or <c>failed</c> and returns it. A failed job is
    /// returned, not thrown: check <see cref="Job.Status"/> and <see cref="Job.Error"/>.
    /// </summary>
    /// <param name="id">The job id.</param>
    /// <param name="options">Total timeout (default 120 s) and poll interval (default 1 s), or null.</param>
    /// <param name="cancellationToken">Cancels the call, including the pause between polls.</param>
    /// <returns>The finished job.</returns>
    /// <exception cref="PageRushException">Code <see cref="ErrorCodes.WaitTimeout"/> (status 0) if the job is still running after the timeout, or any error from <see cref="GetAsync"/>.</exception>
    public Task<Job> WaitAsync(string id, WaitOptions? options = null, CancellationToken cancellationToken = default)
    {
        Transport.Segment(id, nameof(id));
        return _transport.PollAsync(
            ct => GetAsync(id, ct),
            j => j.IsDone,
            options,
            DefaultWaitTimeout,
            DefaultWaitInterval,
            (j, timeout) => new PageRushException(0, ErrorCodes.WaitTimeout, detail: $"Job {id} did not finish within {Seconds(timeout)} (status {j.Status})."),
            cancellationToken);
    }

    /// <summary>
    /// Fetches the job, then downloads its PDF from the signed <see cref="Job.DownloadUrl"/>. Your API key is
    /// not sent there.
    /// </summary>
    /// <param name="id">The job id.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The PDF bytes.</returns>
    /// <exception cref="PageRushException">
    /// Code <see cref="ErrorCodes.FileExpired"/> if the job has no download link (not finished, failed,
    /// expired, or deleted after download), or any request error. Messages never include the link's query string.
    /// </exception>
    public async Task<byte[]> DownloadAsync(string id, CancellationToken cancellationToken = default)
    {
        var job = await GetAsync(id, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(job.DownloadUrl))
        {
            throw new PageRushException(0, ErrorCodes.FileExpired, detail: $"Job {id} has no download_url (status {job.Status}): the PDF is not available.");
        }

        var target = job.DownloadUrl!;
        if (Uri.TryCreate(target, UriKind.Absolute, out var absolute) && target[0] != '/')
        {
            if (absolute.Scheme != Uri.UriSchemeHttp && absolute.Scheme != Uri.UriSchemeHttps)
            {
                throw Transport.InvalidResponse(200, "The job's download_url is not an http or https URL.");
            }
        }
        else if (Uri.TryCreate(new Uri(_transport.BaseUrl + "/"), target.TrimStart('/'), out var relative))
        {
            target = relative.ToString();
        }
        else
        {
            throw Transport.InvalidResponse(200, "The job's download_url is not a valid URL: " + Transport.StripQuery(target));
        }

        var response = await _transport.SendAsync(
            new ApiRequest { Url = target, Accept = "application/pdf", NoAuth = true, Policy = RetryPolicy.Safe },
            cancellationToken).ConfigureAwait(false);
        return response.Body;
    }

    internal static string Seconds(TimeSpan t) => t.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture) + " s";
}
