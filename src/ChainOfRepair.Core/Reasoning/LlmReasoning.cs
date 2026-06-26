using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ChainOfRepair.Core.Models;

namespace ChainOfRepair.Core.Reasoning;

public interface ILLMClient
{
    LlmSettings Settings { get; }
    string ClientName { get; }
    bool IsMock { get; }
    Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken = default);
}

public sealed class OpenAiLLMClient : ILLMClient
{
    private readonly HttpClient _httpClient;

    public OpenAiLLMClient(LlmSettings settings, HttpClient? httpClient = null)
    {
        Settings = settings;
        _httpClient = httpClient ?? new HttpClient { BaseAddress = new Uri("https://api.openai.com/") };
    }

    public LlmSettings Settings { get; }
    public string ClientName => "OpenAiLLMClient";
    public bool IsMock => false;

    public async Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken = default)
    {
        var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new LlmConfigurationException("OPENAI_API_KEY is not set. Chain-of-Repair requires a real OpenAI API key and will not fall back to canned responses.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "v1/chat/completions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = JsonContent(new
        {
            model = Settings.Model,
            temperature = Settings.Temperature,
            max_tokens = Settings.MaxTokens,
            messages = new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPrompt }
            }
        });

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new LlmCallException($"OpenAI request failed with HTTP {(int)response.StatusCode}: {body}");
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var content = document.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString();

            if (string.IsNullOrWhiteSpace(content))
            {
                throw new LlmCallException("OpenAI returned an empty chat completion.");
            }

            return content;
        }
        catch (JsonException ex)
        {
            throw new LlmCallException("OpenAI returned a response that could not be parsed as Chat Completions JSON.", ex);
        }
        catch (KeyNotFoundException ex)
        {
            throw new LlmCallException("OpenAI response did not contain choices[0].message.content.", ex);
        }
    }

    private static StringContent JsonContent(object value) =>
        new(JsonSerializer.Serialize(value, JsonLog.Options), Encoding.UTF8, "application/json");
}

public sealed class DemoMockLLMClient : ILLMClient
{
    public DemoMockLLMClient(LlmSettings settings)
    {
        Settings = settings;
    }

    public LlmSettings Settings { get; }
    public string ClientName => "DemoMockLLMClient";
    public bool IsMock => true;

    public Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken = default)
    {
        throw new LlmConfigurationException("UseMockClient is enabled. Demo mode is not valid for reproducibility and is disabled for the repair pipeline.");
    }
}

public sealed class CoRReasoningService
{
    private readonly ILLMClient _llmClient;
    private readonly PromptTemplateService _templates;

    public CoRReasoningService(ILLMClient llmClient, PromptTemplateService templates)
    {
        _llmClient = llmClient;
        _templates = templates;
    }

    public async Task<LlmExchange> GenerateRootCauseAsync(
        RepairRequest request,
        SuspiciousMethodScore selectedMethod,
        FailingOutputAnalysis failingOutput,
        IReadOnlyList<SuspiciousMethodScore> rankedMethods,
        CancellationToken cancellationToken = default)
    {
        var payload = new
        {
            language = request.Language.ToString(),
            localizedMethod = selectedMethod.Method,
            sourceCodeContext = request.SourceCode,
            failingOutput = request.FailingOutput,
            stackTraceFrames = failingOutput.StackTraceFrames,
            compilerErrors = failingOutput.CompilerErrors,
            compilerWarnings = failingOutput.Warnings,
            bugDescription = request.BugDescription,
            rankedMethodMetadata = rankedMethods
        };

        return await RunStageAsync(
            "root-cause",
            "root_cause.md",
            "You perform root-cause analysis for automated program repair. Return only strict JSON with keys faultCategory, rootCause, evidence, localizedMethod, confidence.",
            payload,
            cancellationToken);
    }

    public async Task<LlmExchange> GeneratePatchAsync(
        RepairRequest request,
        CandidateMethod localizedMethod,
        string rootCauseJson,
        CancellationToken cancellationToken = default)
    {
        var payload = new
        {
            language = request.Language.ToString(),
            originalFullSourceCode = request.SourceCode,
            localizedMethod,
            rootCauseJson = ParseOrRaw(rootCauseJson),
            constraints = new[]
            {
                "Return one real source-code edit as a unified diff.",
                "Keep the edit scoped to the localized method/function.",
                "Do not return placeholder diffs, examples, prose-only patches, or unrelated refactoring."
            }
        };

        return await RunStageAsync(
            "patch-synthesis",
            "patch_synthesis.md",
            "You synthesize minimal program repair patches. Return only strict JSON with keys patchType, targetMethod, changedLinesRationale, unifiedDiff, expectedEffect.",
            payload,
            cancellationToken);
    }

    public async Task<LlmExchange> GenerateSelfConsistencyAsync(
        RepairRequest request,
        CandidateMethod localizedMethod,
        string rootCauseJson,
        string patchJson,
        PatchApplicationResult patchApplication,
        CancellationToken cancellationToken = default)
    {
        var payload = new
        {
            language = request.Language.ToString(),
            localizedMethod,
            rootCauseJson = ParseOrRaw(rootCauseJson),
            patchJson = ParseOrRaw(patchJson),
            patchApplication
        };

        return await RunStageAsync(
            "self-consistency",
            "self_consistency.md",
            "You verify whether a proposed repair is causally consistent. Return concise strict JSON with keys passed, concerns, causalAlignment, scopeAssessment.",
            payload,
            cancellationToken);
    }

