using System.ComponentModel.DataAnnotations;

namespace GameStore.Api.Dtos;

public sealed record MfaVerifyDto([Required] string Code);
