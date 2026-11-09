using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;

namespace UnityCliConnector.Tools
{
    [UnityCliTool(
        Description = "Errors (and optionally warnings) from the latest compilation: live CompilationPipeline messages " +
                      "plus the console, which also carries Burst errors. While the editor compiles, returns " +
                      "compiling=true and no entries. Missing members/types and obsolete APIs come with a hint " +
                      "naming the closest real ones.",
        // Reports compile errors; gating it on them would hide them behind the gate's error response.
        SkipCompilationGate = true)]
    public static class CompileDiagnostics
    {
        public class Parameters
        {
            [ToolParameter("Include warnings (default: errors only)")]
            public bool IncludeWarnings { get; set; }

            [ToolParameter("Drop diagnostics in files under Library/ or PackageCache/ (third-party package code)")]
            public bool SourceTreeOnly { get; set; }

            [ToolParameter("Maximum number of diagnostics to return (default 50)")]
            public int Limit { get; set; }

            [ToolParameter("Include what the reported compile covers (compiled_at, rebuilt assemblies, stale sources) (default: true)")]
            public bool IncludeProvenance { get; set; }

            [ToolParameter("Milliseconds the provenance source scan may take before it reports itself incomplete (default 200)")]
            public int ScanBudget { get; set; }

            [ToolParameter("Name the closest real member/type for a missing or obsolete API (default: true)")]
            public bool IncludeHints { get; set; }

            [ToolParameter("Milliseconds the whole hint pass may take (default 100)")]
            public int HintBudget { get; set; }
        }

        public static object HandleCommand(JObject @params)
        {
            var p = new ToolParams(@params ?? new JObject());
            bool includeWarnings = p.GetBool("include_warnings") || p.GetBool("includeWarnings");
            bool sourceTreeOnly = p.GetBool("source_tree_only") || p.GetBool("sourceTreeOnly");
            int limit = p.GetInt("limit") ?? 50;
            if (limit < 1) limit = 1;
            bool includeProvenance = ParamCoercion.CoerceBoolNullable(
                p.GetRaw("include_provenance") ?? p.GetRaw("includeProvenance")) ?? true;
            int scanBudget = p.GetInt("scan_budget") ?? CompileProvenance.DefaultScanBudgetMs;
            bool includeHints = ParamCoercion.CoerceBoolNullable(
                p.GetRaw("include_hints") ?? p.GetRaw("includeHints")) ?? true;
            int hintBudget = p.GetInt("hint_budget") ?? p.GetInt("hintBudget") ?? CompileHints.DefaultBudgetMs;

            if (EditorApplication.isCompiling)
                return new SuccessResponse("Editor is compiling; call again when it finishes.", new
                {
                    compiling = true,
                    total = 0,
                    diagnostics = new List<object>(),
                    // Provenance of the compile before this one; cheap, and the caller learns the
                    // editor is busy either way.
                    provenance = CompileProvenance.BuildPayload(false, 0),
                });

            var diags = CompilationGate.GetLiveDiagnostics(includeWarnings, includeHints ? hintBudget : -1)
                .Where(d => !sourceTreeOnly || !IsThirdParty(d.File))
                .ToList();

            return new SuccessResponse($"{diags.Count(d => d.Severity == "error")} error(s).", new
            {
                compiling = false,
                total = diags.Count,
                diagnostics = diags.Take(limit).Select(d => new
                {
                    file = d.File,
                    line = d.Line,
                    col = d.Col,
                    severity = d.Severity,
                    code = d.Code,
                    message = d.Message,
                    hint = string.IsNullOrEmpty(d.Hint) ? null : d.Hint,
                }).ToList(),
                // One provenance line per compile-related result is the point: the caller must not
                // have to make a second call to know whether this answer covers its own edit.
                provenance = includeProvenance ? CompileProvenance.BuildPayload(true, scanBudget) : null,
            });
        }

        internal static bool IsThirdParty(string file)
        {
            if (string.IsNullOrEmpty(file)) return false;
            var path = file.Replace('\\', '/');
            return path.StartsWith("Library/") || path.Contains("PackageCache/");
        }
    }
}
