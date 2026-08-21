using System.IO.Pipelines;
using System.Text;
using RapidYencSharp;
using UsenetSharp.Clients;
using UsenetSharp.Exceptions;
using UsenetSharp.Models;
using UsenetSharp.Streams;
using UsenetSharpTest.Support;

namespace UsenetSharpTest.Protocol;

[TestFixture]
public class NntpYencBodyDecoderTests
{
    [Test]
    public async Task DecodeAsync_EmptyPayload_RoundTrips()
    {
        var body = YencWireBodies.SinglePart([]);
        var result = await DecodeAsync(body.Wire, [int.MaxValue]);
        AssertSuccessful(result, body);
    }

    [Test]
    public async Task DecodeAsync_OneBytePayload_RoundTrips()
    {
        var body = YencWireBodies.SinglePart([0x04]);
        var result = await DecodeAsync(body.Wire, [1]);
        AssertSuccessful(result, body);
    }

    [Test]
    public async Task DecodeAsync_AllByteValues_RoundTrips()
    {
        var decoded = Enumerable.Range(0, 256).Select(value => (byte)value).ToArray();
        var body = YencWireBodies.SinglePart(decoded, fileName: "all.bin");
        var result = await DecodeAsync(body.Wire, [17, 64, 3]);
        AssertSuccessful(result, body);
    }

    [Test]
    public async Task DecodeAsync_DotStuffedAndEscapedPayload_RoundTrips()
    {
        var decoded = new byte[] { 0x00, (byte)'\n', (byte)'\r', (byte)'=', 0x04, (byte)'.' };
        var body = YencWireBodies.SinglePart(decoded, fileName: "esc.bin");
        var result = await DecodeAsync(body.Wire, [1], readerBufferSize: 2);
        AssertSuccessful(result, body);
    }

    [Test]
    public async Task DecodeAsync_FlushThresholdEdges_RoundTrip(
        [Values(64 * 1024 - 1, 64 * 1024, 64 * 1024 + 1)] int size)
    {
        var decoded = Enumerable.Range(0, size).Select(index => (byte)index).ToArray();
        var body = YencWireBodies.SinglePart(decoded);
        var result = await DecodeAsync(body.Wire, [4096]);
        AssertSuccessful(result, body);
    }

    [Test]
    public async Task DecodeAsync_LargerThanPauseThreshold_RoundTrips()
    {
        var decoded = Enumerable.Range(0, 1_200_000).Select(index => (byte)(index * 13)).ToArray();
        var body = YencWireBodies.SinglePart(decoded);
        var result = await DecodeAsync(
            body.Wire,
            [64 * 1024],
            pauseWriterThreshold: 4096,
            resumeWriterThreshold: 2048);
        AssertSuccessful(result, body);
    }

    [Test]
    public async Task DecodeAsync_FourMiBArticle_RoundTrips()
    {
        var decoded = new byte[4 * 1024 * 1024];
        new Random(1023).NextBytes(decoded);
        var body = YencWireBodies.SinglePart(decoded, fileName: "article.bin");
        var result = await DecodeAsync(body.Wire, [64 * 1024]);
        AssertSuccessful(result, body);
    }

    [Test]
    public async Task DecodeAsync_PreambleAndPostTrailer_RoundTrips()
    {
        var body = YencWireBodies.SinglePart(
            "hello"u8.ToArray(),
            preamble: "ignored preamble\r\n",
            postTrailer: "junk-after-yend\r\n");
        var result = await DecodeAsync(body.Wire, [8]);
        AssertSuccessful(result, body);
    }

    [Test]
    public async Task DecodeAsync_MultipartHeaders_AreExact()
    {
        var decoded = Encoding.ASCII.GetBytes("multipart-payload");
        var body = YencWireBodies.Multipart(
            decoded,
            fileName: "chunk.bin",
            partNumber: 3,
            totalParts: 8,
            partOffset: 360000,
            fileSize: 1_440_000);
        var result = await DecodeAsync(body.Wire, [5, 11, 64]);
        AssertSuccessful(result, body);
        Assert.That(result.Headers!.PartNumber, Is.EqualTo(3));
        Assert.That(result.Headers.TotalParts, Is.EqualTo(8));
        Assert.That(result.Headers.PartOffset, Is.EqualTo(360000));
        Assert.That(result.Headers.FileSize, Is.EqualTo(1_440_000));
    }

