namespace CBSSupport.API.Tests.Frontend;

public sealed class AdminDashboardBrowserTests
{
    [Fact]
    public void Dashboard_UsesPinnedD3WithIntegrityInsteadOfChartJs()
    {
        var view = ReadApiFile("Views/AdminSupport/Index.cshtml");

        Assert.Contains("d3@7.9.0/dist/d3.min.js", view, StringComparison.Ordinal);
        Assert.Contains("integrity=\"sha384-", view, StringComparison.Ordinal);
        Assert.DoesNotContain("chart.js", view, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("id=\"dashboard-workload-chart\"", view, StringComparison.Ordinal);
        Assert.Contains("id=\"ticket-priority-chart\"", view, StringComparison.Ordinal);
    }

    [Fact]
    public void Dashboard_UsesCurrentCaseContractAndSafeDomRendering()
    {
        var source = ReadApiFile("wwwroot/js/admin/admin-dashboard.js");

        Assert.Contains("Array.isArray(page?.items)", source, StringComparison.Ordinal);
        Assert.Contains("item?.createdAt", source, StringComparison.Ordinal);
        Assert.Contains("inquiry?.status", source, StringComparison.Ordinal);
        Assert.Contains("title.textContent", source, StringComparison.Ordinal);
        Assert.DoesNotContain(".innerHTML", source, StringComparison.Ordinal);
        Assert.DoesNotContain(".html(", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Dashboard_D3ChartsRemainAccessibleAndResponsive()
    {
        var source = ReadApiFile("wwwroot/js/admin/admin-dashboard.js");
        var view = ReadApiFile("Views/AdminSupport/Index.cshtml");
        var styles = ReadApiFile("wwwroot/css/site.css");

        Assert.Contains("window.d3.scaleLinear()", source, StringComparison.Ordinal);
        Assert.Contains("window.d3.scaleBand()", source, StringComparison.Ordinal);
        Assert.Contains("tickValues(tickValues)", source, StringComparison.Ordinal);
        Assert.Contains("attr(\"viewBox\"", source, StringComparison.Ordinal);
        Assert.Contains("id=\"dashboard-workload-summary\"", view, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"Open tickets by priority\"", view, StringComparison.Ordinal);
        Assert.Contains("@media (max-width: 575.98px)", styles, StringComparison.Ordinal);
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
