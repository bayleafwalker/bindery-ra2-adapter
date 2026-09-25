// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Bindery.Ra2.Bot.Claude;

/// <summary>
/// Deterministic compact JSON: object keys sorted ordinally at every depth, no
/// whitespace, doubles rounded to a fixed number of decimals, non-finite
/// numbers written as null. The same logical content always yields the same
/// bytes, which is what prompt caching and the fog probe's context hash need.
/// </summary>
public static class CanonicalJson
{
    /// <summary>Decimals kept for doubles: enough for shares and seconds, few enough to keep the prompt small.</summary>
    public const int Decimals = 3;

    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Indented = false,
        // Keep non-ASCII readable and avoid escaping '+', '<' etc.; the text goes to a model, not an HTML page.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Serialize(JsonNode? node)
    {
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream, WriterOptions))
        {
            Write(writer, node);
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>A rounded number node, or null for NaN/infinity (JSON has no representation for them).</summary>
    public static JsonNode? Number(double value) =>
        double.IsFinite(value) ? JsonValue.Create(Round(value)) : null;

    /// <summary>Rounds to <see cref="Decimals"/> and folds negative zero into zero so both spell "0".</summary>
    private static double Round(double value)
    {
        double rounded = Math.Round(value, Decimals, MidpointRounding.AwayFromZero);
        return rounded == 0 ? 0 : rounded;
    }

    private static void Write(Utf8JsonWriter writer, JsonNode? node)
    {
        switch (node)
        {
            case null:
                writer.WriteNullValue();
                break;
            case JsonObject obj:
                writer.WriteStartObject();
                foreach (KeyValuePair<string, JsonNode?> property in obj.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Key);
                    Write(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonArray array:
                writer.WriteStartArray();
                foreach (JsonNode? item in array)
                {
                    Write(writer, item);
                }
                writer.WriteEndArray();
                break;
            case JsonValue value:
                if (value.TryGetValue(out double d))
                {
                    if (double.IsFinite(d))
                    {
                        writer.WriteNumberValue(Round(d));
                    }
                    else
                    {
                        writer.WriteNullValue();
                    }
                }
                else
                {
                    value.WriteTo(writer);
                }
                break;
        }
    }
}