    [Test]
    public async Task DecodeAsync_FollowingResponse_RemainsUnread()
    {
        var body = YencWireBodies.SinglePart(
            "keep-next"u8.ToArray(),
            followingResponse: "222 0 <next@example> body\r\n");
        var result = await DecodeAsync(body.Wire, [int.MaxValue], readFollowingLine: true);
        AssertSuccessful(result, body);
        Assert.That(result.FollowingLine, Is.EqualTo("222 0 <next@example> body"));
    }

    [TestCaseSource(nameof(ControlSplitCases))]
    public async Task DecodeAsync_ControlAndTerminatorSplits_PreserveBytes(
        string tokenName,
        int split)
    {
        var decoded = Encoding.ASCII.GetBytes("1234567890123");
        var following = tokenName == "terminator-follow" ? "222 0 <next@example> body\r\n" : null;
        var body = tokenName.StartsWith("ypart", StringComparison.Ordinal)
            ? YencWireBodies.Multipart(decoded, fileName: "long-file-name.bin")
            : YencWireBodies.SinglePart(
                decoded,
                fileName: "long-file-name.bin",
                preamble: tokenName == "preamble-crlf" ? "skip-me\r\n" : null,
                followingResponse: following);

        var splitOffset = tokenName switch
        {
            "ybegin" => body.YBeginOffset,
            "ypart" => body.YPartOffset,
            "yend" => body.YEndOffset,
            "terminator" or "terminator-follow" => body.TerminatorOffset,
            "preamble-crlf" => body.YBeginOffset - 2,
            _ => throw new ArgumentOutOfRangeException(nameof(tokenName))
        };

        var result = await DecodeAsync(
            body.Wire,
            [splitOffset + split, int.MaxValue],
            readerBufferSize: Math.Max(split, 1),
            readFollowingLine: following != null);
        AssertSuccessful(result, body);
        if (following != null)
        {
            Assert.That(result.FollowingLine, Is.EqualTo("222 0 <next@example> body"));
        }
    }

    [Test]
    public async Task DecodeAsync_DotStuffedPrefixSplitBetweenDots_RoundTrips()
    {
        var wire = Encoding.ASCII.GetBytes(
            "=ybegin line=128 size=1 name=dot.bin\r\n" +
            "..\r\n" +
            "=yend size=1\r\n" +
            ".\r\n");
        var stuffedAt = IndexOf(wire, ".."u8);
        Assert.That(stuffedAt, Is.GreaterThanOrEqualTo(0));
        var result = await DecodeAsync(
            wire,
            [stuffedAt + 1, int.MaxValue],
            crcMode: YencCrcValidationMode.Off,
            readerBufferSize: 1);
        Assert.That(result.ProducerException, Is.Null, result.ProducerException?.ToString());
        Assert.That(result.Decoded, Is.EqualTo(new byte[] { 0x04 }));
    }

    [Test]
    public async Task DecodeAsync_CrLfSplit_DoesNotLeakIntoOutput()
    {
        var body = YencWireBodies.SinglePart("crlf"u8.ToArray());
        var crIndex = IndexOf(body.Wire, "\r\n"u8);
        var result = await DecodeAsync(body.Wire, [crIndex + 1, int.MaxValue], readerBufferSize: 1);
        AssertSuccessful(result, body);
        Assert.That(result.Decoded, Does.Not.Contain((byte)'\r'));
    }

