// Contracts/Requests/SubmitRequestFormMultipart.cs
using Microsoft.AspNetCore.Http;

namespace police_report_request_backend.Contracts.Requests
{
    public sealed class SubmitRequestFormMultipart
    {
        /// <summary>Stringified JSON of the same payload you currently send.</summary>
        public string SubmittedRequestData { get; set; } = "{}";

        /// <summary>Files chosen by the requestor.</summary>
        public IFormFile[]? Attachments { get; set; }
    }
}
