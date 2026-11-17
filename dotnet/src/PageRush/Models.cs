using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PageRush;

/// <summary>A stored Typst template. Property names follow the API's snake_case fields.</summary>
public sealed class Template
{
    /// <summary>The template id (<c>tpl_...</c>).</summary>
    [JsonPropertyName("id")]
    public string Id { get; init; } = "";

    /// <summary>The template's name.</summary>
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    /// <summary>The version renders use unless they ask for another. Null until a version is published.</summary>
    [JsonPropertyName("live_version")]
    public int? LiveVersion { get; init; }

    /// <summary>The newest version number.</summary>
    [JsonPropertyName("latest_version")]
    public int LatestVersion { get; init; }

    /// <summary>When the template was created.</summary>
    [JsonPropertyName("created_at")]
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>When the template was last changed.</summary>
    [JsonPropertyName("updated_at")]
    public DateTimeOffset UpdatedAt { get; init; }

    /// <summary>The version history, newest first. Only <see cref="TemplatesResource.GetAsync"/> fills it; otherwise null.</summary>
    [JsonPropertyName("versions")]
    public IReadOnlyList<TemplateVersionSummary>? Versions { get; init; }
}

/// <summary>One entry of <see cref="Template.Versions"/>.</summary>
public sealed class TemplateVersionSummary
{
    /// <summary>The version number.</summary>
    [JsonPropertyName("version")]
    public int Version { get; init; }

    /// <summary>Whether this is the live version.</summary>
    [JsonPropertyName("live")]
    public bool Live { get; init; }

    /// <summary>Total size of the version's files in bytes.</summary>
    [JsonPropertyName("bytes")]
    public long Bytes { get; init; }

    /// <summary>Number of files besides <c>main.typ</c>.</summary>
    [JsonPropertyName("file_count")]
    public int FileCount { get; init; }

    /// <summary>Whether the version has sample data.</summary>
    [JsonPropertyName("has_sample_data")]
    public bool HasSampleData { get; init; }

    /// <summary>The Typst release the version renders with.</summary>
    [JsonPropertyName("typst_version")]
    public string? TypstVersion { get; init; }

    /// <summary>When the version was created.</summary>
    [JsonPropertyName("created_at")]
    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>One immutable version of a template: its <c>main.typ</c> source, other files and sample data.</summary>
public sealed class TemplateVersion
{
    /// <summary>The version number.</summary>
    [JsonPropertyName("version")]
    public int Version { get; init; }

    /// <summary>The <c>main.typ</c> source.</summary>
    [JsonPropertyName("source")]
    public string? Source { get; init; }

    /// <summary>The visual-editor document the version was made from, or null.</summary>
    [JsonPropertyName("document")]
    public JsonElement? Document { get; init; }

    /// <summary>Other files of the version.</summary>
    [JsonPropertyName("files")]
    public IReadOnlyList<TemplateFile> Files { get; init; } = Array.Empty<TemplateFile>();

    /// <summary>Example data for dashboard test renders, or null.</summary>
    [JsonPropertyName("sample_data")]
    public JsonElement? SampleData { get; init; }

    /// <summary>The Typst release the version renders with.</summary>
    [JsonPropertyName("typst_version")]
    public string? TypstVersion { get; init; }

    /// <summary>When the version was created.</summary>
    [JsonPropertyName("created_at")]
    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>Returned when a template or a version is created.</summary>
public sealed class TemplateWithVersion
{
    /// <summary>The template.</summary>
    [JsonPropertyName("template")]
    public Template Template { get; init; } = null!;

    /// <summary>The version that was created.</summary>
    [JsonPropertyName("version")]
    public TemplateVersion Version { get; init; } = null!;
}

/// <summary>One page of templates, newest first.</summary>
public sealed class TemplateList
{
    /// <summary>The templates on this page.</summary>
    [JsonPropertyName("items")]
    public IReadOnlyList<Template> Items { get; init; } = Array.Empty<Template>();

