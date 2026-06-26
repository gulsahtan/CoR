using ChainOfRepair.Core.Models;
using ChainOfRepair.Core.Pipeline;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ChainOfRepair.Web.Pages;

public class IndexModel : PageModel
{
    private readonly RepairPipelineService _pipeline;

    public IndexModel(RepairPipelineService pipeline)
    {
        _pipeline = pipeline;
    }

    [BindProperty]
    public RepairInput Input { get; set; } = new();

    public RepairPipelineResult? Result { get; private set; }

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        Result = await _pipeline.RunAsync(new RepairRequest
        {
            SourceCode = Input.SourceCode,
            FailingOutput = Input.FailingOutput,
            BugDescription = Input.BugDescription,
            Language = Input.Language,
            TopK = Input.TopK,
            FileName = Input.FileName
        }, cancellationToken);
        return Page();
    }
}

public sealed class RepairInput
{
    public string SourceCode { get; set; } = SampleJava;
    public string FailingOutput { get; set; } = "java.lang.NullPointerException\n\tat Example.divide(Example.java:3)";
    public string? BugDescription { get; set; } = "Division fails when input is null or zero.";
    public SupportedLanguage Language { get; set; } = SupportedLanguage.Java;
    public int TopK { get; set; } = 5;
    public string? FileName { get; set; } = "Example.java";

    private const string SampleJava = """
        public class Example {
            public int divide(Integer value) {
                return 10 / value;
            }
        }
        """;
}
