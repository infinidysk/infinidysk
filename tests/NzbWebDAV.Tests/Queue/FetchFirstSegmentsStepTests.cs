using System.Text;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Models;
using NzbWebDAV.Models.Nzb;
using NzbWebDAV.Queue.DeobfuscationSteps._1.FetchFirstSegment;
using NzbWebDAV.Streams;
using UsenetSharp.Models;
using UsenetSharp.Streams;

namespace NzbWebDAV.Tests.Queue;

public class FetchFirstSegmentsStepTests
{
    [Fact]
    public async Task FetchFirstSegments_PipelinedDefinitivelyMissingImportant_AbortsEarly()
    {
        var config = CreatePipeliningConfig(enabled: true, depth: 4);
        using var client = new MissingPipelinedNntpClient(definitivelyMissing: true);
        var files = new List<NzbFile>
        {
            CreateFile("a@example.com", "\"a.rar\" yEnc"),
            CreateFile("b@example.com", "\"b.rar\" yEnc"),
            CreateFile("c@example.com", "\"c.rar\" yEnc"),
        };

        var ex = await Assert.ThrowsAsync<NonRetryableDownloadException>(() =>
            FetchFirstSegmentsStep.FetchFirstSegments(files, client, config, CancellationToken.None));

        Assert.Contains("a.rar", ex.Message);
        Assert.Equal(0, client.ArticleFetches);
        Assert.True(client.PipelinedResultsYielded >= 1);
        Assert.True(client.PipelinedResultsYielded < files.Count);
    }

    [Fact]
    public async Task FetchFirstSegments_PipelinedUnimportantMissing_ContinuesThenAbortsOnImportant()
    {
        var config = CreatePipeliningConfig(enabled: true, depth: 4);
        using var client = new MissingPipelinedNntpClient(definitivelyMissing: true);
        var files = new List<NzbFile>
        {
            CreateFile("par@example.com", "\"release.par2\" yEnc"),
            CreateFile("sfv@example.com", "\"checksum.sfv\" yEnc"),
            CreateFile("rar@example.com", "\"part01.rar\" yEnc"),
            CreateFile("rar2@example.com", "\"part02.rar\" yEnc"),
        };

        var ex = await Assert.ThrowsAsync<NonRetryableDownloadException>(() =>
            FetchFirstSegmentsStep.FetchFirstSegments(files, client, config, CancellationToken.None));

        Assert.Contains("part01.rar", ex.Message);
        Assert.Equal(0, client.ArticleFetches);
        // Unimportant files + the triggering rar are yielded; later rar must not be.
        Assert.Equal(3, client.PipelinedResultsYielded);
    }

    [Fact]
    public async Task FetchFirstSegments_PipelinedNonDefinitiveMiss_AbortsOnRescueImportant()
    {
        var config = CreatePipeliningConfig(enabled: true, depth: 4);
        using var client = new MissingPipelinedNntpClient(definitivelyMissing: false);
        // More files than rescue concurrency (maxQueueConnections+5) so abort can skip the tail.
        var files = Enumerable.Range(0, 20)
            .Select(i => CreateFile($"{i}@example.com", $"\"part{i:D2}.rar\" yEnc"))
            .ToList();

        var ex = await Assert.ThrowsAsync<NonRetryableDownloadException>(() =>
            FetchFirstSegmentsStep.FetchFirstSegments(files, client, config, CancellationToken.None));

        Assert.Contains(".rar", ex.Message);
        Assert.True(client.ArticleFetches >= 1);
        Assert.True(client.ArticleFetches < files.Count);
    }

    [Fact]
    public async Task FetchFirstSegments_ConcurrentImportantMissing_AbortsFurtherFetches()
    {
        var config = CreatePipeliningConfig(enabled: false, depth: 4);
        // More files than concurrent fetch slots (maxQueueConnections+5) so abort can skip the tail.
        var missingIds = Enumerable.Range(0, 20).Select(i => $"{i}@example.com").ToArray();
        using var client = new TrackingArticleNntpClient(missingIds: missingIds);
        var files = missingIds
            .Select((id, i) => CreateFile(id, $"\"part{i:D2}.rar\" yEnc"))
            .ToList();

        var ex = await Assert.ThrowsAsync<NonRetryableDownloadException>(() =>
            FetchFirstSegmentsStep.FetchFirstSegments(files, client, config, CancellationToken.None));

        Assert.Contains(".rar", ex.Message);
        Assert.True(client.RequestedSegmentIds.Count < files.Count);
    }

