using NzbWebDAV.Api.Controllers.GetWebdavItem;
using NzbWebDAV.Auth;

namespace NzbWebDAV.Tests.Auth;

public class DownloadKeyTests
{
    [Fact]
    public void Generate_MatchesLegacyApiHelper()
    {
        const string apiKey = "test-api-key";
        const string path = ".ids/aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
        Assert.Equal(
            GetWebdavItemRequest.GenerateDownloadKey(apiKey, path),
            DownloadKey.Generate(apiKey, path));
    }
}
