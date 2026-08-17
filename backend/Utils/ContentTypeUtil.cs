using Microsoft.AspNetCore.StaticFiles;

namespace NzbWebDAV.Utils;

public static class ContentTypeUtil
{
    private static readonly FileExtensionContentTypeProvider ContentTypeProvider = CreateContentTypeProvider();

    private static FileExtensionContentTypeProvider CreateContentTypeProvider()
    {
        // ReSharper disable once UseObjectOrCollectionInitializer
        var provider = new FileExtensionContentTypeProvider();
        // audio
        provider.Mappings[".flac"] = "audio/flac";
        provider.Mappings[".opus"] = "audio/opus";
        provider.Mappings[".ape"] = "audio/x-ape";
        provider.Mappings[".wv"] = "audio/x-wavpack";
        provider.Mappings[".dsf"] = "audio/x-dsf";
        provider.Mappings[".dff"] = "audio/x-dff";
        provider.Mappings[".m4b"] = "audio/mp4";
        provider.Mappings[".mka"] = "audio/x-matroska";
        provider.Mappings[".aiff"] = "audio/aiff";
        provider.Mappings[".aif"] = "audio/aiff";
        // video
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
