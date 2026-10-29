using System.ComponentModel.DataAnnotations;

namespace TradeNest.Api.Dtos;

// POST /auth/reauthenticate uses the existing bearer session to identify the user.
// Code is optional in JSON, but the service requires it when that account has MFA.
// It may contain either an authenticator code or a one-use recovery code.
public sealed record ReauthenticateDto([Required] string CurrentPassword, string? Code = null);
