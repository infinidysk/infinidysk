namespace NzbWebDAV.Par2Recovery.ReedSolomon;

/// <summary>
/// Galois field GF(2^16) per PAR 2.0 (irreducible polynomial 0x1100B).
/// </summary>
public sealed class Gf16Field
{
    private const uint Polynomial = 0x1100B;
    public const int Order = 65536;

    private readonly ushort[] _log = new ushort[Order];
    private readonly ushort[] _exp = new ushort[Order];

    public Gf16Field()
    {
        uint x = 1;
        for (var i = 0; i < Order - 1; i++)
        {
            _exp[i] = (ushort)x;
            _log[x] = (ushort)i;
            x <<= 1;
            if ((x & 0x10000) != 0)
                x ^= Polynomial;
        }
    }

    public ushort Add(ushort a, ushort b) => (ushort)(a ^ b);

    public ushort Mul(ushort a, ushort b)
    {
        if (a == 0 || b == 0) return 0;
        var logSum = _log[a] + _log[b];
        if (logSum >= Order - 1) logSum -= Order - 1;
        return _exp[logSum];
    }

    public ushort Div(ushort a, ushort b)
    {
        if (b == 0) throw new DivideByZeroException();
        if (a == 0) return 0;
        var logDiff = _log[a] - _log[b];
        if (logDiff < 0) logDiff += Order - 1;
        return _exp[logDiff];
    }

    public ushort Pow(ushort baseValue, int exponent)
    {
        if (exponent == 0) return 1;
        if (baseValue == 0) return 0;
        var log = _log[baseValue] * exponent;
        log %= Order - 1;
        if (log < 0) log += Order - 1;
        return _exp[log];
    }

    /// <summary>PAR2 coefficient for recovery-set file index and RecvSlic exponent.</summary>
    public ushort RecoveryCoefficient(uint exponent, int fileIndex)
    {
        var basePow = Pow(2, (int)exponent);
        return Pow(basePow, fileIndex);
    }
}
