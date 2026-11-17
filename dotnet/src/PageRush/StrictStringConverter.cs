using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PageRush;

/// <summary>
/// Writes strings as System.Text.Json does, but rejects unpaired surrogates instead of silently replacing
/// them with U+FFFD, so a request body that isn't valid Unicode fails before it is sent.
/// </summary>
internal sealed class StrictStringConverter : JsonConverter<string>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.GetString();

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
    {
        Check(value);
        writer.WriteStringValue(value);
    }

    public override string ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.GetString()!;

    public override void WriteAsPropertyName(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
    {
        Check(value);
        writer.WritePropertyName(value);
    }

    private static void Check(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
            {
                i++;
            }
            else if (char.IsSurrogate(c))
            {
                throw new JsonException("A string contains an unpaired surrogate (not valid Unicode).");
            }
        }
    }
}
