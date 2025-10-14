using System.Threading;
using System.Threading.Tasks;

namespace police_report_request_backend.Email
{
    public interface IEmailNotificationService
    {
        Task SendSubmissionNotificationsAsync(SubmissionEmailContext ctx, CancellationToken ct = default);
    }
}
