using ChainOfRepair.Core.Diagnostics;
using ChainOfRepair.Core.Models;
using ChainOfRepair.Core.Pipeline;
using ChainOfRepair.Core.Ranking;
using ChainOfRepair.Core.Reasoning;
using ChainOfRepair.Core.Validation;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false).AddEnvironmentVariables();
var logDirectory = Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, "..", "..", "logs"));
var promptDirectory = Directory.Exists(Path.Combine(builder.Environment.ContentRootPath, "prompts"))
    ? Path.Combine(builder.Environment.ContentRootPath, "prompts")
    : Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, "..", "..", "prompts"));
var llmSettings = new LlmSettings
{
    Provider = builder.Configuration["LLM:Provider"] ?? "OpenAI",
    Model = Environment.GetEnvironmentVariable("COR_OPENAI_MODEL") ?? builder.Configuration["LLM:Model"] ?? "gpt-4o",
    Temperature = float.TryParse(builder.Configuration["LLM:Temperature"], out var temperature) ? temperature : 0.2f,
    MaxTokens = int.TryParse(builder.Configuration["LLM:MaxTokens"], out var maxTokens) ? maxTokens : 2048,
    TotalTokenBudget = builder.Configuration.GetValue("LLM:TotalTokenBudget", 16000),
    MaxCalls = builder.Configuration.GetValue("LLM:MaxCalls", 20),
    OutputLimits = builder.Configuration.GetSection("LLM:OutputLimits").Get<StageTokenLimits>() ?? new(),
    UseMockClient = bool.TryParse(builder.Configuration["LLM:UseMockClient"], out var useMockClient) && useMockClient
};

builder.Services.AddSingleton(llmSettings);
if (llmSettings.UseMockClient)
    throw new InvalidOperationException("The application requires live OpenAI calls. Disable LLM:UseMockClient.");
builder.Services.AddScoped<ILLMClient, OpenAiLLMClient>();

builder.Services.AddSingleton(new PromptTemplateService(promptDirectory));
builder.Services.AddScoped<CoRReasoningService>();
builder.Services.AddSingleton<PatchApplicationService>();
builder.Services.AddSingleton(new ValidationService(builder.Configuration.GetSection("Validation").Get<ValidationConfiguration>()));
builder.Services.AddSingleton<WeightedBordaRanker>();
builder.Services.AddSingleton(new DiagnosticsService(llmSettings, logDirectory));
builder.Services.AddScoped(provider => new RepairPipelineService(
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
