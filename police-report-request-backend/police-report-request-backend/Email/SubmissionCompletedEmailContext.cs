// Email/SubmissionCompletedEmailContext.cs
using System;
using System.Collections.Generic;

namespace police_report_request_backend.Email
{
    /// <summary>
    /// Used for the "status changed to Completed" email.
    /// </summary>
    public sealed class SubmissionCompletedEmailContext
    {
        public int SubmissionId { get; set; }

        // Recipients
        public string? SubmitterEmail { get; set; }
        public string SubmitterDisplayName { get; set; } = "";
        public string? AdminEmail { get; set; }  // the actor changing the status

        public DateTime CompletedUtc { get; set; } = DateTime.UtcNow;

        // Friendly info
        public string? Title { get; set; }
        public string? Location { get; set; }
        public string? IncidentDetailsText { get; set; }

        // All attachments on the request (we will try to attach them all)
        public List<EmailAttachmentInfo> Attachments { get; set; } = new();
    }
}
