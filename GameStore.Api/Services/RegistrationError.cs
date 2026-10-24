namespace GameStore.Api.Services;

public enum RegistrationError
{
    None,
    UsernameRequired,
    UsernameLengthInvalid,
    PasswordRequired,
    PasswordTooShort,
    PasswordPolicyFailed,
    UsernameAlreadyTaken,
    EmailRequired,
    EmailInvalid,
    EmailAlreadyTaken,
    RegistrationFailed
}
