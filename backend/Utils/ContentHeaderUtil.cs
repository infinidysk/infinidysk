namespace NzbWebDAV.Utils;

public static class ContentHeaderUtil
{
    public static string GetContentType(string fileName)
    {
        if (fileName == "README") return "text/plain";
        var extension = Path.GetExtension(fileName).ToLower();
        // .mkv falls through to ContentTypeUtil → "video/x-matroska". WebM only
        // permits VP8/VP9 + Vorbis/Opus, while MKV releases commonly use other codecs.
        return extension == ".rclonelink" ? "text/plain"
            : extension == ".nfo" ? "text/plain"
            : ContentTypeUtil.GetContentType(Path.GetFileName(fileName));
    }

    public static string GetContentDisposition(string fileName, bool shouldDownload)
    {
        fileName = new string(fileName.Where(c => !char.IsControl(c)).ToArray());

        var chars = fileName.Select(
            c => c is >= (char)32 and <= (char)126 && c is not '"' and not '\\' and not ';'
                ? c
                : '_');
        var ascii = new string(chars.ToArray());
        var utf8 = Uri.EscapeDataString(fileName);
        var type = shouldDownload ? "attachment" : "inline";

        return $"{type}; filename=\"{ascii}\"; filename*=UTF-8''{utf8}";
    }
}
