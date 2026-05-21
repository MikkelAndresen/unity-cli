using System;

namespace UnityCliConnector
{
    /// <summary>
    /// Marks a static class as a CLI tool handler.
    /// The class must have a static HandleCommand(Newtonsoft.Json.Linq.JObject) method.
    /// Class name is auto-converted to snake_case for the command name.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class)]
    public class UnityCliToolAttribute : Attribute
    {
        public string Description { get; set; } = "";
        public string Name { get; set; }
        public string Group { get; set; } = "";

        /// When true, CommandRouter skips the compilation gate for this tool — the
        /// handler runs even if the editor has outstanding compile errors. Use for
        /// diagnostic tools (read_console, compile_errors, …) that must remain
        /// callable in a broken state. Callers can still force-skip per call via
        /// the `skip_compile_check` parameter.
        public bool SkipCompilationGate { get; set; }
    }

    /// <summary>
    /// Marks a property in a nested Parameters class as a tool parameter.
    /// Used for auto-generating help text and parameter schemas.
    /// </summary>
    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false)]
    public class ToolParameterAttribute : Attribute
    {
        public string Name { get; set; }
        public string Description { get; set; }
        public bool Required { get; set; } = false;
        public string DefaultValue { get; set; }

        public ToolParameterAttribute(string description)
        {
            Description = description;
        }

        public ToolParameterAttribute(string name, string description)
        {
            Name = name;
            Description = description;
        }
    }
}
