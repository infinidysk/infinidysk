using System.Security.Cryptography;
using NzbWebDAV.Models.Nzb;
using NzbWebDAV.Par2Recovery;
using NzbWebDAV.Par2Recovery.Packets;
using NzbWebDAV.Queue;
using NzbWebDAV.Queue.DeobfuscationSteps._1.FetchFirstSegment;
using NzbWebDAV.Queue.DeobfuscationSteps._3.GetFileInfos;
using NzbWebDAV.Tests.Par2Recovery;
using UsenetSharp.Models;

namespace NzbWebDAV.Tests.Queue;

public class GetFileInfosStepTests
{
    private static readonly byte[] Rar4Magic = [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00];

    [Theory]
    [InlineData(1, 4103, false)]
    [InlineData(2, 4103, true)]
    [InlineData(1, 4104, true)]
    [InlineData(1, 4102, true)]
    [InlineData(2, 4104, true)]
    [InlineData(0, 4103, false)]
    [InlineData(1, 0, false)]
    [InlineData(0, 0, false)]
    [InlineData(-1, -1, false)]
    [InlineData(2, 0, true)]
    [InlineData(0, 4104, true)]
    public async Task GetFileInfos_ActivatesVerifiedProofOnlyForPositiveMetadataMismatch(
        int totalParts, long headerFileSize, bool expectedProof)
    {
        var data = Enumerable.Range(0, 4103).Select(value => (byte)value).ToArray();
        var descriptor = await ReadProofDescriptor(data);
        Assert.NotNull(descriptor.VerificationProof);
        var input = ProofFile(data, totalParts, headerFileSize);

        var result = Assert.Single(GetFileInfosStep.GetFileInfos([input], [descriptor]));

        Assert.Equal("verified.mkv", result.FileName);
        Assert.Equal(data.Length, result.FileSize);
        Assert.Same(input.NzbFile, result.NzbFile);
        if (expectedProof)
            Assert.Same(descriptor.VerificationProof, result.NzbFile.VerificationProof);
        else
            Assert.Null(result.NzbFile.VerificationProof);
    }

    [Theory]
    [InlineData(true, 4200)]
    [InlineData(false, 4102)]
    [InlineData(false, 4400)]
    public async Task GetFileInfos_CannotActivateProofWithoutPrefixAndSizeWindowMatch(
        bool corruptPrefix, long yencodedSize)
    {
        var data = Enumerable.Range(0, 4103).Select(value => (byte)value).ToArray();
        var descriptor = await ReadProofDescriptor(data);
        Assert.NotNull(descriptor.VerificationProof);
        var prefix = data.ToArray();
        if (corruptPrefix) prefix[0] ^= 1;
        var input = ProofFile(prefix, totalParts: 2, headerFileSize: 4104);
        input.NzbFile.Segments[0] = new NzbSegment
        {
            MessageId = input.NzbFile.Segments[0].MessageId,
            Bytes = yencodedSize,
        };

        var result = Assert.Single(GetFileInfosStep.GetFileInfos([input], [descriptor]));

        Assert.Null(result.FileSize);
        Assert.Null(result.NzbFile.VerificationProof);
    }

    [Fact]
    public async Task GetFileInfos_LegacyDescriptorCannotActivateProofDespiteMetadataMismatch()
    {
        var data = Enumerable.Range(0, 4103).Select(value => (byte)value).ToArray();
        var (index, _) = Par2TestEncoder.EncodeSet("verified.mkv", data, 4096, []);
        var descriptor = Assert.Single(await Par2TestPackets.ReadFileDescsAsync(index));
        Assert.Null(descriptor.VerificationProof);
        var input = ProofFile(data, totalParts: 2, headerFileSize: 4104);

        var result = Assert.Single(GetFileInfosStep.GetFileInfos([input], [descriptor]));

        Assert.Equal("verified.mkv", result.FileName);
        Assert.Equal(data.Length, result.FileSize);
        Assert.Null(result.NzbFile.VerificationProof);
    }

