using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;
using NzbWebDAV.Api.Controllers;
using NzbWebDAV.Api.Controllers.Warden;
using NzbWebDAV.Logging;
using NzbWebDAV.Services;
using NzbWebDAV.Tests.TestUtils;

namespace NzbWebDAV.Tests.Api;

[Collection(nameof(HttpIntegrationCollection))]
public sealed class WardenSourcesImportControllerTests(
    NzbDavWebApplicationFactory factory)
{
    private const int MaxUploadBytes = 5 * 1024 * 1024;

    [Fact]
    public async Task Import_MultipartWithoutBoundary_ReturnsBadRequestProblem()
    {
        using var client = factory.CreateAuthenticatedClient();
        using var content = MalformedMultipartWithoutBoundary();

        using var response = await client.PostAsync("/api/warden-sources-import", content);
        using var problem = await AdminProblemAssertions.AssertProblemAsync(
            response,
            HttpStatusCode.BadRequest,
            "Invalid form data.");

        Assert.Equal(
            "https://www.infinidysk.com/problems/bad-request",
            problem.RootElement.GetProperty("type").GetString());
        Assert.DoesNotContain(
            "boundary",
            problem.RootElement.GetProperty("detail").GetString(),
            StringComparison.OrdinalIgnoreCase);

        var traceId = problem.RootElement.GetProperty("traceId").GetString();
        var sink = factory.Services.GetRequiredService<LogBufferSink>();
        Assert.DoesNotContain(
            sink.Snapshot(50, ["Error"], source: null, search: null, beforeSequence: null).Entries,
            entry => entry.TraceId == traceId
                && entry.Message.Contains("Unhandled admin API request failure", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Import_MalformedMultipartWithoutApiKey_IsRejectedBeforeFormParsing()
    {
        using var client = factory.CreateClient();
        using var content = MalformedMultipartWithoutBoundary();

        using var response = await client.PostAsync("/api/warden-sources-import", content);

        await AdminProblemAssertions.AssertProblemAsync(
            response,
            HttpStatusCode.Unauthorized,
            "API Key");
    }

    [Fact]
    public async Task Import_AsciiTextAtExactlyFiveMiB_IsAccepted()
    {
        var url = $"https://example.invalid/exact-{Guid.NewGuid():N}";
        SeedRemoteSource(url);
        var content = BuildAsciiContent(url, MaxUploadBytes);
        using var form = BuildTextForm(content);
        using var client = factory.CreateAuthenticatedClient();

        using var response = await client.PostAsync("/api/warden-sources-import", form);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.True(json.RootElement.GetProperty("status").GetBoolean());
        Assert.Equal(0, json.RootElement.GetProperty("added").GetInt32());
        Assert.Equal(1, json.RootElement.GetProperty("skipped").GetInt32());
        Assert.Equal(0, json.RootElement.GetProperty("invalid").GetInt32());
    }

    [Fact]
    public async Task Import_AsciiTextOneByteOverFiveMiB_ReturnsBadRequest()
    {
        var url = $"https://example.invalid/over-{Guid.NewGuid():N}";
        var content = BuildAsciiContent(url, MaxUploadBytes + 1);
        using var form = BuildTextForm(content);
        using var client = factory.CreateAuthenticatedClient();

        using var response = await client.PostAsync("/api/warden-sources-import", form);

        await AdminProblemAssertions.AssertProblemAsync(
            response,
            HttpStatusCode.BadRequest,
            "Input is too large.");
        AssertSourceUrlNotImported(url);
    }

    [Fact]
    public async Task Import_MultibyteTextOneUtf8ByteOverLimit_ReturnsBadRequest()
    {
        var url = $"https://example.invalid/unicode-{Guid.NewGuid():N}";
        var content = BuildMultibyteContent(url, MaxUploadBytes + 1);
        Assert.True(content.Length < MaxUploadBytes);
        Assert.Equal(MaxUploadBytes + 1, Encoding.UTF8.GetByteCount(content));

        using var form = BuildTextForm(content);
        using var client = factory.CreateAuthenticatedClient();
        using var response = await client.PostAsync("/api/warden-sources-import", form);

        await AdminProblemAssertions.AssertProblemAsync(
            response,
            HttpStatusCode.BadRequest,
            "Input is too large.");
        AssertSourceUrlNotImported(url);
    }

    [Fact]
    public async Task Import_Utf16FileUnderRawLimitButOverUtf8Limit_ReturnsBadRequest()
    {
        var url = $"https://example.invalid/utf16-{Guid.NewGuid():N}";
        var content = BuildMultibyteContent(url, MaxUploadBytes + 1);
        var fileBytes = Encoding.Unicode.GetPreamble()
            .Concat(Encoding.Unicode.GetBytes(content))
            .ToArray();

        Assert.True(fileBytes.Length <= MaxUploadBytes);
        Assert.True(content.Length < MaxUploadBytes);
        Assert.Equal(MaxUploadBytes + 1, Encoding.UTF8.GetByteCount(content));

        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(fileBytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("text/plain")
        {
            CharSet = "utf-16",
        };
        form.Add(file, "file", "sources.txt");
        using var client = factory.CreateAuthenticatedClient();

        using var response = await client.PostAsync("/api/warden-sources-import", form);

        await AdminProblemAssertions.AssertProblemAsync(
            response,
            HttpStatusCode.BadRequest,
            "Input is too large.");
        AssertSourceUrlNotImported(url);
    }

    [Fact]
    public async Task Import_AsciiFileAtExactlyFiveMiB_IsAccepted()
    {
        var url = $"https://example.invalid/file-exact-{Guid.NewGuid():N}";
        SeedRemoteSource(url);
        var content = BuildAsciiContent(url, MaxUploadBytes);
        var fileBytes = Encoding.UTF8.GetBytes(content);
        Assert.Equal(MaxUploadBytes, fileBytes.Length);

        using var form = BuildFileForm(fileBytes);
        using var client = factory.CreateAuthenticatedClient();

        using var response = await client.PostAsync("/api/warden-sources-import", form);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.True(json.RootElement.GetProperty("status").GetBoolean());
        Assert.Equal(0, json.RootElement.GetProperty("added").GetInt32());
        Assert.Equal(1, json.RootElement.GetProperty("skipped").GetInt32());
        Assert.Equal(0, json.RootElement.GetProperty("invalid").GetInt32());
    }

    [Fact]
    public async Task Import_AsciiFileOneByteOverFiveMiB_ReturnsFileTooLarge()
    {
        var url = $"https://example.invalid/file-over-{Guid.NewGuid():N}";
        var content = BuildAsciiContent(url, MaxUploadBytes + 1);
        var fileBytes = Encoding.UTF8.GetBytes(content);
        Assert.Equal(MaxUploadBytes + 1, fileBytes.Length);

        using var form = BuildFileForm(fileBytes);
        using var client = factory.CreateAuthenticatedClient();

        using var response = await client.PostAsync("/api/warden-sources-import", form);

        await AdminProblemAssertions.AssertProblemAsync(
            response,
            HttpStatusCode.BadRequest,
            "File is too large.");
        AssertSourceUrlNotImported(url);
    }

    [Fact]
    public async Task Import_FormReadCancellation_RemainsCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var context = new DefaultHttpContext
        {
            RequestAborted = cancellation.Token,
        };
        context.Request.ContentType = "multipart/form-data; boundary=test";
        context.Features.Set<IFormFeature>(
            new FaultingFormFeature(ct => Task.FromCanceled<IFormCollection>(ct)));

        var controller = new TestController
        {
            ControllerContext = new ControllerContext { HttpContext = context },
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => controller.HandleApiRequest());
    }

    [Fact]
    public async Task Import_FileReadCancellation_RemainsCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var context = new DefaultHttpContext
        {
            RequestAborted = cancellation.Token,
        };
        context.Request.ContentType = "multipart/form-data; boundary=test";

        using var stream = new MemoryStream("x"u8.ToArray());
        var file = new FormFile(stream, 0, stream.Length, "file", "sources.txt")
        {
            Headers = new HeaderDictionary(),
            ContentType = "text/plain",
        };
        context.Request.Form = new FormCollection(
            new Dictionary<string, StringValues>(),
            new FormFileCollection { file });

        var controller = new TestController
        {
            ControllerContext = new ControllerContext { HttpContext = context },
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => controller.HandleApiRequest());
    }

    [Theory]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(InvalidDataException))]
    [InlineData(typeof(InvalidOperationException))]
    public async Task Import_FileReadFailure_RemainsInternalServerError(Type exceptionType)
    {
        var sink = factory.Services.GetRequiredService<LogBufferSink>();
        var exception = (Exception)Activator.CreateInstance(exceptionType, "synthetic test failure")!;
        using var stream = new ThrowingReadStream(exception);
        var file = new FormFile(stream, 0, 1, "file", "sources.txt")
        {
            Headers = new HeaderDictionary(),
            ContentType = "text/plain",
        };

        var context = new DefaultHttpContext();
        context.Request.ContentType = "multipart/form-data; boundary=test";
        context.Request.Form = new FormCollection(
            new Dictionary<string, StringValues>(),
            new FormFileCollection { file });

        var controller = new TestController
        {
            ControllerContext = new ControllerContext { HttpContext = context },
        };

        var result = await controller.HandleApiRequest();

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status500InternalServerError, objectResult.StatusCode);
        var body = Assert.IsType<BaseApiResponse>(objectResult.Value);
        Assert.False(body.Status);
        Assert.Equal("An internal server error occurred.", body.Error);
        Assert.DoesNotContain("synthetic test failure", body.Error, StringComparison.Ordinal);

        Assert.Contains(
            sink.Snapshot(50, ["Error"], source: null, search: "Unhandled admin API request failure", beforeSequence: null)
                .Entries,
            entry => entry.Exception is { Length: > 0 } ex
                && ex.Contains(exceptionType.Name, StringComparison.Ordinal)
                && ex.Contains("synthetic test failure", StringComparison.Ordinal));
    }

    private void SeedRemoteSource(string url)
    {
        var warden = factory.Services.GetRequiredService<WardenStore>();
        var id = warden.AddSource(
            "remote",
            "Boundary fixture",
            url,
            WardenStore.TrustCorroborate,
            24);
        warden.UpdateSource(id, enabled: false, trust: null, refreshHours: null, name: null);
    }

    private void AssertSourceUrlNotImported(string url)
    {
        var warden = factory.Services.GetRequiredService<WardenStore>();
        Assert.DoesNotContain(
            warden.GetSources(),
            source => string.Equals(source.Url, url, StringComparison.OrdinalIgnoreCase));
    }

    private static HttpContent MalformedMultipartWithoutBoundary()
    {
        var content = new ByteArrayContent("malformed"u8.ToArray());
        content.Headers.ContentType = new MediaTypeHeaderValue("multipart/form-data");
        return content;
    }

    private static string BuildAsciiContent(string url, int utf8Bytes)
    {
        var prefix = url + "\n#";
        var prefixBytes = Encoding.UTF8.GetByteCount(prefix);
        Assert.True(prefixBytes <= utf8Bytes);

        var content = prefix + new string('a', utf8Bytes - prefixBytes);
        Assert.Equal(utf8Bytes, content.Length);
        Assert.Equal(utf8Bytes, Encoding.UTF8.GetByteCount(content));
        return content;
    }

    private static string BuildMultibyteContent(string url, int utf8Bytes)
    {
        var prefix = url + "\n#";
        var remainingBytes = utf8Bytes - Encoding.UTF8.GetByteCount(prefix);
        Assert.True(remainingBytes > 0);

        var threeByteCharacters = remainingBytes / 3;
        var asciiTailBytes = remainingBytes % 3;
        var content = prefix
            + new string('界', threeByteCharacters)
            + new string('a', asciiTailBytes);

        Assert.Equal(utf8Bytes, Encoding.UTF8.GetByteCount(content));
        return content;
    }

    private static MultipartFormDataContent BuildTextForm(string content)
    {
        var form = new MultipartFormDataContent();
        form.Add(new StringContent(content, Encoding.UTF8), "text");
        form.Add(new StringContent(WardenStore.TrustCorroborate), "trust");
        form.Add(new StringContent("24"), "refreshHours");
        return form;
    }

    private static MultipartFormDataContent BuildFileForm(byte[] fileBytes)
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(fileBytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        form.Add(file, "file", "sources.txt");
        form.Add(new StringContent(WardenStore.TrustCorroborate), "trust");
        form.Add(new StringContent("24"), "refreshHours");
        return form;
    }

    private sealed class TestController()
        : WardenSourcesImportController(null!, null!)
    {
        protected override bool RequiresAuthentication => false;
    }

    private sealed class FaultingFormFeature(Func<CancellationToken, Task<IFormCollection>> readAsync) : IFormFeature
    {
        public bool HasFormContentType => true;

        public IFormCollection? Form { get; set; }

        public IFormCollection ReadForm() => throw new NotSupportedException();

        public Task<IFormCollection> ReadFormAsync(CancellationToken cancellationToken)
            => readAsync(cancellationToken);
    }

    private sealed class ThrowingReadStream(Exception exception) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => 1;
        public override long Position { get; set; }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw exception;

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => throw exception;

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => throw exception;

        public override long Seek(long offset, SeekOrigin origin)
        {
            var next = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => Position + offset,
                SeekOrigin.End => Length + offset,
                _ => throw new ArgumentOutOfRangeException(nameof(origin)),
            };
            ArgumentOutOfRangeException.ThrowIfNegative(next);
            Position = next;
            return Position;
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
