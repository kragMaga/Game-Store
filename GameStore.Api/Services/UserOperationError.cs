namespace GameStore.Api.Services;

public enum UserOperationError
{
    None,
    InvalidRole,
    UserNotFound,
    LastAdmin,
    CannotDeleteSelf
}