    [TestCase(YencCrcValidationMode.Off, false, false, false, true)]
    [TestCase(YencCrcValidationMode.Off, true, false, false, true)]
    [TestCase(YencCrcValidationMode.Off, false, true, false, true)]
    [TestCase(YencCrcValidationMode.WhenPresent, false, false, false, true)]
    [TestCase(YencCrcValidationMode.WhenPresent, true, false, false, false)]
    [TestCase(YencCrcValidationMode.WhenPresent, false, true, false, true)]
    [TestCase(YencCrcValidationMode.Require, false, false, false, true)]
    [TestCase(YencCrcValidationMode.Require, true, false, false, false)]
    [TestCase(YencCrcValidationMode.Require, false, true, false, false)]
    [TestCase(YencCrcValidationMode.Require, false, false, true, false)]
    public async Task DecodeAsync_CrcMatrix(
        YencCrcValidationMode mode,
        bool corruptCrc,
        bool missingCrc,
        bool omitYend,
        bool success)
    {
        var body = YencWireBodies.SinglePart(
            Encoding.ASCII.GetBytes("crc-matrix"),
            includeCrc: !missingCrc,
            corruptCrc: corruptCrc,
            omitYend: omitYend);
        var result = await DecodeAsync(body.Wire, [32], crcMode: mode);
        if (success)
        {
            Assert.That(result.ProducerException, Is.Null);
            Assert.That(result.Decoded, Is.EqualTo(body.Decoded));
        }
        else
        {
            Assert.That(result.ProducerException, Is.TypeOf<InvalidDataException>());
        }
    }

    [Test]
    public async Task DecodeAsync_MultipartCrcUsesPcrc32()
    {
        var decoded = Encoding.ASCII.GetBytes("pcrc-payload");
        var good = RapidYencSharp.Crc32.Compute(decoded);
        var body = YencWireBodies.Multipart(decoded, fileName: "crc.bin");
        var result = await DecodeAsync(body.Wire, [16], crcMode: YencCrcValidationMode.Require);
        Assert.That(result.ProducerException, Is.Null, result.ProducerException?.ToString());
        Assert.That(result.Decoded, Is.EqualTo(decoded));
        Assert.That(good, Is.EqualTo(RapidYencSharp.Crc32.Compute(result.Decoded)));
    }

    [Test]
    public async Task DecodeAsync_MalformedCrcToken_FailsWhenRequired()
    {
        var body = YencWireBodies.SinglePart(
            "badhex"u8.ToArray(),
            includeCrc: false,
            extraYendFields: " crc32=not-hex");
        var result = await DecodeAsync(body.Wire, [int.MaxValue], crcMode: YencCrcValidationMode.Require);
        Assert.That(result.ProducerException, Is.TypeOf<InvalidDataException>());
    }

    [Test]
    public async Task DecodeAsync_YendingPrefix_IsTreatedAsTrailer()
    {
        var decoded = Encoding.ASCII.GetBytes("x");
        int? column = 0;
        var encoded = YencEncoder.EncodeEx(decoded, ref column, 128, true);
        encoded = YencWireBodies.DotStuff(encoded);
        using var output = new MemoryStream();
        output.Write("=ybegin line=128 size=1 name=x.bin\r\n"u8);
        output.Write(encoded);
        output.Write("\r\n=yending size=1\r\n.\r\n"u8);
        var result = await DecodeAsync(output.ToArray(), [3], crcMode: YencCrcValidationMode.Off);
        Assert.That(result.ProducerException, Is.Null, result.ProducerException?.ToString());
        Assert.That(result.Decoded, Is.EqualTo(decoded));
    }

    [Test]
    public async Task DecodeAsync_EqualsDataThatIsNotControl_Decodes()
    {
        // Encoded output may start a line with '=' for escaped bytes; that is payload.
        var decoded = Enumerable.Repeat((byte)0, 200).ToArray();
        var body = YencWireBodies.SinglePart(decoded);
        var result = await DecodeAsync(body.Wire, [7, 1, 64]);
        AssertSuccessful(result, body);
    }

    [Test]
    public async Task DecodeAsync_EofBeforeTerminator_ThrowsProtocolException()
    {
        var body = YencWireBodies.SinglePart("truncated"u8.ToArray());
        var truncated = body.Wire.AsSpan(0, body.TerminatorOffset).ToArray();
        var result = await DecodeAsync(truncated, [8]);
        Assert.That(result.ProducerException, Is.TypeOf<UsenetProtocolException>());
        Assert.That(result.ProducerException!.Message, Does.Contain("terminator"));
    }

