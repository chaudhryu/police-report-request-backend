using System;
using System.Linq;
using System.Net;
using System.Net.Mail;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Hosting;               // for IHostEnvironment
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using police_report_request_backend.Email;

public sealed class SmtpEmailNotificationService : IEmailNotificationService
{
    private readonly SmtpEmailOptions _opts;
    private readonly ILogger<SmtpEmailNotificationService> _log;
    private readonly IHostEnvironment _env;       // injected env (Production/Development/etc.)

    public SmtpEmailNotificationService(
        IOptions<SmtpEmailOptions> opts,
        ILogger<SmtpEmailNotificationService> log,
        IHostEnvironment env)                     // <-- inject IHostEnvironment in DI
    {
        _opts = opts.Value;
        _log = log;
        _env = env;
    }

    public async Task SendSubmissionNotificationsAsync(SubmissionEmailContext ctx, CancellationToken ct = default)
    {
        // Basic validation so we fail loudly and clearly
        if (string.IsNullOrWhiteSpace(_opts.Host))
        {
            _log.LogError("SMTP Host is not configured.");
            return;
        }
        if (string.IsNullOrWhiteSpace(_opts.From))
        {
            _log.LogError("SMTP From is not configured.");
            return;
        }

        _log.LogInformation(
            "EmailNotify: submissionId={SubmissionId} host={Host}:{Port} ssl={UseSsl} from={From} userTo={UserTo} opsTo={OpsTo} env={Env}",
            ctx.SubmissionId, _opts.Host, _opts.Port, _opts.UseSsl, _opts.From,
            string.IsNullOrWhiteSpace(ctx.SubmitterEmail) ? "(none)" : ctx.SubmitterEmail,
            string.IsNullOrWhiteSpace(_opts.OpsTo) ? "(none)" : _opts.OpsTo,
            _env.EnvironmentName);

        // Optional: quick TCP probe (helps triage blocked port 25/firewalls)
        try
        {
            using var tcp = new TcpClient();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await tcp.ConnectAsync(_opts.Host, _opts.Port, cts.Token);
            _log.LogInformation("SMTP connectivity OK to {Host}:{Port}", _opts.Host, _opts.Port);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "SMTP connectivity FAILED to {Host}:{Port}", _opts.Host, _opts.Port);
            // Continue to SendMailAsync to capture SMTP-layer error too
        }

