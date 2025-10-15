using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Azure.Storage.Blobs;
using Azure.Storage.Sas;
using Microsoft.Extensions.Options;

namespace police_report_request_backend.Storage
{
    public sealed class StorageSasService : IStorageSasService
    {
        private readonly BlobServiceClient _svc;
        private readonly BlobStorageOptions _opts;

        public StorageSasService(IOptions<BlobStorageOptions> opts)
        {
            _opts = opts.Value ?? throw new ArgumentNullException(nameof(opts));
            if (string.IsNullOrWhiteSpace(_opts.ConnectionString))
                throw new InvalidOperationException("Storage:ConnectionString is missing.");
            _svc = new BlobServiceClient(_opts.ConnectionString);
        }

        public async Task<(Uri uploadUri, string container, string blobName)> CreateUploadSasAsync(
            string purpose,
            string fileName,
            string contentType,
            long fileSize,
            int? submissionId = null,
            CancellationToken ct = default)
        {
            if (fileSize <= 0 || fileSize > _opts.MaxUploadBytes)
                throw new InvalidOperationException($"File too large. Max {_opts.MaxUploadBytes} bytes.");

            if (_opts.AllowedContentTypes.Length > 0 &&
                !_opts.AllowedContentTypes.Contains(contentType ?? "", StringComparer.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Content type not allowed: {contentType}");
            }

            var safeName = SanitizeFileName(fileName ?? "file");
            var container = string.Equals(purpose, "ops", StringComparison.OrdinalIgnoreCase)
                ? _opts.ContainerOps
                : _opts.ContainerUser;

            var blobName = string.Equals(purpose, "ops", StringComparison.OrdinalIgnoreCase) && submissionId.HasValue
                ? $"submissions/{submissionId}/ops/{Guid.NewGuid():N}_{safeName}"
                : $"incoming/{DateTime.UtcNow:yyyy/MM/dd}/{Guid.NewGuid():N}_{safeName}";

            var cont = _svc.GetBlobContainerClient(container);
            await cont.CreateIfNotExistsAsync(cancellationToken: ct);
            var blob = cont.GetBlobClient(blobName);

            // Build a SAS that allows Create/Write/Add for a short period
            var sas = new BlobSasBuilder
            {
                BlobContainerName = container,
                BlobName = blobName,
                Resource = "b",
                ExpiresOn = DateTimeOffset.UtcNow.AddMinutes(_opts.UploadSasMinutes)
            };
            sas.SetPermissions(BlobSasPermissions.Create | BlobSasPermissions.Write | BlobSasPermissions.Add);

            // Generate SAS using the account key from the connection string
            var uri = blob.GenerateSasUri(sas);
            return (uri, container, blobName);
        }

        public Uri CreateReadSasUri(string container, string blobName, TimeSpan? lifetime = null)
        {
            var cont = _svc.GetBlobContainerClient(container);
            var blob = cont.GetBlobClient(blobName);

            var sas = new BlobSasBuilder
            {
                BlobContainerName = container,
                BlobName = blobName,
                Resource = "b",
                ExpiresOn = DateTimeOffset.UtcNow.Add(lifetime ?? TimeSpan.FromDays(_opts.ReadSasDays))
            };
            sas.SetPermissions(BlobSasPermissions.Read);

            return blob.GenerateSasUri(sas);
        }

        private static string SanitizeFileName(string s)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var clean = new string((s ?? "file").Where(c => !invalid.Contains(c)).ToArray());
            return string.IsNullOrEmpty(clean) ? "file" : clean;
        }
    }
}
