using System;

namespace police_report_request_backend.Storage
{
    public sealed class BlobStorageOptions
    {
        public string? ConnectionString { get; set; }

        public string ContainerUser { get; set; } = "prrsystemtest";
        public string ContainerOps { get; set; } = "prrsystemtest";

        public int UploadSasMinutes { get; set; } = 15;
        public int ReadSasDays { get; set; } = 7;

        public long MaxUploadBytes { get; set; } = 50L * 1024 * 1024; // 50 MB

        public string[] AllowedContentTypes { get; set; } = new[]
        {
            "image/jpeg",
            "image/png",
            "application/pdf",
            "application/msword",
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document"
        };
    }
}
