using Newtonsoft.Json.Linq;
using UnityEditor;

namespace UnityCliConnector.Tools
{
    [UnityCliTool(
        Description = "What the last script compilation covers: when it finished, how long it took, which assemblies it " +
                      "rebuilt, how many source files changed since the previous compile, and which sources were written " +
                      "after it (stale — the edit is in the project but not in the compile).",
        // Diagnostic: reporting compile provenance must not be gated on compile errors.
        SkipCompilationGate = true)]
    public static class CompileStatus
    {
        public class Parameters
        {
            [ToolParameter("Scan the source tree for files written after the last compile (default: true)")]
            public bool Scan { get; set; }

            [ToolParameter("Milliseconds the source scan may take before it reports itself incomplete (default 200)",
                Aliases = new[] { "scan_budget_ms" })]
            public int ScanBudget { get; set; }
        }

        public static object HandleCommand(JObject @params)
        {
            var p = new ToolParams(@params ?? new JObject());
            bool scan = p.GetBool("scan", true);
            int budget = p.GetInt("scan_budget") ?? p.GetInt("scan_budget_ms") ?? CompileProvenance.DefaultScanBudgetMs;

            if (EditorApplication.isCompiling)
            {
                // The scan would walk a tree Unity is still importing; the provenance record itself is
                // cheap and already answers "when did the last compile finish".
                return new SuccessResponse("Editor is compiling; call again when it finishes.",
                    CompileProvenance.BuildPayload(false, 0));
            }

            var payload = CompileProvenance.BuildPayload(scan, budget);
            return new SuccessResponse("Compile provenance.", payload);
        }
    }
}
