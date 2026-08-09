using System.Diagnostics.CodeAnalysis;

// Targeted analyzer suppressions with justification, per the static-analysis
// policy in CONTRIBUTING.md. Rule-level policy lives in the root .editorconfig;
// this file is for specific APIs where the rule misfires on intent.

// The abstract NntpClient hierarchy manages only managed resources and has no
// finalizers anywhere in the hierarchy, so the full Dispose(bool) pattern
// ceremony (a base Dispose() wrapper plus ~30 override conversions across
// production clients and test doubles) buys nothing. The abstract
// public-Dispose contract is the simpler correct pattern here.
[assembly: SuppressMessage(
    "Design",
    "CA1063:Implement IDisposable Correctly",
    Scope = "type",
    Target = "~T:NzbWebDAV.Clients.Usenet.NntpClient",
    Justification = "Abstract NNTP client hierarchy manages only managed resources; no finalizers exist. The abstract public Dispose contract is intentional.")]

// These structs are transient data carriers that are never compared for
// equality, used as collection keys, or stored in hashed containers, so the
// default ValueType.Equals reflection path is never exercised. (Par2PacketHeader
// is a P/Invoke-marshaled layout struct where member equality is meaningless.)
[assembly: SuppressMessage(
    "Performance",
    "CA1815:Override equals and operator equals on value types",
    Scope = "type",
    Target = "~T:NzbWebDAV.Utils.OrganizedLinksUtil.DavItemLink",
    Justification = "Transient helper struct; never compared or used as a key.")]
[assembly: SuppressMessage(
    "Performance",
    "CA1815:Override equals and operator equals on value types",
    Scope = "type",
    Target = "~T:NzbWebDAV.Utils.SymlinkAndStrmUtil.StrmInfo",
    Justification = "Transient helper struct; never compared or used as a key.")]
[assembly: SuppressMessage(
    "Performance",
    "CA1815:Override equals and operator equals on value types",
    Scope = "type",
    Target = "~T:NzbWebDAV.Utils.SymlinkAndStrmUtil.SymlinkInfo",
    Justification = "Transient helper struct; never compared or used as a key.")]
[assembly: SuppressMessage(
    "Performance",
    "CA1815:Override equals and operator equals on value types",
    Scope = "type",
    Target = "~T:NzbWebDAV.Par2Recovery.Packets.Par2PacketHeader",
    Justification = "P/Invoke-marshaled layout struct; member equality is meaningless for a packet read view.")]
[assembly: SuppressMessage(
    "Performance",
    "CA1815:Override equals and operator equals on value types",
    Scope = "type",
    Target = "~T:NzbWebDAV.Clients.Usenet.Models.UsenetExclusiveConnection",
    Justification = "Single-field delegate wrapper; never compared or used as a key.")]
