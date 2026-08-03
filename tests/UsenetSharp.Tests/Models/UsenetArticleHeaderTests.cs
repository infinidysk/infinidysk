using UsenetSharp.Models;

namespace UsenetSharpTest.Models;

[TestFixture]
public class UsenetArticleHeaderTests
{
    #region Basic Record Functionality

    [Test]
    public void UsenetArticleHeader_CanBeInitialized_WithHeaders()
    {
        // Arrange & Act
        var headers = new Dictionary<string, string>
        {
            ["Subject"] = "Test Subject",
            ["From"] = "test@example.com"
        };
        var articleHeader = new UsenetArticleHeader { Headers = headers };

        // Assert
        Assert.That(articleHeader.Headers, Is.Not.Null);
        Assert.That(articleHeader.Headers.Count, Is.EqualTo(2));
    }

    [Test]
    public void UsenetArticleHeader_WithEmptyDictionary_IsValid()
    {
        // Arrange & Act
        var articleHeader = new UsenetArticleHeader { Headers = new Dictionary<string, string>() };

        // Assert
        Assert.That(articleHeader.Headers, Is.Not.Null);
        Assert.That(articleHeader.Headers.Count, Is.EqualTo(0));
    }

    [Test]
    public void UsenetArticleHeader_RecordEquality_WorksCorrectly()
    {
        // Arrange
        var headers1 = new Dictionary<string, string> { ["Subject"] = "Test" };
        var headers2 = new Dictionary<string, string> { ["Subject"] = "Test" };

        var article1 = new UsenetArticleHeader { Headers = headers1 };
        var article2 = new UsenetArticleHeader { Headers = headers1 }; // Same instance
        var article3 = new UsenetArticleHeader { Headers = headers2 }; // Different instance, same content

        // Assert
        Assert.That(article1, Is.EqualTo(article2)); // Same dictionary instance
        Assert.That(article1, Is.Not.EqualTo(article3)); // Different dictionary instances
    }

    #endregion

    #region Header Accessors

    [Test]
    public void Subject_WhenHeaderExists_ReturnsValue()
    {
        // Arrange
        var articleHeader = new UsenetArticleHeader
        {
            Headers = new Dictionary<string, string> { ["Subject"] = "Test Subject" }
        };

        // Act & Assert
        Assert.That(articleHeader.Subject, Is.EqualTo("Test Subject"));
    }

    [Test]
    public void Subject_WhenHeaderDoesNotExist_ReturnsNull()
    {
        // Arrange
        var articleHeader = new UsenetArticleHeader
        {
            Headers = new Dictionary<string, string>()
        };

        // Act & Assert
        Assert.That(articleHeader.Subject, Is.Null);
    }

    [Test]
    public void From_WhenHeaderExists_ReturnsValue()
    {
        // Arrange
        var articleHeader = new UsenetArticleHeader
        {
            Headers = new Dictionary<string, string> { ["From"] = "user@example.com" }
        };

        // Act & Assert
        Assert.That(articleHeader.From, Is.EqualTo("user@example.com"));
    }

    [Test]
    public void MessageId_WhenHeaderExists_ReturnsValue()
    {
        // Arrange
        var articleHeader = new UsenetArticleHeader
        {
            Headers = new Dictionary<string, string> { ["Message-ID"] = "<test123@example.com>" }
        };

        // Act & Assert
        Assert.That(articleHeader.MessageId, Is.EqualTo("<test123@example.com>"));
    }

    [Test]
    public void RawDate_WhenHeaderExists_ReturnsValue()
    {
        // Arrange
        var dateString = "Wed, 15 Nov 2023 10:30:00 +0000";
        var articleHeader = new UsenetArticleHeader
        {
            Headers = new Dictionary<string, string> { ["Date"] = dateString }
        };

        // Act & Assert
        Assert.That(articleHeader.RawDate, Is.EqualTo(dateString));
    }

    [Test]
    public void References_WhenHeaderExists_ReturnsValue()
    {
        // Arrange
        var articleHeader = new UsenetArticleHeader
        {
            Headers = new Dictionary<string, string> { ["References"] = "<ref1@example.com> <ref2@example.com>" }
        };

        // Act & Assert
        Assert.That(articleHeader.References, Is.EqualTo("<ref1@example.com> <ref2@example.com>"));
    }

