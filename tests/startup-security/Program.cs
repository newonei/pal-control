using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using PalControl.ControlApi.Infrastructure;

var root = Path.Combine(Path.GetTempPath(), $"pal-control-startup-security-{Guid.NewGuid():N}");
Directory.CreateDirectory(root);
try
{
    if (OperatingSystem.IsWindows())
    {
        ProtectWindowsPath(root, directory: true);
    }

    TestValidStrictConfiguration(root);
    TestNonLoopbackListenerRejected(root);
    TestTrustedProxyAllowsNonLoopbackListener(root);
    TestMissingSecretRejected(root);
    TestWeakSecretAclRejected(root);
    TestDisabledAdaptersAreConditional(root);
    TestDevelopmentRconSettlementGuard(root);
    Console.WriteLine("PASS: startup security validation");
}
finally
{
    Directory.Delete(root, recursive: true);
}

static void TestValidStrictConfiguration(string root)
{
    var workspace = CreateWorkspace(root, "valid");
    var result = Validate(workspace);
    Assert(result.Succeeded, JoinFailures("A valid strict configuration was rejected.", result));
}

static void TestNonLoopbackListenerRejected(string root)
{
    var workspace = CreateWorkspace(root, "listener", new Dictionary<string, string?>
    {
        ["Urls"] = "http://0.0.0.0:5180"
    });
    var result = Validate(workspace);
    Assert(result.Failed && result.Failures.Any(failure =>
            failure.Contains("non-loopback listener", StringComparison.OrdinalIgnoreCase)),
        "A non-loopback listener was accepted without an explicit trusted reverse proxy.");
}

static void TestTrustedProxyAllowsNonLoopbackListener(string root)
{
    var workspace = CreateWorkspace(root, "trusted-listener", new Dictionary<string, string?>
    {
        ["Urls"] = "http://0.0.0.0:5180",
        ["PlayerPortal:Enabled"] = "true",
        ["PlayerPortal:CookieSecure"] = "true"
    });
    var result = Validate(workspace, new StartupSecurityValidationOptions
    {
        Strict = true,
        AllowNonLoopbackListenerBehindTrustedProxy = true,
        TrustedProxyAddresses = ["127.0.0.1"],
        LogDirectory = Path.Combine(workspace.ContentRoot, "logs")
    });
    Assert(result.Succeeded, JoinFailures(
        "An explicitly trusted reverse-proxy listener was rejected.",
        result));
}

static void TestMissingSecretRejected(string root)
{
    var workspace = CreateWorkspace(root, "missing-secret", new Dictionary<string, string?>
    {
        ["ExtractionMode:Rcon:Enabled"] = "true",
        ["ExtractionMode:Rcon:PasswordFile"] = Path.Combine(root, "does-not-exist.secret")
    });
    var result = Validate(workspace);
    Assert(result.Failed && result.Failures.Any(failure =>
            failure.Contains("existing, non-empty secret file", StringComparison.Ordinal)),
        "An enabled RCON adapter accepted a missing password file.");
}

static void TestWeakSecretAclRejected(string root)
{
    if (!OperatingSystem.IsWindows())
    {
        return;
    }

    var workspace = CreateWorkspace(root, "weak-acl");
    var secretDirectory = Path.Combine(workspace.ContentRoot, "secrets");
    Directory.CreateDirectory(secretDirectory);
    var tokenFile = Path.Combine(secretDirectory, "paldefender.json");
    File.WriteAllText(tokenFile, "{\"Token\":\"contract-test-token\"}");
    ProtectWindowsPath(tokenFile, directory: false);
    RunIcacls(tokenFile, "/grant", "*S-1-1-0:(R)");

    workspace.Settings["Palworld:PalDefenderRestApi:Enabled"] = "true";
    workspace.Settings["Palworld:PalDefenderRestApi:TokenFile"] = tokenFile;
    var result = Validate(workspace);
    Assert(result.Failed && result.Failures.Any(failure =>
            failure.Contains("ACL grants access", StringComparison.Ordinal)),
        "A PalDefender token file readable by Everyone passed strict startup validation.");
}

