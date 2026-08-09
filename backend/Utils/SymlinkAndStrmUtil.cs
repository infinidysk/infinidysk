using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace NzbWebDAV.Utils;

public static class SymlinkAndStrmUtil
{
    private static readonly bool IsLinux = RuntimeInformation.IsOSPlatform(OSPlatform.Linux);
    private const int MaxStderrChars = 4096;
    internal const int MaxStrmTargetBytes = 8 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static IEnumerable<ISymlinkOrStrmInfo> GetAllSymlinksAndStrms(string directoryPath)
    {
        return IsLinux
            ? GetAllSymlinksAndStrmsLinux(directoryPath)
            : GetAllSymlinksAndStrmsWindows(directoryPath);
    }

    private static IEnumerable<ISymlinkOrStrmInfo> GetAllSymlinksAndStrmsLinux(string directoryPath)
    {
        // find -print0 (no shell) keeps traversal errors in find's own exit code and avoids
        // argv quoting / newline framing hazards. Targets are read in managed code.
        var startInfo = CreateLinuxFindStartInfo(directoryPath);
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Library symlink scan failed to start find.");

        // Drain stderr asynchronously. Leaving it unread can fill the OS pipe buffer and
        // deadlock find when a library tree produces many permission errors.
        var stderrBuilder = new StringBuilder();
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            lock (stderrBuilder)
            {
                if (stderrBuilder.Length >= MaxStderrChars) return;
                if (stderrBuilder.Length > 0)
                    stderrBuilder.Append('\n');

                var remaining = MaxStderrChars - stderrBuilder.Length;
                stderrBuilder.Append(e.Data.Length <= remaining ? e.Data : e.Data[..remaining]);
            }
        };
        process.BeginErrorReadLine();

        var completed = false;
        try
        {
            while (true)
            {
                var filePath = ReadNullTerminated(process.StandardOutput);
                if (filePath is null)
                    break;

                var info = ReadLinuxFindEntry(filePath);
                if (info is not null)
                    yield return info;
            }

            completed = true;
        }
        finally
        {
            try
            {
                // Only force-kill on early dispose / mid-scan failure. After a clean EOF,
                // find is exiting normally and Kill would turn a success into a false failure.
                if (!completed && !process.HasExited)
                {
                    try
                    {
                        process.Kill(entireProcessTree: true);
                    }
                    catch
                    {
                        // Best-effort cleanup when the consumer disposes early or a read fails.
                    }
                }

                process.WaitForExit();
            }
            catch
            {
                // Ignore cleanup failures; the primary error (if any) is already propagating.
            }
        }

