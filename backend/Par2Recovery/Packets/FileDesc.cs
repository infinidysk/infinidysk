using System.Buffers.Binary;
using System.Text;

namespace NzbWebDAV.Par2Recovery.Packets
{
    public class FileDesc : Par2Packet
    {
        public const string PacketType = "PAR 2.0\0FileDesc";
        public const int MaxFileNameBytes = 100_000;

        private static readonly Encoding StrictUtf8 = new UTF8Encoding(
            encoderShouldEmitUTF8Identifier: false,
            throwOnInvalidBytes: true);

        public byte[] FileID { get; protected set; } = null!;
        public byte[] FileHash { get; protected set; } = null!;
        public byte[] File16kHash { get; protected set; } = null!;
        public ulong FileLength { get; protected set; }
        public string FileName { get; internal set; } = null!;

        public FileDesc(Par2PacketHeader header) : base(header)
        {
        }

        protected override void ParseBody(byte[] body)
        {
            if (body.Length < 56 || body.Length - 56 > MaxFileNameBytes)
                throw new InvalidDataException("FileDesc packet has an invalid filename length.");

            // 16	MD5 Hash	The File ID.
            FileID = new byte[16];
            Buffer.BlockCopy(body, 0, FileID, 0, 16);

            // 16	MD5 Hash	The MD5 hash of the entire file.
            FileHash = new byte[16];
            Buffer.BlockCopy(body, 16, FileHash, 0, 16);

            // 16	MD5 Hash	The MD5-16k. That is, the MD5 hash of the first 16kB of the file.
            File16kHash = new byte[16];
            Buffer.BlockCopy(body, 32, File16kHash, 0, 16);

            // 8	8-byte uint	Length of the file.
            FileLength = BinaryPrimitives.ReadUInt64LittleEndian(body.AsSpan(48));

            // ?*4	ASCII/UTF-8 char array	Name of the file. Not guaranteed null-terminated.
            var nameBuffer = new byte[body.Length - 56];
            Buffer.BlockCopy(body, 56, nameBuffer, 0, nameBuffer.Length);

            // Strip UTF-8 BOM if present.
            var offset = 0;
            if (nameBuffer.Length >= 3
                && nameBuffer[0] == 0xEF
                && nameBuffer[1] == 0xBB
                && nameBuffer[2] == 0xBF)
            {
                offset = 3;
            }

            string decoded;
            try
            {
                decoded = StrictUtf8.GetString(nameBuffer, offset, nameBuffer.Length - offset);
            }
            catch (DecoderFallbackException)
            {
                decoded = Encoding.GetEncoding(1252).GetString(nameBuffer, offset, nameBuffer.Length - offset);
            }

            FileName = decoded.Normalize().TrimEnd('\0');
        }

        public override string ToString()
        {
            return FileName ?? "FileDesc";
        }
    }
}
