namespace UsenetSharp.Exceptions;

public class UsenetConnectionException(string errorMessage) : UsenetException(errorMessage)
{
    /// <summary>
    /// True when the server indicated a temporary condition (greeting 400).
    /// </summary>
    /// <remarks>
    /// Greeting 502 (and RFC 4643 481 on auth) should not be retried aggressively.
    /// </remarks>
    public bool IsTransient => ResponseCode == 400;
}
