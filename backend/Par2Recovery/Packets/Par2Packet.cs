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
            var bodyLength = Header.PacketLength - (ulong)Marshal.SizeOf<Par2PacketHeader>();

            // Skip uninteresting bodies (RecvSlic recovery data can be GBs) via
            // seek when possible — NzbFileStream turns forward seeks > 1 MiB
            // into a cheap reopen instead of downloading the skipped bytes.
            if (SkipBody && stream.CanSeek)
            {
                var remaining = stream.Length - stream.Position;
                if (bodyLength > (ulong)remaining)
                    throw new EndOfStreamException("Truncated PAR2 packet body.");
                stream.Seek((long)bodyLength, SeekOrigin.Current);
                return;
            }

            // Read the calculated number of bytes from the stream.
            var body = new byte[bodyLength];
            await stream.ReadExactlyAsync(body.AsMemory(0, (int)bodyLength)).ConfigureAwait(false);

            // Pass the body to the further implementation for parsing.
            ParseBody(body);
        }

        protected virtual bool SkipBody => false;

        protected virtual void ParseBody(byte[] body)
        {
            // intentionally left blank
        }
    }
}
