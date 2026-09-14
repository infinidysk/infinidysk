using System.Text;
using System.Text.Json;
using NzbWebDAV.Api.Errors;

namespace NzbWebDAV.Tests.Api;

public class ValidationErrorsTests
{
    [Fact]
    public void ValidationErrors_DeduplicatesExactPairs()
    {
        var errors = new ValidationErrors();
        for (var i = 0; i < 100_000; i++)
        {
            errors.Add("nzb", "Duplicate error message");
        }

        var dict = errors.ToDictionary();
        Assert.Single(dict);
        Assert.Single(dict["nzb"]);
        Assert.Equal("Duplicate error message", dict["nzb"][0]);
    }

    [Fact]
    public void ValidationErrors_MarksOmittedWhenTruncatedInputCollapsesIntoAnExistingPair()
    {
        var errors = new ValidationErrors();

        // Retained in full: exactly at the bound, so this first Add does not truncate
        // and does not itself set the omission flag.
        var exactLengthMessage = new string('a', ValidationErrors.MaxMessageLength);
        errors.Add("nzb", exactLengthMessage);
        Assert.True(errors.HasErrors);

        // A second, textually distinct (longer) input truncates down to the exact same
        // bounded pair already retained above. Even though the (field, message) pair
        // "matches" after normalization, real information (the extra suffix) was lost,
        // so this must still mark omission instead of being treated as a clean duplicate.
        var truncatesToSamePair = exactLengthMessage + "-distinct-tail-that-gets-cut";
        errors.Add("nzb", truncatesToSamePair);

        Assert.True(errors.HasErrors);
        var dict = errors.ToDictionary();
        Assert.Single(dict["nzb"], m => m == exactLengthMessage);
        Assert.Contains(ValidationErrors.OmissionMarker, dict["nzb"]);
    }

    [Fact]
    public void ValidationErrors_BoundsTotalMessagesAndAppendsOmissionMarker()
    {
        var errors = new ValidationErrors();
        for (var i = 0; i < 50; i++)
        {
            errors.Add($"field_{i}", $"Error message {i}");
        }

        var dict = errors.ToDictionary();
        var totalMessages = dict.Sum(kv => kv.Value.Length);
        Assert.True(totalMessages <= 32); // 31 ordinary + 1 omission marker
        var firstFieldKey = dict.Keys.First();
        Assert.Contains(ValidationErrors.OmissionMarker, dict[firstFieldKey]);
    }

    [Fact]
    public void ValidationErrors_BoundsMessagesPerFieldTo7PlusMarker()
    {
        var errors = new ValidationErrors();
        for (var i = 0; i < 20; i++)
        {
            errors.Add("nzb", $"Distinct error message {i}");
        }

        var dict = errors.ToDictionary();
        Assert.Single(dict);
        Assert.Equal(8, dict["nzb"].Length); // 7 ordinary + 1 omission marker
        Assert.Equal(ValidationErrors.OmissionMarker, dict["nzb"][^1]);
    }

    [Fact]
    public void ValidationErrors_TruncatesLongFieldsAndMessagesSafely()
    {
        var errors = new ValidationErrors();
        var longField = new string('f', 300);
        var longMessage = new string('m', 1000);

        errors.Add(longField, longMessage);

        var dict = errors.ToDictionary();
        Assert.Single(dict);
        var key = dict.Keys.First();
        Assert.Equal(128, key.Length);
        Assert.Equal(512, dict[key][0].Length);
    }

    [Fact]
    public void ValidationErrors_DoesNotTrackDiscardedPairs()
    {
        var errors = new ValidationErrors();
        for (var i = 0; i < 100_000; i++)
        {
            errors.Add($"field_{i}", $"message_{i}");
        }

        errors.Add("nzb", "retained after saturation");

        var dict = errors.ToDictionary();
        Assert.DoesNotContain("retained after saturation", dict.Values.SelectMany(messages => messages));
        Assert.True(dict.Sum(pair => pair.Value.Length) <= 32);
    }

    [Fact]
    public void ApiValidationException_BoundsCustomSummaryAndCopiesInput()
    {
        var input = new Dictionary<string, string[]>
        {
            ["field"] = ["message"],
        };
        var exception = new ApiValidationException(input, new string('x', 10_000));
        input["field"][0] = "mutated";

        Assert.Equal(ValidationErrors.MaxMessageLength, exception.Message.Length);
        Assert.Equal("message", exception.Errors["field"][0]);
    }

    [Fact]
    public void ValidationErrors_IdempotentNormalization()
    {
        var initial = new Dictionary<string, string[]>
        {
            ["nzb"] = ["Error 1", "Error 2", ValidationErrors.OmissionMarker],
        };

        var exception1 = new ApiValidationException(initial, "Custom summary");
        var exception2 = new ApiValidationException(exception1.Errors, exception1.Message);

        Assert.Equal(exception1.Message, exception2.Message);
        Assert.Equal(exception1.Errors.Count, exception2.Errors.Count);
        Assert.Equal(exception1.Errors["nzb"], exception2.Errors["nzb"]);
    }

    [Fact]
    public void ValidationErrors_SerializedSizeIsBoundedUnder64KiB()
    {
        var errors = new ValidationErrors();
        for (var i = 0; i < 1000; i++)
        {
            errors.Add($"field_{i}", $"Detailed validation error message {i} with special chars <>&\"'");
        }

        var dict = errors.ToDictionary();
        var json = JsonSerializer.Serialize(dict);
        Assert.True(Encoding.UTF8.GetByteCount(json) < 65_536);
    }
}
