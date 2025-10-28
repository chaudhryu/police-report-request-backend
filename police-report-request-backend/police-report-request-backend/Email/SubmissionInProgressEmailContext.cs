// Add this new class definition
using police_report_request_backend.Email;

public sealed class SubmissionInProgressEmailContext
{
    public int SubmissionId { get; set; }
    public string? SubmitterEmail { get; set; }
    public string SubmitterDisplayName { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; }
    public string? Location { get; set; }
    public string? Title { get; set; }
    public string? IncidentDetailsText { get; set; }
    public IReadOnlyList<EmailAttachmentInfo> Attachments { get; set; } = new List<EmailAttachmentInfo>();
}