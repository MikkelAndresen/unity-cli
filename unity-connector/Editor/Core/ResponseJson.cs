using System;
using Newtonsoft.Json;

namespace UnityCliConnector
{
    /// <summary>
    /// Turns a tool result into the JSON the CLI parses. Serialization is the one step in the request
    /// pipeline the CLI cannot recover from: an exception here escapes the request handler, the
    /// connection is closed with no body, and the CLI reports the command as answered
    /// ("connection closed before response"). So nothing here is allowed to throw.
    /// </summary>
    public static class ResponseJson
    {
        public const int PREVIEW_CHARS = 300;

        /// Hand-written rather than serialized, so it works even when the serializer itself is the
        /// thing that failed.
        public const string LAST_RESORT =
            "{\"success\":false,\"message\":\"serialization failed\",\"data\":{\"code\":\"serialization_failed\"}}";

        public static bool TrySerialize(object result, out string json, out string error)
        {
            json = null;
            error = null;
            try
            {
                json = JsonConvert.SerializeObject(result);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        /// Serializes `result`, or answers with an error naming the tool whose payload failed.
        /// `command` is the request's command; the value is already lost, so say what it was.
        public static string SerializeOrError(object result, string command)
        {
            if (TrySerialize(result, out var json, out var error)) return json;

            var failure = new ErrorResponse(
                $"{command} returned a value that could not be serialized: {error}",
                new SerializationFailureInfo(
                    ErrorCodes.SerializationFailed,
                    command,
                    error,
                    result?.GetType().FullName,
                    Preview(result)));

            // The fallback is built by hand if even serializing the fallback failed.
            return TrySerialize(failure, out var fallbackJson, out _) ? fallbackJson : LAST_RESORT;
        }

        static string Preview(object value)
        {
            if (value == null) return null;
            try
            {
                var text = value.ToString();
                if (string.IsNullOrEmpty(text)) return null;
                return text.Length > PREVIEW_CHARS ? text.Substring(0, PREVIEW_CHARS) + "…" : text;
            }
            catch (Exception ex)
            {
                return $"<ToString threw {ex.GetType().Name}>";
            }
        }

        /// `data` of a response whose payload could not be serialized. The command ran; only the
        /// result is lost, which is the difference the caller needs.
        public readonly struct SerializationFailureInfo
        {
            public readonly string code;
            public readonly string command;
            public readonly string error;
            public readonly string result_type;
            public readonly string result_preview;

            public SerializationFailureInfo(string code, string command, string error,
                string resultType, string resultPreview)
            {
                this.code = code;
                this.command = command;
                this.error = error;
                result_type = resultType;
                result_preview = resultPreview;
            }
        }
    }
}
