using System.ComponentModel.DataAnnotations;

namespace CBSSupport.Shared.Contracts;

public sealed record UpdateInstructionRequest(
    [Required, StringLength(4000, MinimumLength = 1)] string Instruction,
    [StringLength(50)] string? Priority,
    [StringLength(2000)] string? Remarks,
    DateTime? ExpiryDate,
    [Range(1, long.MaxValue)] long ExpectedVersion);
