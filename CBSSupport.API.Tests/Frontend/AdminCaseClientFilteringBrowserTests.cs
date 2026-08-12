namespace CBSSupport.API.Tests.Frontend;

public sealed class AdminCaseClientFilteringBrowserTests
{
    [Theory]
    [InlineData("wwwroot/js/admin/admin-tickets.js", "/api/v1/admin/tickets")]
    [InlineData("wwwroot/js/admin/admin-inquiries.js", "/api/v1/admin/inquiries")]
    public void AdminCaseTable_SendsSelectedClientIdToListEndpoint(
        string relativePath,
        string endpoint)
    {
        var source = ReadApiFile(relativePath);

        Assert.Contains($"\"url\": \"{endpoint}\"", source, StringComparison.Ordinal);
        Assert.Contains("const clientId = window.AdminCore?.getCurrentClientId();", source, StringComparison.Ordinal);
        Assert.Contains("request.clientId = clientId;", source, StringComparison.Ordinal);
        Assert.Contains("ajax.reload(null, true);", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ClientSwitcher_ReloadsCaseTablesWithoutSearchingStatusColumns()
    {
        var source = ReadApiFile("wwwroot/js/admin/admin-core.js");

        Assert.Contains("window.AdminTickets.filterByClient();", source, StringComparison.Ordinal);
        Assert.Contains("window.AdminInquiries.filterByClient();", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ticketsTable.column(1).search(searchTerm", source, StringComparison.Ordinal);
        Assert.DoesNotContain("inquiriesTable.column(1).search(searchTerm", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ClientSwitcher_ClosesDetailsFromThePreviousTenantScope()
    {
        var ticketSource = ReadApiFile("wwwroot/js/admin/admin-tickets.js");
        var inquirySource = ReadApiFile("wwwroot/js/admin/admin-inquiries.js");

        Assert.Contains("closeTicketDetail();\n            ticketsTable.ajax.reload", ticketSource, StringComparison.Ordinal);
        Assert.Contains("closeInquiryDetail();\n            inquiriesTable.ajax.reload", inquirySource, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("wwwroot/js/admin/admin-tickets.js", "#status-filter-tickets")]
    [InlineData("wwwroot/js/admin/admin-inquiries.js", "#status-filter-inquiries")]
    public void StatusFilter_SendsSelectedStatusToListEndpointAndReloads(
        string relativePath,
        string selector)
    {
        var source = ReadApiFile(relativePath);

        Assert.Contains($"const status = $('{selector}').val();", source, StringComparison.Ordinal);
        Assert.Contains("request.status = status;", source, StringComparison.Ordinal);
        Assert.Contains("setupStatusFilter();", source, StringComparison.Ordinal);
        Assert.Contains("filterByStatus($(this).val());", source, StringComparison.Ordinal);
        Assert.Contains("ajax.reload(null, true);", source, StringComparison.Ordinal);
    }

    [Fact]
    public void StatusFilters_ExposeCanonicalTicketAndInquiryOptions()
    {
        var source = ReadApiFile("Views/AdminSupport/Index.cshtml");

        Assert.Contains("id=\"status-filter-tickets\"", source, StringComparison.Ordinal);
        Assert.Contains("<option value=\"Open\">Open</option>", source, StringComparison.Ordinal);
        Assert.Contains("<option value=\"Resolved\">Resolved</option>", source, StringComparison.Ordinal);
        Assert.Contains("id=\"status-filter-inquiries\"", source, StringComparison.Ordinal);
        Assert.Contains("<option value=\"Pending\">Pending</option>", source, StringComparison.Ordinal);
        Assert.Contains("<option value=\"Completed\">Completed</option>", source, StringComparison.Ordinal);
    }

    [Fact]
    public void DashboardStatusNavigation_UsesCaseFilterModulesInsteadOfStaleColumns()
    {
        var source = ReadApiFile("wwwroot/js/admin/admin-navigation.js");

        Assert.Contains("window.AdminTickets.filterByStatus(statusFilter);", source, StringComparison.Ordinal);
        Assert.Contains("window.AdminInquiries.filterByStatus(statusFilter);", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ticketsTable.column(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("inquiriesTable.column(", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AdminCaseQueues_ConsumeV1IdentityStatusAndTimestampFields()
    {
        var ticketSource = ReadApiFile("wwwroot/js/admin/admin-tickets.js");
        var inquirySource = ReadApiFile("wwwroot/js/admin/admin-inquiries.js");

        Assert.Contains("\"data\": \"createdByName\"", ticketSource, StringComparison.Ordinal);
        Assert.Contains("\"data\": \"createdAt\"", ticketSource, StringComparison.Ordinal);
        Assert.Contains("ticket.createdByName", ticketSource, StringComparison.Ordinal);
        Assert.Contains("ticket.resolvedByName", ticketSource, StringComparison.Ordinal);
        Assert.Contains("ticket.resolvedAt", ticketSource, StringComparison.Ordinal);
        Assert.DoesNotContain("ticket.createdBy ||", ticketSource, StringComparison.Ordinal);

        Assert.Contains("\"data\": \"status\"", inquirySource, StringComparison.Ordinal);
        Assert.Contains("\"data\": \"inquiredByName\"", inquirySource, StringComparison.Ordinal);
        Assert.Contains("\"data\": \"createdAt\"", inquirySource, StringComparison.Ordinal);
        Assert.Contains("inquiry.inquiredByName", inquirySource, StringComparison.Ordinal);
        Assert.Contains("inquiry.createdAt", inquirySource, StringComparison.Ordinal);
        Assert.DoesNotContain("inquiry.inquiredBy ||", inquirySource, StringComparison.Ordinal);
        Assert.DoesNotContain("inquiry.outcome ||", inquirySource, StringComparison.Ordinal);
    }

    [Fact]
    public void AdminCaseQueues_DoNotRenderClientFallbackBelowSubjectOrTopic()
    {
        var ticketSource = ReadApiFile("wwwroot/js/admin/admin-tickets.js");
        var inquirySource = ReadApiFile("wwwroot/js/admin/admin-inquiries.js");

        Assert.DoesNotContain("row.clientName || 'Unknown client'", ticketSource, StringComparison.Ordinal);
        Assert.DoesNotContain("row.clientName || 'Unknown client'", inquirySource, StringComparison.Ordinal);
        Assert.DoesNotContain("<div class=\"ticket-client\">", ticketSource, StringComparison.Ordinal);
        Assert.DoesNotContain("<div class=\"ticket-client\">", inquirySource, StringComparison.Ordinal);
    }

    private static string ReadApiFile(string relativePath) =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "CBSSupport.API",
            relativePath.Replace('/', Path.DirectorySeparatorChar))));
}
