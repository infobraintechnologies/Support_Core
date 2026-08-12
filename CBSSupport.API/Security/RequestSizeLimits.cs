namespace CBSSupport.API.Security;

public static class RequestSizeLimits
{
    public const long MaximumBodySizeBytes = 1024 * 1024;
    public const long MaximumAttachmentBodySizeBytes = 10L * 1024 * 1024;
}
