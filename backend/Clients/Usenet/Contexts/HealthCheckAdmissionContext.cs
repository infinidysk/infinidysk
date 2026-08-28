using NzbWebDAV.Services;

namespace NzbWebDAV.Clients.Usenet.Contexts;

public sealed record HealthCheckAdmissionContext(
    HealthCheckConnectionGate Gate,
    HealthCheckAdmissionPriority Priority);
