using System.ComponentModel.DataAnnotations;

namespace GameStore.Api.Dtos;

public sealed record SetEmailDto([Required, EmailAddress, StringLength(256)] string Email);
