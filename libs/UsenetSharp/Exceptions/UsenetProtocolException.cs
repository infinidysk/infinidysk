namespace UsenetSharp.Exceptions;

public class UsenetProtocolException : Exception
{
    public UsenetProtocolException(string errorMessage)
        : base(errorMessage)
    {
    }

    public UsenetProtocolException(string errorMessage, Exception innerException)
        : base(errorMessage, innerException)
    {
    }
}
