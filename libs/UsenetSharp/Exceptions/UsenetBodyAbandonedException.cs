namespace UsenetSharp.Exceptions;

internal sealed class UsenetBodyAbandonedException()
    : UsenetProtocolException("The abandoned NNTP body exceeded the configured drain limit.");