static void TestDisabledAdaptersAreConditional(string root)
{
    var workspace = CreateWorkspace(root, "disabled-adapters", new Dictionary<string, string?>
    {
        ["ExtractionMode:Rcon:Enabled"] = "false",
        ["ExtractionMode:Rcon:PasswordFile"] = "relative/missing.secret",
        ["Palworld:PalDefenderRestApi:Enabled"] = "false",
        ["Palworld:PalDefenderRestApi:TokenFile"] = "relative/missing.json",
        ["Palworld:Bridge:PipeName"] = "",
        ["Palworld:Bridge:ConnectTimeoutSeconds"] = "0",
        ["Palworld:Bridge:CommandTimeoutSeconds"] = "0",
        ["Palworld:Bridge:MaxFrameBytes"] = "1",
        ["ExtractionMode:Safety:RequireNativeForPurchase"] = "false",
        ["ExtractionMode:Safety:RequireNativeForResourceExchange"] = "false"
    });
    var result = Validate(workspace);
    Assert(result.Succeeded, JoinFailures(
        "Disabled RCON, PalDefender, or Native adapters were treated as enabled.",
        result));

    workspace.Settings["ExtractionMode:Safety:RequireNativeForPurchase"] = "true";
    result = Validate(workspace);
    Assert(result.Failed && result.Failures.Any(failure =>
            failure.Contains("Native adapter", StringComparison.Ordinal)),
        "Invalid Native bridge settings were accepted when the Native adapter became required.");
}

static void TestDevelopmentRconSettlementGuard(string root)
{
    var workspace = CreateWorkspace(root, "development-rcon-settlement",
        new Dictionary<string, string?>
        {
            ["Security:DevelopmentMode"] = "true",
            ["PlayerPortal:PublicSteam"] = "false",
            ["ExtractionMode:Rcon:Enabled"] = "true",
            ["ExtractionMode:Rcon:AllowDevelopmentSettlement"] = "true",
            ["ExtractionMode:Rcon:Password"] = "diagnostic-test-password",
            ["ExtractionMode:Safety:RequireNativeForResourceExchange"] = "false"
        });

    var result = Validate(workspace, environmentName: "Development");
    Assert(result.Succeeded, JoinFailures(
        "The fully isolated Development RCON diagnostic was rejected.",
        result));

    result = Validate(workspace, environmentName: "Production");
    AssertPolicyRejected(result, "host environment must be Development",
        "Production accepted the RCON settlement diagnostic.");

    workspace.Settings["Security:DevelopmentMode"] = "false";
    result = Validate(workspace, environmentName: "Development");
    AssertPolicyRejected(result, "Security:DevelopmentMode must be true",
        "Implicit Development mode accepted the RCON settlement diagnostic.");
    workspace.Settings["Security:DevelopmentMode"] = "true";

    workspace.Settings["PlayerPortal:PublicSteam"] = "true";
    result = Validate(workspace, environmentName: "Development");
    AssertPolicyRejected(result, "PlayerPortal:PublicSteam must be false",
        "A public Steam portal accepted the RCON settlement diagnostic.");
    workspace.Settings["PlayerPortal:PublicSteam"] = "false";

    workspace.Settings["ExtractionMode:Rcon:Enabled"] = "false";
    result = Validate(workspace, environmentName: "Development");
    AssertPolicyRejected(result, "ExtractionMode:Rcon:Enabled must be true",
        "A disabled RCON adapter accepted the settlement diagnostic switch.");
    workspace.Settings["ExtractionMode:Rcon:Enabled"] = "true";

    workspace.Settings["ExtractionMode:Safety:RequireNativeForResourceExchange"] = "true";
    result = Validate(workspace, environmentName: "Development");
    AssertPolicyRejected(
        result,
        "ExtractionMode:Safety:RequireNativeForResourceExchange must be false",
        "Native-required settlement accepted the RCON diagnostic switch.");
}

static void AssertPolicyRejected(
    ValidateOptionsResult result,
    string expectedFailure,
    string message) =>
    Assert(result.Failed && result.Failures.Any(failure =>
            failure.Contains(expectedFailure, StringComparison.Ordinal)),
        JoinFailures(message, result));