        try
        {
            using var smtp = new SmtpClient(_opts.Host, _opts.Port) { EnableSsl = _opts.UseSsl };

            if (!string.IsNullOrWhiteSpace(_opts.Username))
            {
                smtp.Credentials = new NetworkCredential(_opts.Username, _opts.Password ?? string.Empty);
            }

            // 1) Confirmation to submitter
            if (!string.IsNullOrWhiteSpace(ctx.SubmitterEmail))
            {
                using var msgUser = new MailMessage
                {
                    From = new MailAddress(_opts.From),
                    Subject = BuildUserSubject(ctx),
                    SubjectEncoding = Encoding.UTF8,
                    Body = BuildUserBody(ctx),
                    BodyEncoding = Encoding.UTF8,
                    IsBodyHtml = false
                };
                msgUser.To.Add(ctx.SubmitterEmail);
                _log.LogInformation("Email -> submitter: {Email}", ctx.SubmitterEmail);
                await smtp.SendMailAsync(msgUser);
                _log.LogInformation("Email sent to submitter OK");
            }
            else
            {
                _log.LogWarning("SubmitterEmail is empty. Skipping user confirmation for submission {Id}", ctx.SubmissionId);
            }

            // 2) Ops/internal notification
            var opsList = SplitAddresses(_opts.OpsTo);
            if (opsList.Count > 0)
            {
                using var msgOps = new MailMessage
                {
                    From = new MailAddress(_opts.From),
                    Subject = BuildOpsSubject(ctx),
                    SubjectEncoding = Encoding.UTF8,
                    Body = BuildOpsBody(ctx),
                    BodyEncoding = Encoding.UTF8,
                    IsBodyHtml = false
                };
                foreach (var to in opsList) msgOps.To.Add(to);
                _log.LogInformation("Email -> ops: {Recipients}", string.Join(", ", opsList));
                await smtp.SendMailAsync(msgOps);
                _log.LogInformation("Email sent to ops OK");
            }
            else
            {
                _log.LogWarning("OpsTo not configured. Skipping ops notification for submission {Id}", ctx.SubmissionId);
            }
        }
        catch (SmtpException ex)
        {
            _log.LogError(ex, "SMTP send failed for submission {Id}", ctx.SubmissionId);
            throw;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Email send unexpected error for submission {Id}", ctx.SubmissionId);
            throw;
        }
    }

    // ===== Subject helpers =====

    private string SubjectPrefix => _env.IsProduction() ? "[PRRP]" : "[PRRP TEST]";

    private static string? FormatLocation(SubmissionEmailContext ctx)
    {
        // If you later add ctx.Location, prefer it here.
        return string.IsNullOrWhiteSpace(ctx.Title) ? null : ctx.Title;
    }

    private string BuildUserSubject(SubmissionEmailContext ctx)
    {
        var loc = FormatLocation(ctx);
        var suffix = string.IsNullOrWhiteSpace(loc) ? "" : $" — {loc}";
        return $"{SubjectPrefix} We received your request #{ctx.SubmissionId}{suffix}";
    }

    private string BuildOpsSubject(SubmissionEmailContext ctx)
    {
        var loc = FormatLocation(ctx);
        var suffix = string.IsNullOrWhiteSpace(loc) ? "" : $" — {loc}";
        return $"{SubjectPrefix} New PRRP Submission #{ctx.SubmissionId}{suffix}";
    }

    // ===== Body builders (include all details + footer) =====

    private static string BuildUserBody(SubmissionEmailContext ctx)
    {
        var loc = FormatLocation(ctx) ?? "(n/a)";
        var sb = new StringBuilder();
        sb.AppendLine($"Hello {ctx.SubmitterDisplayName},");
        sb.AppendLine();
        sb.AppendLine($"We received your request #{ctx.SubmissionId} on {ctx.CreatedUtc:u} (UTC).");
        sb.AppendLine($"Location: {loc}");

        AppendDetailsIfAny(sb, ctx);   // <-- include ALL details here

        sb.AppendLine();
        AppendAutoFooter(sb);          // <-- add the requested footer
        return sb.ToString();
    }

    private static string BuildOpsBody(SubmissionEmailContext ctx)
    {
        var loc = FormatLocation(ctx) ?? "(n/a)";
        var sb = new StringBuilder();
        sb.AppendLine($"New submission: #{ctx.SubmissionId}");
        sb.AppendLine($"Submitter: {ctx.SubmitterDisplayName} <{ctx.SubmitterEmail}>");
        sb.AppendLine($"Created (UTC): {ctx.CreatedUtc:u}");
        sb.AppendLine($"Location: {loc}");

        AppendDetailsIfAny(sb, ctx);   // <-- include ALL details here

        sb.AppendLine();
        AppendAutoFooter(sb);          // <-- add the requested footer
        return sb.ToString();
    }

    private static void AppendDetailsIfAny(StringBuilder sb, SubmissionEmailContext ctx)
    {
        var details = (ctx.IncidentDetailsText ?? "").Trim();
        if (!string.IsNullOrEmpty(details))
        {
            sb.AppendLine();
            sb.AppendLine("Details:");
            sb.AppendLine(details); // expect lines like "Case Number: 12345"
        }
    }

    private static void AppendAutoFooter(StringBuilder sb)
    {
        sb.AppendLine("================");
        sb.Append("This is an automatically generated email. Please do not reply.");
    }

    private static List<string> SplitAddresses(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return new();
        return s.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim())
                .Where(x => x.Contains('@'))
                .ToList();
    }
}
