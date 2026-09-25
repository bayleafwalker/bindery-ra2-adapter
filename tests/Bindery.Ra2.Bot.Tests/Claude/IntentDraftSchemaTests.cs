// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using Bindery.Ra2.Bot.Claude;
using Xunit;

namespace Bindery.Ra2.Bot.Tests.Claude;

public sealed class IntentDraftSchemaTests
{
    private static IEnumerable<JsonElement> Objects(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            if (node.TryGetProperty("type", out JsonElement type) && type.ValueKind == JsonValueKind.String && type.GetString() == "object")
            {
                yield return node;
            }
            foreach (JsonProperty p in node.EnumerateObject())
            {
                foreach (JsonElement inner in Objects(p.Value))
                {
                    yield return inner;
                }
            }
        }
        else if (node.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in node.EnumerateArray())
            {
                foreach (JsonElement inner in Objects(item))
                {
                    yield return inner;
                }
            }
        }
    }

    [Fact]
    public void Schema_is_valid_json_and_every_object_is_closed_with_all_properties_required()
    {
        using JsonDocument schema = JsonDocument.Parse(IntentDraftSchema.Json);
        List<JsonElement> objects = Objects(schema.RootElement).ToList();

        Assert.True(objects.Count >= 7, $"expected nested object schemas, found {objects.Count}");
        foreach (JsonElement obj in objects)
        {
            Assert.False(obj.GetProperty("additionalProperties").GetBoolean());
            List<string> properties = obj.GetProperty("properties").EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
            List<string> required = obj.GetProperty("required").EnumerateArray().Select(e => e.GetString()!).OrderBy(n => n, StringComparer.Ordinal).ToList();
            Assert.Equal(properties, required);
        }
    }

    [Fact]
    public void Schema_uses_no_unsupported_keywords()
    {
        foreach (string keyword in new[] { "\"minimum\"", "\"maximum\"", "\"minLength\"", "\"maxLength\"", "\"multipleOf\"", "\"minItems\"" })
        {
            Assert.DoesNotContain(keyword, IntentDraftSchema.Json, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Schema_enums_are_the_csharp_member_names()
    {
        using JsonDocument schema = JsonDocument.Parse(IntentDraftSchema.Json);
        JsonElement props = schema.RootElement.GetProperty("properties");

        Assert.Equal(Enum.GetNames<StrategicPosture>(), props.GetProperty("posture").GetProperty("enum").EnumerateArray().Select(e => e.GetString()!).ToArray());
        JsonElement condition = props.GetProperty("abortTriggers").GetProperty("items").GetProperty("properties");
        Assert.Equal(Enum.GetNames<ConditionMetric>(), condition.GetProperty("metric").GetProperty("enum").EnumerateArray().Select(e => e.GetString()!).ToArray());
        Assert.Equal(Enum.GetNames<Comparison>(), condition.GetProperty("op").GetProperty("enum").EnumerateArray().Select(e => e.GetString()!).ToArray());
    }

    [Fact]
    public void Schema_property_names_match_the_dto_wire_names_at_every_level()
    {
        using JsonDocument schema = JsonDocument.Parse(IntentDraftSchema.Json);
        using JsonDocument dto = JsonDocument.Parse(ClaudeFixtures.DraftJson());

        AssertSameShape(schema.RootElement, dto.RootElement, "$");
    }

    private static void AssertSameShape(JsonElement schema, JsonElement value, string path)
    {
        if (schema.TryGetProperty("anyOf", out JsonElement anyOf))
        {
            JsonElement nonNull = anyOf.EnumerateArray().First(s => s.GetProperty("type").GetString() != "null");
            if (value.ValueKind != JsonValueKind.Null)
            {
                AssertSameShape(nonNull, value, path);
            }
            return;
        }
        string type = schema.GetProperty("type").GetString()!;
        switch (type)
        {
            case "object":
                List<string> expected = schema.GetProperty("properties").EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
                List<string> actual = value.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
                Assert.True(expected.SequenceEqual(actual), $"{path}: schema [{string.Join(",", expected)}] vs dto [{string.Join(",", actual)}]");
                foreach (JsonProperty p in schema.GetProperty("properties").EnumerateObject())
                {
                    AssertSameShape(p.Value, value.GetProperty(p.Name), $"{path}.{p.Name}");
                }
                break;
            case "array":
                Assert.Equal(JsonValueKind.Array, value.ValueKind);
                foreach (JsonElement item in value.EnumerateArray())
                {
                    AssertSameShape(schema.GetProperty("items"), item, $"{path}[]");
                }
                break;
            case "string":
                Assert.Equal(JsonValueKind.String, value.ValueKind);
                if (schema.TryGetProperty("enum", out JsonElement values))
                {
                    Assert.Contains(value.GetString(), values.EnumerateArray().Select(e => e.GetString()));
                }
                break;
            case "integer":
            case "number":
                Assert.Equal(JsonValueKind.Number, value.ValueKind);
                break;
            default:
                Assert.Fail($"{path}: unexpected schema type {type}");
                break;
        }
    }

    [Fact]
    public void Parser_rejects_malformed_json()
    {
        Assert.Null(IntentDraftSchema.TryParse("{\"playbookId\": \"allied-boom\",", out string? error));
        Assert.StartsWith("parse.", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Parser_rejects_a_missing_required_field()
    {
        string json = ClaudeFixtures.DraftJson().Replace("\"rationale\":", "\"ignored\":", StringComparison.Ordinal);
        Assert.Null(IntentDraftSchema.TryParse(json, out string? error));
        Assert.NotNull(error);
    }

    [Fact]
    public void Parser_rejects_unknown_fields()
    {
        string json = ClaudeFixtures.DraftJson().Replace("{\"playbookId\"", "{\"unitIds\":[1,2],\"playbookId\"", StringComparison.Ordinal);
        Assert.Null(IntentDraftSchema.TryParse(json, out _));
    }

    [Fact]
    public void Parser_rejects_numbers_written_as_strings()
    {
        string json = ClaudeFixtures.DraftJson().Replace("\"confidence\":0.72", "\"confidence\":\"0.72\"", StringComparison.Ordinal);
        Assert.Contains("\"confidence\":\"0.72\"", json, StringComparison.Ordinal);
        Assert.Null(IntentDraftSchema.TryParse(json, out _));
    }

    [Fact]
    public void Parser_accepts_a_complete_draft()
    {
        IntentDraft? draft = IntentDraftSchema.TryParse(ClaudeFixtures.DraftJson(), out string? error);
        Assert.Null(error);
        Assert.NotNull(draft);
        Assert.Equal("allied-boom", draft.PlaybookId);
        Assert.Equal(2, draft.ReplanTriggers[0].RegionId);
    }
}
