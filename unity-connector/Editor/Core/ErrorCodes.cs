namespace UnityCliConnector
{
    /// <summary>
    /// Machine-readable codes carried in an error response's `data.code`. The Go CLI maps them to
    /// next-step hints (cmd/connector_error.go), so a renamed or dropped code is a silent CLI
    /// regression — change both sides together.
    /// </summary>
    public static class ErrorCodes
    {
        /// The request was accepted, but the editor's main thread never started it. The command did not run.
        public const string MainThreadBusy = "main_thread_busy";

        /// Unity reloaded or quit while the request was in flight.
        public const string DomainReload = "domain_reload";

        /// The tool produced a value that could not be serialized; the command did run.
        public const string SerializationFailed = "serialization_failed";
    }
}
