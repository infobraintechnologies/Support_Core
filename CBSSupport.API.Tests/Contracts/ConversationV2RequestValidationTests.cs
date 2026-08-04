using System.ComponentModel.DataAnnotations;
using CBSSupport.Shared.Contracts;

namespace CBSSupport.API.Tests.Contracts;

public sealed class ConversationV2RequestValidationTests
{
    [Theory]
    [InlineData(typeof(CreatePrivateConversationRequest))]
    [InlineData(typeof(SendMessageV2Request))]
    [InlineData(typeof(AdvanceConversationReadRequest))]
    [InlineData(typeof(TransferConversationRequest))]
    [InlineData(typeof(ArchiveConversationRequest))]
    [InlineData(typeof(ReviewPrivateConversationRequest))]
    public void PositionalRecord_ValidationMetadata_IsDefinedOnConstructorParameters(
        Type requestType)
    {
        var constructor = Assert.Single(requestType.GetConstructors());

        Assert.Contains(
            constructor.GetParameters(),
            parameter => parameter
                .GetCustomAttributes(typeof(ValidationAttribute), inherit: true)
                .Any());
        Assert.All(
            requestType.GetProperties(),
            property => Assert.Empty(
                property.GetCustomAttributes(typeof(ValidationAttribute), inherit: true)));
    }

    [Fact]
    public void SendMessage_TextParameter_IsNullableAndBounded()
    {
        var constructor = Assert.Single(typeof(SendMessageV2Request).GetConstructors());
        var text = constructor.GetParameters()
            .Single(parameter => parameter.Name == nameof(SendMessageV2Request.Text));
        var length = Assert.Single(
            text.GetCustomAttributes(typeof(StringLengthAttribute), inherit: true)
                .Cast<StringLengthAttribute>());

        Assert.Empty(text.GetCustomAttributes(typeof(RequiredAttribute), inherit: true));
        Assert.Equal(0, length.MinimumLength);
        Assert.Equal(4000, length.MaximumLength);
    }

    [Fact]
    public void SendMessage_TextOnly_IsValid()
    {
        var request = new SendMessageV2Request(Guid.NewGuid(), "hello");

        Assert.Empty(Validate(request));
    }

    [Fact]
    public void SendMessage_AttachmentOnly_IsValid()
    {
        var request = new SendMessageV2Request(
            Guid.NewGuid(),
            null,
            [Guid.NewGuid()]);

        Assert.Empty(Validate(request));
    }

    [Fact]
    public void SendMessage_WithoutTextOrAttachments_IsInvalid()
    {
        var request = new SendMessageV2Request(Guid.NewGuid(), "   ", []);

        Assert.Contains(
            Validate(request),
            result => result.MemberNames.Contains(nameof(SendMessageV2Request.Text))
                && result.MemberNames.Contains(nameof(SendMessageV2Request.AttachmentIds)));
    }

    [Fact]
    public void SendMessage_AttachmentIdsMustBeDistinctAndAtMostFive()
    {
        var repeated = Guid.NewGuid();
        var request = new SendMessageV2Request(
            Guid.NewGuid(),
            null,
            [repeated, repeated, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()]);

        var results = Validate(request);

        Assert.Contains(results, result =>
            result.ErrorMessage?.Contains("at most five", StringComparison.Ordinal) == true);
        Assert.Contains(results, result =>
            result.ErrorMessage?.Contains("distinct", StringComparison.Ordinal) == true);
    }

    private static IReadOnlyList<ValidationResult> Validate(SendMessageV2Request request)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(
            request,
            new ValidationContext(request),
            results,
            validateAllProperties: true);
        return results;
    }
}
