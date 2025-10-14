// Email/Storage/BlobStorageOptions.cs (any folder in your backend)
public sealed class BlobStorageOptions
{
    public string? ConnectionString { get; set; }
    public string ContainerUser { get; set; } = "prr-user";
    public string ContainerOps { get; set; } = "prr-ops";
    public int UploadSasMinutes { get; set; } = 15;
    public int ReadSasDays { get; set; } = 7;
    public long MaxUploadBytes { get; set; } = 25L * 1024 * 1024;
    public string[] AllowedContentTypes { get; set; } = new[] { "image/jpeg", "image/png", "application/pdf" };
}