    [Test]
    public async Task DecodeAsync_TerminatorBeforeYBegin_ThrowsMissingHeader()
    {
        var wire = Encoding.ASCII.GetBytes("not-yenc\r\n.\r\n");
        var result = await DecodeAsync(wire, [1]);
        Assert.That(result.ProducerException, Is.TypeOf<InvalidDataException>());
        Assert.That(result.ProducerException!.Message, Does.Contain("=ybegin"));
    }

    [Test]
    public async Task DecodeAsync_OverlongLine_Throws()
    {
        var line = new string('a', 20) + "\r\n.\r\n";
        var result = await DecodeAsync(
            Encoding.ASCII.GetBytes(line),
            [3],
            readerBufferSize: 4,
            maximumLineLength: 8);
        Assert.That(result.ProducerException, Is.TypeOf<UsenetProtocolException>());
        Assert.That(result.ProducerException!.Message, Does.Contain("8-byte limit"));
    }

    [Test]
    public async Task DecodeAsync_PreambleBeyondDrainLimit_Throws()
    {
        var preamble = string.Concat(Enumerable.Repeat("skip\r\n", 20));
        var body = YencWireBodies.SinglePart("x"u8.ToArray(), preamble: preamble);
        var result = await DecodeAsync(body.Wire, [int.MaxValue], drainLimit: 10);
        Assert.That(result.ProducerException, Is.TypeOf<UsenetProtocolException>());
        Assert.That(result.ProducerException!.Message, Does.Contain("non-yEnc"));
    }

    [Test]
    public async Task DecodeAsync_TimeoutDuringRefill_ThrowsTimeoutException()
    {
        var body = YencWireBodies.SinglePart("stall"u8.ToArray());
        var timeProvider = new ManualTimeProvider();
        var source = new FragmentedReadStream(body.Wire, [body.PayloadOffset, int.MaxValue], cancelOnRead: 2);
        var decodeTask = DecodeAsync(
            body.Wire,
            [body.PayloadOffset, int.MaxValue],
            crcMode: YencCrcValidationMode.Off,
            timeProvider: timeProvider,
            readTimeout: TimeSpan.FromSeconds(1),
            source: source);
        await source.RefillStarted.WaitAsync(TimeSpan.FromSeconds(2));
        timeProvider.Advance(TimeSpan.FromSeconds(1));
        var result = await decodeTask.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.That(result.ProducerException, Is.TypeOf<TimeoutException>());
    }

    [Test]
    public async Task DecodeAsync_ObserverBalanceReturnsToZero()
    {
        var body = YencWireBodies.SinglePart(Encoding.ASCII.GetBytes("observer"));
        var result = await DecodeAsync(body.Wire, [9]);
        AssertSuccessful(result, body);
        Assert.That(result.ObserverBalance, Is.EqualTo(0));
    }

    private static IEnumerable<TestCaseData> ControlSplitCases()
    {
        foreach (var data in SplitCaseList("ybegin", "=ybegin line=128 size=13 name=long-file-name.bin\r\n"))
        {
            yield return data;
        }

        foreach (var data in SplitCaseList("ypart", "=ypart begin=1 end=13\r\n"))
        {
            yield return data;
        }

        foreach (var data in SplitCaseList("yend", "=yend size=13 crc32=89abcdef\r\n"))
        {
            yield return data;
        }

        foreach (var data in SplitCaseList("terminator", ".\r\n"))
        {
            yield return data;
        }

        foreach (var data in SplitCaseList("terminator-follow", ".\r\n"))
        {
            yield return data;
        }

        foreach (var data in SplitCaseList("preamble-crlf", "\r\n"))
        {
            yield return data;
        }
    }

    private static IEnumerable<TestCaseData> SplitCaseList(string name, string token)
    {
        var bytes = Encoding.ASCII.GetBytes(token);
        for (var split = 1; split < bytes.Length; split++)
        {
            yield return new TestCaseData(name, split).SetName($"{name}_split_{split}");
        }
    }

