using NzbWebDAV.Api.Controllers.GetOverviewStats;
using NzbWebDAV.Services.Metrics;

namespace NzbWebDAV.Tests.Api;

public class ProviderOverviewRowMapperTests
{
    [Fact]
    public void ToApi_CopiesProviderType()
    {
        var row = ProviderOverviewRowMapper.ToApi(new ProviderOverviewRow
        {
            Provider = "k",
            ProviderType = "BackupOnly",
        });

        Assert.Equal("BackupOnly", row.ProviderType);
    }
}