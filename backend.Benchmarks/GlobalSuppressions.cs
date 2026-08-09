using System.Diagnostics.CodeAnalysis;

// Benchmark data generation (segment corpora, shuffles) is not
// security-sensitive; Random is intentional for reproducible cheap fixtures.
[assembly: SuppressMessage(
    "Security",
    "CA5394:Do not use insecure randomness",
    Justification = "Benchmark data generation only; no security decision depends on this randomness.")]
