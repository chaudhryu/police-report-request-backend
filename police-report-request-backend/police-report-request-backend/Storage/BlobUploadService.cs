using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Azure.Storage.Blobs;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using police_report_request_backend.Email;

namespace police_report_request_backend.Storage
{
    public sealed class BlobUploadService : IBlobUploadService
    {
        private readonly BlobStorageOptions _opts;
        private readonly BlobServiceClient _svc;
        private static readonly HashSet<string> AllowedExts = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".pdf", ".doc", ".docx" };

        public BlobUploadService(IOptions<BlobStorageOptions> opts)
        {
            _opts = opts.Value ?? throw new InvalidOperationException("Storage options missing.");
            if (string.IsNullOrWhiteSpace(_opts.ConnectionString))
                throw new InvalidOperationException("Storage:ConnectionString missing.");

            _svc = new BlobServiceClient(_opts.ConnectionString);
        }

        public async Task<List<EmailAttachmentInfo>> SaveManyAsync(
            IEnumerable<IFormFile> files,
            string createdByBadge,
            int? submissionId = null,
            string role = "user",
            CancellationToken ct = default)
        {
            var list = new List<EmailAttachmentInfo>();
            if (files is null) return list;

            var containerName = string.Equals(role, "ops", StringComparison.OrdinalIgnoreCase)
                ? _opts.ContainerOps
                : _opts.ContainerUser;

            var cont = _svc.GetBlobContainerClient(containerName);
            await cont.CreateIfNotExistsAsync(cancellationToken: ct);

            foreach (var f in files)
            {
                if (f == null || f.Length <= 0) continue;

                if (f.Length > _opts.MaxUploadBytes)
                    throw new InvalidOperationException($"File too large: {f.FileName} ({f.Length} bytes).");

                if (!IsAllowed(f))
                    throw new InvalidOperationException($"File type not allowed: {f.FileName} ({f.ContentType}).");

                var safe = SanitizeFileName(f.FileName);
                var blobName = submissionId.HasValue
                    ? $"submissions/{submissionId.Value}/{role}/{Guid.NewGuid():N}_{safe}"
                    : $"incoming/{DateTime.UtcNow:yyyy/MM/dd}/{Guid.NewGuid():N}_{safe}";

                var blob = cont.GetBlobClient(blobName);
                await using var stream = f.OpenReadStream();
                await blob.UploadAsync(stream, overwrite: true, cancellationToken: ct);

                list.Add(new EmailAttachmentInfo
                {
                    Container = containerName,
                    BlobName = blobName,
                    FileName = safe,
                    ContentType = string.IsNullOrWhiteSpace(f.ContentType) ? "application/octet-stream" : f.ContentType,
                    Length = f.Length,
                    UploadedUtc = DateTime.UtcNow
                });
            }

            return list;
        }

        private bool IsAllowed(IFormFile file)
        {
            var ct = file.ContentType ?? "";
            var okByCt = (_opts.AllowedContentTypes?.Any() == true)
                ? _opts.AllowedContentTypes.Contains(ct, StringComparer.OrdinalIgnoreCase)
                : true;

            if (okByCt) return true;

            var ext = Path.GetExtension(file.FileName);
            return AllowedExts.Contains(ext);
        }

        private static string SanitizeFileName(string s)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var clean = new string(s.Where(c => !invalid.Contains(c)).ToArray());
            return string.IsNullOrWhiteSpace(clean) ? "file" : clean;
        }
    }
}