    [Test]
    public void ContentType_WhenHeaderExists_ReturnsValue()
    {
        // Arrange
        var articleHeader = new UsenetArticleHeader
        {
            Headers = new Dictionary<string, string> { ["Content-Type"] = "text/plain; charset=utf-8" }
        };

        // Act & Assert
        Assert.That(articleHeader.ContentType, Is.EqualTo("text/plain; charset=utf-8"));
    }

    [Test]
    public void ContentTransferEncoding_WhenHeaderExists_ReturnsValue()
    {
        // Arrange
        var articleHeader = new UsenetArticleHeader
        {
            Headers = new Dictionary<string, string> { ["Content-Transfer-Encoding"] = "8bit" }
        };

        // Act & Assert
        Assert.That(articleHeader.ContentTransferEncoding, Is.EqualTo("8bit"));
    }

    [Test]
    public void Newsgroups_WhenHeaderExists_ReturnsValue()
    {
        // Arrange
        var articleHeader = new UsenetArticleHeader
        {
            Headers = new Dictionary<string, string> { ["Newsgroups"] = "alt.binaries.test" }
        };

        // Act & Assert
        Assert.That(articleHeader.Newsgroups, Is.EqualTo("alt.binaries.test"));
    }

    [Test]
    public void XrefFull_WhenHeaderExists_ReturnsValue()
    {
        // Arrange
        var articleHeader = new UsenetArticleHeader
        {
            Headers = new Dictionary<string, string> { ["Xref"] = "news.example.com alt.test:12345" }
        };

        // Act & Assert
        Assert.That(articleHeader.XrefFull, Is.EqualTo("news.example.com alt.test:12345"));
    }

    [Test]
    public void Lines_WhenHeaderExists_ReturnsValue()
    {
        // Arrange
        var articleHeader = new UsenetArticleHeader
        {
            Headers = new Dictionary<string, string> { ["Lines"] = "42" }
        };

        // Act & Assert
        Assert.That(articleHeader.Lines, Is.EqualTo("42"));
    }

    [Test]
    public void Bytes_WhenHeaderExists_ReturnsValue()
    {
        // Arrange
        var articleHeader = new UsenetArticleHeader
        {
            Headers = new Dictionary<string, string> { ["Bytes"] = "1024" }
        };

        // Act & Assert
        Assert.That(articleHeader.Bytes, Is.EqualTo("1024"));
    }

    #endregion

    #region Date Parsing - RFC 5322 Format

    [Test]
    public void Date_WithRFC5322Format_ParsesCorrectly()
    {
        // Arrange
        var articleHeader = new UsenetArticleHeader
        {
            Headers = new Dictionary<string, string> { ["Date"] = "Wed, 15 Nov 2023 10:30:00 +0000" }
        };

        // Act
        var date = articleHeader.Date;

        // Assert
        Assert.That(date.Year, Is.EqualTo(2023));
        Assert.That(date.Month, Is.EqualTo(11));
        Assert.That(date.Day, Is.EqualTo(15));
        Assert.That(date.Hour, Is.EqualTo(10));
        Assert.That(date.Minute, Is.EqualTo(30));
        Assert.That(date.Second, Is.EqualTo(0));
    }

    [Test]
    public void Date_WithRFC5322FormatNoSeconds_ParsesCorrectly()
    {
        // Arrange
        var articleHeader = new UsenetArticleHeader
        {
            Headers = new Dictionary<string, string> { ["Date"] = "Wed, 15 Nov 2023 10:30 +0000" }
        };

        // Act
        var date = articleHeader.Date;

        // Assert
        Assert.That(date.Year, Is.EqualTo(2023));
        Assert.That(date.Month, Is.EqualTo(11));
        Assert.That(date.Day, Is.EqualTo(15));
        Assert.That(date.Hour, Is.EqualTo(10));
        Assert.That(date.Minute, Is.EqualTo(30));
    }

    [Test]
    public void Date_WithTwoDigitYear_ParsesCorrectly()
    {
        // Arrange
        var articleHeader = new UsenetArticleHeader
        {
            Headers = new Dictionary<string, string> { ["Date"] = "Wed, 15 Nov 23 10:30:00 +0000" }
        };

        // Act
        var date = articleHeader.Date;

        // Assert
        Assert.That(date.Year, Is.EqualTo(2023));
        Assert.That(date.Month, Is.EqualTo(11));
        Assert.That(date.Day, Is.EqualTo(15));
    }

