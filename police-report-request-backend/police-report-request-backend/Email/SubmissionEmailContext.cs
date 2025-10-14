using System;

namespace police_report_request_backend.Email
{
    public sealed class SubmissionEmailContext
    {
        public int SubmissionId { get; set; }
        public string? SubmitterEmail { get; set; }
        public string SubmitterDisplayName { get; set; } = "";
        public DateTime CreatedUtc { get; set; }
        public string? Title { get; set; }
        public string? Location { get; set; }           // new
        public string? IncidentDetailsText { get; set; } // new
    }
}
