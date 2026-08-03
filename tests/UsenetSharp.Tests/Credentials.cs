namespace UsenetSharpTest;

public static class Credentials
{
    public static string Host => Environment.GetEnvironmentVariable("USENETSHARP_TEST_HOST") ?? string.Empty;
    public static string Username => Environment.GetEnvironmentVariable("USENETSHARP_TEST_USERNAME") ?? string.Empty;
    public static string Password => Environment.GetEnvironmentVariable("USENETSHARP_TEST_PASSWORD") ?? string.Empty;

    public static bool AreConfigured =>
        !string.IsNullOrWhiteSpace(Host) &&
        !string.IsNullOrWhiteSpace(Username) &&
        !string.IsNullOrWhiteSpace(Password);
}
