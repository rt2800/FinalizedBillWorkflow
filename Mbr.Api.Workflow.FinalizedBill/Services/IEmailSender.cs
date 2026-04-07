namespace Mbr.Api.Workflow.FinalizedBill.Services;

/// <summary>
/// Defines basic email sending operations for notifications.
/// </summary>
public interface IEmailSender
{
    Task SendEmailAsync(string subject, string body, string? toAddress = null);
}