    [Fact]
    public async Task FetchFirstSegments_ConcurrentUnimportantMissing_FetchesImportantAfter()
    {
        var config = CreatePipeliningConfig(enabled: false, depth: 4);
        using var client = new TrackingArticleNntpClient(
            missingIds: ["par@example.com"],
            presentPayload: Encoding.ASCII.GetBytes(new string('x', 64)));
        var files = new List<NzbFile>
        {
            CreateFile("par@example.com", "\"release.par2\" yEnc"),
            CreateFile("rar@example.com", "\"movie.rar\" yEnc"),
        };

        var results = await FetchFirstSegmentsStep.FetchFirstSegments(
            files, client, config, CancellationToken.None);

        Assert.Equal(2, results.Count);
        Assert.True(results[0].MissingFirstSegment);
        Assert.False(results[1].MissingFirstSegment);
        Assert.Contains("par@example.com", client.RequestedSegmentIds);
        Assert.Contains("rar@example.com", client.RequestedSegmentIds);
    }

    [Fact]
    public async Task FetchFirstSegments_ConcurrentTransientError_DoesNotTreatAsMissing()
    {
        var config = CreatePipeliningConfig(enabled: false, depth: 4);
        using var client = new TransientErrorNntpClient();
        var files = new List<NzbFile>
        {
            CreateFile("a@example.com", "\"a.rar\" yEnc"),
        };

        var ex = await Assert.ThrowsAsync<RetryableDownloadException>(() =>
            FetchFirstSegmentsStep.FetchFirstSegments(files, client, config, CancellationToken.None));

        Assert.IsType<IOException>(ex.InnerException);
        Assert.Equal("provider blip", ex.InnerException!.Message);
        Assert.Contains("a.rar", ex.Message);
    }

    [Fact]
    public async Task FetchFirstSegments_ConcurrentNestedSocketError_IsRetryable()
    {
        var config = CreatePipeliningConfig(enabled: false, depth: 4);
        using var client = new TransientErrorNntpClient(nestedSocket: true);
        var files = new List<NzbFile>
        {
            CreateFile("a@example.com", "\"a.rar\" yEnc"),
        };

        var ex = await Assert.ThrowsAsync<RetryableDownloadException>(() =>
            FetchFirstSegmentsStep.FetchFirstSegments(files, client, config, CancellationToken.None));

        Assert.IsType<IOException>(ex.InnerException);
        Assert.IsType<System.Net.Sockets.SocketException>(ex.InnerException!.InnerException);
    }

    [Fact]
    public async Task FetchFirstSegments_PipelinedRescueTransientError_IsRetryable()
    {
        var config = CreatePipeliningConfig(enabled: true, depth: 4);
        using var client = new PipelinedFailThenRescueTransientNntpClient();
        var files = new List<NzbFile>
        {
            CreateFile("a@example.com", "\"a.rar\" yEnc"),
        };

        var ex = await Assert.ThrowsAsync<RetryableDownloadException>(() =>
            FetchFirstSegmentsStep.FetchFirstSegments(files, client, config, CancellationToken.None));

        Assert.IsType<IOException>(ex.InnerException);
        Assert.Equal(1, client.ArticleFetches);
    }