    public async Task<LlmExchange> GenerateRefinementAsync(
        RepairRequest request,
        CandidateMethod localizedMethod,
        string rootCauseJson,
        string previousPatchJson,
        PatchApplicationResult previousPatchApplication,
        ValidationResult validation,
        int iteration,
        CancellationToken cancellationToken = default)
    {
        var payload = new
        {
            iteration,
            language = request.Language.ToString(),
            originalSourceCode = request.SourceCode,
            previousPatchJson = ParseOrRaw(previousPatchJson),
            previousPatchedCode = previousPatchApplication.PatchedCode,
            previousUnifiedDiff = previousPatchApplication.UnifiedDiff,
            validationSummary = validation.RefinementInstruction,
            validationFailures = validation.Checks.Where(check => !check.Passed).ToArray(),
            originalRootCauseJson = ParseOrRaw(rootCauseJson),
            localizedMethod,
            instruction = "Produce a smaller corrected patch JSON scoped to the same method/function."
        };

        return await RunStageAsync(
            "refinement",
            "refinement.md",
            "You refine failed repair patches. Return only strict JSON with keys patchType, targetMethod, changedLinesRationale, unifiedDiff, expectedEffect.",
            payload,
            cancellationToken);
    }

    public async Task<LlmExchange> GenerateExplanationAsync(
        RepairRequest request,
        CandidateMethod localizedMethod,
        string rootCauseJson,
        string patchJson,
        string selfConsistencyJson,
        ValidationResult validation,
        CancellationToken cancellationToken = default)
    {
        var payload = new
        {
            language = request.Language.ToString(),
            localizedMethod,
            rootCauseJson = ParseOrRaw(rootCauseJson),
            patchJson = ParseOrRaw(patchJson),
            selfConsistencyJson = ParseOrRaw(selfConsistencyJson),
            validationResult = validation,
            finalPatchedSourceCode = validation.PatchApplication.PatchedCode,
            finalUnifiedDiff = validation.PatchApplication.UnifiedDiff
        };

        return await RunStageAsync(
            "explanation",
            "explanation.md",
            "You explain automated program repairs to developers. Return plain text, grounded only in the provided artifacts.",
            payload,
            cancellationToken);
    }

    private async Task<LlmExchange> RunStageAsync(
        string stage,
        string templateName,
        string systemPrompt,
        object payload,
        CancellationToken cancellationToken)
    {
        var template = await _templates.LoadAsync(templateName, cancellationToken);
        var userPrompt = template.Content + Environment.NewLine + Environment.NewLine +
                         "INPUT ARTIFACT JSON:" + Environment.NewLine +
                         JsonSerializer.Serialize(payload, JsonLog.Options);

        try
        {
            var response = await _llmClient.CompleteAsync(systemPrompt, userPrompt, cancellationToken);
            return new LlmExchange
            {
                Stage = stage,
                Client = _llmClient.ClientName,
                Model = _llmClient.Settings.Model,
                Temperature = _llmClient.Settings.Temperature,
                MaxTokens = _llmClient.Settings.MaxTokens,
                TemplatePath = template.Path,
                SystemPrompt = systemPrompt,
                UserPrompt = userPrompt,
                Response = response,
                MockClientUsed = _llmClient.IsMock
            };
        }
        catch (Exception ex) when (ex is LlmConfigurationException or LlmCallException or HttpRequestException or TaskCanceledException)
        {
            throw new LlmStageException(stage, template.Path, systemPrompt, userPrompt, ex.Message, ex);
        }
    }

    private static object ParseOrRaw(string text)
    {
        var normalized = JsonResponse.Normalize(text);
        try
        {
            return JsonSerializer.Deserialize<JsonElement>(normalized);
        }
        catch (JsonException)
        {
            return new { raw = text };
        }
    }
}

public sealed class PromptTemplateService
{
    private readonly string _promptDirectory;

    public PromptTemplateService(string promptDirectory)
    {
        _promptDirectory = promptDirectory;
    }

    public async Task<PromptTemplate> LoadAsync(string fileName, CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(_promptDirectory, fileName);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Prompt template '{fileName}' was not found at '{path}'.", path);
        }

        return new PromptTemplate(path, await File.ReadAllTextAsync(path, cancellationToken));
    }
}

public sealed record PromptTemplate(string Path, string Content);

public static class JsonResponse
{
    public static string Normalize(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewline = trimmed.IndexOf('\n');
            var lastFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
            if (firstNewline >= 0 && lastFence > firstNewline)
            {
                trimmed = trimmed[(firstNewline + 1)..lastFence].Trim();
            }
        }

        return trimmed;
    }

    public static string GetRequiredString(string json, string propertyName)
    {
        using var document = JsonDocument.Parse(Normalize(json));
        if (!document.RootElement.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException($"LLM patch JSON did not contain a string '{propertyName}' property.");
        }

        return property.GetString() ?? "";
    }
}

public class LlmConfigurationException : Exception
{
    public LlmConfigurationException(string message) : base(message)
    {
    }
}

public class LlmCallException : Exception
{
    public LlmCallException(string message) : base(message)
    {
    }

    public LlmCallException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

public sealed class LlmStageException : Exception
{
    public LlmStageException(string stage, string templatePath, string systemPrompt, string userPrompt, string message, Exception innerException)
        : base(message, innerException)
    {
        Stage = stage;
        TemplatePath = templatePath;
        SystemPrompt = systemPrompt;
        UserPrompt = userPrompt;
    }

    public string Stage { get; }
    public string TemplatePath { get; }
    public string SystemPrompt { get; }
    public string UserPrompt { get; }
}
