using Newtonsoft.Json;

namespace UnityCliConnector
{
    public class SuccessResponse
    {
        public bool success = true;
        public string message;
        public object data;

        /// How the call itself was served (e.g. exec: cache state and compile time) — kept
        /// out of `data` so callers parsing a tool's result see the same shape. Omitted
        /// from the JSON when unset, which is every other tool.
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public object meta;

        public SuccessResponse(string message, object data = null, object meta = null)
        {
            this.message = message;
            this.data = data;
            this.meta = meta;
        }
    }

    public class ErrorResponse
    {
        public bool success = false;
        public string message;
        public object data;

        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public object meta;

        public ErrorResponse(string message, object data = null, object meta = null)
        {
            this.message = message;
            this.data = data;
            this.meta = meta;
        }
    }
}
