using System.ComponentModel.DataAnnotations;

namespace GameStore.Api.Dtos;

public record class UpdateUserRoleDto(
    [Required, RegularExpression("^(admin|customer)$")] string Role
);