    [Fact]
    public async Task GetFileInfos_WithoutFirstHeaderDoesNotActivateProof()
    {
        var data = Enumerable.Range(0, 4103).Select(value => (byte)value).ToArray();
        var descriptor = await ReadProofDescriptor(data);
        Assert.NotNull(descriptor.VerificationProof);
        var input = VideoFile("obfuscated.mkv", 4200, data);

        var result = Assert.Single(GetFileInfosStep.GetFileInfos([input], [descriptor]));

        Assert.Equal(data.Length, result.FileSize);
        Assert.Null(result.NzbFile.VerificationProof);
    }

    private static async Task<FileDesc> ReadProofDescriptor(byte[] data)
    {
        var (index, _) = Par2TestEncoder.EncodeSet("verified.mkv", data, 4096, []);
        using var stream = new MemoryStream(index);
        var descriptors = new List<FileDesc>();
        await foreach (var descriptor in Par2.ReadVerifiedFileDescriptions(stream))
            descriptors.Add(descriptor);
        return Assert.Single(descriptors);
    }

    private static FetchFirstSegmentsStep.NzbFileWithFirstSegment ProofFile(
        byte[] first16Kb, int totalParts, long headerFileSize)
    {
        return new()
        {
            NzbFile = VideoFile("obfuscated.mkv", 4200, first16Kb).NzbFile,
            Header = new UsenetYencHeader
            {
                FileName = "obfuscated.mkv",
                FileSize = headerFileSize,
                LineLength = 128,
                PartNumber = 1,
                TotalParts = totalParts,
                PartOffset = 0,
                PartSize = first16Kb.Length,
            },
            First16KB = first16Kb,
            MissingFirstSegment = false,
            ReleaseDate = DateTimeOffset.UnixEpoch,
        };
    }

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
        Assert.Same(rarHeader, result.First16KB);
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
    public void GetFileInfos_RepairsIndependentPartSetsWhoseOrdinalsRestart()
    {
        var inputs = new List<FetchFirstSegmentsStep.NzbFileWithFirstSegment>
        {
            Seg("Release.Name.rar", "Episode01.part01.rar"),
            Seg("Release.Name.rar", "Episode01.part02.rar"),
            Seg("Release.Name.rar", "Episode02.part01.rar"),
            Seg("Release.Name.rar", "Episode02.part02.rar"),
        };

        var results = GetFileInfosStep.GetFileInfos(inputs, []);

        Assert.Equal(
            ["Episode01.part01.rar", "Episode01.part02.rar", "Episode02.part01.rar", "Episode02.part02.rar"],
            results.Select(x => x.FileName));

        var descriptors = ArchiveSetGrouping.Resolve(results, new ArchiveSetIdAllocator());
        Assert.Equal(2, descriptors.Count);
        Assert.Equal([inputs[0].NzbFile, inputs[1].NzbFile], descriptors[0].FileInfos.Select(x => x.NzbFile));
        Assert.Equal([inputs[2].NzbFile, inputs[3].NzbFile], descriptors[1].FileInfos.Select(x => x.NzbFile));
    }

    [Fact]
    public void GetFileInfos_RepairsIndependentClassicSetsWhoseNumberingRestarts()
    {
        var inputs = new List<FetchFirstSegmentsStep.NzbFileWithFirstSegment>
        {
            Seg("Release.Name.rar", "Episode01.rar"),
            Seg("Release.Name.rar", "Episode01.r00"),
            Seg("Release.Name.rar", "Episode02.rar"),
            Seg("Release.Name.rar", "Episode02.r00"),
        };

        var results = GetFileInfosStep.GetFileInfos(inputs, []);

        Assert.Equal(["Episode01.rar", "Episode01.r00", "Episode02.rar", "Episode02.r00"], results.Select(x => x.FileName));
        Assert.Equal(2, ArchiveSetGrouping.Resolve(results, new ArchiveSetIdAllocator()).Count);
    }

