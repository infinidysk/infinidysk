using UsenetSharp.Models;

namespace UsenetSharp.Exceptions;

public class UsenetException(string errorMessage) : Exception(errorMessage)
{
    public int? ResponseCode { get; init; }

    public UsenetResponseType ResponseType => Enum.IsDefined(typeof(UsenetResponseType), ResponseCode ?? 0)
        ? (UsenetResponseType)(ResponseCode ?? 0)
        : UsenetResponseType.Unknown;
}
