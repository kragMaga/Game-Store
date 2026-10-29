using Microsoft.AspNetCore.Identity;

namespace TradeNest.Api.Entities;

public class User : IdentityUser<int>
{
    // Permanent deactivation marker: reset/confirmation tokens must not reopen a closed account.
    public DateTime? AccountClosedAtUtc { get; set; }
}