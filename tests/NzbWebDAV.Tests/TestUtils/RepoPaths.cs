namespace NzbWebDAV.Tests.TestUtils;

internal static class RepoPaths
{
    public static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var sln = Path.Join(directory.FullName, "NzbWebDAV.sln");
            var frontend = Path.Join(directory.FullName, "frontend");
            if (File.Exists(sln) && Directory.Exists(frontend))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate the repository root from the test output path.");
    }

    public static string FrontendRoot => Path.Join(FindRepoRoot(), "frontend");

    public static bool FrontendProductionBuildExists()
    {
        var frontend = FrontendRoot;
        return File.Exists(Path.Join(frontend, "dist-node", "server.js"))
            && File.Exists(Path.Join(frontend, "build", "server", "index.js"));
    }
}
