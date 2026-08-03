namespace UsenetSharp.Models;

public record UsenetAuthenticateResponse : UsenetResponse
{
    public bool Authenticated => ResponseType == UsenetResponseType.AuthenticationAccepted;
}
