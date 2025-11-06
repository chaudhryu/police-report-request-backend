using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Mail;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using police_report_request_backend.Email;
using police_report_request_backend.Storage;
using police_report_request_backend.Helpers;

public sealed class SmtpEmailNotificationService : IEmailNotificationService
{
    private readonly SmtpEmailOptions _opts;
    private readonly ILogger<SmtpEmailNotificationService> _log;
    private readonly IHostEnvironment _env;

    // storage (download + links)
    private readonly BlobStorageOptions _storage;
    private readonly IStorageSasService _sas;
    private readonly BlobServiceClient _blobSvc;

    // ---- Mail guardrails ----
    private const long MaxAttachBytesEach = 12L * 1024 * 1024;  // 12 MB per file
    private const long MaxAttachBytesTotal = 22L * 1024 * 1024; // 22 MB total

    public SmtpEmailNotificationService(
        IOptions<SmtpEmailOptions> opts,
        ILogger<SmtpEmailNotificationService> log,
        IHostEnvironment env,
        IOptions<BlobStorageOptions> storageOpts,
        IStorageSasService sas)
    {
        _opts = opts.Value;
        _log = log;
        _env = env;

        _storage = storageOpts.Value;
        _sas = sas ?? throw new ArgumentNullException(nameof(sas));
        _blobSvc = new BlobServiceClient(_storage.ConnectionString);
    }

    // ====== "submission created" ======
    public async Task SendSubmissionNotificationsAsync(SubmissionEmailContext ctx, CancellationToken ct = default)
    {
        if (!ValidateSmtp()) return;

        try
        {
            using var smtp = CreateClient();
            var links = BuildAttachmentLinksForEmail(ctx.Attachments);

            // submitter
            if (!string.IsNullOrWhiteSpace(ctx.SubmitterEmail))
            {
                using var msgUser = new MailMessage
                {
                    From = new MailAddress(_opts.From),
                    Subject = BuildCreatedSubject(ctx),
                    SubjectEncoding = Encoding.UTF8,
                    Body = BuildCreatedBody(ctx, links),
                    BodyEncoding = Encoding.UTF8,
                    IsBodyHtml = false
                };

                using var attached = await AttachEligibleFilesAsync(msgUser, ctx.Attachments, ct);

                msgUser.To.Add(ctx.SubmitterEmail);
                await smtp.SendMailAsync(msgUser);
            }

            // ops distribution (optional)
            var ops = SplitAddresses(_opts.OpsTo);
            if (ops.Count > 0)
            {
                using var msgOps = new MailMessage
                {
                    From = new MailAddress(_opts.From),
                    Subject = BuildOpsCreatedSubject(ctx),
                    SubjectEncoding = Encoding.UTF8,
                    Body = BuildOpsCreatedBody(ctx, links),
                    BodyEncoding = Encoding.UTF8,
                    IsBodyHtml = false
                };
                using var attachedOps = await AttachEligibleFilesAsync(msgOps, ctx.Attachments, ct);
                foreach (var to in ops) msgOps.To.Add(to);
                await smtp.SendMailAsync(msgOps);
            }
        }
        catch (SmtpException ex)
        {
            _log.LogError(ex, "SMTP send failed (created) for submission {Id}", ctx.SubmissionId);
            throw;
        }
    }

    // ====== "Completed" ======
    public async Task SendSubmissionCompletedAsync(SubmissionCompletedEmailContext ctx, CancellationToken ct = default)
    {
        if (!ValidateSmtp()) return;

        try
        {
            using var smtp = CreateClient();
            var links = BuildAttachmentLinksForEmail(ctx.Attachments);

            // submitter
            if (!string.IsNullOrWhiteSpace(ctx.SubmitterEmail))
            {
                using var msgUser = new MailMessage
                {
                    From = new MailAddress(_opts.From),
                    Subject = BuildUserCompletedSubject(ctx),
                    SubjectEncoding = Encoding.UTF8,
                    Body = BuildUserCompletedBody(ctx, links),
                    BodyEncoding = Encoding.UTF8,
                    IsBodyHtml = false
                };

                using var attachedUser = await AttachEligibleFilesAsync(msgUser, ctx.Attachments, ct);

                msgUser.To.Add(ctx.SubmitterEmail);
                await smtp.SendMailAsync(msgUser);
            }

            // admin (actor)
            if (!string.IsNullOrWhiteSpace(ctx.AdminEmail))
            {
                using var msgAdmin = new MailMessage
                {
                    From = new MailAddress(_opts.From),
                    Subject = BuildAdminCompletedSubject(ctx),
                    SubjectEncoding = Encoding.UTF8,
                    Body = BuildAdminCompletedBody(ctx, links),
                    BodyEncoding = Encoding.UTF8,
                    IsBodyHtml = false
                };

                using var attachedAdmin = await AttachEligibleFilesAsync(msgAdmin, ctx.Attachments, ct);

                msgAdmin.To.Add(ctx.AdminEmail);
                await smtp.SendMailAsync(msgAdmin);
            }
        }
        catch (SmtpException ex)
        {
            _log.LogError(ex, "SMTP send failed (completed) for submission {Id}", ctx.SubmissionId);
            throw;
        }
    }

