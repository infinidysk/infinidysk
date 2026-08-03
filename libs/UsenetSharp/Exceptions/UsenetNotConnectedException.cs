namespace UsenetSharp.Exceptions;

public class UsenetNotConnectedException(string errorMessage) : Exception(errorMessage)
{
}
