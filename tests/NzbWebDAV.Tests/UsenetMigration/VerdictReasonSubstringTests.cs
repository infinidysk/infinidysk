using System.Reflection;
using NzbWebDAV.UsenetMigration.Triage;

namespace NzbWebDAV.Tests.UsenetMigration;

public class VerdictReasonSubstringTests
{
    [Fact]
    public void NoVerdictReasonIsSubstringOfAnother()
    {
        var codes = typeof(VerdictReason)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && !f.IsInitOnly && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .OrderBy(c => c.Length)
            .ToList();

        Assert.NotEmpty(codes);
        for (var i = 0; i < codes.Count; i++)
        {
            for (var j = i + 1; j < codes.Count; j++)
            {
                Assert.False(
                    codes[j].Contains(codes[i], StringComparison.Ordinal),
                    $"VerdictReason '{codes[i]}' is a substring of '{codes[j]}' — " +
                    "SQL substring filters on VerdictReasons JSON would collide.");
            }
        }
    }
}
