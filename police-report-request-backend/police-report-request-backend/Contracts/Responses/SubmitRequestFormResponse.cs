namespace police_report_request_backend.Contracts.Responses;

public sealed class SubmitRequestFormResponse
{
    public int Id { get; init; }
    public string Status { get; init; } = "Submitted";
}
