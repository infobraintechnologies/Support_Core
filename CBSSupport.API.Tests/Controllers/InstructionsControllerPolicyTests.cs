using System.Reflection;
using CBSSupport.API.Controllers;
using CBSSupport.API.Security;
using Microsoft.AspNetCore.Authorization;

namespace CBSSupport.API.Tests.Controllers;

public sealed class InstructionsControllerPolicyTests
{
    [Fact]
    public void Controller_UsesAdminOrClientPolicy()
    {
        AssertPolicy(typeof(InstructionsController), Policies.AdminOrClient);
    }

    [Theory]
    [InlineData(nameof(InstructionsController.SaveAdminSupportGroupChat))]
    [InlineData(nameof(InstructionsController.SaveInternalTeamChat))]
    [InlineData(nameof(InstructionsController.GetConversationsByChatType))]
    [InlineData(nameof(InstructionsController.GetAllTickets))]
    [InlineData(nameof(InstructionsController.GetAllInquiries))]
    [InlineData(nameof(InstructionsController.UpdateTicket))]
    [InlineData(nameof(InstructionsController.UpdateTicketStatus))]
    [InlineData(nameof(InstructionsController.UpdateInquiryStatus))]
    [InlineData(nameof(InstructionsController.MarkNotificationSeenByAdmin))]
    [InlineData(nameof(InstructionsController.MarkAllNotificationsSeenByAdmin))]
    public void AdminAction_UsesAdminOnlyPolicy(string actionName)
    {
        AssertPolicy(GetAction(actionName), Policies.AdminOnly);
    }

    [Theory]
    [InlineData(nameof(InstructionsController.SaveSupportPrivateChat))]
    [InlineData(nameof(InstructionsController.SaveTicketTraining))]
    [InlineData(nameof(InstructionsController.SaveMigrationTicket))]
    [InlineData(nameof(InstructionsController.SaveSetupTicket))]
    [InlineData(nameof(InstructionsController.SaveCorrectionTicket))]
    [InlineData(nameof(InstructionsController.SaveBugFixTicket))]
    [InlineData(nameof(InstructionsController.SaveNewFeatureTicket))]
    [InlineData(nameof(InstructionsController.SaveFeatureEnhancementTicket))]
    [InlineData(nameof(InstructionsController.SaveBackendWorkaroundTicket))]
    [InlineData(nameof(InstructionsController.SaveAccountsInquiry))]
    [InlineData(nameof(InstructionsController.SaveSalesInquiry))]
    [InlineData(nameof(InstructionsController.GetTicketsForCurrentClient))]
    [InlineData(nameof(InstructionsController.GetInquiriesForCurrentClient))]
    [InlineData(nameof(InstructionsController.MarkAllNotificationsSeenByClient))]
    [InlineData(nameof(InstructionsController.MarkNotificationSeenByClient))]
    public void ClientAction_UsesClientOnlyPolicy(string actionName)
    {
        AssertPolicy(GetAction(actionName), Policies.ClientOnly);
    }

    private static MethodInfo GetAction(string actionName)
    {
        return Assert.Single(
            typeof(InstructionsController).GetMethods(BindingFlags.Instance | BindingFlags.Public),
            method => method.Name == actionName);
    }

    private static void AssertPolicy(MemberInfo member, string expectedPolicy)
    {
        var attributes = member.GetCustomAttributes<AuthorizeAttribute>();
        Assert.Contains(attributes, attribute => attribute.Policy == expectedPolicy);
    }
}
