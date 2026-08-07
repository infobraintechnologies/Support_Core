using System.Security.Cryptography.X509Certificates;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Runtime.Versioning;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Hosting;

namespace CBSSupport.API.Configuration;

public sealed class DataProtectionOptions
{
    public const string SectionName = "DataProtection";
    public const string DefaultApplicationName = "CBSSupport";

    public string ApplicationName { get; set; } = DefaultApplicationName;

    public string? KeyRingPath { get; set; }

    public string? AtRestProtection { get; set; }

    public string? CertificatePath { get; set; }

    public string? CertificatePassword { get; set; }

    public string? CertificateThumbprint { get; set; }

    public string CertificateStoreName { get; set; } = StoreName.My.ToString();

    public string CertificateStoreLocation { get; set; } = StoreLocation.CurrentUser.ToString();

    public bool EnforcePrivateKeyRingPermissions { get; set; } = true;

    public bool IsDevelopmentOrTesting(IHostEnvironment environment) =>
        environment.IsDevelopment() || environment.IsEnvironment("Testing");

    public string ResolveKeyRingPath(IHostEnvironment environment)
    {
        if (!string.IsNullOrWhiteSpace(KeyRingPath))
        {
            return Path.GetFullPath(KeyRingPath);
        }

        if (environment.IsDevelopment())
        {
            var localApplicationData = Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(
                string.IsNullOrWhiteSpace(localApplicationData)
                    ? AppContext.BaseDirectory
                    : localApplicationData,
                "CBSSupport",
                "DataProtection-Keys");
        }

        if (environment.IsEnvironment("Testing"))
        {
            return Path.Combine(
                Path.GetTempPath(),
                "CBSSupport",
                "DataProtection-Keys");
        }

        throw new InvalidOperationException(
            "Production Data Protection requires DataProtection:KeyRingPath to point to a durable shared location. " +
            "Ephemeral or process-local keys are not supported.");
    }

    public string ResolveAtRestProtection(IHostEnvironment environment)
    {
        if (!string.IsNullOrWhiteSpace(AtRestProtection))
        {
            return AtRestProtection.Trim();
        }

        return IsDevelopmentOrTesting(environment)
            ? "None"
            : "Certificate";
    }

    public void Validate(
        IHostEnvironment environment,
        string resolvedKeyRingPath)
    {
        if (string.IsNullOrWhiteSpace(ApplicationName))
        {
            throw new InvalidOperationException(
                "DataProtection:ApplicationName must be a non-empty stable application name.");
        }

        var protection = ResolveAtRestProtection(environment);
        if (!protection.Equals("None", StringComparison.OrdinalIgnoreCase)
            && !protection.Equals("Certificate", StringComparison.OrdinalIgnoreCase)
            && !protection.Equals("Platform", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "DataProtection:AtRestProtection must be Certificate, Platform, or None.");
        }

        if (IsDevelopmentOrTesting(environment))
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(KeyRingPath) && !Path.IsPathRooted(KeyRingPath))
        {
            throw new InvalidOperationException(
                "Production DataProtection:KeyRingPath must be an absolute path.");
        }

        if (!Path.IsPathRooted(resolvedKeyRingPath)
            || resolvedKeyRingPath.StartsWith(
                Path.GetFullPath(Path.GetTempPath()),
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Production DataProtection:KeyRingPath must be an absolute durable path outside the system temporary directory.");
        }

        if (protection.Equals("None", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Production Data Protection keys must be protected at rest. Configure a certificate or platform protection.");
        }

        if (protection.Equals("Certificate", StringComparison.OrdinalIgnoreCase))
        {
            ValidateCertificateConfiguration();
        }

        if (protection.Equals("Platform", StringComparison.OrdinalIgnoreCase)
            && !OperatingSystem.IsWindows())
        {
            throw new InvalidOperationException(
                "DataProtection:AtRestProtection=Platform is only supported on Windows hosts. Configure certificate protection for this deployment.");
        }
    }

    public X509Certificate2 LoadCertificate()
    {
        ValidateCertificateConfiguration();

        if (!string.IsNullOrWhiteSpace(CertificatePath))
        {
#pragma warning disable SYSLIB0057 // X509CertificateLoader is not available on the .NET 8 compatibility floor.
            var loadedCertificate = new X509Certificate2(
                Path.GetFullPath(CertificatePath),
                CertificatePassword,
                X509KeyStorageFlags.EphemeralKeySet);
#pragma warning restore SYSLIB0057
            EnsurePrivateKey(loadedCertificate);
            return loadedCertificate;
        }

        if (!Enum.TryParse<StoreName>(CertificateStoreName, ignoreCase: true, out var storeName)
            || !Enum.TryParse<StoreLocation>(CertificateStoreLocation, ignoreCase: true, out var storeLocation))
        {
            throw new InvalidOperationException(
                "DataProtection certificate store configuration is invalid.");
        }

        using var store = new X509Store(storeName, storeLocation);
        store.Open(OpenFlags.ReadOnly);
        var certificate = store.Certificates
            .Find(
                X509FindType.FindByThumbprint,
                CertificateThumbprint!.Replace(" ", string.Empty, StringComparison.Ordinal),
                validOnly: false)
            .OfType<X509Certificate2>()
            .SingleOrDefault();

        if (certificate is null)
        {
            throw new InvalidOperationException(
                "The configured Data Protection certificate was not found in the certificate store.");
        }

        EnsurePrivateKey(certificate);
        return certificate;
    }

    private void ValidateCertificateConfiguration()
    {
        var hasPath = !string.IsNullOrWhiteSpace(CertificatePath);
        var hasThumbprint = !string.IsNullOrWhiteSpace(CertificateThumbprint);
        if (hasPath == hasThumbprint)
        {
            throw new InvalidOperationException(
                "Data Protection certificate protection requires exactly one of CertificatePath or CertificateThumbprint.");
        }

        if (hasPath && !Path.IsPathRooted(CertificatePath!))
        {
            throw new InvalidOperationException(
                "DataProtection:CertificatePath must be an absolute path.");
        }
    }

    private static void EnsurePrivateKey(X509Certificate2 certificate)
    {
        if (!certificate.HasPrivateKey)
        {
            certificate.Dispose();
            throw new InvalidOperationException(
                "The configured Data Protection certificate must include a private key.");
        }
    }
}

public sealed class DataProtectionStartupValidator(
    IHostEnvironment environment,
    DataProtectionOptions options,
    IDataProtectionProvider dataProtectionProvider,
    ILogger<DataProtectionStartupValidator> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!options.IsDevelopmentOrTesting(environment)
            && dataProtectionProvider is EphemeralDataProtectionProvider)
        {
            throw new InvalidOperationException(
                "Unsafe ephemeral ASP.NET Core Data Protection keys were registered in a production environment. " +
                "Configure the durable shared key ring before starting the application.");
        }

