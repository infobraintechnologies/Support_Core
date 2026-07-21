using System.ComponentModel.DataAnnotations;

namespace CBSSupport.Shared.Contracts;

public sealed record SendConversationMessageRequest(
    [Required, StringLength(4000, MinimumLength = 1)] string Text,
    IReadOnlyCollection<string>? AttachmentIds = null);
