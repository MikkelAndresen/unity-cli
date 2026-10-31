using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;

namespace UnityCliConnector.Tools
{
    [UnityCliTool(
        Description = "Errors (and optionally warnings) from the latest compilation: live CompilationPipeline messages " +
                      "plus the console, which also carries Burst errors. While the editor compiles, returns " +
                      "compiling=true and no entries.",
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
        }

        public static object HandleCommand(JObject @params)
        {
            var p = new ToolParams(@params ?? new JObject());
            bool includeWarnings = p.GetBool("include_warnings") || p.GetBool("includeWarnings");
            bool sourceTreeOnly = p.GetBool("source_tree_only") || p.GetBool("sourceTreeOnly");
            int limit = p.GetInt("limit") ?? 50;
            if (limit < 1) limit = 1;

            if (EditorApplication.isCompiling)
                return new SuccessResponse("Editor is compiling; call again when it finishes.", new
                {
                    compiling = true,
                    total = 0,
                    diagnostics = new List<object>(),
                });

            var diags = CompilationGate.GetLiveDiagnostics(includeWarnings)
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
                }).ToList(),
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
