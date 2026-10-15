using System.ComponentModel.DataAnnotations;

namespace GameStore.Api.Dtos;

public sealed record SetEmailDto([Required, EmailAddress] string Email);
