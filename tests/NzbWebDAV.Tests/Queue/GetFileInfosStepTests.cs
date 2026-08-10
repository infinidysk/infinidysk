using System.Security.Cryptography;
using NzbWebDAV.Models.Nzb;
using NzbWebDAV.Queue.DeobfuscationSteps._1.FetchFirstSegment;
using NzbWebDAV.Queue.DeobfuscationSteps._3.GetFileInfos;
using NzbWebDAV.Tests.Par2Recovery;
using UsenetSharp.Models;

namespace NzbWebDAV.Tests.Queue;

public class GetFileInfosStepTests
{
    private static readonly byte[] Rar4Magic = [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00];

    [Fact]
    public async Task GetFileInfos_AssignsPar2NamesFromMultiplePar2Sets()
    {
        // Season packs with one par2 set per episode yield one descriptor per
        // set; every file whose 16k hash matches must get its own par2 name.
        var first16kA = Enumerable.Range(0, 64).Select(i => (byte)i).ToArray();
        var first16kB = Enumerable.Range(100, 64).Select(i => (byte)i).ToArray();
#pragma warning disable CA5351 // MD5 here is content hashing for the NZB/PAR2 ecosystem, not security
        var descriptors = await Par2TestPackets.ReadFileDescsAsync(Par2TestPackets.BuildPar2Bytes(
            Par2TestPackets.BuildFileDescBody(
                FileId(0x0A), "Show.S01E01.mkv", MD5.HashData(first16kA), fileLength: 970),
            Par2TestPackets.BuildFileDescBody(
                FileId(0x0B), "Show.S01E02.mkv", MD5.HashData(first16kB), fileLength: 1950)));
#pragma warning restore CA5351

        var inputs = new List<FetchFirstSegmentsStep.NzbFileWithFirstSegment>
        {
            VideoFile("obfuscated [AAAAAAAA].mkv", yencodedSize: 1000, first16kA),
            VideoFile("obfuscated [BBBBBBBB].mkv", yencodedSize: 2000, first16kB),
            VideoFile("obfuscated [CCCCCCCC].mkv", yencodedSize: 3000, new byte[64]),
        };

        var results = GetFileInfosStep.GetFileInfos(inputs, descriptors);

        Assert.Equal("Show.S01E01.mkv", results[0].FileName);
        Assert.Equal("Show.S01E02.mkv", results[1].FileName);
        Assert.Equal("obfuscated [CCCCCCCC].mkv", results[2].FileName);
    }

    private static byte[] FileId(byte fill) => Enumerable.Repeat(fill, 16).ToArray();

    private static FetchFirstSegmentsStep.NzbFileWithFirstSegment VideoFile(
        string subject, long yencodedSize, byte[] first16Kb)
    {
        return new()
        {
            NzbFile = new NzbFile
            {
                Subject = $"\"{subject}\" yEnc (1/1)",
                Segments = { new NzbSegment { MessageId = "video@example.com", Bytes = yencodedSize } },
            },
            Header = null,
            First16KB = first16Kb,
            MissingFirstSegment = false,
            ReleaseDate = DateTimeOffset.UnixEpoch,
        };
    }
    private static readonly byte[] EbmlMagic = [0x1A, 0x45, 0xDF, 0xA3, 0x00, 0x00, 0x00, 0x00];

    [Fact]
    public void GetFileInfos_UsesSubjectNameAndDetectsRarMagic()
    {
        byte[] rarHeader = [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00, 0x00];
        var releaseDate = DateTimeOffset.UtcNow;
        var file = new NzbFile
        {
            Subject = "\"Movie.Release.2026.rar\" yEnc (1/1)"
        };
        var input = new FetchFirstSegmentsStep.NzbFileWithFirstSegment
        {
            NzbFile = file,
            Header = null,
            First16KB = rarHeader,
            MissingFirstSegment = false,
            ReleaseDate = releaseDate
        };

        var result = Assert.Single(GetFileInfosStep.GetFileInfos([input], []));

        Assert.Equal("Movie.Release.2026.rar", result.FileName);
        Assert.Equal(releaseDate, result.ReleaseDate);
        Assert.True(result.IsRar);
        Assert.Null(result.FileSize);
    }

    [Fact]
    public void GetFileInfos_SniffsObfuscatedVideoExtensionFromFirstSegment()
    {
        var file = new NzbFile { Subject = "\"b082fa0beaa644d3aa01045d5b8d0b36.xyz\" yEnc" };
        var input = new FetchFirstSegmentsStep.NzbFileWithFirstSegment
        {
            NzbFile = file,
            Header = null,
            First16KB = EbmlMagic,
            MissingFirstSegment = false,
            ReleaseDate = DateTimeOffset.UtcNow
        };

        var result = Assert.Single(GetFileInfosStep.GetFileInfos([input], []));

        Assert.Equal("b082fa0beaa644d3aa01045d5b8d0b36.xyz", result.FileName);
        Assert.Equal(".mkv", result.SniffedVideoExtension);
    }

