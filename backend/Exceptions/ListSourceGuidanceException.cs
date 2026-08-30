namespace NzbWebDAV.Exceptions;

/// <summary>
/// Author-controlled Watchtower list-source guidance. Safe for LastSyncError
/// and discover 400 responses; does not carry request URLs or body excerpts.
/// </summary>
public sealed class ListSourceGuidanceException(string message) : InvalidOperationException(message);
