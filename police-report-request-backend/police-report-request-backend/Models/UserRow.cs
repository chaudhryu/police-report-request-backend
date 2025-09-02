namespace police_report_request_backend.Models;

public sealed class UserRow
{
    public string Badge { get; set; } = default!;
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public string? DisplayName { get; set; }
    public string? Email { get; set; }
    public string? Position { get; set; }
    public int? IsAdmin { get; set; }
    public DateTime? CreatedDate { get; set; }
    public string? LastUpdatedBy { get; set; }
    public DateTime? LastUpdatedDate { get; set; }
}
