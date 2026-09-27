using System.Text;

namespace NzbWebDAV.Services.Plex;

public static class PlexInstallationIdentity
{
    private static readonly object Gate = new();

    public static string LoadOrCreate(string configPath)
    {
        lock (Gate)
        {
            Directory.CreateDirectory(configPath);
            var path = Path.Combine(configPath, "plex-client-id");
            if (File.Exists(path)) return Read(path);
            var identity = Guid.NewGuid().ToString("D");
            var temporary = Path.Combine(configPath, ".plex-client-id-" + Guid.NewGuid().ToString("N"));
            try
            {
                var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Options = FileOptions.WriteThrough };
                if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                using (var stream = new FileStream(temporary, options))
                {
                    stream.Write(Encoding.UTF8.GetBytes(identity));
                    stream.Flush(true);
                }
                try { File.Move(temporary, path, overwrite: false); }
                catch (IOException) when (File.Exists(path)) { return Read(path); }
                return identity;
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }

    private static string Read(string path)
    {
        var value = File.ReadAllText(path).Trim();
        return Guid.TryParse(value, out var identity) ? identity.ToString("D")
            : throw new InvalidDataException("The persisted Plex installation identity is invalid.");
    }
}
