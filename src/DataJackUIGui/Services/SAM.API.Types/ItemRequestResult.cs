namespace SAM.API.Types;

public enum ItemRequestResult
{
    InvalidValue = -1,
    OK,
    Denied,
    ServerError,
    Timeout,
    Invalid,
    NoMatch,
    UnknownError,
    NotLoggedOn
}
