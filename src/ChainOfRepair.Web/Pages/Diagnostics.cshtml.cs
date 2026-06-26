using ChainOfRepair.Core.Diagnostics;
using ChainOfRepair.Core.Models;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ChainOfRepair.Web.Pages;

public sealed class DiagnosticsModel : PageModel
{
    private readonly DiagnosticsService _diagnosticsService;

    public DiagnosticsModel(DiagnosticsService diagnosticsService)
    {
        _diagnosticsService = diagnosticsService;
    }

    public EnvironmentDiagnostics Diagnostics { get; private set; } = new();

    public void OnGet()
    {
        Diagnostics = _diagnosticsService.GetDiagnostics();
    }
}
