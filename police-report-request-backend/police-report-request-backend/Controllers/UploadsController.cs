using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using police_report_request_backend.Storage;

namespace police_report_request_backend.Controllers;

[ApiController]
[Route("api/uploads")]
[Authorize]
public sealed class UploadsController : ControllerBase
{
    private readonly IStorageSasService _sas;
    private readonly ILogger<UploadsController> _log;

    public UploadsController(IStorageSasService sas, ILogger<UploadsController> log)
    {
        _sas = sas;
        _log = log;
    }

    public sealed class CreateUploadSasRequest
    {
        public string FileName { get; set; } = "";
        public string ContentType { get; set; } = "application/octet-stream";
        public long FileSize { get; set; }
        public string Purpose { get; set; } = "user"; // "user" or "ops"
        public int? SubmissionId { get; set; }        // required if ops
    }

    public sealed class CreateUploadSasResponse
    {
        public string UploadUrl { get; set; } = "";
        public string Container { get; set; } = "";
        public string BlobName { get; set; } = "";
        public string? PublicUrl { get; set; }
    }

    [HttpPost("sas")]
    public async Task<IActionResult> CreateUploadSas([FromBody] CreateUploadSasRequest req, CancellationToken ct)
    {
        if (string.Equals(req.Purpose, "ops", StringComparison.OrdinalIgnoreCase) && !req.SubmissionId.HasValue)
            return BadRequest("submissionId is required for ops uploads.");

        // TODO (optional): enforce admin for ops SAS
        var (uri, container, blobName) = await _sas.CreateUploadSasAsync(
            req.Purpose, req.FileName, req.ContentType, req.FileSize, req.SubmissionId, ct);

        return Ok(new CreateUploadSasResponse
        {
            UploadUrl = uri.ToString(),
            Container = container,
            BlobName = blobName,
            PublicUrl = null // add read SAS later if you want previews
        });
    }
}
