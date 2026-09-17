using NzbWebDAV.Config;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Connections;
using NzbWebDAV.Clients.Usenet.Contexts;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Models;
using NzbWebDAV.Models.Nzb;
using NzbWebDAV.Streams;
using NzbWebDAV.Tests.Fakes;
using NzbWebDAV.Tests.TestUtils;
using Serilog;
using Serilog.Events;
using UsenetSharp.Models;
using UsenetSharp.Streams;

namespace NzbWebDAV.Tests.Clients.Usenet;

[Collection(nameof(GlobalLoggerCollection))]
public sealed class MultiProviderNntpClientYencValidationTests
{
    [Fact]
    public async Task ValidationContext_Par2DeferralIsLimitedToItsCandidateAndRestoresStrictValidation()
    {
        string[] segmentIds = ["first", "second"];
        var mismatch = CreateHeader(557, 931);
        using var parent = YencFileValidationContext.Begin(2);
        Assert.False(YencFileValidationContext.MatchesExpectedFile(mismatch));
        await Task.Run(() =>
        {
            using var proofRead = YencFileValidationContext.BeginBufferedPar2ProofRead(segmentIds, null);
            using (YencFileValidationContext.BeginStreaming(segmentIds, null))
                Assert.True(YencFileValidationContext.MatchesExpectedFile(mismatch));
            using (YencFileValidationContext.BeginStreaming(["other"], null))
                Assert.False(YencFileValidationContext.MatchesExpectedFile(mismatch));
            using (YencFileValidationContext.Begin(2))
                Assert.False(YencFileValidationContext.MatchesExpectedFile(mismatch));
        });
        Assert.False(YencFileValidationContext.MatchesExpectedFile(mismatch));
        Assert.Equal(2, YencFileValidationContext.CurrentExpectedTotalParts);
    }

    [Fact]
    public void Par2ProviderScope_RestoresAttributionAndCannotSelectDisabledOrOpenProviders()
    {
        using var fake = new FakeNntpClient(new Dictionary<string, byte[]>());
        using var healthy = MultiProviderNntpClientTests.CreateProvider(fake, host: "healthy.example");
        using var disabled = MultiProviderNntpClientTests.CreateProvider(fake, host: "disabled.example", providerType: ProviderType.Disabled);
        var breaker = new ProviderCircuitBreaker("open.example");
        breaker.RecordFailure();
        breaker.RecordFailure();
        breaker.RecordFailure();
        using var open = MultiProviderNntpClientTests.CreateProvider(fake, host: "open.example", circuitBreaker: breaker);
        using var client = new MultiProviderNntpClient([healthy, disabled, open]);
        var previous = MultiProviderNntpClient.AttributionContext.Value;
        var parent = new MultiProviderNntpClient.ResponderAttribution { Host = "parent" };
        MultiProviderNntpClient.AttributionContext.Value = parent;
        try
        {
            Assert.Same(healthy, Assert.Single(client.GetPar2VerificationProviders()));
            using (new Par2VerificationReadContext(disabled))
            {
                Assert.Empty(client.GetPar2VerificationProviders());
                Assert.NotSame(parent, MultiProviderNntpClient.AttributionContext.Value);
            }
            using (new Par2VerificationReadContext(open))
                Assert.Empty(client.GetPar2VerificationProviders());
            Assert.Same(parent, MultiProviderNntpClient.AttributionContext.Value);
            Assert.Null(Par2VerificationReadContext.PreferredProvider);
        }
        finally
        {
            MultiProviderNntpClient.AttributionContext.Value = previous;
        }
    }

