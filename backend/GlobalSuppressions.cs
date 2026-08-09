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
