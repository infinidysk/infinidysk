namespace NzbWebDAV.Clients;

/// <summary>
/// An Arr/Prowlarr request exceeded its deadline. Derives from
/// <see cref="TaskCanceledException"/> so existing reachability, retry, and
/// expected-failure classifiers keep treating it as a timeout, while the message
/// says which request timed out instead of the generic "Operation canceled".
/// </summary>
public sealed class ArrRequestTimeoutException : TaskCanceledException
{
    public ArrRequestTimeoutException(string operation, string host, TimeSpan budget, Exception? innerException)
        : base(BuildMessage(operation, host, budget), innerException)
    {
        Operation = operation;
        Budget = budget;
    }

    public string Operation { get; }
    public TimeSpan Budget { get; }

    private static string BuildMessage(string operation, string host, TimeSpan budget)
    {
        // Scheme and authority only: never echo query strings or user info from the configured URL.
        var created = Uri.TryCreate(host, UriKind.Absolute, out var uri);
        var instance = created && uri is not null
            ? uri.GetComponents(UriComponents.SchemeAndServer, UriFormat.SafeUnescaped)
            : "the configured instance";
        var routing = created && uri is not null ? ArrHttpTransport.DescribeRouting(uri) : "unknown";
        return $"{operation} request to {instance} timed out after {budget.TotalSeconds:0.##} seconds; routing: {routing}.";
    }
}