    [Fact]
    public void GetFileInfos_SkipsVideoSniffingForRarMagic()
    {
        var file = new NzbFile { Subject = "\"archive.rar\" yEnc" };
        var input = new FetchFirstSegmentsStep.NzbFileWithFirstSegment
        {
            NzbFile = file,
            Header = null,
            First16KB = Rar4Magic,
            MissingFirstSegment = false,
            ReleaseDate = DateTimeOffset.UtcNow
        };

        var result = Assert.Single(GetFileInfosStep.GetFileInfos([input], []));

        Assert.True(result.IsRar);
        Assert.Null(result.SniffedVideoExtension);
    }

    [Fact]
    public void GetFileInfos_HandlesMissingFirstSegment()
    {
        var file = new NzbFile { Subject = "\"video.mkv\" yEnc" };
        var input = new FetchFirstSegmentsStep.NzbFileWithFirstSegment
        {
            NzbFile = file,
            Header = null,
            First16KB = null,
            MissingFirstSegment = true,
            ReleaseDate = DateTimeOffset.UtcNow
        };

        var result = Assert.Single(GetFileInfosStep.GetFileInfos([input], []));

        Assert.Equal("video.mkv", result.FileName);
        Assert.False(result.IsRar);
    }

    [Fact]
    public void GetFileInfos_RepairsCollidingSubjectsUsingDistinctYencHeaders()
    {
        var inputs = new List<FetchFirstSegmentsStep.NzbFileWithFirstSegment>
        {
            Seg("Release.Name.rar", "abc123.rar"),
            Seg("Release.Name.rar", "abc123.r00"),
            Seg("Release.Name.rar", "abc123.r01"),
        };

        var results = GetFileInfosStep.GetFileInfos(inputs, []);

        Assert.Equal(["abc123.rar", "abc123.r00", "abc123.r01"], results.Select(x => x.FileName));
        Assert.All(results, r => Assert.True(r.IsRar));
    }

    [Fact]
    public void GetFileInfos_LeavesWellFormedPartNamesUntouched()
    {
        var inputs = new List<FetchFirstSegmentsStep.NzbFileWithFirstSegment>
        {
            Seg("Release.part1.rar", "hashA.r00"),
            Seg("Release.part2.rar", "hashB.r01"),
        };

        var results = GetFileInfosStep.GetFileInfos(inputs, []);

        Assert.Equal(["Release.part1.rar", "Release.part2.rar"], results.Select(x => x.FileName));
    }

    [Fact]
    public void GetFileInfos_DeclinesRepairWhenHeadersAlsoCollide()
    {
        var inputs = new List<FetchFirstSegmentsStep.NzbFileWithFirstSegment>
        {
            Seg("Release.Name.rar", "same.rar"),
            Seg("Release.Name.rar", "same.rar"),
        };

        var results = GetFileInfosStep.GetFileInfos(inputs, []);

        Assert.All(results, r => Assert.Equal("Release.Name.rar", r.FileName));
    }

    [Fact]
    public void GetFileInfos_DeclinesRepairWhenHeadersLackRarSuffixes()
    {
        var inputs = new List<FetchFirstSegmentsStep.NzbFileWithFirstSegment>
        {
            Seg("Release.Name.rar", "hashA.bin"),
            Seg("Release.Name.rar", "hashB.bin"),
        };

        var results = GetFileInfosStep.GetFileInfos(inputs, []);

        Assert.All(results, r => Assert.Equal("Release.Name.rar", r.FileName));
    }

    [Fact]
    public void RepairRarGroupNames_SkipsWhenAnyVolumeHasPar2Name()
    {
        var picks = new List<GetFileInfosStep.NamePick>
        {
            new()
            {
                Info = new GetFileInfosStep.FileInfo
                {
                    NzbFile = new NzbFile { Subject = "\"Release.rar\" yEnc" },
                    FileName = "Release.rar",
                    ReleaseDate = DateTimeOffset.UnixEpoch,
                    IsRar = true,
                },
                HeaderName = "vol.rar",
                HasPar2Name = true,
            },
            new()
            {
                Info = new GetFileInfosStep.FileInfo
                {
                    NzbFile = new NzbFile { Subject = "\"Release.rar\" yEnc" },
                    FileName = "Release.rar",
                    ReleaseDate = DateTimeOffset.UnixEpoch,
                    IsRar = true,
                },
                HeaderName = "vol.r00",
                HasPar2Name = false,
            },
        };

        GetFileInfosStep.RepairRarGroupNames(picks);

        Assert.All(picks, p => Assert.Equal("Release.rar", p.Info.FileName));
    }

    private static FetchFirstSegmentsStep.NzbFileWithFirstSegment Seg(
        string subject, string? headerName, byte[]? first16Kb = null)
    {
        return new()
        {
            NzbFile = new NzbFile { Subject = $"\"{subject}\" yEnc (1/1)" },
            Header = headerName is null ? null : new UsenetYencHeader
            {
                FileName = headerName,
                FileSize = 1,
                LineLength = 128,
                PartNumber = 1,
                TotalParts = 1,
                PartOffset = 0,
                PartSize = 1,
            },
            First16KB = first16Kb ?? Rar4Magic,
            MissingFirstSegment = false,
            ReleaseDate = DateTimeOffset.UnixEpoch,
        };
    }
}
