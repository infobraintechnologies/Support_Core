using System.Buffers.Binary;
using System.Globalization;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace CBSSupport.Shared.Services;

public sealed partial class ClamAvFileScanner(
    AttachmentScanningOptions options,
    TimeProvider timeProvider) : IFileScanner
{
    private static readonly string[] DefinitionTimeFormats =
    [
        "ddd MMM d HH:mm:ss yyyy",
        "ddd MMM  d HH:mm:ss yyyy"
    ];

    private FileScannerHealth _health = new(
        false,
        DateTimeOffset.MinValue,
        null,
        "clamav_not_checked");

    public FileScannerHealth Health => Volatile.Read(ref _health);

    public async Task<FileScannerHealth> CheckHealthAsync(
        CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        try
        {
            var response = await ExecuteCommandAsync("zVERSION\0", cancellationToken);
            var definitionTime = ParseDefinitionTime(response);
            var healthy = definitionTime is not null
                && now - definitionTime.Value
                    <= TimeSpan.FromHours(options.MaximumDefinitionAgeHours);
            var result = new FileScannerHealth(
                healthy,
                now,
                definitionTime,
                healthy ? null : "clamav_definitions_stale");
            Volatile.Write(ref _health, result);
            return result;
        }
        catch (Exception exception) when (
            exception is SocketException or IOException or TimeoutException)
        {
            var result = new FileScannerHealth(
                false,
                now,
                null,
                "clamav_unavailable");
            Volatile.Write(ref _health, result);
            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            var result = new FileScannerHealth(
                false,
                now,
                null,
                "clamav_timeout");
            Volatile.Write(ref _health, result);
            return result;
        }
    }

    public async Task<FileScanResult> ScanAsync(
        Stream content,
        CancellationToken cancellationToken = default)
    {
        if (!Health.Healthy)
        {
            return new(FileScanStatus.Unavailable, ErrorCode: Health.ErrorCode);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(options.Host, options.Port, timeout.Token);
            await using var network = client.GetStream();
            await network.WriteAsync(Encoding.ASCII.GetBytes("zINSTREAM\0"), timeout.Token);

            var buffer = new byte[64 * 1024];
            var length = new byte[4];
            int read;
            while ((read = await content.ReadAsync(buffer, timeout.Token)) > 0)
            {
                BinaryPrimitives.WriteInt32BigEndian(length, read);
                await network.WriteAsync(length, timeout.Token);
                await network.WriteAsync(buffer.AsMemory(0, read), timeout.Token);
            }
            await network.WriteAsync(new byte[4], timeout.Token);
            await network.FlushAsync(timeout.Token);

            var response = await ReadResponseAsync(network, timeout.Token);
            if (response.EndsWith(" OK", StringComparison.Ordinal))
            {
                return new(FileScanStatus.Clean);
            }
            var found = FoundResponse().Match(response);
            if (found.Success)
            {
                return new(FileScanStatus.Infected, found.Groups["signature"].Value);
            }
            return new(FileScanStatus.Unavailable, ErrorCode: "clamav_protocol_error");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(FileScanStatus.Unavailable, ErrorCode: "clamav_timeout");
        }
        catch (Exception exception) when (exception is SocketException or IOException)
        {
            return new(FileScanStatus.Unavailable, ErrorCode: "clamav_unavailable");
        }
    }

    private async Task<string> ExecuteCommandAsync(
        string command,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
        using var client = new TcpClient();
        await client.ConnectAsync(options.Host, options.Port, timeout.Token);
        await using var network = client.GetStream();
        await network.WriteAsync(Encoding.ASCII.GetBytes(command), timeout.Token);
        await network.FlushAsync(timeout.Token);
        return await ReadResponseAsync(network, timeout.Token);
    }

    private static async Task<string> ReadResponseAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        using var memory = new MemoryStream();
        var buffer = new byte[1024];
        while (memory.Length < 16 * 1024)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }
            var terminator = Array.IndexOf(buffer, (byte)0, 0, read);
            if (terminator >= 0)
            {
                memory.Write(buffer, 0, terminator);
                break;
            }
            memory.Write(buffer, 0, read);
        }
        return Encoding.UTF8.GetString(memory.ToArray()).Trim();
    }

    private static DateTimeOffset? ParseDefinitionTime(string version)
    {
        var parts = version.Split('/', StringSplitOptions.TrimEntries);
        if (parts.Length != 3
            || !parts[0].StartsWith("ClamAV ", StringComparison.Ordinal)
            || parts[0].Length == "ClamAV ".Length
            || parts[1].Length == 0
            || !parts[1].All(char.IsAsciiDigit))
        {
            return null;
        }

        return DateTimeOffset.TryParseExact(
            parts[^1],
            DefinitionTimeFormats,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : null;
    }

    [GeneratedRegex(@"stream:\s+(?<signature>.+)\s+FOUND$", RegexOptions.CultureInvariant)]
    private static partial Regex FoundResponse();
}
