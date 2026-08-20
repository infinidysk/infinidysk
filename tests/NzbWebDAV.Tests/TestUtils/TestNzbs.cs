using System.Text;

namespace NzbWebDAV.Tests.TestUtils;

internal static class TestNzbs
{
    public const string SingleFileSegmentId = "contract-seg1@example.test";

    public static readonly byte[] SingleFile = Encoding.UTF8.GetBytes(
        """
        <?xml version="1.0" encoding="utf-8"?>
        <nzb xmlns="http://www.newzbin.com/DTD/2003/nzb">
          <file poster="contract@example.test" date="1" subject="sample.mkv">
            <groups><group>alt.binaries.test</group></groups>
            <segments>
              <segment bytes="128" number="1">contract-seg1@example.test</segment>
            </segments>
          </file>
        </nzb>
        """);
}
