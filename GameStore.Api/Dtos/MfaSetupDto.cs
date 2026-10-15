using System.ComponentModel.DataAnnotations;

namespace GameStore.Api.Dtos;

public sealed record MfaSetupDto([Required] string CurrentPassword);
