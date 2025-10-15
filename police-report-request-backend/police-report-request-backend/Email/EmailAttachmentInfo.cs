// Email/EmailAttachmentInfo.cs
using System;

namespace police_report_request_backend.Email
{
    /// <summary>
    /// Minimal info required to download/attach a blob and build a read link.
    /// </summary>
    public sealed class EmailAttachmentInfo
    {
        public string Container { get; set; } = "";
        public string BlobName { get; set; } = "";
        public string? FileName { get; set; }
        public string ContentType { get; set; } = "application/octet-stream";
        public long Length { get; set; }
        public DateTime? UploadedUtc { get; set; }
    }
}