    /// <summary>Pass as <see cref="ListTemplatesOptions.Cursor"/> to get the next page. Null on the last page.</summary>
    [JsonPropertyName("next_cursor")]
    public string? NextCursor { get; init; }
}

/// <summary>Values of <see cref="Job.Status"/> and <see cref="BatchItem.Status"/>.</summary>
public static class JobStatus
{
    /// <summary>Waiting to render.</summary>
    public const string Queued = "queued";
    /// <summary>Rendering.</summary>
    public const string Rendering = "rendering";
    /// <summary>Rendered; the PDF can be downloaded.</summary>
    public const string Succeeded = "succeeded";
    /// <summary>Failed; see <see cref="Job.Error"/>.</summary>
    public const string Failed = "failed";
}

/// <summary>A render job.</summary>
public sealed class Job
{
    /// <summary>The job id (<c>job_...</c>).</summary>
    [JsonPropertyName("id")]
    public string Id { get; init; } = "";

    /// <summary>One of the <see cref="JobStatus"/> values.</summary>
    [JsonPropertyName("status")]
    public string Status { get; init; } = "";

    /// <summary><c>sync</c> or <c>async</c>.</summary>
    [JsonPropertyName("mode")]
    public string? Mode { get; init; }

    /// <summary>Where the job rendered and its PDF is stored.</summary>
    [JsonPropertyName("region")]
    public string? Region { get; init; }

    /// <summary>The template id; null once the template has been deleted.</summary>
    [JsonPropertyName("template_id")]
    public string? TemplateId { get; init; }

    /// <summary>The template version that was (or will be) rendered.</summary>
    [JsonPropertyName("template_version")]
    public int? TemplateVersion { get; init; }

    /// <summary>The compiler that produced the PDF.</summary>
    [JsonPropertyName("engine")]
    public string? Engine { get; init; }

    /// <summary>The PDF's size in bytes.</summary>
    [JsonPropertyName("bytes")]
    public long? Bytes { get; init; }

    /// <summary>How long the render took, in milliseconds.</summary>
    [JsonPropertyName("duration_ms")]
    public long? DurationMs { get; init; }

    /// <summary>Why the job failed, or null.</summary>
    [JsonPropertyName("error")]
    public JobError? Error { get; init; }

    /// <summary>The state of <c>callback_url</c> delivery, or null without a callback.</summary>
    [JsonPropertyName("delivery")]
    public JobDelivery? Delivery { get; init; }

    /// <summary>The metadata passed to the render, or null.</summary>
    [JsonPropertyName("metadata")]
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }

    /// <summary>
    /// A signed link to the PDF, valid for up to an hour. Null once the file has expired or was deleted after
    /// download. <see cref="JobsResource.DownloadAsync"/> uses it.
    /// </summary>
    [JsonPropertyName("download_url")]
    public string? DownloadUrl { get; init; }

    /// <summary>Whether the PDF is deleted once it has been downloaded or delivered.</summary>
    [JsonPropertyName("delete_after_download")]
    public bool DeleteAfterDownload { get; init; }

    /// <summary>When the job was created.</summary>
    [JsonPropertyName("created_at")]
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>When the job finished, or null.</summary>
    [JsonPropertyName("completed_at")]
    public DateTimeOffset? CompletedAt { get; init; }

    /// <summary>True when <see cref="Status"/> is <c>succeeded</c> or <c>failed</c>.</summary>
    [JsonIgnore]
    public bool IsDone => Status == JobStatus.Succeeded || Status == JobStatus.Failed;
}

/// <summary>Why a job or batch item failed.</summary>
public sealed class JobError
{
    /// <summary>The error code, such as <c>TEMPLATE_COMPILE_FAILED</c>.</summary>
    [JsonPropertyName("code")]
    public string? Code { get; init; }

    /// <summary>What went wrong.</summary>
    [JsonPropertyName("message")]
    public string? Message { get; init; }
}

/// <summary>The state of <c>callback_url</c> delivery for a job or batch.</summary>
public sealed class JobDelivery
{
    /// <summary><c>pending</c>, <c>delivered</c> or <c>failed</c>.</summary>
    [JsonPropertyName("status")]
    public string? Status { get; init; }

    /// <summary>Delivery attempts so far.</summary>
    [JsonPropertyName("attempts")]
    public int Attempts { get; init; }

