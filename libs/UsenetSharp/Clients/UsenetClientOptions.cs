using System.Security.Cryptography.X509Certificates;

namespace UsenetSharp.Clients;

/// <summary>
/// Configures NNTP operation timeouts, TLS validation, and connection reuse behavior.
/// </summary>
public sealed record UsenetClientOptions
{
    /// <summary>
    /// Gets the maximum idle time allowed for an individual network read or write.
    /// </summary>
    public TimeSpan ReadTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Gets the idle time before the first TCP keepalive probe.
    /// </summary>
    /// <remarks>
    /// Defaults to 60 seconds so pooled idle connections are detected as dead
    /// well before typical NAT/firewall idle timeouts force a full
    /// <see cref="ReadTimeout"/> stall on the next command.
    /// </remarks>
    public TimeSpan TcpKeepAliveTime { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Gets the interval between unacknowledged TCP keepalive probes.
    /// </summary>
    public TimeSpan TcpKeepAliveInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Gets the number of unacknowledged keepalive probes before the OS
    /// declares the connection dead.
    /// </summary>
    public int TcpKeepAliveRetryCount { get; init; } = 3;

    /// <summary>
    /// Gets the maximum number of bytes drained after a body consumer stops reading.
    /// </summary>
    public long AbandonedBodyDrainLimit { get; init; } = 1024 * 1024;

    /// <summary>
    /// Gets the number of unread decoded BODY bytes that pauses the pipe writer.
    /// </summary>
    /// <remarks>
    /// Defaults to 1 MiB. A decode flush can temporarily exceed this threshold by
    /// up to one decoded chunk before backpressure takes effect.
    /// </remarks>
    public long DecodedBodyPauseWriterThreshold { get; init; } = 1024 * 1024;

    /// <summary>
    /// Gets the number of unread decoded BODY bytes that resumes a paused pipe writer.
    /// </summary>
    /// <remarks>
    /// Defaults to 512 KiB and must not exceed
    /// <see cref="DecodedBodyPauseWriterThreshold"/>.
    /// </remarks>
    public long DecodedBodyResumeWriterThreshold { get; init; } = 512 * 1024;

    /// <summary>
    /// Gets how cancelled body transfers release the connection.
    /// </summary>
    /// <remarks>
    /// <see cref="ConnectionReleasePolicy.DrainToReuse"/> (default) drains remaining
    /// protocol data so the connection can be reused — preferred for queue downloads
    /// where cancellation is rare. <see cref="ConnectionReleasePolicy.AbandonConnection"/>
    /// poisons immediately so the owner reconnects — preferred for seek-heavy WebDAV
    /// streaming where drain latency dominates.
    /// </remarks>
    public ConnectionReleasePolicy CancellationPolicy { get; init; } =
        ConnectionReleasePolicy.DrainToReuse;

    /// <summary>
    /// Gets the maximum number of commands that may be in flight in one pipelined batch.
    /// </summary>
    /// <remarks>
    /// Defaults to 64 to stay within the RFC 3977 §3.5 TCP-window caution (~4 KiB)
    /// for typical message-id lengths. Pipelined BODY batches require the caller
    /// to split at this depth. <see cref="UsenetClient.StatPipelinedAsync"/> windows
    /// larger STAT batches internally at this depth.
    /// </remarks>
    public int MaxPipelineDepth { get; init; } = 64;

    /// <summary>
    /// Gets whether <see cref="UsenetClient.DecodedBodyAsync(UsenetSharp.Models.SegmentId, CancellationToken)"/>
    /// validates decoded content against the CRC32 value in the yEnc trailer.
    /// </summary>
    /// <remarks>
    /// Disabled by default for backward compatibility. Prefer
    /// <see cref="CrcValidation"/> for the tri-state modes.
    /// </remarks>
    [Obsolete("Use CrcValidation instead.")]
    public bool ValidateDecodedBodyCrc32
    {
        get => CrcValidation != YencCrcValidationMode.Off;
        init => CrcValidation = value
            ? YencCrcValidationMode.Require
            : YencCrcValidationMode.Off;
    }

    /// <summary>
    /// Gets how decoded yEnc CRC32 trailer fields are validated.
    /// </summary>
    /// <remarks>
    /// Defaults to <see cref="YencCrcValidationMode.Off"/> for backward compatibility.
    /// Planned to default to <see cref="YencCrcValidationMode.WhenPresent"/> in a future major release.
    /// </remarks>
    public YencCrcValidationMode CrcValidation { get; init; } = YencCrcValidationMode.Off;

    /// <summary>
    /// Gets whether authentication is refused on non-TLS connections.
    /// </summary>
    /// <remarks>
    /// Defaults to <see langword="false"/> for backward compatibility with plaintext
    /// test servers. Set to <see langword="true"/> to prevent accidental credential
    /// disclosure (RFC 4643 §4). Planned to default to true in a future major release.
    /// </remarks>
    public bool RequireTlsForAuthentication { get; init; }

    /// <summary>
    /// Gets the certificate revocation mode used when establishing TLS connections.
    /// </summary>
    /// <remarks>
    /// Defaults to <see cref="X509RevocationMode.NoCheck"/> to avoid revocation lookup
    /// latency during frequent streaming reconnects. Platform certificate chain and
    /// hostname validation remain enabled unless <see cref="SkipTlsVerification"/> is set.
    /// </remarks>
    public X509RevocationMode CertificateRevocationCheckMode { get; init; } =
        X509RevocationMode.NoCheck;

    /// <summary>
    /// Gets whether TLS certificate chain and hostname validation is bypassed.
    /// </summary>
    /// <remarks>
    /// Defaults to <see langword="false"/>. Enable only for a specific trusted
    /// server with a broken certificate. This applies to every host connected by
    /// this client instance and also disables certificate revocation lookup. TLS
    /// traffic remains encrypted, but a network attacker can impersonate the
    /// server and read NNTP credentials.
    /// </remarks>
    public bool SkipTlsVerification { get; init; }
}
