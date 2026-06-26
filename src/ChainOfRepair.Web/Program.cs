using ChainOfRepair.Core.Diagnostics;
using ChainOfRepair.Core.Models;
using ChainOfRepair.Core.Pipeline;
using ChainOfRepair.Core.Ranking;
using ChainOfRepair.Core.Reasoning;
using ChainOfRepair.Core.Validation;

var builder = WebApplication.CreateBuilder(args);
var logDirectory = Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, "..", "..", "logs"));
var promptDirectory = Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, "..", "..", "prompts"));
var llmSettings = new LlmSettings
{
    Provider = builder.Configuration["LLM:Provider"] ?? "OpenAI",
    Model = builder.Configuration["LLM:Model"] ?? Environment.GetEnvironmentVariable("COR_OPENAI_MODEL") ?? "gpt-4o",
    Temperature = float.TryParse(builder.Configuration["LLM:Temperature"], out var temperature) ? temperature : 0.2f,
    MaxTokens = int.TryParse(builder.Configuration["LLM:MaxTokens"], out var maxTokens) ? maxTokens : 2048,
    UseMockClient = bool.TryParse(builder.Configuration["LLM:UseMockClient"], out var useMockClient) && useMockClient
};

builder.Services.AddSingleton(llmSettings);
if (llmSettings.UseMockClient)
{
    builder.Services.AddSingleton<ILLMClient, DemoMockLLMClient>();
}
else
{
    builder.Services.AddSingleton<ILLMClient, OpenAiLLMClient>();
}

builder.Services.AddSingleton(new PromptTemplateService(promptDirectory));
builder.Services.AddSingleton<CoRReasoningService>();
builder.Services.AddSingleton<PatchApplicationService>();
builder.Services.AddSingleton<ValidationService>();
builder.Services.AddSingleton<WeightedBordaRanker>();
builder.Services.AddSingleton(new DiagnosticsService(llmSettings, logDirectory));
builder.Services.AddSingleton(provider => new RepairPipelineService(
    provider.GetRequiredService<CoRReasoningService>(),
    provider.GetRequiredService<PatchApplicationService>(),
    provider.GetRequiredService<ValidationService>(),
    provider.GetRequiredService<WeightedBordaRanker>(),
    provider.GetRequiredService<ILLMClient>(),
    logDirectory));
builder.Services.AddRazorPages();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
    app.UseHttpsRedirection();
}

app.UseRouting();
app.UseAuthorization();
app.MapStaticAssets();
app.MapRazorPages().WithStaticAssets();
app.Run();
