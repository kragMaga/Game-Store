namespace TradeNest.Api.Services;

public enum RegistrationError
{
    None,
    ConfirmationEmailUnavailable,
    UsernameRequired,
    UsernameInvalid,
    PasswordRequired,
    PasswordTooShort,
    PasswordPolicyFailed,
    UsernameAlreadyTaken,
    EmailRequired,
    EmailInvalid,
    EmailAlreadyTaken,
    RegistrationFailed
}