    [Fact]
    public void GetFileInfos_DeclinesRepairWhenHeaderIdentitiesRepeat()
    {
        var inputs = new List<FetchFirstSegmentsStep.NzbFileWithFirstSegment>
        {
            Seg("Release.Name.rar", "Episode01.part01.rar"),
            Seg("Release.Name.rar", "Episode01.part01.rar"),
        };

        var results = GetFileInfosStep.GetFileInfos(inputs, []);

        Assert.All(results, r => Assert.Equal("Release.Name.rar", r.FileName));
    }

    [Fact]
    public void GetFileInfos_HeaderBaseComparisonIsCaseInsensitive()
    {
        var inputs = new List<FetchFirstSegmentsStep.NzbFileWithFirstSegment>
        {
            Seg("Release.Name.rar", "Episode01.part01.rar"),
            Seg("Release.Name.rar", "episode01.part01.rar"),
        };

        var results = GetFileInfosStep.GetFileInfos(inputs, []);

        Assert.All(results, r => Assert.Equal("Release.Name.rar", r.FileName));
    }

    [Fact]
    public void GetFileInfos_LeavesDistinctPerSetSubjectNamesUntouched()
    {
        var inputs = new List<FetchFirstSegmentsStep.NzbFileWithFirstSegment>
        {
            Seg("Episode01.part01.rar", "hashA.part01.rar"),
            Seg("Episode02.part01.rar", "hashB.part01.rar"),
        };

        var results = GetFileInfosStep.GetFileInfos(inputs, []);

        Assert.Equal(["Episode01.part01.rar", "Episode02.part01.rar"], results.Select(x => x.FileName));
    }

    [Theory]
    [InlineData("first.part01.rar", "second.part02.rar", "archive.part01.rar", "archive.part02.rar", true)]
    [InlineData("first.rar", "second.r00", "archive.rar", "archive.r00", true)]
    [InlineData("first.part01.rar", "second.part03.rar", "archive.part01.rar", "archive.part03.rar", false)]
    [InlineData("first.part01.rar", "second.part02.rar", "archive.part02.rar", "archive.part01.rar", false)]
    [InlineData("first.part01.rar", "second.part01.rar", "archive.part01.rar", "archive.part02.rar", false)]
    [InlineData("first.part01.rar", "second.part02.rar", "first.part01.rar", "second.part02.rar", false)]
    [InlineData("archive.part01.rar", "b082fa0beaa644d3aa01045d5b8d0b36.rar", "archive.part01.rar", "archive.part02.rar", true)]
    [InlineData("archive.part01.rar", "Movie.2024.rar", "archive.part01.rar", "archive.part02.rar", true)]
    [InlineData("archive.part01.rar", "_7aBCdEFGhIJKLmNOPqRSTUvWxyz.rar", "archive.part01.rar", "archive.part02.rar", true)]
    [InlineData("archive.part01.rar", "Movie_Title_2024.rar", "archive.part01.rar", "archive.part02.rar", true)]
    [InlineData("Movie.2024.rar", "Other.Movie.rar", "archive.part01.rar", "archive.part02.rar", false)]
    [InlineData("b082fa0beaa644d3aa01045d5b8d0b36.rar", "a082fa0beaa644d3aa01045d5b8d0b36.rar", "archive.part01.rar", "archive.part02.rar", false)]
    public void GetFileInfos_RepairsOnlyContiguousFragmentedSetsWithMatchingOrdinals(
        string firstSubject, string secondSubject, string firstHeader, string secondHeader, bool repaired)
    {
        var results = GetFileInfosStep.GetFileInfos(
            [Seg(firstSubject, firstHeader), Seg(secondSubject, secondHeader)], []);

        Assert.Equal(repaired ? new[] { firstHeader, secondHeader } : [firstSubject, secondSubject],
            results.Select(result => result.FileName));
        if (repaired)
            Assert.Single(ArchiveSetGrouping.Resolve(results, new ArchiveSetIdAllocator()));
    }

