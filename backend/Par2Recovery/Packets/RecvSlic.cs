namespace NzbWebDAV.Par2Recovery.Packets
{
    /// <summary>
    /// PAR 2.0 Recovery Slice packet ("PAR 2.0\0RecvSlic"). Carries the recovery
    /// data itself, so it dominates file size; we only need to know where it
    /// starts, never its payload.
    /// </summary>
    public class RecvSlic : Par2Packet
    {
        public const string PacketType = "PAR 2.0\0RecvSlic";

        public RecvSlic(Par2PacketHeader header) : base(header)
        {
        }

        protected override bool SkipBody => true;
    }
}
