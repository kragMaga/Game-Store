namespace TradeNest.Api.Services;

// Distinguish bad credentials from an operational failure. The endpoint can return
// 400 for a wrong password/code, 500 for an incomplete update, and 204 only on commit.
public enum MfaDisableResult
{
    Success,
    InvalidCredentials,
    Failed
}