    [Test]
    public void Date_WithoutDayOfWeek_ParsesCorrectly()
    {
        // Arrange
        var articleHeader = new UsenetArticleHeader
        {
            Headers = new Dictionary<string, string> { ["Date"] = "15 Nov 2023 10:30:00 +0000" }
        };

        // Act
        var date = articleHeader.Date;

        // Assert
        Assert.That(date.Year, Is.EqualTo(2023));
        Assert.That(date.Month, Is.EqualTo(11));
        Assert.That(date.Day, Is.EqualTo(15));
    }

    #endregion

    #region Date Parsing - Timezone Handling

    [Test]
    public void Date_WithUTCTimezone_ParsesCorrectly()
    {
        // Arrange
        var articleHeader = new UsenetArticleHeader
        {
            Headers = new Dictionary<string, string> { ["Date"] = "Wed, 15 Nov 2023 10:30:00 UTC" }
        };

        // Act
        var date = articleHeader.Date;

        // Assert
        Assert.That(date.Offset, Is.EqualTo(TimeSpan.Zero));
    }

    [Test]
    public void Date_WithGMTTimezone_ParsesCorrectly()
    {
        // Arrange
        var articleHeader = new UsenetArticleHeader
        {
            Headers = new Dictionary<string, string> { ["Date"] = "Wed, 15 Nov 2023 10:30:00 GMT" }
        };

        // Act
        var date = articleHeader.Date;

        // Assert
        Assert.That(date.Offset, Is.EqualTo(TimeSpan.Zero));
    }

    [Test]
    public void Date_WithESTTimezone_ParsesCorrectly()
    {
        // Arrange
        var articleHeader = new UsenetArticleHeader
        {
            Headers = new Dictionary<string, string> { ["Date"] = "Wed, 15 Nov 2023 10:30:00 EST" }
        };

        // Act
        var date = articleHeader.Date;

        // Assert - EST is -0500, converted to UTC the hour should be 15:30
        Assert.That(date.UtcDateTime.Hour, Is.EqualTo(15));
        Assert.That(date.UtcDateTime.Minute, Is.EqualTo(30));
    }

    [Test]
    public void Date_WithPSTTimezone_ParsesCorrectly()
    {
        // Arrange
        var articleHeader = new UsenetArticleHeader
        {
            Headers = new Dictionary<string, string> { ["Date"] = "Wed, 15 Nov 2023 10:30:00 PST" }
        };

        // Act
        var date = articleHeader.Date;

        // Assert - PST is -0800, converted to UTC the hour should be 18:30
        Assert.That(date.UtcDateTime.Hour, Is.EqualTo(18));
        Assert.That(date.UtcDateTime.Minute, Is.EqualTo(30));
    }

    [Test]
    public void Date_WithCETTimezone_ParsesCorrectly()
    {
        // Arrange
        var articleHeader = new UsenetArticleHeader
        {
            Headers = new Dictionary<string, string> { ["Date"] = "Wed, 15 Nov 2023 10:30:00 CET" }
        };

        // Act
        var date = articleHeader.Date;

        // Assert - CET is +0100, converted to UTC the hour should be 09:30
        Assert.That(date.UtcDateTime.Hour, Is.EqualTo(9));
        Assert.That(date.UtcDateTime.Minute, Is.EqualTo(30));
    }

    [Test]
    public void Date_WithJSTTimezone_ParsesCorrectly()
    {
        // Arrange
        var articleHeader = new UsenetArticleHeader
        {
            Headers = new Dictionary<string, string> { ["Date"] = "Wed, 15 Nov 2023 10:30:00 JST" }
        };

        // Act
        var date = articleHeader.Date;

        // Assert - JST is +0900, converted to UTC the hour should be 01:30
        Assert.That(date.UtcDateTime.Hour, Is.EqualTo(1));
        Assert.That(date.UtcDateTime.Minute, Is.EqualTo(30));
    }

