using System.ComponentModel.DataAnnotations;
using CBSSupport.Shared.Contracts;

namespace CBSSupport.API.Tests.Contracts;

public sealed class CaseApiV1RequestValidationTests
{
    [Theory]
    [InlineData(typeof(CreateTicketRequest))]
    [InlineData(typeof(CreateInquiryRequest))]
    [InlineData(typeof(UpdateCaseStatusRequest))]
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
    public void CreateTicketRequest_Valid_NoValidationErrors()
    {
        var request = new CreateTicketRequest(
            "Posting mismatch",
            "Observed after closing batch 18.",
            CaseTypes.Migration,
            CasePriorities.High);

        Assert.Empty(Validate(request));
    }

    [Fact]
    public void CreateTicketRequest_MissingSubjectDescriptionOrType_IsInvalid()
    {
        var request = new CreateTicketRequest(null, null, null, null);

        var results = Validate(request);

        Assert.Contains(results, r => r.MemberNames.Contains(nameof(CreateTicketRequest.Subject)));
        Assert.Contains(results, r => r.MemberNames.Contains(nameof(CreateTicketRequest.Description)));
        Assert.Contains(results, r => r.MemberNames.Contains(nameof(CreateTicketRequest.Type)));
    }

    [Fact]
    public void CreateTicketRequest_DescriptionOverLength_IsInvalid()
    {
        var request = new CreateTicketRequest(
            "Subject",
            new string('x', 4001),
            CaseTypes.Migration,
            null);

        Assert.Contains(
            Validate(request),
            r => r.MemberNames.Contains(nameof(CreateTicketRequest.Description)));
    }

    [Fact]
    public void CreateTicketRequest_UnknownPriority_IsInvalid()
    {
        var request = new CreateTicketRequest(
            "Subject",
            "Description",
            CaseTypes.Migration,
            "Immediate");

        var results = new List<ValidationResult>();
        Assert.False(Validator.TryValidateObject(request, new ValidationContext(request), results, true));
        Assert.Contains(results, result => result.MemberNames.Contains(nameof(CreateTicketRequest.Priority)));
    }

    [Fact]
    public void CreateInquiryRequest_Valid_NoValidationErrors()
    {
        var request = new CreateInquiryRequest(
            "Account statement format",
            "Clarification requested for fiscal export.",
            CaseTypes.Accounts,
            CasePriorities.Normal);

        Assert.Empty(Validate(request));
    }

    [Fact]
    public void CreateInquiryRequest_MissingTopicOrType_IsInvalid()
    {
        var request = new CreateInquiryRequest(null, "desc", null, null);

        var results = Validate(request);

        Assert.Contains(results, r => r.MemberNames.Contains(nameof(CreateInquiryRequest.Topic)));
        Assert.Contains(results, r => r.MemberNames.Contains(nameof(CreateInquiryRequest.Type)));
    }

    [Fact]
    public void UpdateCaseStatusRequest_MissingStatus_IsInvalid()
    {
        var request = new UpdateCaseStatusRequest(null);

        Assert.Contains(
            Validate(request),
            r => r.MemberNames.Contains(nameof(UpdateCaseStatusRequest.Status)));
    }

    [Fact]
    public void CaseTypes_TicketAndInquiryLabelsResolveToCanonicalCodes()
    {
        Assert.True(CaseTypes.TryResolveTicket(CaseTypes.Migration, out var migration));
        Assert.Equal(ConversationTypes.MigrationTicket, migration);
        Assert.True(CaseTypes.TryResolveTicket(CaseTypes.BugFix, out var bugFix));
        Assert.Equal(ConversationTypes.BugFixTicket, bugFix);
        Assert.True(CaseTypes.TryResolveInquiry(CaseTypes.Sales, out var sales));
        Assert.Equal(ConversationTypes.SalesInquiry, sales);
    }

    [Fact]
    public void CaseTypes_UnknownLabel_DoesNotResolve()
    {
        Assert.False(CaseTypes.TryResolveTicket("not-a-type", out _));
        Assert.False(CaseTypes.TryResolveInquiry(null, out _));
    }

    [Theory]
    [InlineData("low", CasePriorities.Low)]
    [InlineData(" Normal ", CasePriorities.Normal)]
    [InlineData("HIGH", CasePriorities.High)]
    [InlineData("urgent", CasePriorities.Urgent)]
    public void CasePriorities_NormalizesAcceptedValues(string rawValue, string expected)
    {
        Assert.True(CasePriorities.TryNormalize(rawValue, out var priority));
        Assert.Equal(expected, priority);
    }

    private static IReadOnlyList<ValidationResult> Validate(object request)
    {
        var results = new List<ValidationResult>();
        var constructor = request.GetType().GetConstructors().Single();
        var context = new ValidationContext(request);
        foreach (var parameter in constructor.GetParameters())
        {
            var property = request.GetType().GetProperty(parameter.Name!);
            var value = property?.GetValue(request);
            context.MemberName = parameter.Name;
            foreach (var attribute in parameter
                .GetCustomAttributes(typeof(ValidationAttribute), inherit: true)
                .Cast<ValidationAttribute>())
            {
                var result = attribute.GetValidationResult(value, context);
                if (result is not null)
                {
                    results.Add(result);
                }
            }
        }

        return results;
    }
}
