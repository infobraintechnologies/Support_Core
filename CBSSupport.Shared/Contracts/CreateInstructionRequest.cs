using System.ComponentModel.DataAnnotations;

namespace CBSSupport.Shared.Contracts;

public sealed record CreateInstructionRequest(
    [Required, StringLength(4000, MinimumLength = 1)] string Instruction,
    long? InstructionId,
    [StringLength(50)] string? Priority,
    [StringLength(2000)] string? Remarks,
    DateTime? ExpiryDate);
