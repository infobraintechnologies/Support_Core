using System.Diagnostics;
using System.Text;

namespace CBSSupport.API.Tests.Frontend;

/// <summary>
/// Regression tests for the notification, toast, and error-rendering XSS fixes.
/// The Node execution test loads the real browser scripts under a minimal DOM shim
/// and verifies XSS payloads are rendered as plain text and never execute.
/// </summary>
public sealed class NotificationXssRegressionTests
{
    private const string AdminUtilsPath = "wwwroot/js/admin/admin-utils.js";
    private const string AdminNotificationPath = "wwwroot/js/admin/admin-notification.js";
    private const string ChatPath = "wwwroot/js/chat.js";
    private const string AdminLegacyPath = "wwwroot/js/admin.js";

    [Fact]
    public void AdminUtils_ToastSink_ConstructsDomAndUsesTextContent()
    {
        var source = ReadApiFile(AdminUtilsPath);

        Assert.Contains(
            "body.textContent = message == null ? '' : String(message);",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain("toast.innerHTML", source, StringComparison.Ordinal);
        Assert.DoesNotContain("${message}", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AdminNotification_ListSink_ConstructsDomAndUsesTextContent()
    {
        var source = ReadApiFile(AdminNotificationPath);

        Assert.Contains("container.replaceChildren();", source, StringComparison.Ordinal);
        Assert.Contains("message.textContent = notification.message;", source, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "container.innerHTML = notifications.map",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain("${AdminUtils.escapeHtml(notification.message)}", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ClientChat_ToastAndNotificationSinks_ConstructDomAndUseTextContent()
    {
        var source = ReadApiFile(ChatPath);

        Assert.Contains(
            "body.textContent = message == null ? '' : String(message);",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain("insertAdjacentHTML('beforeend', toastHtml)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("${escapeHtml(message)}", source, StringComparison.Ordinal);

        Assert.Contains(
            "message.textContent = notification.message;",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "container.innerHTML = notifications.map",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void LegacyAdmin_ToastNotificationAndErrorSinks_ConstructDomAndUseTextContent()
    {
        var source = ReadApiFile(AdminLegacyPath);

        Assert.Contains(
            "body.textContent = message == null ? '' : String(message);",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain("toast.innerHTML", source, StringComparison.Ordinal);

        Assert.Contains(
            "message.textContent = notification.message;",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "container.innerHTML = notifications.map",
            source,
            StringComparison.Ordinal);

        Assert.DoesNotContain(
            "Error loading messages: ${error.message}</div>",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "errorEl.textContent = `Error loading messages: ${error.message}`;",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void NotificationSinks_RunPayloadsUnderNodeAndRenderAsTextWithoutExecution()
    {
        var script = TestFilePath(
            "Frontend",
            "scripts",
            "notification-xss.test.mjs");

        if (!File.Exists(script))
        {
            Assert.Fail($"Node regression script not found: {script}");
        }

        var nodePath = FindNodeExecutable();
        if (nodePath is null)
        {
            Assert.Fail(
                "Node.js runtime was not found on PATH. The notification/toast XSS execution " +
                "regression tests cannot run without it. Install Node.js and re-run.");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = nodePath,
            Arguments = $"\"{script}\"",
            WorkingDirectory = Path.GetDirectoryName(script)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        using var process = Process.Start(startInfo);
        Assert.NotNull(process);

        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        Assert.True(
            process.ExitCode == 0,
            "Node notification/toast XSS regression script failed.\n"
            + $"Exit code: {process.ExitCode}\nSTDOUT:\n{stdout}\nSTDERR:\n{stderr}");
        Assert.Contains("PASS:", stdout, StringComparison.Ordinal);
    }

    private static string? FindNodeExecutable()
    {
        var candidates = new[]
        {
            "node",
            "node.exe"
        };

        foreach (var candidate in candidates)
        {
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = candidate,
                    Arguments = "--version",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var process = Process.Start(startInfo);
                if (process is null) continue;
                process.WaitForExit();
                if (process.ExitCode == 0) return candidate;
            }
            catch (System.ComponentModel.Win32Exception)
            {
            }
        }

        return null;
    }

    private static string ReadApiFile(string relativePath) =>
        File.ReadAllText(ApiFilePath(relativePath));

    private static string ApiFilePath(string relativePath)
    {
        var supportRoot = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            ".."));
        return Path.Combine(supportRoot, "CBSSupport.API", relativePath);
    }

    private static string TestFilePath(params string[] relativeSegments)
    {
        var supportRoot = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            ".."));
        return relativeSegments.Aggregate(
            Path.Combine(supportRoot, "CBSSupport.API.Tests"),
            Path.Combine);
    }
}