    [Test]
    public void Date_WithMilitaryTimezoneZ_ParsesAsUTC()
    {
        // Arrange
        var articleHeader = new UsenetArticleHeader
        {
            Headers = new Dictionary<string, string> { ["Date"] = "Wed, 15 Nov 2023 10:30:00 Z" }
        };

        // Act
        var date = articleHeader.Date;

        // Assert
        Assert.That(date.Offset, Is.EqualTo(TimeSpan.Zero));
    }

    [Test]
    public void Date_WithMilitaryTimezoneA_ParsesCorrectly()
    {
        // Arrange
        var articleHeader = new UsenetArticleHeader
        {
            Headers = new Dictionary<string, string> { ["Date"] = "Wed, 15 Nov 2023 10:30:00 A" }
        };

        // Act
        var date = articleHeader.Date;

        // Assert - A is +0100, converted to UTC the hour should be 09:30
        Assert.That(date.UtcDateTime.Hour, Is.EqualTo(9));
        Assert.That(date.UtcDateTime.Minute, Is.EqualTo(30));
    }

    [Test]
    public void Date_WithNumericTimezonePositive_ParsesCorrectly()
    {
        // Arrange
        var articleHeader = new UsenetArticleHeader
        {
            Headers = new Dictionary<string, string> { ["Date"] = "Wed, 15 Nov 2023 10:30:00 +0530" }
        };

        // Act
        var date = articleHeader.Date;

        // Assert - +0530 (IST), converted to UTC the hour should be 05:00
        Assert.That(date.UtcDateTime.Hour, Is.EqualTo(5));
        Assert.That(date.UtcDateTime.Minute, Is.EqualTo(0));
    }

    [Test]
    public void Date_WithNumericTimezoneNegative_ParsesCorrectly()
    {
        // Arrange
        var articleHeader = new UsenetArticleHeader
        {
            Headers = new Dictionary<string, string> { ["Date"] = "Wed, 15 Nov 2023 10:30:00 -0800" }
        };

        // Act
        var date = articleHeader.Date;

        // Assert - -0800, converted to UTC the hour should be 18:30
        Assert.That(date.UtcDateTime.Hour, Is.EqualTo(18));
        Assert.That(date.UtcDateTime.Minute, Is.EqualTo(30));
    }

    #endregion

    #region Date Parsing - Edge Cases

    [Test]
    public void Date_WithNoDateHeader_ReturnsCurrentTime()
    {
        // Arrange
        var articleHeader = new UsenetArticleHeader
        {
            Headers = new Dictionary<string, string>()
        };
        var beforeTest = DateTimeOffset.UtcNow;

        // Act
        var date = articleHeader.Date;
        var afterTest = DateTimeOffset.UtcNow;

        // Assert - Should return current UTC time
        Assert.That(date, Is.GreaterThanOrEqualTo(beforeTest));
        Assert.That(date, Is.LessThanOrEqualTo(afterTest.AddSeconds(1)));
    }

    [Test]
    public void Date_WithEmptyDateString_ReturnsCurrentTime()
    {
        // Arrange
        var articleHeader = new UsenetArticleHeader
        {
            Headers = new Dictionary<string, string> { ["Date"] = "" }
        };
        var beforeTest = DateTimeOffset.UtcNow;

        // Act
        var date = articleHeader.Date;
        var afterTest = DateTimeOffset.UtcNow;

        // Assert
        Assert.That(date, Is.GreaterThanOrEqualTo(beforeTest));
        Assert.That(date, Is.LessThanOrEqualTo(afterTest.AddSeconds(1)));
    }

    [Test]
    public void Date_WithWhitespaceOnlyDateString_ReturnsCurrentTime()
    {
        // Arrange
        var articleHeader = new UsenetArticleHeader
        {
            Headers = new Dictionary<string, string> { ["Date"] = "   " }
        };
        var beforeTest = DateTimeOffset.UtcNow;

        // Act
        var date = articleHeader.Date;
        var afterTest = DateTimeOffset.UtcNow;

        // Assert
        Assert.That(date, Is.GreaterThanOrEqualTo(beforeTest));
        Assert.That(date, Is.LessThanOrEqualTo(afterTest.AddSeconds(1)));
    }

