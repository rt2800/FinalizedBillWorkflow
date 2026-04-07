using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Options;

namespace Mbr.Api.Workflow.FinalizedBill.Services;

public sealed class EmailSettings
{
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 587;
    public string UserName { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string FromAddress { get; set; } = string.Empty;
    public string DefaultToAddress { get; set; } = string.Empty;
    public bool EnableSsl { get; set; } = true;
}

public sealed class EmailSender : IEmailSender
{
    private readonly EmailSettings _settings;
    private readonly ILogger<EmailSender> _logger;

    public EmailSender(IOptions<EmailSettings> settings, ILogger<EmailSender> logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task SendEmailAsync(string subject, string body, string? toAddress = null)
    {
        var targetEmail = toAddress ?? _settings.DefaultToAddress;

        if (string.IsNullOrWhiteSpace(targetEmail))
        {
            _logger.LogWarning("Target email address is not configured. Email notification skipped.");
            return;
        }

        try
        {
            using var client = new SmtpClient(_settings.Host, _settings.Port)
            {
                Credentials = new NetworkCredential(_settings.UserName, _settings.Password),
                EnableSsl = _settings.EnableSsl
            };

            var message = new MailMessage(_settings.FromAddress, targetEmail, subject, body);
            await client.SendMailAsync(message);

            _logger.LogInformation("Email notification sent successfully to {ToAddress}", targetEmail);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send email notification to {ToAddress}", targetEmail);
        }
    }
}
