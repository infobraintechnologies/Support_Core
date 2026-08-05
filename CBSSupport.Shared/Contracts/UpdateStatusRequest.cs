using System.ComponentModel.DataAnnotations;

namespace CBSSupport.Shared.Contracts;

public sealed record UpdateStatusRequest(
    bool IsCompleted,
    [Range(1, long.MaxValue)] long ExpectedVersion);
