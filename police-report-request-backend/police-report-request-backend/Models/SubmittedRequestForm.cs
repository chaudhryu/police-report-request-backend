// Models/SubmittedRequestForm.cs
public sealed class SubmittedRequestForm
{
    public int Id { get; set; }
    public int RequestFormId { get; set; }

    // FK to Users.Badge (varchar(50))
    public string CreatedBy { get; set; } = default!;

    // Stored as NVARCHAR(MAX) containing JSON
    public string SubmittedRequestData { get; set; } = default!;

    // Defaults to 'Submitted' in DB
    public string Status { get; set; } = "Submitted";

    // DB default: sysutcdatetime()
    public DateTime CreatedDate { get; set; }

    public string? LastUpdatedBy { get; set; }

    // DB default: sysutcdatetime()
    public DateTime LastUpdatedDate { get; set; }
}
