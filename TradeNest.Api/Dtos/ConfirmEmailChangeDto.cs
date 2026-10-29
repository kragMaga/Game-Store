using System.ComponentModel.DataAnnotations;

namespace TradeNest.Api.Dtos;

// The frontend extracts these values from the email link's fragment and posts them.
// They identify the intended change, but do not authorize it: Identity must validate
// that the token belongs to this user and this exact new email before changing it.
public sealed record ConfirmEmailChangeDto(
    [Range(1, int.MaxValue)] int UserId,
    [Required, EmailAddress, StringLength(256)] string Email,
    [Required] string Token);
