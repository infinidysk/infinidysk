using NzbWebDAV.Utils;

namespace NzbWebDAV.Tests.Utils;

public sealed class AddressSpaceDiagnosticsTests
{
    [Fact]
    public void ParseLinuxAddressSpaceLimit_ReturnsFiniteSoftLimit()
    {
        const string limits = """
            Limit                     Soft Limit           Hard Limit           Units
            Max address space         10000000000          unlimited            bytes
            """;

        Assert.Equal(10_000_000_000, AddressSpaceDiagnostics.ParseLinuxAddressSpaceLimit(limits));
    }

    [Fact]
    public void ParseLinuxAddressSpaceLimit_ReturnsNullForUnlimitedOrMissingLimit()
    {
        const string unlimited = """
            Limit                     Soft Limit           Hard Limit           Units
            Max address space         unlimited            unlimited            bytes
            """;

        Assert.Null(AddressSpaceDiagnostics.ParseLinuxAddressSpaceLimit(unlimited));
        Assert.Null(AddressSpaceDiagnostics.ParseLinuxAddressSpaceLimit("Max open files            1024                 4096                 files"));
    }

    [Fact]
    public void Capture_ReturnsNonNegativeVirtualMemoryWhenAvailable()
    {
        var snapshot = AddressSpaceDiagnostics.Capture();

        Assert.True(snapshot.VirtualMemoryBytes is null or >= 0);
        Assert.True(snapshot.GcCommittedBytes is null or >= 0);
        Assert.True(snapshot.WorkingSetBytes is null or >= 0);
        Assert.True(snapshot.GcHeapHardLimitLohBytes is null or >= 0);
        Assert.True(snapshot.GcHeapHardLimitLohPercent is null or >= 0);
    }
}
