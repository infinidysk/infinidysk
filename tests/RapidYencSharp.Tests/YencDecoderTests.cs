namespace RapidYencSharp.Tests;

public sealed class YencDecoderTests
{
    [Test]
    public void RawDecodingRemovesNntpDotStuffing()
    {
        byte[] encoded = "..+"u8.ToArray();
        byte[] rawOutput = new byte[encoded.Length];
        byte[] plainOutput = new byte[encoded.Length];
        RapidYencDecoderState? rawState = RapidYencDecoderState.RYDEC_STATE_CRLF;
        RapidYencDecoderState? plainState = RapidYencDecoderState.RYDEC_STATE_CRLF;

        int rawLength = YencDecoder.DecodeEx(encoded, rawOutput, ref rawState, isRaw: true);
        int plainLength = YencDecoder.DecodeEx(encoded, plainOutput, ref plainState, isRaw: false);

        Assert.Multiple(() =>
        {
            Assert.That(rawOutput.AsSpan(0, rawLength).ToArray(), Is.EqualTo(new byte[] { 4, 1 }));
            Assert.That(plainOutput.AsSpan(0, plainLength).ToArray(), Is.EqualTo(new byte[] { 4, 4, 1 }));
        });
    }

    [TestCase("\r\n=y", RapidYencDecoderEnd.RYDEC_END_CONTROL)]
    [TestCase("\r\n.\r\n", RapidYencDecoderEnd.RYDEC_END_ARTICLE)]
    public void IncrementalDecoderDetectsEndSequences(string terminator, RapidYencDecoderEnd expected)
    {
        byte[] input = System.Text.Encoding.ASCII.GetBytes($"klm{terminator}");
        byte[] output = new byte[input.Length];
        RapidYencDecoderState state = RapidYencDecoderState.RYDEC_STATE_CRLF;

        int written = YencDecoder.DecodeIncremental(input, output, ref state, out RapidYencDecoderEnd actual);

        Assert.Multiple(() =>
        {
            Assert.That(actual, Is.EqualTo(expected));
            Assert.That(written, Is.InRange(0, input.Length));
        });
    }

    [Test]
    public void EmptyInputsPreserveDocumentedState()
    {
        int? column = 37;
        RapidYencDecoderState? nullableState = RapidYencDecoderState.RYDEC_STATE_EQ;
        RapidYencDecoderState state = RapidYencDecoderState.RYDEC_STATE_CR;

        Assert.Multiple(() =>
        {
            Assert.That(YencEncoder.EncodeEx([], ref column), Is.Empty);
            Assert.That(column, Is.EqualTo(37));
            Assert.That(YencDecoder.DecodeEx([], ref nullableState), Is.Empty);
            Assert.That(nullableState, Is.EqualTo(RapidYencDecoderState.RYDEC_STATE_EQ));
            Assert.That(
                YencDecoder.DecodeIncremental([], ref state),
                Is.EqualTo((Array.Empty<byte>(), RapidYencDecoderEnd.RYDEC_END_NONE)));
            Assert.That(state, Is.EqualTo(RapidYencDecoderState.RYDEC_STATE_CR));
        });
    }

    [Test]
    public void ManagedEnumsMatchNativeAbiOrdinals()
    {
        Assert.Multiple(() =>
        {
            Assert.That((int)RapidYencDecoderState.RYDEC_STATE_CRLF, Is.Zero);
            Assert.That((int)RapidYencDecoderState.RYDEC_STATE_EQ, Is.EqualTo(1));
            Assert.That((int)RapidYencDecoderState.RYDEC_STATE_CR, Is.EqualTo(2));
            Assert.That((int)RapidYencDecoderState.RYDEC_STATE_NONE, Is.EqualTo(3));
            Assert.That((int)RapidYencDecoderState.RYDEC_STATE_CRLFDT, Is.EqualTo(4));
            Assert.That((int)RapidYencDecoderState.RYDEC_STATE_CRLFDTCR, Is.EqualTo(5));
            Assert.That((int)RapidYencDecoderState.RYDEC_STATE_CRLFEQ, Is.EqualTo(6));
            Assert.That((int)RapidYencDecoderEnd.RYDEC_END_NONE, Is.Zero);
            Assert.That((int)RapidYencDecoderEnd.RYDEC_END_CONTROL, Is.EqualTo(1));
            Assert.That((int)RapidYencDecoderEnd.RYDEC_END_ARTICLE, Is.EqualTo(2));
        });
    }

    [Test]
    public void BundledNativeVersionMatchesVendoredHeader()
    {
        Assert.Multiple(() =>
        {
            Assert.That(RapidYencSharp.Version.GetVersion(), Is.EqualTo(0x010101));
            Assert.That(RapidYencSharp.Version.Major, Is.EqualTo(1));
            Assert.That(RapidYencSharp.Version.Minor, Is.EqualTo(1));
            Assert.That(RapidYencSharp.Version.Patch, Is.EqualTo(1));
        });
    }
}
