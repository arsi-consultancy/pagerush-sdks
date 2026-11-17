using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace PageRush;

/// <summary>
/// The outcome of <see cref="TemplatesResource.RenderAsync"/>. When <see cref="Queued"/> is false,
/// <see cref="Pdf"/> holds the document. When it is true, the render was accepted as a job: use
/// <see cref="JobsResource.WaitAsync"/> and <see cref="JobsResource.DownloadAsync"/>, or wait for the callback.
/// </summary>
public sealed class RenderResult
{
    /// <summary>True when the render was queued as a job (HTTP 202) instead of returning the PDF.</summary>
    public bool Queued { get; init; }

    /// <summary>The PDF, or null when queued.</summary>
    public byte[]? Pdf { get; init; }

    /// <summary>The queued job, or null when not queued.</summary>
    public Job? Job { get; init; }

    /// <summary>The <c>X-Job-Id</c> header, or the queued job's id.</summary>
    public string? JobId { get; init; }

    /// <summary>The version that was (or will be) rendered: the <c>X-Template-Version</c> header, or the queued job's version.</summary>
    public int? TemplateVersion { get; init; }

    /// <summary>The <c>X-Render-Engine</c> header, or null.</summary>
    public string? Engine { get; init; }

    /// <summary>The <c>X-Page-Count</c> header, or null.</summary>
    public int? PageCount { get; init; }

    /// <summary>The number of Typst warnings (<c>X-Template-Warnings</c>), or null.</summary>
    public int? Warnings { get; init; }

    /// <summary>The <c>X-Request-Id</c> header, or null.</summary>
    public string? RequestId { get; init; }

    /// <summary>The file name from Content-Disposition, or null.</summary>
    public string? Filename { get; init; }

    /// <summary>Writes the PDF to <paramref name="path"/>, replacing any existing file.</summary>
    /// <param name="path">The file to write.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes when the file is written.</returns>
    /// <exception cref="InvalidOperationException">The render was queued, so there is no PDF.</exception>
    public async Task SaveAsync(string path, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(path, nameof(path));

        if (Queued || Pdf is null)
        {
            throw new InvalidOperationException("The render was queued, so this result has no PDF. Use Jobs.WaitAsync and Jobs.DownloadAsync.");
        }

        using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true);
#if NET
        await file.WriteAsync(Pdf.AsMemory(), cancellationToken).ConfigureAwait(false);
#else
        await file.WriteAsync(Pdf, 0, Pdf.Length, cancellationToken).ConfigureAwait(false);
#endif
    }
}
