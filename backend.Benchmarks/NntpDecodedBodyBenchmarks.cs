using System.IO.Pipelines;
using System.Text;
using BenchmarkDotNet.Attributes;
using RapidYencSharp;
using UsenetSharp.Clients;
using UsenetSharp.Models;
using UsenetSharp.Streams;

namespace NzbWebDAV.Benchmarks;

/// <summary>
/// Measures the decoded NNTP BODY path used by playback: <c>NntpLineReader</c>
/// plus <c>NntpYencBodyDecoder</c> writing into a pipe that a concurrent consumer
/// drains. This is the controlling path for issue #1023, unlike
/// <see cref="YencDecodeBenchmarks"/> which only exercises <c>YencStream</c>.
/// </summary>
[MemoryDiagnoser]
[WarmupCount(1)]
[IterationCount(5)]
public class NntpDecodedBodyBenchmarks
{
    private byte[] _wireBody = null!;
    private int _expectedLength;

    [Params(4 * 1024 * 1024, 32 * 1024 * 1024)]
    public int DecodedSize { get; set; }

    [Params(YencCrcValidationMode.Off, YencCrcValidationMode.Require)]
    public YencCrcValidationMode CrcValidation { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        YencEncoder.EnsureInitialized();
        YencDecoder.EnsureInitialized();
        Crc32.EnsureInitialized();

        var payload = new byte[DecodedSize];
        new Random(1023).NextBytes(payload);
        var crc32 = Crc32.Compute(payload);

        int? column = 0;
        var encoded = YencEncoder.EncodeEx(payload, ref column, 128, true);
        encoded = DotStuff(encoded);

        using var output = new MemoryStream(encoded.Length + 256);
        WriteAscii(output, $"=ybegin line=128 size={DecodedSize} name=benchmark.bin\r\n");
        output.Write(encoded);
        WriteAscii(output, $"\r\n=yend size={DecodedSize} crc32={crc32:x8}\r\n.\r\n");

        _wireBody = output.ToArray();
        _expectedLength = DecodedSize;
    }

    [Benchmark]
    public async Task DecodeBody()
    {
        await using var source = new MemoryStream(_wireBody, writable: false);
        using var reader = new NntpLineReader(source);
        var pipe = new Pipe(new PipeOptions(
            pauseWriterThreshold: 1024 * 1024,
            resumeWriterThreshold: 512 * 1024,
            minimumSegmentSize: 64 * 1024,
            useSynchronizationContext: false));
        await using var decodedStream = new DecodedBodyReadStream(
            pipe.Reader.AsStream(), static _ => { });
        var headers = new TaskCompletionSource<UsenetYencHeader?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var operationCts = new CancellationTokenSource();
        using var timeout = new CoalescedReadTimeout(
            TimeSpan.FromSeconds(30), TimeProvider.System, operationCts.Token);
        var options = new UsenetClientOptions { CrcValidation = CrcValidation };
        var decoder = new NntpYencBodyDecoder(
            reader, pipe.Writer, headers, decodedStream, options, 64 * 1024);

#pragma warning disable CA2025 // producer is awaited below before timeout/CTS dispose
        var producer = ProduceAsync(decoder, pipe.Writer, timeout, operationCts.Token);
#pragma warning restore CA2025
        long bytesRead;
        try
        {
            bytesRead = await DrainAndCountAsync(decodedStream).ConfigureAwait(false);
        }
        catch
        {
            await operationCts.CancelAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            await producer.ConfigureAwait(false);
        }

        if (bytesRead != _expectedLength)
        {
            throw new InvalidOperationException(
                $"Decoded benchmark length changed: {bytesRead} != {_expectedLength}.");
        }
    }

    private static async Task ProduceAsync(
        NntpYencBodyDecoder decoder,
        PipeWriter writer,
        CoalescedReadTimeout timeout,
        CancellationToken cancellationToken)
    {
        Exception? failure = null;
        try
        {
            await decoder.ReadAsync(timeout, cancellationToken).ConfigureAwait(false);
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

    private static async Task<long> DrainAndCountAsync(Stream stream)
    {
        var buffer = new byte[64 * 1024];
        long total = 0;
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory()).ConfigureAwait(false);
            if (read == 0)
            {
                return total;
            }

            total += read;
        }
    }

    private static byte[] DotStuff(ReadOnlySpan<byte> data)
    {
        using var stuffed = new MemoryStream(data.Length);
        var atLineStart = true;
        foreach (var value in data)
        {
            if (atLineStart && value == (byte)'.')
            {
                stuffed.WriteByte(value);
            }

            stuffed.WriteByte(value);
            atLineStart = value == (byte)'\n';
        }

        return stuffed.ToArray();
    }

    private static void WriteAscii(Stream output, string value) =>
        output.Write(Encoding.ASCII.GetBytes(value));
}
