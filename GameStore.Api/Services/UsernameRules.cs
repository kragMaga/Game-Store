using System.ComponentModel.DataAnnotations;
using GameStore.Api.Entities;
using Microsoft.AspNetCore.Identity;

namespace GameStore.Api.Services;

public static class UsernameRules
{
    // One policy for HTTP validation, service calls, Identity, and bootstrap accounts.
    // In particular, periods must be accepted by Identity if the API accepts them.
    public const string AllowedCharacters = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_.";
    public const string Message = "Use 5–20 ASCII letters, digits, periods, underscores, or hyphens.";
    // Keep the length check here too: Identity's character-list option alone does
    // not enforce the same 5–20-character rule as our account endpoints.
    public static bool IsValid(string? value) => value is { Length: >= 5 and <= 20 } &&
        value.All(character => AllowedCharacters.Contains(character));
}

// Used as [Username] on request DTOs. Group-level parameter validation invokes it
// before the endpoint handler, producing a field-level HTTP 400 error.
public sealed class UsernameAttribute : ValidationAttribute
{
    public UsernameAttribute() : base(UsernameRules.Message) { }
    // Match the services' trimming behavior; whitespace inside a name is still invalid.
    public override bool IsValid(object? value) => value is string name && UsernameRules.IsValid(name.Trim());
}

// Registered with AddUserValidator in Program.cs. This protects Identity calls
// made outside HTTP handlers too, such as bootstrap or external-account creation.
public sealed class UsernameValidator : IUserValidator<User>
{
    public Task<IdentityResult> ValidateAsync(UserManager<User> manager, User user) => Task.FromResult(
        UsernameRules.IsValid(user.UserName) ? IdentityResult.Success : IdentityResult.Failed(
            new IdentityError { Code = "InvalidUserName", Description = UsernameRules.Message }));
}
