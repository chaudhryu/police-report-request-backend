namespace police_report_request_backend.Contracts.Requests
{
    public sealed class UpdateSubmittedRequestStatusRequest
    {
        public string Status { get; set; } = default!;
    }
}
