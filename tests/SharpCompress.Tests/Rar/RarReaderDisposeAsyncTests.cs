using System.IO;
using System.Threading.Tasks;
using SharpCompress.Readers;
using SharpCompress.Readers.Rar;
using SharpCompress.Test.Mocks;
using Xunit;

namespace SharpCompress.Test.Rar;

public class RarReaderDisposeAsyncTests : ReaderTests
{
    [Fact]
    public async ValueTask RarReader_AwaitUsing_DisposesUnpackV1_AfterEntryExtract()
    {
        using var stream = File.OpenRead(Path.Combine(TEST_ARCHIVES_PATH, "Rar.rar"));
        RarReader rarReader;
        await using (
            var reader = await ReaderFactory.OpenAsyncReader(
                new AsyncOnlyStream(stream),
                ReaderOptions.ForExternalStream with
                {
                    LookForHeader = true,
                }
            )
        )
        {
            rarReader = Assert.IsAssignableFrom<RarReader>(reader);
            Assert.True(await reader.MoveToNextEntryAsync());
            Assert.False(reader.Entry.IsDirectory);

            await using var entryStream = await reader.OpenEntryStreamAsync();
            var buffer = new byte[4096];
            while (await entryStream.ReadAsync(buffer) > 0) { }
        }

        Assert.True(rarReader.IsUnpackV1Disposed);
    }

    [Fact]
    public async ValueTask Rar5Reader_AwaitUsing_DisposesUnpackV2017_AfterEntryExtract()
    {
        using var stream = File.OpenRead(Path.Combine(TEST_ARCHIVES_PATH, "Rar5.rar"));
        RarReader rarReader;
        await using (
            var reader = await ReaderFactory.OpenAsyncReader(
                new AsyncOnlyStream(stream),
                ReaderOptions.ForExternalStream with
                {
                    LookForHeader = true,
                }
            )
        )
        {
            rarReader = Assert.IsAssignableFrom<RarReader>(reader);
            Assert.True(await reader.MoveToNextEntryAsync());
            Assert.False(reader.Entry.IsDirectory);

            await using var entryStream = await reader.OpenEntryStreamAsync();
            var buffer = new byte[4096];
            while (await entryStream.ReadAsync(buffer) > 0) { }
        }

        Assert.True(rarReader.IsUnpackV2017Disposed);
    }
}
