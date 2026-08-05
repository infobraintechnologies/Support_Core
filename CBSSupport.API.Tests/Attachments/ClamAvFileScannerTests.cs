using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using CBSSupport.Shared.Services;

namespace CBSSupport.API.Tests.Attachments;

public sealed class ClamAvFileScannerTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 27, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task CheckHealthAsync_ExactCurrentVersionResponse_ReturnsHealthy()
    {
        var health = await CheckHealthAsync(
            "ClamAV 1.5.3/28078/Fri Jul 31 06:24:10 2026",
            new DateTimeOffset(2026, 8, 1, 6, 24, 9, TimeSpan.Zero));

        Assert.True(health.Healthy);
        Assert.Null(health.ErrorCode);
        Assert.Equal(
            new DateTimeOffset(2026, 7, 31, 6, 24, 10, TimeSpan.Zero),
            health.DefinitionsUpdatedAt);
    }

    [Theory]
    [InlineData("ClamAV 1.5.3/28078/Tue Jul  7 06:24:10 2026", 7)]
    [InlineData("ClamAV 1.5.3/28078/Fri Jul 31 06:24:10 2026", 31)]
    public async Task CheckHealthAsync_CtimeDayFormats_ReturnHealthy(
        string response,
        int expectedDay)
    {
        var health = await CheckHealthAsync(
            response,
            new DateTimeOffset(2026, 7, expectedDay, 7, 0, 0, TimeSpan.Zero));

        Assert.True(health.Healthy);
        Assert.Equal(expectedDay, health.DefinitionsUpdatedAt?.Day);
    }

    [Fact]
    public async Task CheckHealthAsync_DefinitionsOlderThanMaximumAge_ReturnsStale()
    {
        var health = await CheckHealthAsync(
            "ClamAV 1.5.3/28078/Fri Jul 31 06:24:10 2026",
            new DateTimeOffset(2026, 8, 1, 6, 24, 11, TimeSpan.Zero));

        Assert.False(health.Healthy);
        Assert.Equal("clamav_definitions_stale", health.ErrorCode);
    }

    [Theory]
    [InlineData("ClamAV 1.5.3/28078/not-a-date")]
    [InlineData("ClamAV 1.5.3/Fri Jul 31 06:24:10 2026")]
    [InlineData("Other 1.5.3/28078/Fri Jul 31 06:24:10 2026")]
    [InlineData("ClamAV 1.5.3/not-a-revision/Fri Jul 31 06:24:10 2026")]
    public async Task CheckHealthAsync_MalformedVersionResponse_ReturnsUnhealthy(
        string response)
    {
        var health = await CheckHealthAsync(
            response,
            new DateTimeOffset(2026, 8, 1, 6, 0, 0, TimeSpan.Zero));

        Assert.False(health.Healthy);
        Assert.Null(health.DefinitionsUpdatedAt);
        Assert.Equal("clamav_definitions_stale", health.ErrorCode);
    }

    [Fact]
    public async Task CheckHealthAsync_NonEnglishCulture_ParsesDefinitionTimeAsUtc()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ne-NP");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("ne-NP");

            var health = await CheckHealthAsync(
                "ClamAV 1.5.3/28078/Fri Jul 31 06:24:10 2026",
                new DateTimeOffset(2026, 8, 1, 6, 0, 0, TimeSpan.Zero));

            Assert.True(health.Healthy);
            Assert.Equal(TimeSpan.Zero, health.DefinitionsUpdatedAt?.Offset);
            Assert.Equal(
                new DateTimeOffset(2026, 7, 31, 6, 24, 10, TimeSpan.Zero),
                health.DefinitionsUpdatedAt);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    [Fact]
    public async Task CheckHealthAsync_FragmentedVersionResponse_ReadsCompleteResponse()
    {
        var health = await CheckHealthAsync(
            "ClamAV 1.5.3/28078/Fri Jul 31 06:24:10 2026",
            new DateTimeOffset(2026, 8, 1, 6, 0, 0, TimeSpan.Zero),
            fragmentResponse: true);

        Assert.True(health.Healthy);
    }

    [Fact]
    public async Task CheckHealthAsync_InternalTimeout_ReturnsDegradedHealth()
    {
        using var listener = StartListener(out var port);
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            await ReadNullTerminatedAsync(stream);
            _ = await stream.ReadAsync(new byte[1]);
        });
        var scanner = CreateScanner(port, timeoutSeconds: 1);

        var health = await scanner.CheckHealthAsync();

        Assert.False(health.Healthy);
        Assert.Equal("clamav_timeout", health.ErrorCode);
        await server;
    }

    [Fact]
    public async Task ScanAsync_EicarResponse_IsReportedAsInfected()
    {
        using var listener = StartListener(out var port);
        var server = Task.Run(async () =>
        {
            using (var healthClient = await listener.AcceptTcpClientAsync())
            await using (var healthStream = healthClient.GetStream())
            {
                await ReadNullTerminatedAsync(healthStream);
                await healthStream.WriteAsync(
                    Encoding.UTF8.GetBytes(
                        "ClamAV 1.4.2/27667/Mon Jul 27 10:00:00 2026\0"));
            }

            using var scanClient = await listener.AcceptTcpClientAsync();
            await using var scanStream = scanClient.GetStream();
            Assert.Equal("zINSTREAM", await ReadNullTerminatedAsync(scanStream));
            while (true)
            {
                var lengthBytes = await ReadExactlyAsync(scanStream, 4);
                var length = BinaryPrimitives.ReadInt32BigEndian(lengthBytes);
                if (length == 0)
                {
                    break;
                }
                _ = await ReadExactlyAsync(scanStream, length);
            }
            await scanStream.WriteAsync(
                Encoding.UTF8.GetBytes("stream: Win.Test.EICAR_HDB-1 FOUND\0"));
        });
        var scanner = CreateScanner(port, timeoutSeconds: 2);
        var health = await scanner.CheckHealthAsync();
        await using var content = new MemoryStream(
            Encoding.ASCII.GetBytes(
                "X5O!P%@AP[4\\PZX54(P^)7CC)7}$EICAR-STANDARD-ANTIVIRUS-TEST-FILE!$H+H*"));

        var result = await scanner.ScanAsync(content);

        Assert.True(health.Healthy);
        Assert.Equal(FileScanStatus.Infected, result.Status);
        Assert.Equal("Win.Test.EICAR_HDB-1", result.Signature);
        await server;
    }

    private static async Task<FileScannerHealth> CheckHealthAsync(
        string response,
        DateTimeOffset now,
        bool fragmentResponse = false)
    {
        using var listener = StartListener(out var port);
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            Assert.Equal("zVERSION", await ReadNullTerminatedAsync(stream));
            var bytes = Encoding.UTF8.GetBytes(response + '\0');
            if (fragmentResponse)
            {
                var split = bytes.Length / 2;
                await stream.WriteAsync(bytes.AsMemory(0, split));
                await stream.FlushAsync();
                await Task.Delay(50);
                await stream.WriteAsync(bytes.AsMemory(split));
            }
            else
            {
                await stream.WriteAsync(bytes);
            }
        });
        var scanner = CreateScanner(port, timeoutSeconds: 2, now);

        var health = await scanner.CheckHealthAsync();

        await server;
        return health;
    }

    private static ClamAvFileScanner CreateScanner(
        int port,
        int timeoutSeconds,
        DateTimeOffset? now = null) =>
        new(
            new AttachmentScanningOptions
            {
                Host = IPAddress.Loopback.ToString(),
                Port = port,
                TimeoutSeconds = timeoutSeconds,
                MaximumDefinitionAgeHours = 24
            },
            new FixedTimeProvider(now ?? Now));

    private static TcpListener StartListener(out int port)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        port = ((IPEndPoint)listener.LocalEndpoint).Port;
        return listener;
    }

    private static async Task<string> ReadNullTerminatedAsync(Stream stream)
    {
        using var content = new MemoryStream();
        var buffer = new byte[1];
        while (await stream.ReadAsync(buffer) == 1 && buffer[0] != 0)
        {
            content.WriteByte(buffer[0]);
        }
        return Encoding.ASCII.GetString(content.ToArray());
    }

    private static async Task<byte[]> ReadExactlyAsync(Stream stream, int length)
    {
        var result = new byte[length];
        var offset = 0;
        while (offset < length)
        {
            var read = await stream.ReadAsync(result.AsMemory(offset, length - offset));
            if (read == 0)
            {
                throw new EndOfStreamException();
            }
            offset += read;
        }
        return result;
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
