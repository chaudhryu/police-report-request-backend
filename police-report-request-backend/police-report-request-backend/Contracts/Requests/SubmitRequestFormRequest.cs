// Contracts/Requests/SubmitRequestFormRequest.cs
using System.Text.Json;

namespace police_report_request_backend.Contracts.Requests
{
    /// <summary>
    /// Body for creating a submitted request.
    /// NOTE: RequestFormId has been removed from the DB and API.
    /// </summary>
    public sealed class SubmitRequestFormRequest
    {
        public JsonElement SubmittedRequestData { get; set; }
    }
}
