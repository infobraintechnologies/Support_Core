using CBSSupport.Shared.Contracts;
using CBSSupport.Shared.Services;

namespace CBSSupport.API.Tests.Attachments;

public sealed class AttachmentRepositoryParameterTests
{
    [Fact]
    public void CreateIntentParameters_AdminWithTargetTenant_PreservesBothScopes()
    {
        var parameters = AttachmentRepository.CreateIntentParameters(
            new AttachmentIntentRecord(
                Guid.NewGuid(),
                42,
                224,
                new AttachmentActor(9, ClientId: null, IsAdmin: true),
                "quarantine/test",
                "test.txt",
                "text/plain",
                100,
                DateTimeOffset.UtcNow));

        Assert.True(parameters.Get<bool>("IsAdmin"));
        Assert.Equal(9, parameters.Get<long>("UserId"));
        Assert.Null(parameters.Get<long?>("ClientId"));
        Assert.Equal(42, parameters.Get<long>("TargetClientId"));
    }
}
