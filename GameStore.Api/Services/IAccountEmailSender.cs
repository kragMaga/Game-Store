namespace GameStore.Api.Services;

public interface IAccountEmailSender
{
    Task SendAsync(string emailAddress, string subject, string textBody, CancellationToken cancellationToken = default);
}