    private static void AssertSuccessful(DecodeResult result, YencWireBody body)
    {
        Assert.That(result.ProducerException, Is.Null, result.ProducerException?.ToString());
        Assert.That(result.Decoded, Is.EqualTo(body.Decoded));
        Assert.That(result.Headers, Is.Not.Null);
        Assert.That(result.Headers!.FileName, Is.EqualTo(body.FileName));
        Assert.That(result.Headers.FileSize, Is.EqualTo(body.FileSize));
        Assert.That(result.ObserverBalance, Is.EqualTo(0));
    }

    private static int IndexOf(byte[] haystack, ReadOnlySpan<byte> needle)
    {
        var span = haystack.AsSpan();
        var index = span.IndexOf(needle);
        return index;
    }

    private static async Task<DecodeResult> DecodeAsync(
        byte[] wireBody,
        int[] fragmentSizes,
        YencCrcValidationMode crcMode = YencCrcValidationMode.Require,
        int readerBufferSize = 64 * 1024,
        int maximumLineLength = 64 * 1024,
        long drainLimit = 1024 * 1024,
        long pauseWriterThreshold = 1024 * 1024,
        long resumeWriterThreshold = 512 * 1024,
        bool readFollowingLine = false,
        TimeProvider? timeProvider = null,
        TimeSpan? readTimeout = null,
        FragmentedReadStream? source = null)
    {
        source ??= new FragmentedReadStream(wireBody, fragmentSizes);
        using var reader = new NntpLineReader(source, maximumLineLength, readerBufferSize);
        var pipe = new Pipe(new PipeOptions(
            pauseWriterThreshold: pauseWriterThreshold,
            resumeWriterThreshold: resumeWriterThreshold,
            minimumSegmentSize: 64 * 1024,
            useSynchronizationContext: false));
        var observerBalance = 0L;
        await using var decodedStream = new DecodedBodyReadStream(
            pipe.Reader.AsStream(),
            delta => Interlocked.Add(ref observerBalance, delta));
        var headers = new TaskCompletionSource<UsenetYencHeader?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var operationCts = new CancellationTokenSource();
        using var timeout = new CoalescedReadTimeout(
            readTimeout ?? TimeSpan.FromSeconds(30),
            timeProvider ?? TimeProvider.System,
            operationCts.Token);
        var options = new UsenetClientOptions
        {
            CrcValidation = crcMode,
            AbandonedBodyDrainLimit = drainLimit,
            DecodedBodyPauseWriterThreshold = pauseWriterThreshold,
            DecodedBodyResumeWriterThreshold = resumeWriterThreshold
        };

        var decoder = new NntpYencBodyDecoder(
            reader, pipe.Writer, headers, decodedStream, options, 64 * 1024);
        var producer = ProduceAsync(() => decoder.ReadAsync(timeout, operationCts.Token), pipe.Writer);

        using var decoded = new MemoryStream();
        Exception? consumerException = null;
        try
        {
            await decodedStream.CopyToAsync(decoded).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            consumerException = exception;
        }

        Exception? producerException = null;
        try
        {
            await producer.ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            producerException = exception;
        }

        UsenetYencHeader? parsedHeaders = null;
        if (headers.Task.IsCompleted)
        {
            try
            {
                parsedHeaders = await headers.Task.ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                producerException ??= exception;
            }
        }

        string? followingLine = null;
        if (readFollowingLine && producerException is null)
        {
            followingLine = await reader.ReadLineAsync(CancellationToken.None).ConfigureAwait(false);
        }

        return new DecodeResult(
            decoded.ToArray(),
            parsedHeaders,
            producerException ?? consumerException,
            Volatile.Read(ref observerBalance),
            source.SourceOffset,
            followingLine);
    }

    private static async Task ProduceAsync(Func<Task> readAsync, PipeWriter writer)
    {
        Exception? failure = null;
        try
        {
            await readAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            failure = exception;
            throw;
        }
        finally
        {
            await writer.CompleteAsync(failure).ConfigureAwait(false);
        }
    }

    private sealed record DecodeResult(
        byte[] Decoded,
        UsenetYencHeader? Headers,
        Exception? ProducerException,
        long ObserverBalance,
        int SourceBytesConsumed,
        string? FollowingLine);
}
