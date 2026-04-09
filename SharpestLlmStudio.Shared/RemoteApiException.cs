using System;
using System.Collections.Generic;
using System.Net;

namespace SharpestLlmStudio.Shared
{
    public sealed class RemoteApiException : Exception
    {
        public HttpStatusCode? StatusCode { get; }
        public string RawBody { get; set; } = string.Empty;
        public string ErrorMessage { get; set; } = string.Empty;
        public List<string> FieldViolations { get; set; } = new();

        public RemoteApiException(HttpStatusCode? statusCode, string message) : base(message)
        {
            this.StatusCode = statusCode;
            this.ErrorMessage = message ?? string.Empty;
            this.RawBody = message ?? string.Empty;
        }
    }
}
