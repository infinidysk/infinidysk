using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Models.Nzb;
using NzbWebDAV.Queue.DeobfuscationSteps._3.GetFileInfos;
using NzbWebDAV.Queue.FileProcessors;
using NzbWebDAV.Tests.Fakes;
using UsenetSharp.Models;

namespace NzbWebDAV.Tests.Queue;

public class FileProcessorMissingArticlesTests
{
    [Fact]
    public void IsSkipNonMediaOnMissingArticlesEnabled_DefaultsTrue()
    {
        var config = new ConfigManager();
        Assert.True(config.IsSkipNonMediaOnMissingArticlesEnabled());
    }

    [Fact]
    public async Task ProcessAsync_SkipsNonVideoWhenFlagEnabled()
    {
        var config = Config(skip: true);
        var processor = CreateProcessor("file.par2", config, missing: true);

        var result = await processor.ProcessAsync();

        Assert.Null(result);
    }

    [Fact]
    public async Task ProcessAsync_ThrowsForNonVideoWhenFlagDisabled()
    {
        var config = Config(skip: false);
        var processor = CreateProcessor("file.par2", config, missing: true);

        await Assert.ThrowsAsync<UsenetArticleNotFoundException>(() => processor.ProcessAsync());
    }

    private static ConfigManager Config(bool skip)
    {
        var config = new ConfigManager();
        config.UpdateValues(
        [
            new ConfigItem
            {
                ConfigName = ConfigKeys.ApiSkipNonVideoOnMissingArticles,
                ConfigValue = skip ? "true" : "false"
            }
        ]);
        return config;
    }

    private static FileProcessor CreateProcessor(string fileName, ConfigManager config, bool missing)
    {
        var segments = missing
            ? new Dictionary<string, byte[]>()
            : new Dictionary<string, byte[]> { ["seg"] = [1, 2, 3] };
        var client = new FakeNntpClient(segments);
        var nzbFile = new NzbFile { Subject = fileName };
        if (!missing)
            nzbFile.Segments.Add(new NzbSegment { Bytes = 3, MessageId = "seg" });
        else
            nzbFile.Segments.Add(new NzbSegment { Bytes = 3, MessageId = "missing" });

        var fileInfo = new GetFileInfosStep.FileInfo
        {
            NzbFile = nzbFile,
            FileName = fileName,
            FileSize = null,
            ReleaseDate = DateTimeOffset.UnixEpoch,
        };
        return new FileProcessor(fileInfo, client, config, CancellationToken.None);
    }
}