    /// <summary>The last delivery error, or null.</summary>
    [JsonPropertyName("error")]
    public string? Error { get; init; }
}

/// <summary>Values of <see cref="Batch.Status"/>. A batch is done once it is succeeded, partial or failed.</summary>
public static class BatchStatus
{
    /// <summary>Waiting to render.</summary>
    public const string Queued = "queued";
    /// <summary>Rendering.</summary>
    public const string Rendering = "rendering";
    /// <summary>Every item rendered.</summary>
    public const string Succeeded = "succeeded";
    /// <summary>Some items failed.</summary>
    public const string Partial = "partial";
    /// <summary>Every item failed.</summary>
    public const string Failed = "failed";
}

/// <summary>A batch render: one template rendered once per item.</summary>
public sealed class Batch
{
    /// <summary>The batch id (<c>bat_...</c>).</summary>
    [JsonPropertyName("id")]
    public string Id { get; init; } = "";

    /// <summary>One of the <see cref="BatchStatus"/> values.</summary>
    [JsonPropertyName("status")]
    public string Status { get; init; } = "";

    /// <summary>The template id; null once the template has been deleted.</summary>
    [JsonPropertyName("template_id")]
    public string? TemplateId { get; init; }

    /// <summary>The template version the batch renders.</summary>
    [JsonPropertyName("template_version")]
    public int TemplateVersion { get; init; }

    /// <summary>The number of items.</summary>
    [JsonPropertyName("item_count")]
    public int ItemCount { get; init; }

    /// <summary>Items rendered so far.</summary>
    [JsonPropertyName("succeeded")]
    public int Succeeded { get; init; }

    /// <summary>Items failed so far.</summary>
    [JsonPropertyName("failed")]
    public int Failed { get; init; }

    /// <summary>The API URL of the ZIP of rendered PDFs. It needs your API key: use <see cref="BatchesResource.DownloadZipAsync"/>.</summary>
    [JsonPropertyName("zip_url")]
    public string? ZipUrl { get; init; }

    /// <summary>When the PDFs are deleted, or null.</summary>
    [JsonPropertyName("expires_at")]
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>The metadata passed when the batch was created, or null.</summary>
    [JsonPropertyName("metadata")]
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }

    /// <summary>Whether the PDFs are deleted once the ZIP has been downloaded.</summary>
    [JsonPropertyName("delete_after_download")]
    public bool DeleteAfterDownload { get; init; }

    /// <summary>The state of the <c>batch.completed</c> callback, or null without a callback URL.</summary>
    [JsonPropertyName("delivery")]
    public JobDelivery? Delivery { get; init; }

    /// <summary>When the batch was created.</summary>
    [JsonPropertyName("created_at")]
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>When the batch finished, or null.</summary>
    [JsonPropertyName("finished_at")]
    public DateTimeOffset? FinishedAt { get; init; }

    /// <summary>Each item's state, in request order. Only <see cref="BatchesResource.GetAsync"/> and <see cref="BatchesResource.WaitAsync"/> fill it; otherwise null.</summary>
    [JsonPropertyName("items")]
    public IReadOnlyList<BatchItem>? Items { get; init; }

    /// <summary>True when <see cref="Status"/> is <c>succeeded</c>, <c>partial</c> or <c>failed</c>.</summary>
    [JsonIgnore]
    public bool IsDone => Status == BatchStatus.Succeeded || Status == BatchStatus.Partial || Status == BatchStatus.Failed;
}

/// <summary>The state of one item of a batch.</summary>
public sealed class BatchItem
{
    /// <summary>The item's position in <see cref="CreateBatchOptions.Items"/>, from 0.</summary>
    [JsonPropertyName("index")]
    public int Index { get; init; }

    /// <summary>The render job for this item.</summary>
    [JsonPropertyName("job_id")]
    public string JobId { get; init; } = "";

    /// <summary>One of the <see cref="JobStatus"/> values.</summary>
    [JsonPropertyName("status")]
    public string Status { get; init; } = "";

    /// <summary>The PDF's name in the ZIP.</summary>
    [JsonPropertyName("filename")]
    public string? Filename { get; init; }

    /// <summary>The PDF's size, or null until it has rendered.</summary>
    [JsonPropertyName("bytes")]
    public long? Bytes { get; init; }

    /// <summary>The number of renders this item used.</summary>
    [JsonPropertyName("renders")]
    public int Renders { get; init; }

    /// <summary>Why the item failed, or null.</summary>
    [JsonPropertyName("error")]
    public JobError? Error { get; init; }
}
