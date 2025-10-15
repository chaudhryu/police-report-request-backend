using System;
using System.Threading;
using System.Threading.Tasks;

namespace police_report_request_backend.Storage
{
    public interface IStorageSasService
    {
        /// <summary>
        /// Create an upload SAS URL for a single blob.
        /// purpose: "user" or "ops"
        /// submissionId: required when purpose == "ops"
        /// </summary>
        Task<(Uri uploadUri, string container, string blobName)> CreateUploadSasAsync(
            string purpose,
            string fileName,
            string contentType,
            long fileSize,
            int? submissionId = null,
            CancellationToken ct = default);

        /// <summary> Create a read-only SAS URL for an existing blob. </summary>
        Uri CreateReadSasUri(string container, string blobName, TimeSpan? lifetime = null);
    }
}
