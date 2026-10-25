using System.Text.Json;
using ChainOfRepair.Core.Models;

namespace ChainOfRepair.Core.Reasoning;

public sealed record StageRequest(string Stage, int MaxOutputTokens, int Attempt = 1)
{
    public string? Schema => StageOutputPolicy.Schema(Stage);
}

public sealed class InvalidModelOutputException(string kind, string response, string message) : LlmCallException(message)
{
    public string Kind { get; } = kind;
    public string Response { get; } = response;
}

public static class StageOutputPolicy
{
    public static int Limit(string stage, LlmSettings settings, bool lengthRetry = false)
    {
        var limits = settings.OutputLimits;
        var initial = stage switch
        {
            "root-cause" => limits.RootCause, "patch-synthesis" => limits.Patch,
            "refinement" => limits.Refinement, "direct-patch" => limits.DirectPatch,
            "self-consistency" => limits.SelfConsistency, "explanation" => limits.Explanation,
            _ => Math.Min(2048, settings.MaxTokens)
        };
        return lengthRetry && stage is "patch-synthesis" or "refinement" or "direct-patch"
            ? Math.Min(settings.MaxTokens, Math.Max(initial, limits.PatchLengthRetry)) : initial;
    }

    private static Dictionary<string, object>? Properties(string stage) => stage switch
    {
        "root-cause" => new() { ["faultCategory"] = Text(80), ["rootCause"] = Text(300),
            ["evidence"] = Items(), ["localizedMethod"] = Text(), ["confidence"] = new { type = "number", minimum = 0, maximum = 1 } },
        "self-consistency" => new() { ["passed"] = new { type = "boolean" }, ["consistent"] = new { type = "boolean" },
            ["aligned"] = new { type = "boolean" }, ["concerns"] = Items(), ["causalAlignment"] = Text(), ["scopeAssessment"] = Text() },
        "patch-synthesis" or "refinement" or "direct-patch" => new() { ["patchType"] = Text(), ["targetMethod"] = Text(),
            ["changedLinesRationale"] = Text(), ["unifiedDiff"] = new { type = "string" }, ["expectedEffect"] = Text() },
        _ => null
    };
    private static object Text(int limit = 600) => new { type = "string", pattern = $"^[\\s\\S]{{1,{limit}}}$" };
    private static object Items() => new { type = "array", items = Text(240), maxItems = 3 };
    public static string? Schema(string stage) => Properties(stage) is { } properties
        ? JsonSerializer.Serialize(new { type = "object", properties, required = properties.Keys.ToArray(), additionalProperties = false }) : null;

    // Conservative structural/verbosity checks, not a claim to detect every semantic hallucination.
    private static bool Repetitive(string text)
    {
        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return words.Length >= 20 && words.Distinct(StringComparer.OrdinalIgnoreCase).Count() * 4 < words.Length;
    }

    public static void Validate(string stage, string response)
    {
        var properties = Properties(stage);
        if (properties is null) return;
        JsonDocument doc;
        try { doc = JsonDocument.Parse(response); }
        catch (JsonException) { throw new InvalidModelOutputException("invalid-json", response, "Return one complete JSON object without fences."); }
        using (doc)
        {
            var root = doc.RootElement;
            void Fail(string kind, string message) => throw new InvalidModelOutputException(kind, response, message);
            if (root.ValueKind != JsonValueKind.Object) Fail("invalid-schema", "Expected a JSON object.");
            if (root.EnumerateObject().Count() != properties.Count || properties.Keys.Any(k => !root.TryGetProperty(k, out _)))
                Fail("invalid-schema", "Use exactly the required fields.");
            foreach (var (key, definition) in properties)
            {
                var type = JsonSerializer.SerializeToElement(definition).GetProperty("type").GetString();
                var value = root.GetProperty(key);
                var valid = type switch { "string" => value.ValueKind == JsonValueKind.String,
                    "array" => value.ValueKind == JsonValueKind.Array && value.EnumerateArray().All(v => v.ValueKind == JsonValueKind.String),
                    "number" => value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var n) && double.IsFinite(n) && n >= 0 && n <= 1,
                    "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False, _ => false };
                if (!valid) Fail("invalid-schema", $"Incorrect type or range for {key}.");
                if (key == "unifiedDiff") continue; // Never shorten an actual repair to pass a prose check.
                var texts = value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().Select(v => v.GetString()!).ToArray()
                    : value.ValueKind == JsonValueKind.String ? [value.GetString()!] : Array.Empty<string>();
                if (texts.Length > 3 || texts.Any(t => string.IsNullOrWhiteSpace(t) || t.Length > 600 || Repetitive(t)))
                    Fail("meaningless-output", "Prose is empty or excessively verbose; use at most three brief evidence/concern items and one causal sentence.");
            }
            if (stage == "root-cause" && root.GetProperty("evidence").GetArrayLength() == 0)
                Fail("meaningless-output", "Provide at least one concrete artifact observation, or state what evidence is missing.");
        }
    }
}