    // ====== "In Progress" ======
    public async Task SendSubmissionInProgressAsync(SubmissionInProgressEmailContext ctx, CancellationToken ct = default)
    {
        if (!ValidateSmtp()) return;

        try
        {
            using var smtp = CreateClient();
            var links = BuildAttachmentLinksForEmail(ctx.Attachments);

            // submitter
            if (!string.IsNullOrWhiteSpace(ctx.SubmitterEmail))
            {
                using var msgUser = new MailMessage
                {
                    From = new MailAddress(_opts.From),
                    Subject = BuildUserInProgressSubject(ctx),
                    SubjectEncoding = Encoding.UTF8,
                    Body = BuildUserInProgressBody(ctx, links),
                    BodyEncoding = Encoding.UTF8,
                    IsBodyHtml = false
                };

                using var attachedUser = await AttachEligibleFilesAsync(msgUser, ctx.Attachments, ct);

                msgUser.To.Add(ctx.SubmitterEmail);
                await smtp.SendMailAsync(msgUser);
            }
        }
        catch (SmtpException ex)
        {
            _log.LogError(ex, "SMTP send failed (in progress) for submission {Id}", ctx.SubmissionId);
            throw;
        }
    }

    private string BuildUserInProgressSubject(SubmissionInProgressEmailContext ctx)
    {
        var loc = FormatLocation(ctx.Location, ctx.Title);
        var suffix = string.IsNullOrWhiteSpace(loc) ? "" : $" — {loc}";
        return $"{SubjectPrefix} Your request #{ctx.SubmissionId} is in progress{suffix}";
    }

    private string BuildUserInProgressBody(SubmissionInProgressEmailContext ctx, IReadOnlyList<(string FileName, Uri Url, long Len)> links)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Hello {ctx.SubmitterDisplayName},");
        sb.AppendLine();
        sb.AppendLine($"This is an update on your request #{ctx.SubmissionId}. Our team has begun processing it and is gathering the necessary information.");
        sb.AppendLine();

        // ---- Admin note (optional) ----
        if (!string.IsNullOrWhiteSpace(ctx.AdminNote))
        {
            sb.AppendLine("Note from the administrator:");
            sb.AppendLine(ctx.AdminNote);
            sb.AppendLine();
        }