    [Fact]
    public void ValidationContext_SizeProbe_PreservesOrdinalAndRestoresParent()
    {
        using var parent = YencFileValidationContext.Begin(17);
        var file = new NzbFile { Subject = "private-file.bin" };
        file.Segments.Add(new NzbSegment { Bytes = 3, MessageId = "first", Number = 2 });
        file.Segments.Add(new NzbSegment
        {
            Bytes = 3, MessageId = "last", Number = 8, FallbackMessageIds = ["alternate"],
        });

        using (YencFileValidationContext.BeginSizeProbe(file))
        {
            var context = Assert.IsType<YencFileValidationContext>(YencFileValidationContext.Current);
            Assert.Equal("SizeProbe", context.Stage);
            Assert.Equal(2, context.ExpectedTotalParts);
            Assert.Equal(("first", (int?)2, (int?)8), context.GetRequestDetails("last"));
            Assert.Equal(("first", (int?)2, (int?)8), context.GetRequestDetails("alternate"));
            Assert.Equal(("first", (int?)null, (int?)null), context.GetRequestDetails("unknown"));
        }

        Assert.Equal(17, YencFileValidationContext.CurrentExpectedTotalParts);
    }

    [Fact]
    public async Task ValidationContext_Streaming_DoesNotInferOrdinalOrLeakAcrossTasks()
    {
        using var parent = YencFileValidationContext.Begin(17);
        await Task.Run(() =>
        {
            using var validation = YencFileValidationContext.BeginStreaming(
                ["first", "last"], [[], ["alternate"]]);
            var context = Assert.IsType<YencFileValidationContext>(YencFileValidationContext.Current);
            Assert.Equal("Streaming", context.Stage);
            Assert.Equal(("first", (int?)2, (int?)null), context.GetRequestDetails("last"));
            Assert.Equal(("first", (int?)2, (int?)null), context.GetRequestDetails("alternate"));
            Assert.Equal(("first", (int?)null, (int?)null), context.GetRequestDetails("unknown"));
        });

        Assert.Equal(17, YencFileValidationContext.CurrentExpectedTotalParts);
    }

    [Theory]
    [InlineData(1, 0, true)]
    [InlineData(3, 0, false)]
    [InlineData(3, 3, true)]
    [InlineData(3, 931, false)]
    public void MatchesExpectedFile_ValidatesTotalParts(
        int expectedTotalParts,
        int actualTotalParts,
        bool expected)
    {
        using var validation = YencFileValidationContext.Begin(expectedTotalParts);
        var header = CreateHeader(partNumber: 1, totalParts: actualTotalParts);

        Assert.Equal(expected, YencFileValidationContext.MatchesExpectedFile(header));
    }