    [Fact]
    public async Task FetchFirstSegments_PipelinedMismatch_RescuesViaPerArticlePath()
    {
        var config = CreatePipeliningConfig(enabled: true, depth: 4);
        using var client = new MismatchThenRescueNntpClient();
        var files = new List<NzbFile>
        {
            CreateFile("a@example.com", "\"a.rar\" yEnc"),
            CreateFile("b@example.com", "\"b.rar\" yEnc"),
        };

        var results = await FetchFirstSegmentsStep.FetchFirstSegments(
            files, client, config, CancellationToken.None);

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.False(r.MissingFirstSegment));
        Assert.All(results, r => Assert.NotNull(r.First16KB));
        Assert.Equal(2, client.ArticleFetches);
        Assert.Equal(1, client.PipelinedEnumerations);
    }

    [Fact]
    public async Task FetchFirstSegments_PipelinedSuccess_SetsByteRangeFromYencHeaders()
    {
        var config = CreatePipeliningConfig(enabled: true, depth: 2);
        var payload = Encoding.ASCII.GetBytes(new string('x', 64));
        using var client = new MatchingPipelinedNntpClient(new Dictionary<string, byte[]>
        {
            ["seg@example.com"] = payload,
        });
        var file = CreateFile("seg@example.com", "\"movie.rar\" yEnc");

        var result = Assert.Single(await FetchFirstSegmentsStep.FetchFirstSegments(
            [file], client, config, CancellationToken.None));

        Assert.False(result.MissingFirstSegment);
        Assert.NotNull(result.Header);
        Assert.NotNull(file.Segments[0].ByteRange);
        Assert.Equal(result.Header!.PartOffset, file.Segments[0].ByteRange!.StartInclusive);
        Assert.Equal(result.Header.PartSize, file.Segments[0].ByteRange!.Count);
        Assert.Equal(0, client.ArticleFetches);
    }

    private static ConfigManager CreatePipeliningConfig(bool enabled, int depth)
    {
        var config = new ConfigManager();
        config.UpdateValues(
        [
            new ConfigItem
            {
                ConfigName = ConfigKeys.UsenetPipeliningEnabled,
                ConfigValue = enabled ? "true" : "false",
            },
            new ConfigItem
            {
                ConfigName = ConfigKeys.UsenetPipeliningDepth,
                ConfigValue = depth.ToString(),
            },
            new ConfigItem
            {
                ConfigName = ConfigKeys.UsenetMaxQueueConnections,
                ConfigValue = "1",
            },
        ]);
        return config;
    }

    private static NzbFile CreateFile(string messageId, string subject) => new()
    {
        Subject = subject,
        Segments =
        {
            new NzbSegment { MessageId = messageId, Bytes = 100 },
        },
    };

    private static CachedYencStream CreateCachedStream(byte[] payload, long partOffset = 0)
    {
        var headers = new UsenetYencHeader
        {
            FileName = "fake.bin",
            FileSize = partOffset + payload.Length,
            LineLength = 128,
            PartNumber = 1,
            TotalParts = 1,
            PartOffset = partOffset,
            PartSize = payload.Length,
        };
        return new CachedYencStream(headers, new MemoryStream(payload, writable: false));
    }

    private sealed class MissingPipelinedNntpClient(bool definitivelyMissing) : NntpClient
    {
        public int ArticleFetches { get; private set; }
        public int PipelinedResultsYielded { get; private set; }

        public override async IAsyncEnumerable<PipelinedArticleResult> DecodedArticlesPipelinedAsync(
            IReadOnlyList<string> segmentIds,
            int depth,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var id in segmentIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                PipelinedResultsYielded++;
                yield return new PipelinedArticleResult
                {
                    SegmentId = id,
                    Found = false,
                    Stream = null,
                    ArticleHeaders = null,
                    DefinitivelyMissing = definitivelyMissing,
                };
            }
        }

        public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
            SegmentId segmentId, CancellationToken cancellationToken) =>
            DecodedArticleAsync(segmentId, null, cancellationToken);

        public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
            SegmentId segmentId,
            ArticleBodyCompletionHandler? onConnectionReadyAgain,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArticleFetches++;
            onConnectionReadyAgain?.Invoke(ArticleBodyResult.NotFound);
            throw new UsenetArticleNotFoundException(segmentId.ToString());
        }

        public override Task ConnectAsync(
            string host, int port, bool useSsl, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public override Task<UsenetResponse> AuthenticateAsync(
            string user, string pass, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetStatResponse> StatAsync(
            SegmentId segmentId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetHeadResponse> HeadAsync(
            SegmentId segmentId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
            SegmentId segmentId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
            SegmentId segmentId,
            ArticleBodyCompletionHandler? onConnectionReadyAgain,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetDecodedBodyBatch> DecodedBodiesAsync(
            IReadOnlyList<SegmentId> segmentIds,
            ArticleBodyCompletionHandler? onConnectionReadyAgain,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetDateResponse> DateAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override void Dispose()
        {
        }
    }

    private sealed class TrackingArticleNntpClient(
        IReadOnlyCollection<string> missingIds,
        byte[]? presentPayload = null) : NntpClient
    {
        public HashSet<string> RequestedSegmentIds { get; } = new(StringComparer.Ordinal);

        public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
            SegmentId segmentId, CancellationToken cancellationToken) =>
            DecodedArticleAsync(segmentId, null, cancellationToken);

        public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
            SegmentId segmentId,
            ArticleBodyCompletionHandler? onConnectionReadyAgain,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = segmentId.ToString();
            RequestedSegmentIds.Add(key);

            if (missingIds.Contains(key))
            {
                onConnectionReadyAgain?.Invoke(ArticleBodyResult.NotFound);
                throw new UsenetArticleNotFoundException(key);
            }

            var payload = presentPayload ?? Encoding.ASCII.GetBytes(new string('x', 64));
            onConnectionReadyAgain?.Invoke(ArticleBodyResult.Retrieved);
            return Task.FromResult(new UsenetDecodedArticleResponse
            {
                SegmentId = key,
                ResponseCode = (int)UsenetResponseType.ArticleRetrievedHeadAndBodyFollow,
                ResponseMessage = "220 article",
                Stream = CreateCachedStream(payload),
                ArticleHeaders = new UsenetArticleHeader
                {
                    Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["Date"] = DateTimeOffset.UtcNow.ToString("R"),
                    },
                },
            });
        }

        public override Task ConnectAsync(
            string host, int port, bool useSsl, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public override Task<UsenetResponse> AuthenticateAsync(
            string user, string pass, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetStatResponse> StatAsync(
            SegmentId segmentId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetHeadResponse> HeadAsync(
            SegmentId segmentId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
            SegmentId segmentId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
            SegmentId segmentId,
            ArticleBodyCompletionHandler? onConnectionReadyAgain,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetDecodedBodyBatch> DecodedBodiesAsync(
            IReadOnlyList<SegmentId> segmentIds,
            ArticleBodyCompletionHandler? onConnectionReadyAgain,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetDateResponse> DateAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override void Dispose()
        {
        }
    }

    private sealed class TransientErrorNntpClient(bool nestedSocket = false) : NntpClient
    {
        public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
            SegmentId segmentId, CancellationToken cancellationToken) =>
            DecodedArticleAsync(segmentId, null, cancellationToken);

        public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
            SegmentId segmentId,
            ArticleBodyCompletionHandler? onConnectionReadyAgain,
            CancellationToken cancellationToken)
        {
            onConnectionReadyAgain?.Invoke(ArticleBodyResult.NotRetrieved);
            if (nestedSocket)
            {
                throw new IOException(
                    "provider blip",
                    new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.ConnectionReset));
            }
            throw new IOException("provider blip");
        }

        public override Task ConnectAsync(
            string host, int port, bool useSsl, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public override Task<UsenetResponse> AuthenticateAsync(
            string user, string pass, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetStatResponse> StatAsync(
            SegmentId segmentId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetHeadResponse> HeadAsync(
            SegmentId segmentId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
            SegmentId segmentId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
            SegmentId segmentId,
            ArticleBodyCompletionHandler? onConnectionReadyAgain,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetDecodedBodyBatch> DecodedBodiesAsync(
            IReadOnlyList<SegmentId> segmentIds,
            ArticleBodyCompletionHandler? onConnectionReadyAgain,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetDateResponse> DateAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override void Dispose()
        {
        }
    }

    /// <summary>
    /// Pipelined batch yields nothing useful (mismatched ids), then rescue DecodedArticleAsync
    /// throws a transient IOException — should surface as RetryableDownloadException.
    /// </summary>
    private sealed class PipelinedFailThenRescueTransientNntpClient : NntpClient
    {
        public int ArticleFetches { get; private set; }

        public override async IAsyncEnumerable<PipelinedArticleResult> DecodedArticlesPipelinedAsync(
            IReadOnlyList<string> segmentIds,
            int depth,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var _ in segmentIds)
            {
                yield return new PipelinedArticleResult
                {
                    SegmentId = "unknown@example.com",
                    Found = true,
                    Stream = CreateCachedStream(Encoding.ASCII.GetBytes("wrong")),
                    ArticleHeaders = null,
                    DefinitivelyMissing = false,
                };
            }
        }

        public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
            SegmentId segmentId, CancellationToken cancellationToken) =>
            DecodedArticleAsync(segmentId, null, cancellationToken);

        public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
            SegmentId segmentId,
            ArticleBodyCompletionHandler? onConnectionReadyAgain,
            CancellationToken cancellationToken)
        {
            ArticleFetches++;
            onConnectionReadyAgain?.Invoke(ArticleBodyResult.NotRetrieved);
            throw new IOException("rescue provider blip");
        }

        public override Task ConnectAsync(
            string host, int port, bool useSsl, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public override Task<UsenetResponse> AuthenticateAsync(
            string user, string pass, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetStatResponse> StatAsync(
            SegmentId segmentId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetHeadResponse> HeadAsync(
            SegmentId segmentId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
            SegmentId segmentId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
            SegmentId segmentId,
            ArticleBodyCompletionHandler? onConnectionReadyAgain,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetDecodedBodyBatch> DecodedBodiesAsync(
            IReadOnlyList<SegmentId> segmentIds,
            ArticleBodyCompletionHandler? onConnectionReadyAgain,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetDateResponse> DateAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override void Dispose()
        {
        }
    }

    private sealed class MismatchThenRescueNntpClient : NntpClient
    {
        public int PipelinedEnumerations { get; private set; }
        public int ArticleFetches { get; private set; }

        public override async IAsyncEnumerable<PipelinedArticleResult> DecodedArticlesPipelinedAsync(
            IReadOnlyList<string> segmentIds,
            int depth,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            PipelinedEnumerations++;
            // Wrong SegmentId → FetchFirstSegments leaves slots null for rescue.
            foreach (var _ in segmentIds)
            {
                yield return new PipelinedArticleResult
                {
                    SegmentId = "unknown@example.com",
                    Found = true,
                    Stream = CreateCachedStream(Encoding.ASCII.GetBytes("wrong")),
                    ArticleHeaders = null,
                };
            }
        }

        public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
            SegmentId segmentId, CancellationToken cancellationToken) =>
            DecodedArticleAsync(segmentId, null, cancellationToken);

        public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
            SegmentId segmentId,
            ArticleBodyCompletionHandler? onConnectionReadyAgain,
            CancellationToken cancellationToken)
        {
            ArticleFetches++;
            onConnectionReadyAgain?.Invoke(ArticleBodyResult.Retrieved);
            return Task.FromResult(new UsenetDecodedArticleResponse
            {
                SegmentId = segmentId.ToString(),
                ResponseCode = (int)UsenetResponseType.ArticleRetrievedHeadAndBodyFollow,
                ResponseMessage = "220 article",
                Stream = CreateCachedStream(Encoding.ASCII.GetBytes(segmentId.ToString())),
                ArticleHeaders = new UsenetArticleHeader
                {
                    Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["Date"] = DateTimeOffset.UtcNow.ToString("R"),
                    },
                },
            });
        }

        public override Task ConnectAsync(
            string host, int port, bool useSsl, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public override Task<UsenetResponse> AuthenticateAsync(
            string user, string pass, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetStatResponse> StatAsync(
            SegmentId segmentId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetHeadResponse> HeadAsync(
            SegmentId segmentId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
            SegmentId segmentId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
            SegmentId segmentId,
            ArticleBodyCompletionHandler? onConnectionReadyAgain,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetDecodedBodyBatch> DecodedBodiesAsync(
            IReadOnlyList<SegmentId> segmentIds,
            ArticleBodyCompletionHandler? onConnectionReadyAgain,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetDateResponse> DateAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override void Dispose()
        {
        }
    }

    private sealed class MatchingPipelinedNntpClient(IReadOnlyDictionary<string, byte[]> segments) : NntpClient
    {
        public int ArticleFetches { get; private set; }

        public override Task<UsenetDecodedBodyBatch> DecodedBodiesAsync(
            IReadOnlyList<SegmentId> segmentIds,
            ArticleBodyCompletionHandler? onConnectionReadyAgain,
            CancellationToken cancellationToken)
        {
            var responses = segmentIds.Select(id =>
            {
                var key = id.ToString();
                var bytes = segments[key];
                return Task.FromResult(new UsenetDecodedBodyResponse
                {
                    SegmentId = key,
                    ResponseCode = (int)UsenetResponseType.ArticleRetrievedBodyFollows,
                    ResponseMessage = $"222 0 <{key}>",
                    Stream = CreateCachedStream(bytes, partOffset: 100),
                });
            }).ToArray();
            onConnectionReadyAgain?.Invoke(ArticleBodyResult.Retrieved);
            return Task.FromResult(new UsenetDecodedBodyBatch { Responses = responses });
        }

        public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
            SegmentId segmentId, CancellationToken cancellationToken)
        {
            ArticleFetches++;
            throw new InvalidOperationException("Rescue path should not run for matching pipelined results");
        }

        public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
            SegmentId segmentId,
            ArticleBodyCompletionHandler? onConnectionReadyAgain,
            CancellationToken cancellationToken) =>
            DecodedArticleAsync(segmentId, cancellationToken);

        public override Task ConnectAsync(
            string host, int port, bool useSsl, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public override Task<UsenetResponse> AuthenticateAsync(
            string user, string pass, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetStatResponse> StatAsync(
            SegmentId segmentId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetHeadResponse> HeadAsync(
            SegmentId segmentId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
            SegmentId segmentId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
            SegmentId segmentId,
            ArticleBodyCompletionHandler? onConnectionReadyAgain,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetDateResponse> DateAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override void Dispose()
        {
        }
    }
}
