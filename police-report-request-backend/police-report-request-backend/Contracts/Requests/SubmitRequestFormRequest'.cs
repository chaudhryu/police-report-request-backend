using System.ComponentModel.DataAnnotations;
using System.Text.Json;

namespace police_report_request_backend.Contracts.Requests;

public sealed class SubmitRequestFormRequest
{
    [Range(1, int.MaxValue)]
    public int RequestFormId { get; set; }

    // Arbitrary JSON for the filled form
    [Required]
    public JsonElement SubmittedRequestData { get; set; }
}
