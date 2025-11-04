using System;
using System.Collections.Generic;

namespace police_report_request_backend.Email
{
    /// <summary>Used for the "status changed to Closed" email.</summary>
    public sealed class SubmissionClosedEmailContext
    {
        public int SubmissionId { get; set; }

        // Recipients
        public string? SubmitterEmail { get; set; }
        public string SubmitterDisplayName { get; set; } = "";
        public string? AdminEmail { get; set; }  // the actor changing the status

        public DateTime ClosedUtc { get; set; } = DateTime.UtcNow;

        // Friendly info
        public string? Title { get; set; }
        public string? Location { get; set; }
        public string? IncidentDetailsText { get; set; }

        // Attachments (same behavior as Completed)
        public List<EmailAttachmentInfo> Attachments { get; set; } = new();

        // Optional note to include in user email
        public string? AdminNote { get; set; }  // ← NEW
    }
}