static TestWorkspace CreateWorkspace(
    string root,
    string name,
    IReadOnlyDictionary<string, string?>? overrides = null)
{
    var contentRoot = Path.Combine(root, name);
    var paths = new[]
    {
        contentRoot,
        Path.Combine(contentRoot, "data"),
        Path.Combine(contentRoot, "data", "extraction"),
        Path.Combine(contentRoot, "backups", "save"),
        Path.Combine(contentRoot, "backups", "economy"),
        Path.Combine(contentRoot, "staging", "economy"),
        Path.Combine(contentRoot, "logs"),
        Path.Combine(contentRoot, "palserver"),
        Path.Combine(contentRoot, "wwwroot")
    };
    foreach (var path in paths)
    {
        Directory.CreateDirectory(path);
        if (OperatingSystem.IsWindows())
        {
            ProtectWindowsPath(path, directory: true);
        }
    }

    var settings = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
    {
        ["Urls"] = "http://127.0.0.1:5180",
        ["Security:DevelopmentMode"] = "false",
        ["Palworld:InstallRoot"] = Path.Combine(contentRoot, "palserver"),
        ["Palworld:OfficialRestApi:BaseUrl"] = "http://127.0.0.1:8212/v1/api/",
        ["Palworld:OfficialRestApi:Username"] = "admin",
        ["Palworld:OfficialRestApi:TimeoutSeconds"] = "3",
        ["Palworld:PalDefenderRestApi:Enabled"] = "false",
        ["Palworld:PalDefenderRestApi:BaseUrl"] = "http://127.0.0.1:17993/v1/pdapi/",
        ["Palworld:PalDefenderRestApi:Origin"] = "http://127.0.0.1:5180",
        ["Palworld:PalDefenderRestApi:TimeoutSeconds"] = "7",
        ["CommandPersistence:DataDirectory"] = Path.Combine(contentRoot, "data"),
        ["ExtractionMode:Persistence:DataDirectory"] = Path.Combine(contentRoot, "data", "extraction"),
        ["SaveManagement:BackupRoot"] = Path.Combine(contentRoot, "backups", "save"),
        ["ExtractionMode:Continuity:BackupRoot"] = Path.Combine(contentRoot, "backups", "economy"),
        ["ExtractionMode:Continuity:StagingRoot"] = Path.Combine(contentRoot, "staging", "economy"),
        ["ExtractionMode:Rcon:Enabled"] = "false",
        ["ExtractionMode:Rcon:AllowDevelopmentSettlement"] = "false",
        ["ExtractionMode:Safety:RequireNativeForPurchase"] = "false",
        ["ExtractionMode:Safety:RequireNativeForResourceExchange"] = "false",
        ["PlayerPortal:Enabled"] = "false",
        ["PlayerPortal:PublicSteam"] = "false",
        ["PlayerPortal:CookieSecure"] = "true"
    };
    if (overrides is not null)
    {
        foreach (var pair in overrides)
        {
            settings[pair.Key] = pair.Value;
        }
    }
    return new TestWorkspace(contentRoot, settings);
}

static ValidateOptionsResult Validate(
    TestWorkspace workspace,
    StartupSecurityValidationOptions? options = null,
    string environmentName = "Production")
{
    var configuration = new ConfigurationBuilder()
        .AddInMemoryCollection(workspace.Settings)
        .Build();
    var environment = new TestWebHostEnvironment(workspace.ContentRoot)
    {
        EnvironmentName = environmentName
    };
    var validator = new StartupSecurityValidator(configuration, environment);
    return validator.Validate(null, options ?? new StartupSecurityValidationOptions
    {
        Strict = true,
        LogDirectory = Path.Combine(workspace.ContentRoot, "logs")
    });
}

static string JoinFailures(string message, ValidateOptionsResult result) =>
    result.Failures is null ? message : $"{message} {string.Join(" | ", result.Failures)}";

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

[SupportedOSPlatform("windows")]
static void ProtectWindowsPath(string path, bool directory)
{
    var identity = WindowsIdentity.GetCurrent().Name;
    var inheritance = directory ? "(OI)(CI)" : string.Empty;
    RunIcacls(
        path,
        "/inheritance:r",
        "/grant:r",
        $"{identity}:{inheritance}(F)",
        $"*S-1-5-18:{inheritance}(F)");
}

[SupportedOSPlatform("windows")]
static void RunIcacls(string path, params string[] arguments)
{
    var startInfo = new ProcessStartInfo("icacls.exe")
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true
    };
    startInfo.ArgumentList.Add(path);
    foreach (var argument in arguments)
    {
        startInfo.ArgumentList.Add(argument);
    }
    using var process = Process.Start(startInfo) ??
        throw new InvalidOperationException("Could not start icacls.exe.");
    var standardOutput = process.StandardOutput.ReadToEnd();
    var standardError = process.StandardError.ReadToEnd();
    process.WaitForExit();
    if (process.ExitCode != 0)
    {
        throw new InvalidOperationException(
            $"icacls.exe failed ({process.ExitCode}): {standardError} {standardOutput}");
    }
}

internal sealed record TestWorkspace(
    string ContentRoot,
    Dictionary<string, string?> Settings);

internal sealed class TestWebHostEnvironment : IWebHostEnvironment
{
    public TestWebHostEnvironment(string contentRoot)
    {
        ContentRootPath = contentRoot;
        WebRootPath = Path.Combine(contentRoot, "wwwroot");
        ContentRootFileProvider = new PhysicalFileProvider(contentRoot);
        WebRootFileProvider = new PhysicalFileProvider(WebRootPath);
    }

    public string ApplicationName { get; set; } = "PalControl.StartupSecurity.ContractTests";
    public IFileProvider WebRootFileProvider { get; set; }
    public string WebRootPath { get; set; }
    public string EnvironmentName { get; set; } = "Production";
    public string ContentRootPath { get; set; }
    public IFileProvider ContentRootFileProvider { get; set; }
}
