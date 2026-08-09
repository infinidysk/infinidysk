using UsenetSharp.Models;

namespace NzbWebDAV.Clients.Usenet.Models;

public readonly struct UsenetExclusiveConnection(ArticleBodyCompletionHandler? onConnectionReadyAgain)
{
    public ArticleBodyCompletionHandler? OnConnectionReadyAgain => onConnectionReadyAgain;
}
