using Microsoft.AspNetCore.Identity;

namespace GameStore.Api.Entities;

public class User : IdentityUser<int>
{
    public required string Role { get; set; }
}