    [Test]
    public void Date_WithInvalidDateString_ReturnsCurrentTime()
    {
        // Arrange
        var articleHeader = new UsenetArticleHeader
        {
            Headers = new Dictionary<string, string> { ["Date"] = "not a valid date" }
        };
        var beforeTest = DateTimeOffset.UtcNow;

        // Act
        var date = articleHeader.Date;
        var afterTest = DateTimeOffset.UtcNow;

        // Assert
        Assert.That(date, Is.GreaterThanOrEqualTo(beforeTest));
        Assert.That(date, Is.LessThanOrEqualTo(afterTest.AddSeconds(1)));
    }

    [Test]
    public void Date_WithCommentInDateString_ParsesCorrectly()
    {
        // Arrange
        var articleHeader = new UsenetArticleHeader
        {
            Headers = new Dictionary<string, string> { ["Date"] = "Wed, 15 Nov 2023 10:30:00 +0000 (UTC)" }
        };

        // Act
        var date = articleHeader.Date;

        // Assert
        Assert.That(date.Year, Is.EqualTo(2023));
        Assert.That(date.Month, Is.EqualTo(11));
        Assert.That(date.Day, Is.EqualTo(15));
        Assert.That(date.Hour, Is.EqualTo(10));
        Assert.That(date.Minute, Is.EqualTo(30));
    }

    [Test]
    public void Date_WithMultipleSpaces_ParsesCorrectly()
    {
        // Arrange
        var articleHeader = new UsenetArticleHeader
        {
            Headers = new Dictionary<string, string> { ["Date"] = "Wed,  15   Nov   2023  10:30:00  +0000" }
        };

        // Act
        var date = articleHeader.Date;

        // Assert
        Assert.That(date.Year, Is.EqualTo(2023));
        Assert.That(date.Month, Is.EqualTo(11));
        Assert.That(date.Day, Is.EqualTo(15));
    }

    [Test]
    public void Date_AlwaysReturnsUTC()
    {
        // Arrange
        var articleHeader = new UsenetArticleHeader
        {
            Headers = new Dictionary<string, string> { ["Date"] = "Wed, 15 Nov 2023 10:30:00 -0500" }
        };

        // Act
        var date = articleHeader.Date;

        // Assert - Even though input was -0500, output should be in UTC
        Assert.That(date.Offset, Is.EqualTo(TimeSpan.Zero));
        Assert.That(date.UtcDateTime.Hour, Is.EqualTo(15)); // 10:30 -0500 = 15:30 UTC
    }

    #endregion

    #region Date Parsing - Various Formats

    [Test]
    public void Date_WithDifferentMonths_ParsesCorrectly()
    {
        // Test various month abbreviations
        var testCases = new[]
        {
            ("15 Jan 2023 10:30:00 +0000", 1),
            ("15 Feb 2023 10:30:00 +0000", 2),
            ("15 Mar 2023 10:30:00 +0000", 3),
            ("15 Apr 2023 10:30:00 +0000", 4),
            ("15 May 2023 10:30:00 +0000", 5),
            ("15 Jun 2023 10:30:00 +0000", 6),
            ("15 Jul 2023 10:30:00 +0000", 7),
            ("15 Aug 2023 10:30:00 +0000", 8),
            ("15 Sep 2023 10:30:00 +0000", 9),
            ("15 Oct 2023 10:30:00 +0000", 10),
            ("15 Nov 2023 10:30:00 +0000", 11),
            ("15 Dec 2023 10:30:00 +0000", 12)
        };

        foreach (var (dateString, expectedMonth) in testCases)
        {
            // Arrange
            var articleHeader = new UsenetArticleHeader
            {
                Headers = new Dictionary<string, string> { ["Date"] = dateString }
            };

            // Act
            var date = articleHeader.Date;

            // Assert
            Assert.That(date.Month, Is.EqualTo(expectedMonth), $"Failed to parse month from: {dateString}");
        }
    }

