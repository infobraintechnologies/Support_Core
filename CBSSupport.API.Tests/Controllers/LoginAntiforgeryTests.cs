using System.Reflection;
using CBSSupport.API.Controllers;
using CBSSupport.Shared.Models;
using Microsoft.AspNetCore.Mvc;

namespace CBSSupport.API.Tests.Controllers;

public sealed class LoginAntiforgeryTests
{
    [Fact]
    public void LoginPost_Contract_RequiresAntiforgeryValidation()
    {
        var action = typeof(LoginController).GetMethod(
            nameof(LoginController.Index),
            BindingFlags.Instance | BindingFlags.Public,
            binder: null,
            types: [typeof(LoginViewModel)],
            modifiers: null);

        var member = Assert.IsAssignableFrom<MemberInfo>(action);
        Assert.NotNull(member.GetCustomAttribute<HttpPostAttribute>());
        Assert.NotNull(member.GetCustomAttribute<ValidateAntiForgeryTokenAttribute>());
    }
}
