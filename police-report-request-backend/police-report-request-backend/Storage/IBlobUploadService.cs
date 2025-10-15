using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using police_report_request_backend.Email;

namespace police_report_request_backend.Storage
{
    public interface IBlobUploadService
    {
        /// <summary>
        /// Save files, return info that can be emailed and also embedded in the submission JSON.
        /// role: "user" for requestor uploads, "ops" for admin uploads.
        /// </summary>
        Task<List<EmailAttachmentInfo>> SaveManyAsync(
            IEnumerable<IFormFile> files,
            string createdByBadge,
            int? submissionId = null,
            string role = "user",
            CancellationToken ct = default);
    }
}
