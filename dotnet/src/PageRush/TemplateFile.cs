using System;
using System.IO;
using System.Text;
using System.Text.Json.Serialization;

namespace PageRush;

/// <summary>
/// A file a template imports or reads besides <c>main.typ</c>: more <c>.typ</c> modules, images, fonts or data.
/// Build one with <see cref="FromString"/>, <see cref="FromBytes"/> or <see cref="FromPath"/>, which pick the
/// encoding from the file extension.
/// </summary>
public sealed class TemplateFile
{
    /// <summary>The <see cref="Encoding"/> for text content.</summary>
    public const string EncodingUtf8 = "utf8";

    /// <summary>The <see cref="Encoding"/> for base64-encoded binary content.</summary>
    public const string EncodingBase64 = "base64";

    private static readonly string[] _textExtensions =
    [
        ".typ", ".txt", ".csv", ".json", ".svg", ".xml", ".md", ".yml", ".yaml", ".bib",
    ];

    private static readonly UTF8Encoding _strictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>Creates an empty file entry. Prefer the <c>From...</c> helpers.</summary>
    public TemplateFile()
    {
    }

    /// <summary>Creates a file entry from its parts.</summary>
    /// <param name="path">The path relative to <c>main.typ</c>, with forward slashes.</param>
    /// <param name="content">The content: text for <c>utf8</c>, base64 for <c>base64</c>.</param>
    /// <param name="encoding"><see cref="EncodingUtf8"/> or <see cref="EncodingBase64"/>.</param>
    public TemplateFile(string path, string content, string encoding)
    {
        Path = path;
        Content = content;
        Encoding = encoding;
    }

    /// <summary>The path relative to <c>main.typ</c>, with forward slashes, such as <c>parts/header.typ</c> or <c>logo.png</c>.</summary>
    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    /// <summary>The content: text when <see cref="Encoding"/> is <c>utf8</c>, base64 when it is <c>base64</c>.</summary>
    [JsonPropertyName("content")]
    public string Content { get; set; } = "";

    /// <summary><see cref="EncodingUtf8"/> or <see cref="EncodingBase64"/>.</summary>
    [JsonPropertyName("encoding")]
    public string Encoding { get; set; } = EncodingUtf8;

    /// <summary>
    /// Builds a file entry from text. Files ending in <c>.typ .txt .csv .json .svg .xml .md .yml .yaml .bib</c> are
    /// sent as <c>utf8</c>; anything else is sent as the base64 of the text's UTF-8 bytes.
    /// </summary>
    /// <param name="path">The path inside the template, with forward slashes.</param>
    /// <param name="contents">The file's text.</param>
    /// <returns>The file entry.</returns>
    public static TemplateFile FromString(string path, string contents)
    {
        Guard.NotNull(path, nameof(path));

        Guard.NotNull(contents, nameof(contents));

        return IsText(path)
            ? new TemplateFile(path, contents, EncodingUtf8)
            : new TemplateFile(path, Convert.ToBase64String(_strictUtf8.GetBytes(contents)), EncodingBase64);
    }

    /// <summary>
    /// Builds a file entry from raw bytes. Text extensions (see <see cref="FromString"/>) are sent as <c>utf8</c>
    /// and must be valid UTF-8; everything else (images, fonts) is sent as <c>base64</c>.
    /// </summary>
    /// <param name="path">The path inside the template, with forward slashes.</param>
    /// <param name="contents">The file's bytes.</param>
    /// <returns>The file entry.</returns>
    /// <exception cref="ArgumentException">A text file's bytes are not valid UTF-8.</exception>
    public static TemplateFile FromBytes(string path, byte[] contents)
    {
        Guard.NotNull(path, nameof(path));

        Guard.NotNull(contents, nameof(contents));

        if (!IsText(path))
        {
            return new TemplateFile(path, Convert.ToBase64String(contents), EncodingBase64);
        }

        try
        {
            return new TemplateFile(path, _strictUtf8.GetString(contents), EncodingUtf8);
        }
        catch (DecoderFallbackException e)
        {
            throw new ArgumentException($"{path} is a text file but its contents are not valid UTF-8.", nameof(contents), e);
        }
    }

    /// <summary>
    /// Reads a local file and builds a file entry for it, as <see cref="FromBytes"/> does.
    /// </summary>
    /// <param name="localPath">The file on disk.</param>
    /// <param name="targetPath">The path inside the template. When null, the local file's name is used. Directory separators are turned into forward slashes.</param>
    /// <returns>The file entry.</returns>
    public static TemplateFile FromPath(string localPath, string? targetPath = null)
    {
        Guard.NotNull(localPath, nameof(localPath));

        var contents = File.ReadAllBytes(localPath);
        var target = string.IsNullOrEmpty(targetPath) ? System.IO.Path.GetFileName(localPath) : targetPath!;
        target = target.Replace(System.IO.Path.DirectorySeparatorChar, '/').Replace(System.IO.Path.AltDirectorySeparatorChar, '/');
        return FromBytes(target, contents);
    }

    private static bool IsText(string path)
    {
        var name = path.Substring(path.LastIndexOf('/') + 1);
        var dot = name.LastIndexOf('.');
        if (dot < 0)
        {
            return false;
        }

        var ext = name.Substring(dot);
        foreach (var t in _textExtensions)
        {
            if (string.Equals(t, ext, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
