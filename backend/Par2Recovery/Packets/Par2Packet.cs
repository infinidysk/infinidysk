using System.Runtime.InteropServices;

namespace NzbWebDAV.Par2Recovery.Packets
{
    /// <summary>
    /// Implements the basic Read mechanism, passing the body bytes to any child class.
    /// </summary>
    public class Par2Packet
    {
        public Par2PacketHeader Header { get; protected set; }

        public Par2Packet(Par2PacketHeader header)
        {
            Header = header;
        }

        public async Task ReadAsync(Stream stream)
        {
            // Determine the length of the body as the given packet length, minus the length of the header.
            var headerSize = (ulong)Marshal.SizeOf<Par2PacketHeader>();
            var packetLength = Header.PacketLength;
            // Guard malformed/truncated lengths before they can underflow the
            // subtraction or drive a huge allocation / impossible seek.
            if (packetLength < headerSize || packetLength > (ulong)int.MaxValue)
                throw new InvalidDataException($"Invalid PAR2 packet length {packetLength}.");
            var bodyLength = (int)(packetLength - headerSize);

            if (stream.CanSeek)
            {
                var remaining = stream.Length - stream.Position;
                if (bodyLength > remaining)
                    throw new EndOfStreamException("Truncated PAR2 packet body.");
            }

            // Skip uninteresting bodies (RecvSlic recovery data can be GBs) via
            // seek when possible — NzbFileStream turns forward seeks > 1 MiB
            // into a cheap reopen instead of downloading the skipped bytes.
            // Non-seekable streams fall back to draining in bounded chunks so a
            // large body is never buffered whole.
            if (SkipBody)
            {
                if (stream.CanSeek)
                {
                    stream.Seek(bodyLength, SeekOrigin.Current);
                }
                else
                {
                    await DrainAsync(stream, bodyLength).ConfigureAwait(false);
                }
                return;
            }

            // Read the calculated number of bytes from the stream.
            var body = new byte[bodyLength];
            await stream.ReadExactlyAsync(body.AsMemory(0, bodyLength)).ConfigureAwait(false);

            // Pass the body to the further implementation for parsing.
            ParseBody(body);
        }

        private static async Task DrainAsync(Stream stream, int bytes)
        {
            var scratch = new byte[Math.Min(bytes, 64 * 1024)];
            var remaining = bytes;
            while (remaining > 0)
            {
                var read = await stream.ReadAsync(
                        scratch.AsMemory(0, Math.Min(scratch.Length, remaining)))
                    .ConfigureAwait(false);
                if (read == 0)
                    throw new EndOfStreamException("Truncated PAR2 packet body.");
                remaining -= read;
            }
        }

        protected virtual bool SkipBody => false;

        protected virtual void ParseBody(byte[] body)
        {
            // intentionally left blank
        }
    }
}
