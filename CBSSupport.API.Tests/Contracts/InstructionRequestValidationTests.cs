using System.ComponentModel.DataAnnotations;
using CBSSupport.Shared.Contracts;

namespace CBSSupport.API.Tests.Contracts;

public sealed class InstructionRequestValidationTests
{
    [Theory]
    [InlineData(typeof(CreateInstructionRequest))]
    [InlineData(typeof(UpdateInstructionRequest))]
    public void PositionalRecord_ValidationMetadata_IsDefinedOnConstructorParameters(Type requestType)
    {
        var constructor = Assert.Single(requestType.GetConstructors());
        var parameters = constructor.GetParameters();

        Assert.Contains(
            parameters.Single(parameter => parameter.Name == "Instruction")
                .GetCustomAttributes(typeof(RequiredAttribute), inherit: true),
            attribute => attribute is RequiredAttribute);
        AssertParameterStringLength(parameters, "Instruction", 4000);
        AssertParameterStringLength(parameters, "Priority", 50);
        AssertParameterStringLength(parameters, "Remarks", 2000);

        Assert.All(
            requestType.GetProperties(),
            property => Assert.Empty(
                property.GetCustomAttributes(typeof(ValidationAttribute), inherit: true)));
    }

    private static void AssertParameterStringLength(
        System.Reflection.ParameterInfo[] parameters,
        string parameterName,
        int maximumLength)
    {
        var attribute = Assert.Single(
            parameters.Single(parameter => parameter.Name == parameterName)
                .GetCustomAttributes(typeof(StringLengthAttribute), inherit: true)
                .Cast<StringLengthAttribute>());

        Assert.Equal(maximumLength, attribute.MaximumLength);
    }
}
