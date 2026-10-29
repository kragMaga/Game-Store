using System.ComponentModel.DataAnnotations;

namespace TradeNest.Api.Dtos;

public sealed record MfaSetupDto([Required] string CurrentPassword);
