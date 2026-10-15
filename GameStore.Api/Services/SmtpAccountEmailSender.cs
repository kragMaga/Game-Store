using System.Net;
using System.Net.Mail;

namespace GameStore.Api.Services;

public sealed class SmtpAccountEmailSender(IConfiguration configuration) : IAccountEmailSender
{
    public async Task SendAsync(
        string emailAddress,
        string subject,
        string textBody,
        CancellationToken cancellationToken = default)
    {
        var host = configuration["Email:Smtp:Host"];
        var fromAddress = configuration["Email:FromAddress"];
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(fromAddress))
        {
            throw new InvalidOperationException("Email delivery is not configured. Set Email:Smtp:Host and Email:FromAddress.");
        }

        using var message = new MailMessage(fromAddress, emailAddress, subject, textBody)
        {
            IsBodyHtml = false
        };
        using var client = new SmtpClient(host, configuration.GetValue("Email:Smtp:Port", 587))
        {
            EnableSsl = configuration.GetValue("Email:Smtp:EnableSsl", true),
            DeliveryMethod = SmtpDeliveryMethod.Network,
            Timeout = 15000
        };

        var username = configuration["Email:Smtp:Username"];
        var password = configuration["Email:Smtp:Password"];
        if (!string.IsNullOrWhiteSpace(username))
        {
            client.Credentials = new NetworkCredential(username, password);
        }

        await client.SendMailAsync(message, cancellationToken);
    }
}