    [Theory]
    [InlineData(false, 0, true)]
    [InlineData(true, 0, false)]
    [InlineData(null, 0, false)]
    [InlineData(false, 931, false)]
    [InlineData(true, 931, false)]
    [InlineData(null, 931, false)]
    [InlineData(true, -1, false)]
    [InlineData(true, 3, true)]
    public void MatchesExpectedFile_OnlyConfirmedOmissionBypassesCountComparison(
        bool? hasTotalParts, int totalParts, bool expected)
    {
        using var validation = YencFileValidationContext.Begin(3);
        var header = CreateHeader(partNumber: 2, totalParts: totalParts) with
        {
            HasTotalParts = hasTotalParts,
        };

        Assert.Equal(expected, YencFileValidationContext.MatchesExpectedFile(header));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OmittedTotal_SizeProbeAndStreaming_UsePrimaryProvider(bool pipelined)
    {
        var segments = new Dictionary<string, byte[]>
        {
            ["first"] = [1, 2, 3],
            ["second"] = [4, 5, 6],
            ["third"] = [7, 8, 9],
        };
        var headers = segments.Keys.Select((segmentId, index) => new
        {
            SegmentId = segmentId,
            Header = CreateHeader(index + 1, totalParts: 0) with
            {
                HasTotalParts = false,
                PartOffset = index * 3,
            },
        }).ToDictionary(entry => entry.SegmentId, entry => entry.Header);
        using var primary = new FakeNntpClient(segments, useCachedYencStreams: true, yencHeaders: headers);
        using var backup = new FakeNntpClient(segments, useCachedYencStreams: true);
        using var client = CreateProviderClient(primary, backup);
        var file = new NzbFile { Subject = "fake.bin" };
        foreach (var (segmentId, index) in segments.Keys.Select((segmentId, index) => (segmentId, index)))
        {
            file.Segments.Add(new NzbSegment { Bytes = 3, MessageId = segmentId, Number = index + 1 });
        }

        var fileSize = await client.GetFileSizeAsync(file, CancellationToken.None);
        Assert.Equal(9, fileSize);
        Assert.Equal(new LongRange(6, 9), file.Segments[^1].ByteRange);

        await using var stream = new NzbFileStream(
            segments.Keys.ToArray(), fileSize, client,
            articleBufferSize: pipelined ? 4 : 0,
            usePipelinedBodyRequests: pipelined);
        using var output = new MemoryStream();
        await stream.CopyToAsync(output);

        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8, 9], output.ToArray());
        Assert.True(primary.BodyRequestCount >= 3);
        Assert.Equal(0, backup.BodyRequestCount);
    }

    [Fact]
    public async Task GetYencHeadersAsync_MismatchedTotalParts_UsesBackupProvider()
    {
        var segments = new Dictionary<string, byte[]> { ["segment"] = [1, 2, 3] };
        var wrongPost = CreateClient(segments, partNumber: 557, totalParts: 931);
        var correctPost = CreateClient(segments, partNumber: 1, totalParts: 3);
        using var client = new MultiProviderNntpClient(
            [
                MultiProviderNntpClientTests.CreateProvider(wrongPost, host: "wrong.example"),
                MultiProviderNntpClientTests.CreateProvider(
                    correctPost,
                    host: "correct.example",
                    providerType: ProviderType.BackupOnly),
            ],
            cascadeEnabled: () => true);
        using var validation = YencFileValidationContext.Begin(expectedTotalParts: 3);

        var header = await client.GetYencHeadersAsync("segment", CancellationToken.None);

        Assert.Equal(1, header.PartNumber);
        Assert.Equal(3, header.TotalParts);
        Assert.Equal(1, wrongPost.BodyRequestCount);
        Assert.Equal(1, correctPost.BodyRequestCount);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(true)]
    public async Task GetYencHeadersAsync_MultipartZeroTotalWithoutConfirmedOmission_UsesBackupProvider(
        bool? hasTotalParts)
    {
        var segments = new Dictionary<string, byte[]> { ["segment"] = [1, 2, 3] };
        var wrongPost = CreateClient(segments, partNumber: 1, totalParts: 0, hasTotalParts: hasTotalParts);
        var correctPost = CreateClient(segments, partNumber: 1, totalParts: 3);
        using var client = CreateProviderClient(wrongPost, correctPost);
        using var validation = YencFileValidationContext.Begin(expectedTotalParts: 3);

        var header = await client.GetYencHeadersAsync("segment", CancellationToken.None);

        Assert.Equal(3, header.TotalParts);
        Assert.Equal(1, wrongPost.BodyRequestCount);
        Assert.Equal(1, correctPost.BodyRequestCount);
    }

    [Fact]
    public async Task GetFileSizeAsync_MismatchedLastSegment_UsesBackupProvider()
    {
        var sink = new CollectingLogEventSink();
        using var logger = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();
        var previousLogger = Log.Logger;
        var segments = new Dictionary<string, byte[]>
        {
            ["first"] = [1, 2, 3],
            ["second"] = [4, 5, 6],
            ["third"] = [7, 8, 9],
        };
        var wrongPost = CreateClient(
            segments,
            partNumber: 557,
            totalParts: 931,
            segmentId: "third",
            partOffset: 187_525_120);
        var correctPost = CreateClient(
            segments,
            partNumber: 3,
            totalParts: 3,
            segmentId: "third",
            partOffset: 6);
        using var client = CreateProviderClient(wrongPost, correctPost);
        var file = new NzbFile { Subject = "fake.bin" };
        foreach (var (segmentId, index) in segments.Keys.Select((id, index) => (id, index)))
        {
            file.Segments.Add(new NzbSegment
            {
                Bytes = 3,
                MessageId = segmentId,
                Number = index + 1,
            });
        }

        long fileSize;
        try
        {
            Log.Logger = logger;
            fileSize = await client.GetFileSizeAsync(file, CancellationToken.None);
        }
        finally
        {
            Log.Logger = previousLogger;
        }

        Assert.Equal(9, fileSize);
        Assert.Equal(new LongRange(6, 9), file.Segments[^1].ByteRange);
        Assert.Equal(1, wrongPost.BodyRequestCounts["third"]);
        Assert.Equal(1, correctPost.BodyRequestCounts["third"]);
        var warning = Assert.Single(sink.Events, IsMismatchWarning);
        Assert.Null(warning.Exception);
        Assert.Equal("SizeProbe", Scalar(warning, "Stage"));
        Assert.Equal(3, Scalar(warning, "RequestedSegmentPosition"));
        Assert.Equal(3, Scalar(warning, "NzbSegmentNumber"));
        Assert.Equal(3, Scalar(warning, "ExpectedTotalParts"));
        Assert.Equal(557, Scalar(warning, "ReturnedPartNumber"));
        Assert.Equal(931, Scalar(warning, "ReturnedTotalParts"));
        Assert.Equal(187_525_120L, Scalar(warning, "ReturnedPartOffset"));
        Assert.Equal(3L, Scalar(warning, "ReturnedPartSize"));
        Assert.Equal(9L, Scalar(warning, "ReturnedFileSize"));
        Assert.DoesNotContain("wrong.example", warning.RenderMessage());
        Assert.DoesNotContain("fake.bin", warning.RenderMessage());
        Assert.DoesNotContain("third", warning.RenderMessage());
    }

    [Fact]
    public async Task DecodedArticleAsync_MismatchedTotalParts_UsesBackupProvider()
    {
        var sink = new CollectingLogEventSink();
        using var logger = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();
        var previousLogger = Log.Logger;
        var innerSegments = new Dictionary<string, byte[]> { ["segment"] = [1, 2, 3] };
        using var wrongInner = new FakeNntpClient(innerSegments, useCachedYencStreams: true);
        using var correctInner = new FakeNntpClient(innerSegments, useCachedYencStreams: true);
        using var wrongPost = new ArticleNntpClient(
            wrongInner, CreateHeader(partNumber: 557, totalParts: 931));
        using var correctPost = new ArticleNntpClient(
            correctInner, CreateHeader(partNumber: 1, totalParts: 3));
        using var client = CreateProviderClient(wrongPost, correctPost);
        using var validation = YencFileValidationContext.Begin(expectedTotalParts: 3);

        UsenetDecodedArticleResponse response;
        try
        {
            Log.Logger = logger;
            response = await client.DecodedArticleAsync("segment", CancellationToken.None);
        }
        finally
        {
            Log.Logger = previousLogger;
        }
        await using var responseStream = response.Stream;
        var header = await responseStream.GetYencHeadersAsync();

        Assert.Equal(3, header!.TotalParts);
        Assert.Equal(1, wrongPost.ArticleRequestCount);
        Assert.Equal(1, correctPost.ArticleRequestCount);
        var warning = Assert.Single(sink.Events, IsMismatchWarning);
        Assert.Equal(220, Scalar(warning, "ResponseCode"));
        Assert.Equal("Unknown", Scalar(warning, "Stage"));
        Assert.Null(Scalar(warning, "FileRef"));
        Assert.Null(Scalar(warning, "RequestedSegmentPosition"));
        Assert.Null(Scalar(warning, "NzbSegmentNumber"));
    }

    [Fact]
    public async Task DecodedArticleAsync_HeaderInspectionFails_DisposesStreamAndUsesBackupProvider()
    {
        var innerSegments = new Dictionary<string, byte[]> { ["segment"] = [1, 2, 3] };
        using var wrongInner = new FakeNntpClient(innerSegments, useCachedYencStreams: true);
        using var correctInner = new FakeNntpClient(innerSegments, useCachedYencStreams: true);
        var failingStream = new ThrowingHeaderYencStream();
        using var wrongPost = new ArticleNntpClient(wrongInner, failingStream);
        using var correctPost = new ArticleNntpClient(
            correctInner, CreateHeader(partNumber: 1, totalParts: 3));
        using var client = CreateProviderClient(wrongPost, correctPost);
        using var validation = YencFileValidationContext.Begin(expectedTotalParts: 3);

        var response = await client.DecodedArticleAsync("segment", CancellationToken.None);
        await using var responseStream = response.Stream;

        Assert.True(failingStream.IsDisposed);
        Assert.Equal(1, wrongPost.ArticleRequestCount);
        Assert.Equal(1, correctPost.ArticleRequestCount);
    }

    [Fact]
    public async Task GetYencHeadersAsync_MismatchedTotalParts_DoesNotSkipStorageGroupSibling()
    {
        var segments = new Dictionary<string, byte[]> { ["segment"] = [1, 2, 3] };
        var wrongPost = CreateClient(segments, partNumber: 557, totalParts: 931);
        var correctPost = CreateClient(segments, partNumber: 1, totalParts: 3);
        using var client = new MultiProviderNntpClient(
            [
                MultiProviderNntpClientTests.CreateProvider(
                    wrongPost, host: "wrong.example", storageGroup: "shared"),
                MultiProviderNntpClientTests.CreateProvider(
                    correctPost, host: "correct.example", storageGroup: "shared"),
            ],
            articleMissCache: new ArticleMissNegativeCache(new ConfigManager()));
        using var validation = YencFileValidationContext.Begin(expectedTotalParts: 3);

        var header = await client.GetYencHeadersAsync("segment", CancellationToken.None);

        Assert.Equal(3, header.TotalParts);
        Assert.Equal(1, wrongPost.BodyRequestCount);
        Assert.Equal(1, correctPost.BodyRequestCount);
    }

    [Fact]
    public async Task NzbFileStream_ByteZero_MismatchedTotalParts_UsesBackupProvider()
    {
        var segments = new Dictionary<string, byte[]>
        {
            ["first"] = [1, 2, 3],
            ["second"] = [4, 5, 6],
            ["third"] = [7, 8, 9],
        };
        var wrongPost = new FakeNntpClient(
            segments,
            useCachedYencStreams: true,
            yencHeaders: new Dictionary<string, UsenetYencHeader>
            {
                ["first"] = new()
                {
                    FileName = "wrong.bin",
                    FileSize = 318_803_968,
                    LineLength = 128,
                    PartNumber = 557,
                    TotalParts = 931,
                    PartOffset = 187_525_120,
                    PartSize = 3,
                },
            });
        var correctPost = new FakeNntpClient(segments, useCachedYencStreams: true);
        using var client = new MultiProviderNntpClient(
            [
                MultiProviderNntpClientTests.CreateProvider(wrongPost, host: "wrong.example"),
                MultiProviderNntpClientTests.CreateProvider(
                    correctPost,
                    host: "correct.example",
                    providerType: ProviderType.BackupOnly),
            ],
            cascadeEnabled: () => true);
        await using var stream = new NzbFileStream(
            ["first", "second", "third"],
            fileSize: 9,
            client,
            articleBufferSize: 4,
            usePipelinedBodyRequests: true);

        var buffer = new byte[1];
        Assert.Equal(1, await stream.ReadAsync(buffer));

        Assert.Equal(1, buffer[0]);
        Assert.Equal(1, wrongPost.BodyRequestCounts["first"]);
        Assert.Equal(1, correctPost.BodyRequestCounts["first"]);
    }

    [Fact]
    public async Task NzbFileStream_SeekProbe_MismatchedTotalParts_UsesBackupProvider()
    {
        var segments = new Dictionary<string, byte[]>
        {
            ["first"] = [1, 2, 3],
            ["second"] = [4, 5, 6],
            ["third"] = [7, 8, 9],
        };
        var wrongPost = CreateClient(
            segments, partNumber: 557, totalParts: 931, segmentId: "first");
        var correctPost = new FakeNntpClient(segments, useCachedYencStreams: true);
        using var client = CreateProviderClient(wrongPost, correctPost);
        var previousBudget = NzbWebDAV.WebDav.Requests.RangeContext.GetReadBudget();
        NzbWebDAV.WebDav.Requests.RangeContext.SetReadBudget(1);
        try
        {
            await using var stream = new NzbFileStream(
                ["first", "second", "third"],
                fileSize: 9,
                client,
                articleBufferSize: 0,
                usePipelinedBodyRequests: false);
            stream.Seek(1, SeekOrigin.Begin);

            var buffer = new byte[1];
            Assert.Equal(1, await stream.ReadAsync(buffer));

            Assert.Equal(2, buffer[0]);
            Assert.True(wrongPost.BodyRequestCounts["first"] >= 1);
            Assert.True(correctPost.BodyRequestCounts["first"] >= 1);
        }
        finally
        {
            NzbWebDAV.WebDav.Requests.RangeContext.SetReadBudget(previousBudget);
        }
    }

    [Fact]
    public async Task NzbFileStream_PipelinedMismatch_UsesBackupProvider()
    {
        var sink = new CollectingLogEventSink();
        using var logger = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();
        var previousLogger = Log.Logger;
        var correctSegments = new Dictionary<string, byte[]>
        {
            ["first"] = [1, 2, 3],
            ["second"] = [4, 5, 6],
            ["third"] = [7, 8, 9],
        };
        var wrongSegments = correctSegments.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray());
        wrongSegments["second"] = [99, 99, 99];
        var wrongPost = new FakeNntpClient(
            wrongSegments,
            useCachedYencStreams: true,
            yencHeaders: new Dictionary<string, UsenetYencHeader>
            {
                ["second"] = new()
                {
                    FileName = "wrong.bin",
                    FileSize = 318_803_968,
                    LineLength = 128,
                    PartNumber = 557,
                    TotalParts = 931,
                    PartOffset = 187_525_120,
                    PartSize = 3,
                },
            });
        var correctPost = new FakeNntpClient(correctSegments, useCachedYencStreams: true);
        using var client = CreateProviderClient(wrongPost, correctPost);
        await using var stream = new NzbFileStream(
            ["first", "second", "third"],
            fileSize: 9,
            client,
            articleBufferSize: 4,
            usePipelinedBodyRequests: true);
        using var output = new MemoryStream();

        try
        {
            Log.Logger = logger;
            await stream.CopyToAsync(output);
        }
        finally
        {
            Log.Logger = previousLogger;
        }

        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8, 9], output.ToArray());
        Assert.True(wrongPost.BodyRequestCounts["second"] >= 1);
        Assert.True(correctPost.BodyRequestCounts["second"] >= 1);
        var warning = Assert.Single(sink.Events, IsMismatchWarning);
        Assert.Equal("Streaming", Scalar(warning, "Stage"));
        Assert.Equal(2, Scalar(warning, "RequestedSegmentPosition"));
        Assert.Null(Scalar(warning, "NzbSegmentNumber"));
        Assert.Equal(3, Scalar(warning, "ExpectedTotalParts"));
        Assert.Equal(318_803_968L, Scalar(warning, "ReturnedFileSize"));
    }

    [Fact]
    public async Task GetFileSizeAsync_AllProvidersMismatch_LogsBothAndPreservesRejection()
    {
        var segments = new Dictionary<string, byte[]>
        {
            ["diagnostic-first"] = [1, 2, 3],
            ["diagnostic-last"] = [4, 5, 6],
        };
        var primary = CreateClient(segments, 1, 58, "diagnostic-last");
        var backup = CreateClient(segments, 1, 62, "diagnostic-last");
        using var client = CreateProviderClient(primary, backup);
        var file = new NzbFile { Subject = "private-job.bin" };
        file.Segments.Add(new NzbSegment { Bytes = 3, MessageId = "diagnostic-first", Number = 2 });
        file.Segments.Add(new NzbSegment { Bytes = 3, MessageId = "diagnostic-last", Number = 8 });
        var sink = new CollectingLogEventSink();
        using var logger = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();
        var previousLogger = Log.Logger;
        try
        {
            Log.Logger = logger;
            var exception = await Assert.ThrowsAsync<UsenetMismatchedArticleException>(
                () => client.GetFileSizeAsync(file, CancellationToken.None));
            Assert.Contains("part 1/62 for a file with 2 parts", exception.Message);
        }
        finally
        {
            Log.Logger = previousLogger;
        }

        var warnings = sink.Events.Where(IsMismatchWarning).ToArray();
        Assert.Equal(2, warnings.Length);
        Assert.Equal(58, Scalar(warnings[0], "ReturnedTotalParts"));
        Assert.Equal(62, Scalar(warnings[1], "ReturnedTotalParts"));
        Assert.NotEqual(Scalar(warnings[0], "ProviderRef"), Scalar(warnings[1], "ProviderRef"));
        foreach (var warning in warnings)
        {
            Assert.Equal(2, Scalar(warning, "RequestedSegmentPosition"));
            Assert.Equal(8, Scalar(warning, "NzbSegmentNumber"));
            Assert.Equal(1, Scalar(warning, "ReturnedPartNumber"));
        }
        Assert.Null(file.Segments[^1].ByteRange);
        Assert.Equal(1, primary.BodyRequestCounts["diagnostic-last"]);
        Assert.Equal(1, backup.BodyRequestCounts["diagnostic-last"]);
    }

    [Fact]
    public void MismatchDiagnostics_RedactsIdentifiersAndOnlyThrottlesIdenticalEvidence()
    {
        var sink = new CollectingLogEventSink();
        using var logger = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();
        using var validation = YencFileValidationContext.BeginStreaming(
            ["private-anchor@example", "private-article@example"], null);
        var context = Assert.IsType<YencFileValidationContext>(YencFileValidationContext.Current);
        var header = CreateHeader(1, 58) with { FileName = "/private/media/file.bin", PartOffset = 0 };
        var previousLogger = Log.Logger;
        try
        {
            Log.Logger = logger;
            context.ReportMismatch("private-article@example", "private-provider-a", 222, header);
            context.ReportMismatch("private-article@example", "private-provider-a", 222, header);
            context.ReportMismatch("private-article@example", "private-provider-b", 222, header);
            context.ReportMismatch("private-article@example", "private-provider-a", 222, header with { PartOffset = 42 });
        }
        finally
        {
            Log.Logger = previousLogger;
        }

        var warnings = sink.Events.Where(IsMismatchWarning).ToArray();
        Assert.Equal(3, warnings.Length);
        foreach (var warning in warnings)
        {
            Assert.Null(warning.Exception);
            Assert.Equal(LogEventLevel.Warning, warning.Level);
            Assert.DoesNotContain("private", warning.RenderMessage(), StringComparison.OrdinalIgnoreCase);
            Assert.Equal(1, Scalar(warning, "ReturnedPartNumber"));
            Assert.Equal(58, Scalar(warning, "ReturnedTotalParts"));
            Assert.Equal(222, Scalar(warning, "ResponseCode"));
            foreach (var property in new[] { "FileRef", "ArticleRef", "ProviderRef", "ReturnedNameRef" })
                Assert.Matches("^[0-9A-F]{64}$", Assert.IsType<string>(Scalar(warning, property)));
        }

        Assert.Equal(Scalar(warnings[0], "ArticleRef"), Scalar(warnings[1], "ArticleRef"));
        Assert.Equal(Scalar(warnings[0], "FileRef"), Scalar(warnings[1], "FileRef"));
        Assert.NotEqual(Scalar(warnings[0], "ProviderRef"), Scalar(warnings[1], "ProviderRef"));
        Assert.Equal(0L, Scalar(warnings[0], "ReturnedPartOffset"));
        Assert.Equal(42L, Scalar(warnings[2], "ReturnedPartOffset"));
    }

    private static bool IsMismatchWarning(LogEvent logEvent) =>
        logEvent.MessageTemplate.Text.StartsWith("Rejected yEnc article", StringComparison.Ordinal);

    private static object? Scalar(LogEvent logEvent, string property) =>
        Assert.IsType<ScalarValue>(logEvent.Properties[property]).Value;

    private static FakeNntpClient CreateClient(
        IReadOnlyDictionary<string, byte[]> segments,
        int partNumber,
        int totalParts,
        string segmentId = "segment",
        long partOffset = 0,
        bool? hasTotalParts = null) =>
        new(
            segments,
            useCachedYencStreams: true,
            yencHeaders: new Dictionary<string, UsenetYencHeader>
            {
                [segmentId] = new()
                {
                    FileName = "fake.bin",
                    FileSize = 9,
                    LineLength = 128,
                    PartNumber = partNumber,
                    TotalParts = totalParts,
                    HasTotalParts = hasTotalParts,
                    PartOffset = partOffset,
                    PartSize = 3,
                },
            });

    private static UsenetYencHeader CreateHeader(int partNumber, int totalParts) =>
        new()
        {
            FileName = "fake.bin",
            FileSize = 9,
            LineLength = 128,
            PartNumber = partNumber,
            TotalParts = totalParts,
            PartOffset = 0,
            PartSize = 3,
        };

    private sealed class ArticleNntpClient(
        INntpClient inner,
        YencStream stream) : WrappingNntpClient(inner)
    {
        public ArticleNntpClient(INntpClient inner, UsenetYencHeader header)
            : this(inner, new CachedYencStream(
                header,
                new MemoryStream([1, 2, 3], writable: false)))
        {
        }

        public int ArticleRequestCount { get; private set; }

        public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
            SegmentId segmentId,
            CancellationToken cancellationToken) =>
            DecodedArticleAsync(segmentId, null, cancellationToken);

        public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
            SegmentId segmentId,
            ArticleBodyCompletionHandler? onConnectionReadyAgain,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArticleRequestCount++;
            onConnectionReadyAgain?.Invoke(ArticleBodyResult.Retrieved);
            return Task.FromResult(new UsenetDecodedArticleResponse
            {
                SegmentId = segmentId.ToString(),
                ResponseCode = (int)UsenetResponseType.ArticleRetrievedHeadAndBodyFollow,
                ResponseMessage = "220 fake article",
                ArticleHeaders = new UsenetArticleHeader { Headers = [] },
                Stream = stream,
            });
        }
    }

    private sealed class ThrowingHeaderYencStream() : YencStream(Stream.Null)
    {
        public bool IsDisposed { get; private set; }

        public override ValueTask<UsenetYencHeader?> GetYencHeadersAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<UsenetYencHeader?>(new InvalidDataException("Malformed yEnc header"));

        protected override void Dispose(bool disposing)
        {
            IsDisposed = disposing;
            base.Dispose(disposing);
        }
    }

    private static MultiProviderNntpClient CreateProviderClient(
        INntpClient wrongPost,
        INntpClient correctPost) =>
        new(
            [
                MultiProviderNntpClientTests.CreateProvider(wrongPost, host: "wrong.example"),
                MultiProviderNntpClientTests.CreateProvider(
                    correctPost,
                    host: "correct.example",
                    providerType: ProviderType.BackupOnly),
            ],
            cascadeEnabled: () => true);
}