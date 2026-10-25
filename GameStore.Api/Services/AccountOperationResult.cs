namespace GameStore.Api.Services;

public enum AccountOperationResult
{
    Success,
    UserNotFound,
    InvalidUsername,
    UsernameAlreadyInUse,
    InvalidEmail,
    EmailAlreadyInUse,
    CurrentPasswordIncorrect,
    PasswordPolicyFailed,
    EmailDeliveryFailed
}
