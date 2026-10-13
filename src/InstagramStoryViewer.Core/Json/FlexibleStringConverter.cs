using System.Text.Json;
using System.Text.Json.Serialization;

namespace InstagramStoryViewer.Json;

/// <summary>
/// Instagram returns some ids as numbers in one endpoint and as strings in another
/// (e.g. "pk": 123 vs "pk": "123", or reel ids like "highlight:1789..."). This converter
/// reads either form into a string so the models do not break when the format changes.
/// </summary>
public sealed class FlexibleStringConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Number => reader.TryGetInt64(out var l)
                ? l.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : reader.GetDouble().ToString(System.Globalization.CultureInfo.InvariantCulture),
            JsonTokenType.Null => null,
            JsonTokenType.True => "true",
            JsonTokenType.False => "false",
            _ => SkipAndReturnNull(ref reader)
        };
    }

    private static string? SkipAndReturnNull(ref Utf8JsonReader reader)
    {
        reader.Skip();
        return null;
    }

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
    {
        if (value is null) writer.WriteNullValue();
        else writer.WriteStringValue(value);
    }
}
