// Email/IEmailNotificationService.cs
using System.Threading;
using System.Threading.Tasks;

namespace police_report_request_backend.Email
{
    public interface IEmailNotificationService
    {
        // existing "created" confirmation
        Task SendSubmissionNotificationsAsync(SubmissionEmailContext ctx, CancellationToken ct = default);

        // NEW: called only when status changes to Completed
        Task SendSubmissionCompletedAsync(SubmissionCompletedEmailContext ctx, CancellationToken ct = default);
    }
}
