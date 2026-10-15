using GameStore.Api.Services;
using System.Collections.Concurrent;

namespace GameStore.Api.Tests;

public sealed class TestAccountEmailSender : IAccountEmailSender
{
    public ConcurrentQueue<(string Email, string Subject, string Body)> SentMessages { get; } = new();

    public Task SendAsync(string emailAddress, string subject, string textBody, CancellationToken cancellationToken = default)
    {
        SentMessages.Enqueue((emailAddress, subject, textBody));
        return Task.CompletedTask;
    }
}