    [Test]
    public void Date_WithVariousDaysOfWeek_ParsesCorrectly()
    {
        // Using dates that actually match their day of week
        var testCases = new[]
        {
            ("Mon, 13 Nov 2023 10:30:00 +0000", 13),
            ("Tue, 14 Nov 2023 10:30:00 +0000", 14),
            ("Wed, 15 Nov 2023 10:30:00 +0000", 15),
            ("Thu, 16 Nov 2023 10:30:00 +0000", 16),
            ("Fri, 17 Nov 2023 10:30:00 +0000", 17),
            ("Sat, 18 Nov 2023 10:30:00 +0000", 18),
            ("Sun, 19 Nov 2023 10:30:00 +0000", 19)
        };

        foreach (var (dateString, expectedDay) in testCases)
        {
            // Arrange
            var articleHeader = new UsenetArticleHeader
            {
                Headers = new Dictionary<string, string> { ["Date"] = dateString }
            };

            // Act
            var date = articleHeader.Date;

            // Assert
            Assert.That(date.Year, Is.EqualTo(2023), $"Failed to parse: {dateString}");
            Assert.That(date.Month, Is.EqualTo(11), $"Failed to parse: {dateString}");
            Assert.That(date.Day, Is.EqualTo(expectedDay), $"Failed to parse: {dateString}");
        }
    }

    #endregion

    #region Case Sensitivity

    [Test]
    public void Headers_AreCaseInsensitive()
    {
        // Arrange
        var articleHeader = new UsenetArticleHeader
        {
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["subject"] = "Test Subject"
            }
        };

        // Act & Assert
        Assert.That(articleHeader.Headers["Subject"], Is.EqualTo("Test Subject"));
        Assert.That(articleHeader.Headers["SUBJECT"], Is.EqualTo("Test Subject"));
        Assert.That(articleHeader.Headers["subject"], Is.EqualTo("Test Subject"));
    }

    #endregion

    #region Lazy Evaluation

    [Test]
    public void Date_IsLazyEvaluated_ReturnsSameValue()
    {
        // Arrange
        var articleHeader = new UsenetArticleHeader
        {
            Headers = new Dictionary<string, string> { ["Date"] = "Wed, 15 Nov 2023 10:30:00 +0000" }
        };

        // Act - Access Date multiple times
        var date1 = articleHeader.Date;
        var date2 = articleHeader.Date;
        var date3 = articleHeader.Date;

        // Assert - All should return the same value
        Assert.That(date1, Is.EqualTo(date2));
        Assert.That(date2, Is.EqualTo(date3));
        Assert.That(date1.Year, Is.EqualTo(2023));
        Assert.That(date1.Month, Is.EqualTo(11));
        Assert.That(date1.Day, Is.EqualTo(15));
    }

    #endregion

    #region Multiple Header Access

    [Test]
    public void AllCommonHeaders_CanBeAccessedSimultaneously()
    {
        // Arrange
        var articleHeader = new UsenetArticleHeader
        {
            Headers = new Dictionary<string, string>
            {
                ["Subject"] = "Test Subject",
                ["From"] = "user@example.com",
                ["Date"] = "Wed, 15 Nov 2023 10:30:00 +0000",
                ["Message-ID"] = "<test123@example.com>",
                ["References"] = "<ref@example.com>",
                ["Content-Type"] = "text/plain",
                ["Content-Transfer-Encoding"] = "8bit",
                ["Newsgroups"] = "alt.test",
                ["Xref"] = "server alt.test:123",
                ["Lines"] = "10",
                ["Bytes"] = "500"
            }
        };

        // Act & Assert
        Assert.That(articleHeader.Subject, Is.EqualTo("Test Subject"));
        Assert.That(articleHeader.From, Is.EqualTo("user@example.com"));
        Assert.That(articleHeader.RawDate, Is.EqualTo("Wed, 15 Nov 2023 10:30:00 +0000"));
        Assert.That(articleHeader.MessageId, Is.EqualTo("<test123@example.com>"));
        Assert.That(articleHeader.References, Is.EqualTo("<ref@example.com>"));
        Assert.That(articleHeader.ContentType, Is.EqualTo("text/plain"));
        Assert.That(articleHeader.ContentTransferEncoding, Is.EqualTo("8bit"));
        Assert.That(articleHeader.Newsgroups, Is.EqualTo("alt.test"));
        Assert.That(articleHeader.XrefFull, Is.EqualTo("server alt.test:123"));
        Assert.That(articleHeader.Lines, Is.EqualTo("10"));
        Assert.That(articleHeader.Bytes, Is.EqualTo("500"));
        Assert.That(articleHeader.Date.Year, Is.EqualTo(2023));
    }

    #endregion
}
