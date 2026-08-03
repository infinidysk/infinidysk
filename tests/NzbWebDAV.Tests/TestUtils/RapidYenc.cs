using System.Runtime.InteropServices;

namespace NzbWebDAV.Tests.TestUtils;

public static class RapidYenc
{
    public static readonly bool IsAvailable = Probe();

    private static bool Probe()
    {
        try
        {
            var explicitPath = Environment.GetEnvironmentVariable("RAPIDYENC_LIBRARY_PATH");
            if (!string.IsNullOrWhiteSpace(explicitPath) &&
                NativeLibrary.TryLoad(explicitPath, out _))
            {
                return true;
            }

            return NativeLibrary.TryLoad("rapidyenc", typeof(RapidYenc).Assembly, null, out _);
        }
        catch
        {
            return false;
        }
    }
}
