using System.ComponentModel.DataAnnotations;

namespace GameStore.Api.Dtos;

public sealed record ForgotPasswordDto([Required, EmailAddress] string Email);