        logger.LogInformation(
            "ASP.NET Core Data Protection is configured with application name {ApplicationName} and key ring path {KeyRingPath}.",
            options.ApplicationName,
            options.KeyRingPath);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

public static class DataProtectionServiceCollectionExtensions
{
    public static IServiceCollection AddCbsDataProtection(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        var options = configuration
            .GetSection(DataProtectionOptions.SectionName)
            .Get<DataProtectionOptions>() ?? new DataProtectionOptions();
        var keyRingPath = options.ResolveKeyRingPath(environment);
        options.KeyRingPath = keyRingPath;
        options.Validate(environment, keyRingPath);
        DataProtectionKeyRingPermissions.Ensure(keyRingPath, environment, options.EnforcePrivateKeyRingPermissions);

        var dataProtectionBuilder = services
            .AddDataProtection()
            .SetApplicationName(options.ApplicationName)
            .PersistKeysToFileSystem(new DirectoryInfo(keyRingPath));

        var protection = options.ResolveAtRestProtection(environment);
        if (protection.Equals("Certificate", StringComparison.OrdinalIgnoreCase))
        {
            dataProtectionBuilder.ProtectKeysWithCertificate(options.LoadCertificate());
        }
        else if (protection.Equals("Platform", StringComparison.OrdinalIgnoreCase))
        {
            if (!OperatingSystem.IsWindows())
            {
                throw new InvalidOperationException(
                    "DataProtection:AtRestProtection=Platform is only supported on Windows hosts.");
            }

            ConfigureWindowsPlatformProtection(dataProtectionBuilder);
        }

        services.AddSingleton(options);
        services.AddHostedService<DataProtectionStartupValidator>();

        return services;
    }

    [SupportedOSPlatform("windows")]
    private static void ConfigureWindowsPlatformProtection(IDataProtectionBuilder dataProtectionBuilder)
    {
        dataProtectionBuilder.ProtectKeysWithDpapi();
    }
}

internal static class DataProtectionKeyRingPermissions
{
    public static void Ensure(
        string keyRingPath,
        IHostEnvironment environment,
        bool enforcePrivatePermissions)
    {
        var existed = Directory.Exists(keyRingPath);
        Directory.CreateDirectory(keyRingPath);

        if (!enforcePrivatePermissions || environment.IsDevelopment() || environment.IsEnvironment("Testing"))
        {
            return;
        }

        if (OperatingSystem.IsWindows())
        {
            EnsureWindowsPrivatePermissions(keyRingPath);
            return;
        }

        if (!existed)
        {
            File.SetUnixFileMode(
                keyRingPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var mode = File.GetUnixFileMode(keyRingPath);
        if ((mode & (UnixFileMode.GroupRead
            | UnixFileMode.GroupWrite
            | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead
            | UnixFileMode.OtherWrite
            | UnixFileMode.OtherExecute)) != 0)
        {
            throw new InvalidOperationException(
                $"Production Data Protection key ring '{keyRingPath}' must not be readable or writable by group/other users. Set its Unix mode to 0700.");
        }
    }

    [SupportedOSPlatform("windows")]
    private static void EnsureWindowsPrivatePermissions(string keyRingPath)
    {
        var security = new DirectoryInfo(keyRingPath)
            .GetAccessControl(AccessControlSections.Access);
        foreach (FileSystemAccessRule rule in security.GetAccessRules(
            includeExplicit: true,
            includeInherited: true,
            targetType: typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow
                || !IsBroadWindowsPrincipal((SecurityIdentifier)rule.IdentityReference))
            {
                continue;
            }

            var sensitiveRights = FileSystemRights.Read
                | FileSystemRights.Write
                | FileSystemRights.Modify
                | FileSystemRights.FullControl;
            if ((rule.FileSystemRights & sensitiveRights) != 0)
            {
                throw new InvalidOperationException(
                    $"Production Data Protection key ring '{keyRingPath}' grants broad Windows access to '{rule.IdentityReference}'. Restrict its ACL to the application identity and required system administrators.");
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool IsBroadWindowsPrincipal(SecurityIdentifier sid) =>
        sid.IsWellKnown(WellKnownSidType.WorldSid)
        || sid.IsWellKnown(WellKnownSidType.AuthenticatedUserSid)
        || sid.IsWellKnown(WellKnownSidType.BuiltinUsersSid)
        || sid.IsWellKnown(WellKnownSidType.BuiltinGuestsSid);
}
