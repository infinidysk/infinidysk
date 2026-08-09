using Microsoft.AspNetCore.StaticFiles;

namespace NzbWebDAV.Utils;

public static class ContentTypeUtil
{
    private static readonly FileExtensionContentTypeProvider ContentTypeProvider = CreateContentTypeProvider();

    private static FileExtensionContentTypeProvider CreateContentTypeProvider()
    {
        // ReSharper disable once UseObjectOrCollectionInitializer
        var provider = new FileExtensionContentTypeProvider();
        provider.Mappings[".flac"] = "audio/flac";
        provider.Mappings[".mkv"] = "video/x-matroska";
        provider.Mappings[".mk3d"] = "video/x-matroska";
        provider.Mappings[".m4v"] = "video/x-m4v";
        provider.Mappings[".ts"] = "video/mp2t";
        provider.Mappings[".m2ts"] = "video/mp2t";
        provider.Mappings[".mts"] = "video/mp2t";
        provider.Mappings[".divx"] = "video/divx";
        provider.Mappings[".rmvb"] = "application/vnd.rn-realmedia-vbr";
        return provider;
    }

    public static string GetContentType(string fileName)
    {
        return !ContentTypeProvider.TryGetContentType(Path.GetFileName(fileName), out var contentType)
            ? "application/octet-stream"
            : contentType;
    }
}
