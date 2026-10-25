using System.ClientModel;
using System.ClientModel.Primitives;
using System.Diagnostics;
using System.Text;
using ChainOfRepair.Core.Models;
using OpenAI;
using OpenAI.Chat;

namespace ChainOfRepair.Core.Reasoning;

// One instance per repair run/session. SDK retries are disabled so every HTTP attempt is counted.
public sealed class OpenAiLLMClient : ILLMClient
{
    private readonly HttpClient? _httpClient;
    private readonly List<ModelCall> _calls = [];
    private bool _unknownUsage;
    public OpenAiLLMClient(LlmSettings settings, HttpClient? httpClient = null)
    {
        if (settings.MaxTokens is < 1 or > 4096 || settings.MaxCalls is < 1 or > 20 ||
            settings.TotalTokenBudget is < 1 or > 16000)
            throw new LlmConfigurationException("Budgets must be positive and at most 4096 output tokens, 20 calls and 16000 total tokens.");
        foreach (var stage in new[] { "root-cause", "patch-synthesis", "refinement", "direct-patch", "self-consistency", "explanation" })
            if (StageOutputPolicy.Limit(stage, settings) < 1 || StageOutputPolicy.Limit(stage, settings) > settings.MaxTokens)
                throw new LlmConfigurationException($"Output limit for {stage} must be positive and not exceed MaxTokens. A shared ceiling must not silently reduce stage limits.");
        if (settings.OutputLimits.PatchLengthRetry is < 1 or > 4096)
            throw new LlmConfigurationException("PatchLengthRetry must be between 1 and 4096.");
        Settings = settings;
        _httpClient = httpClient;
    }
    public LlmSettings Settings { get; }
    public string ClientName => "OpenAiLLMClient";
    public bool IsMock => false;
    public IReadOnlyList<ModelCall> Calls => _calls;
    public void BeginRun() { _calls.Clear(); _unknownUsage = false; }

    public async Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken = default)
        => await CompleteAsync(systemPrompt, userPrompt, new StageRequest("unspecified", Math.Min(2048, Settings.MaxTokens)), cancellationToken);

    public async Task<string> CompleteAsync(string systemPrompt, string userPrompt, StageRequest request, CancellationToken cancellationToken = default)
    {
        var schema = request.Schema;
        var limit = Math.Min(Settings.MaxTokens, request.MaxOutputTokens);
        if (limit < 1) throw new LlmConfigurationException("Output limit must be positive.");
        var key = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrWhiteSpace(key))
            throw new LlmConfigurationException("OPENAI_API_KEY is not set. No model request was sent.");
        // UTF-8 bytes bound ordinary BPE text tokens from above. Reserve additional framing tokens.
        // This deliberately conservative admission bound is NOT recorded as measured token usage.
        var inputBound = Encoding.UTF8.GetByteCount(systemPrompt) + Encoding.UTF8.GetByteCount(userPrompt) + Encoding.UTF8.GetByteCount(schema ?? "") + 128;
        var used = _calls.Sum(c => (c.InputTokens ?? 0) + (c.OutputTokens ?? 0));
        if (_unknownUsage || _calls.Count >= Settings.MaxCalls || inputBound + limit > Settings.TotalTokenBudget - used)
            throw new InferenceBudgetException("Inference budget exhausted or insufficient for the complete prompt and reserved output. Unknown API usage also stops further calls.");
        var options = new OpenAIClientOptions { RetryPolicy = new ClientRetryPolicy(0) };
        if (_httpClient is not null) options.Transport = new HttpClientPipelineTransport(_httpClient);
        var client = new ChatClient(Settings.Model, new ApiKeyCredential(key), options);
        var timer = Stopwatch.StartNew();
        var number = _calls.Count + 1;
        try
        {
            ChatCompletion completion = await client.CompleteChatAsync(
                [new SystemChatMessage(systemPrompt), new UserChatMessage(userPrompt)],
                new ChatCompletionOptions
                {
                    Temperature = Settings.Temperature,
                    MaxOutputTokenCount = limit,
                    ResponseFormat = schema is not null
                        ? ChatResponseFormat.CreateJsonSchemaFormat(request.Stage.Replace("-", "_"), BinaryData.FromString(schema), jsonSchemaIsStrict: true) : null
                }, cancellationToken);
            var usage = completion.Usage;
            _calls.Add(new(number, usage?.InputTokenCount, usage?.OutputTokenCount, timer.Elapsed.TotalSeconds,
                completion.Model, completion.FinishReason.ToString(), null) { CachedInputTokens = usage?.InputTokenDetails?.CachedTokenCount, Stage = request.Stage, RetryCount = request.Attempt - 1, MaxOutputTokens = limit });
            if (usage is null) { _unknownUsage = true; throw new LlmCallException("Response did not contain measured token usage."); }
            if (used + usage.InputTokenCount + usage.OutputTokenCount > Settings.TotalTokenBudget)
                throw new InferenceBudgetException("Server-reported usage exceeded the admission bound; run stopped.");
            var content = string.Concat(completion.Content.Select(p => p.Text));
            if (completion.FinishReason == ChatFinishReason.Length)
                throw new LlmOutputTruncatedException(content, limit, request.Stage);
            if (completion.FinishReason != ChatFinishReason.Stop)
                throw new InvalidModelOutputException("incomplete-response", content, $"Incomplete model response: {completion.FinishReason}.");
            if (string.IsNullOrWhiteSpace(content)) throw new InvalidModelOutputException("incomplete-response", content, "Empty model response.");
            StageOutputPolicy.Validate(request.Stage, content);
            return content;
        }
        catch (Exception ex)
        {
            // Do not log API response bodies, credentials or arbitrary SDK exception text.
            var safeError = ex is ClientResultException api ? $"OpenAI HTTP {api.Status}." :
                ex is LlmOutputTruncatedException ? "Output token limit reached (Length)." : $"Model call failed ({ex.GetType().Name}).";
            if (_calls.Count < number)
            {
                _unknownUsage = true;
                _calls.Add(new(number, null, null, timer.Elapsed.TotalSeconds, null, null, safeError) { Stage = request.Stage, RetryCount = request.Attempt - 1, MaxOutputTokens = limit, ErrorKind = "transport-error" });
            }
            else _calls[^1] = _calls[^1] with { Error = safeError, ErrorKind = ex is InvalidModelOutputException invalid ? invalid.Kind : ex is LlmOutputTruncatedException ? "output-limit" : "call-error" };
            if (ex is OperationCanceledException) throw;
            if (ex is LlmCallException) throw;
            throw new LlmCallException(safeError);
        }
    }
}

public sealed class InferenceBudgetException(string message) : LlmCallException(message);

public sealed class LlmOutputTruncatedException(string partialResponse, int outputLimit, string stage = "model")
    : LlmCallException($"The {stage} stage reached the {outputLimit}-token output limit. Its incomplete response is retained for inspection and must not be applied as a patch.")
{
    public string PartialResponse { get; } = partialResponse;
}
