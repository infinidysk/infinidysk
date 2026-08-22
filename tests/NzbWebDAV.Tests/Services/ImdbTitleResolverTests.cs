using System.Text.Json;
using NzbWebDAV.Services;

namespace NzbWebDAV.Tests.Services;

public class ImdbTitleResolverTests
{
    [Fact]
    public void ParseTvmazeTitle_ReadsNameAndLeavesYearUnknown()
    {
        using var doc = JsonDocument.Parse("""{"name":"The Bear","premiered":"2022-06-23"}""");

        var title = ImdbTitleResolver.ParseTvmazeTitle(doc.RootElement);

        Assert.Equal("The Bear", title);
    }

    [Fact]
    public void ParseWikidataMetadata_ReadsTitleAndSingleDate()
    {
        using var doc = JsonDocument.Parse(
            """
            {
              "results": {
                "bindings": [
                  {
                    "label": { "value": "Dune" },
                    "date": { "value": "2021-10-22T00:00:00Z" }
                  }
                ]
              }
            }
            """);

        var metadata = ImdbTitleResolver.ParseWikidataMetadata(doc.RootElement);

        Assert.NotNull(metadata);
        Assert.Equal("Dune", metadata.Title);
        Assert.Equal(2021, metadata.Year);
    }

    [Fact]
    public void ParseWikidataMetadata_MultipleDates_ChoosesEarliestValidYear()
    {
        using var doc = JsonDocument.Parse(
            """
            {
              "results": {
                "bindings": [
                  {
                    "label": { "value": "Blade Runner 2049" },
                    "date": { "value": "2017-10-06T00:00:00Z" }
                  },
                  {
                    "label": { "value": "Blade Runner 2049" },
                    "date": { "value": "2018-03-01T00:00:00Z" }
                  },
                  {
                    "label": { "value": "Blade Runner 2049" },
                    "year": { "value": "2016" }
                  }
                ]
              }
            }
            """);

        var metadata = ImdbTitleResolver.ParseWikidataMetadata(doc.RootElement);

        Assert.NotNull(metadata);
        Assert.Equal("Blade Runner 2049", metadata.Title);
        Assert.Equal(2016, metadata.Year);
    }

    [Fact]
    public void ParseWikidataMetadata_TitleWithoutDate_ReturnsNullYear()
    {
        using var doc = JsonDocument.Parse(
            """
            {
              "results": {
                "bindings": [
                  { "label": { "value": "Mystery Film" } }
                ]
              }
            }
            """);

        var metadata = ImdbTitleResolver.ParseWikidataMetadata(doc.RootElement);

        Assert.NotNull(metadata);
        Assert.Equal("Mystery Film", metadata.Title);
        Assert.Null(metadata.Year);
    }
}
