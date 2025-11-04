using System.Threading;
using System.Threading.Tasks;

namespace police_report_request_backend.Email
{
    public interface IEmailNotificationService
    {
        // existing "created" confirmation
        Task SendSubmissionNotificationsAsync(SubmissionEmailContext ctx, CancellationToken ct = default);

        // status changes
        Task SendSubmissionCompletedAsync(SubmissionCompletedEmailContext ctx, CancellationToken ct = default);
        Task SendSubmissionInProgressAsync(SubmissionInProgressEmailContext ctx, CancellationToken ct = default);

        // NEW: for Closed
        Task SendSubmissionClosedAsync(SubmissionClosedEmailContext ctx, CancellationToken ct = default);
    }
}