        AppendAttachmentLinks(sb, links, "The following attachments were included with your original request:");
        sb.AppendLine();
        AppendAutoFooter(sb);
        return sb.ToString();
    }

    // ====== "Closed" ======
    public async Task SendSubmissionClosedAsync(SubmissionClosedEmailContext ctx, CancellationToken ct = default)
    {
        if (!ValidateSmtp()) return;

        try
        {
            using var smtp = CreateClient();
            var links = BuildAttachmentLinksForEmail(ctx.Attachments);

            // submitter
            if (!string.IsNullOrWhiteSpace(ctx.SubmitterEmail))
            {
                using var msgUser = new MailMessage
                {
                    From = new MailAddress(_opts.From),
                    Subject = BuildUserClosedSubject(ctx),
                    SubjectEncoding = Encoding.UTF8,
                    Body = BuildUserClosedBody(ctx, links),
                    BodyEncoding = Encoding.UTF8,
                    IsBodyHtml = false
                };

                using var attachedUser = await AttachEligibleFilesAsync(msgUser, ctx.Attachments, ct);

                msgUser.To.Add(ctx.SubmitterEmail);
                await smtp.SendMailAsync(msgUser);
            }

            // admin (actor)
            if (!string.IsNullOrWhiteSpace(ctx.AdminEmail))
            {
                using var msgAdmin = new MailMessage
                {
                    From = new MailAddress(_opts.From),
                    Subject = BuildAdminClosedSubject(ctx),
                    SubjectEncoding = Encoding.UTF8,
                    Body = BuildAdminClosedBody(ctx, links),
                    BodyEncoding = Encoding.UTF8,
                    IsBodyHtml = false
                };

                using var attachedAdmin = await AttachEligibleFilesAsync(msgAdmin, ctx.Attachments, ct);

                msgAdmin.To.Add(ctx.AdminEmail);
                await smtp.SendMailAsync(msgAdmin);
            }
        }
        catch (SmtpException ex)
        {
            _log.LogError(ex, "SMTP send failed (closed) for submission {Id}", ctx.SubmissionId);
            throw;
        }
    }

    private string BuildUserClosedSubject(SubmissionClosedEmailContext ctx)
    {
        var loc = FormatLocation(ctx.Location, ctx.Title);
        var suffix = string.IsNullOrWhiteSpace(loc) ? "" : $" — {loc}";
        return $"{SubjectPrefix} Your request has been closed{suffix}";
    }

    private string BuildUserClosedBody(SubmissionClosedEmailContext ctx, IReadOnlyList<(string FileName, Uri Url, long Len)> links)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Hello {ctx.SubmitterDisplayName},");
        sb.AppendLine();
        sb.AppendLine("Your request has been closed. Please see the attached documents or use the links below to download them.");
        sb.AppendLine();

        // ---- Admin note (optional) ----
        if (!string.IsNullOrWhiteSpace(ctx.AdminNote))
        {
            sb.AppendLine("Note from the administrator:");
            sb.AppendLine(ctx.AdminNote);
            sb.AppendLine();
        }

        AppendAttachmentLinks(sb, links, "Documents:");
        sb.AppendLine();
        sb.AppendLine("If you have additional questions, please contact TCRRequest@metro.net.");
        sb.AppendLine();
        AppendAutoFooter(sb);
        return sb.ToString();
    }

    private string BuildAdminClosedSubject(SubmissionClosedEmailContext ctx)
    {
        var loc = FormatLocation(ctx.Location, ctx.Title);
        var suffix = string.IsNullOrWhiteSpace(loc) ? "" : $" — {loc}";
        return $"{SubjectPrefix} PRRP submission #{ctx.SubmissionId} marked CLOSED{suffix}";
    }

    private string BuildAdminClosedBody(SubmissionClosedEmailContext ctx, IReadOnlyList<(string FileName, Uri Url, long Len)> links)
    {
        var loc = FormatLocation(ctx.Location, ctx.Title) ?? "(n/a)";
        var sb = new StringBuilder();
        sb.AppendLine($"Submission #{ctx.SubmissionId} was marked Closed at {DateFormatter.ToFriendlyPacificTime(ctx.ClosedUtc)}.");
        sb.AppendLine($"Location: {loc}");
        AppendDetailsIfAny(sb, ctx.IncidentDetailsText);
        AppendAttachmentLinks(sb, links, "Attached files & download links:");
        sb.AppendLine();
        AppendAutoFooter(sb);
        return sb.ToString();
    }

    // ===== Created/Completed builders =====

    private string SubjectPrefix => _env.IsProduction() ? "[PRRP]" : "[PRRP TEST]";

    private static string? FormatLocation(string? location, string? title)
        => string.IsNullOrWhiteSpace(location) ? (string.IsNullOrWhiteSpace(title) ? null : title) : location;

    private string BuildCreatedSubject(SubmissionEmailContext ctx)
    {
        var loc = FormatLocation(ctx.Location, ctx.Title);
        var suffix = string.IsNullOrWhiteSpace(loc) ? "" : $" — {loc}";
        return $"{SubjectPrefix} We received your request #{ctx.SubmissionId}{suffix}";
    }
    private string BuildCreatedBody(SubmissionEmailContext ctx, IReadOnlyList<(string FileName, Uri Url, long Len)> links)
    {
        var loc = FormatLocation(ctx.Location, ctx.Title) ?? "(n/a)";
        var sb = new StringBuilder();
        sb.AppendLine($"Hello {ctx.SubmitterDisplayName},");
        sb.AppendLine();
        sb.AppendLine($"We received your request #{ctx.SubmissionId} on {DateFormatter.ToFriendlyPacificTime(ctx.CreatedUtc)}.");
        sb.AppendLine($"Location: {loc}");
        AppendDetailsIfAny(sb, ctx.IncidentDetailsText);
        AppendAttachmentLinks(sb, links, "Attachments (download links):");
        sb.AppendLine();
        AppendAutoFooter(sb);
        return sb.ToString();
    }
    private string BuildOpsCreatedSubject(SubmissionEmailContext ctx)
    {
        var loc = FormatLocation(ctx.Location, ctx.Title);
        var suffix = string.IsNullOrWhiteSpace(loc) ? "" : $" — {loc}";
        return $"{SubjectPrefix} New PRRP Submission #{ctx.SubmissionId}{suffix}";
    }
    private string BuildOpsCreatedBody(SubmissionEmailContext ctx, IReadOnlyList<(string FileName, Uri Url, long Len)> links)
    {
        var loc = FormatLocation(ctx.Location, ctx.Title) ?? "(n/a)";
        var sb = new StringBuilder();
        sb.AppendLine($"New submission: #{ctx.SubmissionId}");
        sb.AppendLine($"Submitter: {ctx.SubmitterDisplayName} <{ctx.SubmitterEmail}>");
        sb.AppendLine($"Created: {DateFormatter.ToFriendlyPacificTime(ctx.CreatedUtc)}");
        sb.AppendLine($"Location: {loc}");
        AppendDetailsIfAny(sb, ctx.IncidentDetailsText);
        AppendAttachmentLinks(sb, links, "Attachments (download links):");
        sb.AppendLine();
        AppendAutoFooter(sb);
        return sb.ToString();
    }

    private string BuildUserCompletedSubject(SubmissionCompletedEmailContext ctx)
    {
        return $"{SubjectPrefix} Your request has been completed";
    }
    private string BuildAdminCompletedSubject(SubmissionCompletedEmailContext ctx)
    {
        var loc = FormatLocation(ctx.Location, ctx.Title);
        var suffix = string.IsNullOrWhiteSpace(loc) ? "" : $" — {loc}";
        return $"{SubjectPrefix} PRRP submission #{ctx.SubmissionId} marked COMPLETED{suffix}";
    }
    private string BuildUserCompletedBody(SubmissionCompletedEmailContext ctx, IReadOnlyList<(string FileName, Uri Url, long Len)> links)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Hello {ctx.SubmitterDisplayName},");
        sb.AppendLine();
        sb.AppendLine("Please see the attached documents regarding your request.");
        sb.AppendLine();

        // ---- Admin note (optional) ----
        if (!string.IsNullOrWhiteSpace(ctx.AdminNote))
        {
            sb.AppendLine("Note from the administrator:");
            sb.AppendLine(ctx.AdminNote);
            sb.AppendLine();
        }

        AppendAttachmentLinks(sb, links, "Your documents (attachments and download links):");
        sb.AppendLine();
        sb.AppendLine("If you have any more questions, please reach out to this email:TCRRequest@metro.net");
        sb.AppendLine();
        AppendAutoFooter(sb);
        return sb.ToString();
    }
    private string BuildAdminCompletedBody(SubmissionCompletedEmailContext ctx, IReadOnlyList<(string FileName, Uri Url, long Len)> links)
    {
        var loc = FormatLocation(ctx.Location, ctx.Title) ?? "(n/a)";
        var sb = new StringBuilder();
        sb.AppendLine($"Submission #{ctx.SubmissionId} was marked Completed at {DateFormatter.ToFriendlyPacificTime(ctx.CompletedUtc)}.");
        sb.AppendLine($"Location: {loc}");
        AppendDetailsIfAny(sb, ctx.IncidentDetailsText);
        AppendAttachmentLinks(sb, links, "Attached files & download links:");
        sb.AppendLine();
        AppendAutoFooter(sb);
        return sb.ToString();
    }

    private static void AppendDetailsIfAny(StringBuilder sb, string? details)
    {
        var d = (details ?? "").Trim();
        if (!string.IsNullOrEmpty(d))
        {
            sb.AppendLine();
            sb.AppendLine("Details:");
            sb.AppendLine(d);
        }
    }

    private void AppendAttachmentLinks(StringBuilder sb, IReadOnlyList<(string FileName, Uri Url, long Len)> links, string heading)
    {
        if (links.Count == 0) return;
        sb.AppendLine();
        sb.AppendLine(heading);
        foreach (var (name, url, len) in links)
        {
            var kb = Math.Max(1, len / 1024);
            sb.AppendLine($" - {name} ({kb:N0} KB): {url}");
        }
    }

    private static void AppendAutoFooter(StringBuilder sb)
    {
        sb.AppendLine("================");
        sb.Append("This is an automatically generated email. Please do not reply.");
    }

    // ===== download & attach helpers =====

    private sealed class AttachmentDisposables : IDisposable
    {
        public int AttachedCount { get; set; }
        public int SkippedCount { get; set; }
        private readonly List<IDisposable> _toDispose = new();
        public void Add(IDisposable d) => _toDispose.Add(d);
        public void Dispose()
        {
            foreach (var d in _toDispose) d.Dispose();
        }
    }

    private async Task<AttachmentDisposables> AttachEligibleFilesAsync(
        MailMessage msg,
        IEnumerable<EmailAttachmentInfo>? files,
        CancellationToken ct)
    {
        var result = new AttachmentDisposables();
        if (files is null) return result;

        long total = 0;
        foreach (var f in files)
        {
            if (f.Length <= 0) { result.SkippedCount++; continue; }
            if (f.Length > MaxAttachBytesEach) { result.SkippedCount++; continue; }
            if (total + f.Length > MaxAttachBytesTotal) { result.SkippedCount++; continue; }

            try
            {
                var container = _blobSvc.GetBlobContainerClient(f.Container);
                var blob = container.GetBlobClient(f.BlobName);

                var dl = await blob.DownloadStreamingAsync(cancellationToken: ct);
                var ms = new System.IO.MemoryStream();
                await dl.Value.Content.CopyToAsync(ms, ct);
                ms.Position = 0;

                var attach = new Attachment(ms, string.IsNullOrWhiteSpace(f.FileName) ? "file" : f.FileName!, f.ContentType);
                msg.Attachments.Add(attach);

                result.Add(attach);
                result.AttachedCount++;
                total += f.Length;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Failed to attach blob {Container}/{Blob}", f.Container, f.BlobName);
                result.SkippedCount++;
            }
        }

        return result;
    }

    private IReadOnlyList<(string FileName, Uri Url, long Len)> BuildAttachmentLinksForEmail(IEnumerable<EmailAttachmentInfo>? items)
    {
        var links = new List<(string, Uri, long)>();
        if (items == null) return links;

        foreach (var a in items)
        {
            if (string.IsNullOrWhiteSpace(a.Container) || string.IsNullOrWhiteSpace(a.BlobName)) continue;
            var url = _sas.CreateReadSasUri(a.Container, a.BlobName, TimeSpan.FromDays(_storage.ReadSasDays));
            links.Add((string.IsNullOrWhiteSpace(a.FileName) ? "file" : a.FileName!, url, a.Length));
        }
        return links;
    }

    // ===== SMTP helpers =====

    private bool ValidateSmtp()
    {
        if (string.IsNullOrWhiteSpace(_opts.Host))
        {
            _log.LogError("SMTP Host is not configured.");
            return false;
        }
        if (string.IsNullOrWhiteSpace(_opts.From))
        {
            _log.LogError("SMTP From is not configured.");
            return false;
        }

        try
        {
            using var tcp = new TcpClient();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            tcp.ConnectAsync(_opts.Host, _opts.Port, cts.Token).GetAwaiter().GetResult();
            _log.LogInformation("SMTP connectivity OK to {Host}:{Port}", _opts.Host, _opts.Port);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "SMTP connectivity probe failed to {Host}:{Port}", _opts.Host, _opts.Port);
        }

        return true;
    }

    private SmtpClient CreateClient()
    {
        var smtp = new SmtpClient(_opts.Host, _opts.Port) { EnableSsl = _opts.UseSsl };
        if (!string.IsNullOrWhiteSpace(_opts.Username))
            smtp.Credentials = new NetworkCredential(_opts.Username, _opts.Password ?? string.Empty);
        return smtp;
    }

    private static List<string> SplitAddresses(string? s)
        => string.IsNullOrWhiteSpace(s)
            ? new()
            : s.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim())
                .Where(x => x.Contains('@'))
                .ToList();
}