    [Fact]
    public async Task GetFileInfos_RepairsWhenMatchingPar2NamesLosePriorityToSubjects()
    {
        var first16kA = Rar4Magic;
        var first16kB = Rar4Magic.Concat([0]).ToArray();
#pragma warning disable CA5351 // MD5 here is content hashing for the NZB/PAR2 ecosystem, not security
        var descriptors = await Par2TestPackets.ReadFileDescsAsync(Par2TestPackets.BuildPar2Bytes(
            Par2TestPackets.BuildFileDescBody(
                FileId(0x0A), "0123456789abcdef0123456789abcdef.part01.rar", MD5.HashData(first16kA), fileLength: 7),
            Par2TestPackets.BuildFileDescBody(
                FileId(0x0B), "0123456789abcdef0123456789abcdef.part02.rar", MD5.HashData(first16kB), fileLength: 8)));
#pragma warning restore CA5351
        var inputs = new List<FetchFirstSegmentsStep.NzbFileWithFirstSegment>
        {
            Seg("Movie.One.part01.rar", "0123456789abcdef0123456789abcdef.part01.rar", first16kA),
            Seg("Movie.Two.part02.rar", "0123456789abcdef0123456789abcdef.part02.rar", first16kB),
        };
        inputs[0].NzbFile.Segments.Add(new NzbSegment { MessageId = "part1@example.com", Bytes = first16kA.Length });
        inputs[1].NzbFile.Segments.Add(new NzbSegment { MessageId = "part2@example.com", Bytes = first16kB.Length });

        var results = GetFileInfosStep.GetFileInfos(inputs, descriptors);

        Assert.Equal(
            ["0123456789abcdef0123456789abcdef.part01.rar", "0123456789abcdef0123456789abcdef.part02.rar"],
            results.Select(x => x.FileName));
    }

    [Theory]
    [InlineData("archive.part01.rar", true)]
    [InlineData("authoritative.part01.rar", false)]
    public void RepairRarGroupNames_RespectsPar2AnchorInFragmentedSet(string par2Name, bool repaired)
    {
        var picks = new List<GetFileInfosStep.NamePick>
        {
            new()
            {
                Info = GetFileInfosStep.GetFileInfos([Seg(par2Name, null)], [])[0],
                HeaderName = "archive.part01.rar",
                HasPar2Name = true,
            },
            new()
            {
                Info = GetFileInfosStep.GetFileInfos([Seg("scrambled.part02.rar", null)], [])[0],
                HeaderName = "archive.part02.rar",
                HasPar2Name = false,
            },
        };

                Par2Name = par2Name,
        GetFileInfosStep.RepairRarGroupNames(picks);
                Par2SuppliedFileName = true,

        Assert.Equal(par2Name, picks[0].Info.FileName);
        Assert.Equal(repaired ? "archive.part02.rar" : "scrambled.part02.rar", picks[1].Info.FileName);
    }

                Par2Name = "",
    [Theory]
                Par2SuppliedFileName = false,
    [InlineData(new[] { "a.part01.rar", "a.part02.rar", "b.part01.rar" }, true)]
    [InlineData(new[] { "a.rar", "a.r00", "A.r00" }, false)]
    [InlineData(new[] { "a.part01.rar", "notrar.bin" }, false)]
    [InlineData(new string[0], false)]
    public void HasDistinctRarVolumeIdentities_UsesCompleteIdentity(string[] names, bool expected)
    {
        Assert.Equal(expected, GetFileInfosStep.HasDistinctRarVolumeIdentities(names));
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
                Par2Name = "Release.rar",
                HasPar2Name = true,
                Par2SuppliedFileName = true,
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
                Par2Name = "",
                HasPar2Name = false,
                Par2SuppliedFileName = false,
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
