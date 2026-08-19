namespace NzbWebDAV.Par2Recovery.Packets
{
    /// <summary>
    /// PAR 2.0 Recovery Slice packet ("PAR 2.0\0RecvSlic"). Carries the recovery
    /// data itself. Import-time descriptor scans skip the body; the repair fetcher
    /// constructs with <paramref name="readPayload"/> to parse exponent + slice bytes.
    /// </summary>
    public class RecvSlic : Par2Packet
    {
        public const string PacketType = "PAR 2.0\0RecvSlic";

        private readonly bool _readPayload;

        public uint Exponent { get; private set; }
        public byte[] Payload { get; private set; } = [];

        public RecvSlic(Par2PacketHeader header, bool readPayload = false) : base(header)
        {
            _readPayload = readPayload;
        }

        protected override bool SkipBody => !_readPayload;

        protected override void ParseBody(byte[] body)
        {
            if (body.Length < 4)
                throw new InvalidDataException("RecvSlic body too short for exponent.");

            Exponent = BitConverter.ToUInt32(body, 0);
            Payload = new byte[body.Length - 4];
            if (Payload.Length > 0)
                Buffer.BlockCopy(body, 4, Payload, 0, Payload.Length);
        }
    }
}
