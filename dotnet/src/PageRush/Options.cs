using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace PageRush;

/// <summary>Options for <see cref="TemplatesResource.ListAsync"/> and <see cref="TemplatesResource.ListAllAsync"/>.</summary>
public sealed class ListTemplatesOptions
{
    /// <summary>The page size, 1 to 100. The API defaults to 50.</summary>
    public int? Limit { get; set; }

    /// <summary>The <see cref="TemplateList.NextCursor"/> of the previous page.</summary>
    public string? Cursor { get; set; }
}

/// <summary>The body of <see cref="TemplatesResource.CreateAsync"/>. Send <see cref="Source"/> or <see cref="Document"/>, not both.</summary>
public sealed class CreateTemplateOptions
{
    /// <summary>Required: the template's name (1 to 120 characters).</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>The template's <c>main.typ</c>.</summary>
    [JsonPropertyName("source")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Source { get; set; }

    /// <summary>A template from the visual editor, as any JSON object.</summary>
    [JsonPropertyName("document")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Document { get; set; }

    /// <summary>Other files the template imports or reads. Build them with <see cref="TemplateFile.FromString"/>, <see cref="TemplateFile.FromBytes"/> or <see cref="TemplateFile.FromPath"/>.</summary>
    [JsonPropertyName("files")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<TemplateFile>? Files { get; set; }

    /// <summary>Example data for dashboard test renders. API renders don't use it.</summary>
    [JsonPropertyName("sample_data")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? SampleData { get; set; }

    /// <summary>The Typst release to render with. The API picks its current default when null.</summary>
    [JsonPropertyName("typst_version")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TypstVersion { get; set; }
}

/// <summary>The body of <see cref="TemplatesResource.CreateVersionAsync"/>. Send <see cref="Source"/> or <see cref="Document"/>, not both.</summary>
public sealed class CreateVersionOptions
{
    /// <summary>The version's <c>main.typ</c>.</summary>
    [JsonPropertyName("source")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Source { get; set; }

    /// <summary>A template from the visual editor, as any JSON object.</summary>
    [JsonPropertyName("document")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Document { get; set; }

    /// <summary>Other files the template imports or reads.</summary>
    [JsonPropertyName("files")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<TemplateFile>? Files { get; set; }

    /// <summary>Example data for dashboard test renders.</summary>
    [JsonPropertyName("sample_data")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? SampleData { get; set; }

    /// <summary>The Typst release to render with. The API picks its current default when null.</summary>
    [JsonPropertyName("typst_version")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TypstVersion { get; set; }

    /// <summary>Whether the new version goes live. The API defaults to true.</summary>
    [JsonPropertyName("publish")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Publish { get; set; }
}

/// <summary>Options for <see cref="TemplatesResource.RenderAsync"/>.</summary>
public sealed class RenderOptions
{
    /// <summary>
    /// Required: any JSON value. The template reads it with <c>#let data = json("data.json")</c>. Pass a
    /// dictionary, an anonymous object, a class, or a <see cref="System.Text.Json.JsonElement"/>. It is
    /// serialised with System.Text.Json as it is, without renaming properties.
    /// </summary>
    [JsonPropertyName("data")]
    public object? Data { get; set; }

    /// <summary>Render this version instead of the live one.</summary>
    [JsonPropertyName("version")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Version { get; set; }

    /// <summary>The file name in the response's Content-Disposition header.</summary>
    [JsonPropertyName("filename")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Filename { get; set; }

    /// <summary>Up to 20 string pairs (at most 4 KB as JSON), echoed on the job and the callback.</summary>
    [JsonPropertyName("metadata")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, string>? Metadata { get; set; }

    /// <summary>Delete the PDF once you have it instead of keeping it for 24 hours. Null uses the project's setting.</summary>
    [JsonPropertyName("delete_after_download")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? DeleteAfterDownload { get; set; }

    /// <summary>Queue the render and POST the signed PDF to this public https URL when it is ready. The call then returns a queued result.</summary>
    [JsonPropertyName("callback_url")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CallbackUrl { get; set; }

    /// <summary>
    /// Sent as the <c>Idempotency-Key</c> header: 1 to 255 letters, digits, <c>_ - : .</c>. Any other value
    /// throws <see cref="PageRushException"/> with code <see cref="ErrorCodes.ValidationFailed"/> before
    /// anything is sent. When null, the SDK uses a random UUID v4 and reuses it on every retry of this call,
    /// so a retry never renders or charges twice.
    /// </summary>
    [JsonIgnore]
    public string? IdempotencyKey { get; set; }

    /// <summary>
    /// Sends <c>Prefer: respond-async</c>. This does not ask for an async render: the API still renders right
    /// away, and only if that attempt hits a temporary capacity problem does it queue the job (202) instead of
    /// answering 503. Code that sets it must handle both a PDF and a queued result.
    /// </summary>
    [JsonIgnore]
    public bool PreferAsync { get; set; }
}

/// <summary>One document of a batch.</summary>
public sealed class BatchItemInput
{
    /// <summary>Required: any JSON value, as for <see cref="RenderOptions.Data"/>.</summary>
    [JsonPropertyName("data")]
    public object? Data { get; set; }

    /// <summary>The PDF's name in the ZIP. The API picks one when null.</summary>
    [JsonPropertyName("filename")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Filename { get; set; }
}

/// <summary>Options for <see cref="BatchesResource.CreateAsync"/>.</summary>
public sealed class CreateBatchOptions
{
    /// <summary>Required: the documents to render, 1 to 10,000.</summary>
    [JsonPropertyName("items")]
    public IReadOnlyList<BatchItemInput> Items { get; set; } = Array.Empty<BatchItemInput>();

    /// <summary>Render this version instead of the live one.</summary>
    [JsonPropertyName("version")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Version { get; set; }

    /// <summary>Up to 20 string pairs (at most 4 KB as JSON), echoed on the batch and its <c>batch.completed</c> callback.</summary>
    [JsonPropertyName("metadata")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, string>? Metadata { get; set; }

    /// <summary>Delete the PDFs once the ZIP has been downloaded instead of keeping them for 24 hours. Null uses the project's setting.</summary>
    [JsonPropertyName("delete_after_download")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? DeleteAfterDownload { get; set; }

    /// <summary>Receives a signed <c>batch.completed</c> notice (JSON, without the PDFs) when the batch has finished. Must be a public https URL.</summary>
    [JsonPropertyName("callback_url")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CallbackUrl { get; set; }

    /// <summary>
    /// Sent as the <c>Idempotency-Key</c> header, with the same rules as <see cref="RenderOptions.IdempotencyKey"/>.
    /// When null, the SDK uses a random UUID v4 and reuses it on every retry of this call, so a retry never
    /// creates a second batch.
    /// </summary>
    [JsonIgnore]
    public string? IdempotencyKey { get; set; }
}

/// <summary>Options for <see cref="JobsResource.WaitAsync"/> and <see cref="BatchesResource.WaitAsync"/>. Null values use the method's defaults.</summary>
public sealed class WaitOptions
{
    /// <summary>How long to wait in total (default 120 s for jobs, 600 s for batches).</summary>
    public TimeSpan? Timeout { get; set; }

    /// <summary>The pause between polls (default 1 s for jobs, 2 s for batches).</summary>
    public TimeSpan? Interval { get; set; }
}