        if (process.ExitCode != 0)
        {
            string stderr;
            lock (stderrBuilder)
                stderr = stderrBuilder.ToString();

            throw new InvalidOperationException(
                $"Library symlink scan failed with exit code {process.ExitCode}" +
                (string.IsNullOrWhiteSpace(stderr) ? "." : $": {stderr}"));
        }
    }

    /// <summary>
    /// Builds the Linux traversal process without a command shell. Every value,
    /// including the user-selected root, is passed as a distinct argv entry.
    /// </summary>
    internal static ProcessStartInfo CreateLinuxFindStartInfo(string directoryPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "find",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        // -H preserves the old `cd root && find .` behavior when the selected
        // root itself is a symlink, without following symlinks found below it.
        startInfo.ArgumentList.Add("-H");
        // An absolute starting point cannot be interpreted as a find option even
        // when a caller supplies a relative directory beginning with '-'.
        startInfo.ArgumentList.Add(Path.GetFullPath(directoryPath));
        startInfo.ArgumentList.Add("(");
        startInfo.ArgumentList.Add("-type");
        startInfo.ArgumentList.Add("l");
        startInfo.ArgumentList.Add("-o");
        startInfo.ArgumentList.Add("-name");
        startInfo.ArgumentList.Add("*.strm");
        startInfo.ArgumentList.Add(")");
        startInfo.ArgumentList.Add("-print0");
        return startInfo;
    }

    /// <summary>
    /// Reads one NUL-terminated record. Clean EOF with no pending bytes returns null;
    /// EOF with a partial record is a protocol failure.
    /// </summary>
    internal static string? ReadNullTerminated(TextReader reader)
    {
        var value = new StringBuilder();
        while (true)
        {
            var next = reader.Read();
            if (next < 0)
            {
                if (value.Length == 0)
                    return null;

                throw new InvalidOperationException(
                    "Library symlink scan received a truncated NUL-terminated path.");
            }

            if (next == '\0')
                return value.ToString();

            value.Append((char)next);
        }
    }

    /// <summary>
    /// Resolves one path emitted by find into a symlink or strm record.
    /// </summary>
    internal static ISymlinkOrStrmInfo? ReadLinuxFindEntry(string filePath)
    {
        var fullPath = Path.GetFullPath(filePath);
        if (Path.GetExtension(fullPath).Equals(".strm", StringComparison.OrdinalIgnoreCase))
        {
            var targetUrl = ReadStrmTargetUrl(fullPath);
            if (targetUrl is null)
                return null;

            return new StrmInfo
            {
                StrmPath = fullPath,
                TargetUrl = targetUrl
            };
        }

        // Do not require Exists: broken library symlinks are still links and must be reported.
        var fileInfo = new FileInfo(fullPath);
        var target = fileInfo.LinkTarget;
        if (target is null)
        {
            throw new InvalidOperationException(
                $"Library symlink scan could not read the symlink target for '{fullPath}'.");
        }

        return new SymlinkInfo
        {
            SymlinkPath = fullPath,
            TargetPath = target
        };
    }

    private static IEnumerable<ISymlinkOrStrmInfo> GetAllSymlinksAndStrmsWindows(string directoryPath)
    {
        return Directory.EnumerateFileSystemEntries(directoryPath, "*", SearchOption.AllDirectories)
            .Select(x => new FileInfo(x))
            .Select(GetSymlinkOrStrmInfo)
            .Where(x => x != null)
            .Select(x => x!);
    }

    public static ISymlinkOrStrmInfo? GetSymlinkOrStrmInfo(FileInfo x)
    {
        if (IsStrm(x))
        {
            var targetUrl = ReadStrmTargetUrl(x.FullName);
            return targetUrl is null
                ? null
                : new StrmInfo { StrmPath = x.FullName, TargetUrl = targetUrl };
        }

        return IsSymLink(x)
            ? new SymlinkInfo { SymlinkPath = x.FullName, TargetPath = x.LinkTarget! }
            : null;
    }

    /// <summary>
    /// Returns the first non-empty STRM target line, inspecting at most
    /// <see cref="MaxStrmTargetBytes"/>. Empty files yield null; an oversized first
    /// non-empty line throws rather than truncating into a wrong mapping.
    /// </summary>
    internal static string? ReadStrmTargetUrl(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var buffer = new byte[MaxStrmTargetBytes];
        var bytesRead = stream.Read(buffer, 0, buffer.Length);
        if (bytesRead == 0)
            return null;

        var span = buffer.AsSpan(0, bytesRead);
        if (span.Length >= 3 && span[0] == 0xEF && span[1] == 0xBB && span[2] == 0xBF)
            span = span[3..];

        var offset = 0;
        while (offset <= span.Length)
        {
            var remaining = span[offset..];
            var newline = remaining.IndexOf((byte)'\n');
            ReadOnlySpan<byte> lineBytes;
            bool lineComplete;
            if (newline >= 0)
            {
                lineBytes = remaining[..newline];
                lineComplete = true;
                offset += newline + 1;
            }
            else
            {
                lineBytes = remaining;
                // The first non-empty line is complete only when the inspected window
                // reached EOF. A full buffer with no newline means the line may continue.
                lineComplete = bytesRead < MaxStrmTargetBytes;
                offset = span.Length + 1; // no further lines in this window
            }

            if (lineBytes.Length > 0 && lineBytes[^1] == (byte)'\r')
                lineBytes = lineBytes[..^1];

            if (lineBytes.IsEmpty)
            {
                if (!lineComplete)
                {
                    throw new InvalidOperationException(
                        $"Library strm target in '{path}' exceeds {MaxStrmTargetBytes} bytes.");
                }

                continue;
            }

            if (!lineComplete)
            {
                throw new InvalidOperationException(
                    $"Library strm target in '{path}' exceeds {MaxStrmTargetBytes} bytes.");
            }

            try
            {
                return StrictUtf8.GetString(lineBytes);
            }
            catch (DecoderFallbackException e)
            {
                throw new InvalidOperationException(
                    $"Library strm target in '{path}' is not valid UTF-8.", e);
            }
        }

        return null;
    }

    private static bool IsStrm(FileInfo x) =>
        x.Extension.Equals(".strm", StringComparison.CurrentCultureIgnoreCase);

    private static bool IsSymLink(FileInfo x) =>
        x.Attributes.HasFlag(FileAttributes.ReparsePoint) && x.LinkTarget is not null;

    public interface ISymlinkOrStrmInfo;

    public struct SymlinkInfo : ISymlinkOrStrmInfo
    {
        public required string SymlinkPath;
        public required string TargetPath;
    }

    public struct StrmInfo : ISymlinkOrStrmInfo
    {
        public required string StrmPath;
        public required string TargetUrl;
    }
}
