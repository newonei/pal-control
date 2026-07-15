using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace PalControl.ControlApi.Infrastructure;

public static class AdminAuthenticationDefaults
{
    public const string Scheme = "PalControlAdminApiKey";
}

public static class AdminRoles
{
    public const string Viewer = "Viewer";
    public const string Operator = "Operator";
    public const string EconomyAdmin = "EconomyAdmin";
    public const string SeasonAdmin = "SeasonAdmin";
    public const string Owner = "Owner";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(
        [Viewer, Operator, EconomyAdmin, SeasonAdmin, Owner],
        StringComparer.Ordinal);

    public static IReadOnlySet<string> Expand(IEnumerable<string> configuredRoles)
    {
        var expanded = new HashSet<string>(StringComparer.Ordinal);
        foreach (var role in configuredRoles)
        {
            switch (role)
            {
                case Owner:
                    expanded.UnionWith(All);
                    break;
                case EconomyAdmin:
                    expanded.Add(Viewer);
                    expanded.Add(Operator);
                    expanded.Add(EconomyAdmin);
                    break;
                case SeasonAdmin:
                    expanded.Add(Viewer);
                    expanded.Add(Operator);
                    expanded.Add(SeasonAdmin);
                    break;
                case Operator:
                    expanded.Add(Viewer);
                    expanded.Add(Operator);
                    break;
                case Viewer:
                    expanded.Add(Viewer);
                    break;
            }
        }
        return expanded;
    }
}

public static class AdminPolicies
{
    public const string ApiAccess = "admin.api-access";
    public const string Viewer = "admin.viewer";
    public const string Operator = "admin.operator";
    public const string EconomyAdmin = "admin.economy";
    public const string SeasonAdmin = "admin.season";
    public const string Owner = "admin.owner";
    public const string EconomyHighRisk = "admin.economy.high-risk";
    public const string SeasonHighRisk = "admin.season.high-risk";
}

public sealed class AdminApiAccessRequirement : IAuthorizationRequirement;

public sealed class AdminApiAccessAuthorizationHandler
    : AuthorizationHandler<AdminApiAccessRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        AdminApiAccessRequirement requirement)
    {
        var httpContext = context.Resource as HttpContext;
        var readOnly = httpContext is not null &&
            (HttpMethods.IsGet(httpContext.Request.Method) ||
             HttpMethods.IsHead(httpContext.Request.Method) ||
             HttpMethods.IsOptions(httpContext.Request.Method));
        var requiredRole = readOnly ? AdminRoles.Viewer : AdminRoles.Operator;
        if (context.User.IsInRole(requiredRole))
        {
            context.Succeed(requirement);
        }
        return Task.CompletedTask;
    }
}

public sealed class AdminAuthenticationOptions : AuthenticationSchemeOptions
{
    public bool Enabled { get; init; } = true;
    public string ApiKeyHeaderName { get; init; } = "X-Pal-Admin-Key";
    public string TotpHeaderName { get; init; } = "X-Pal-Admin-Totp";
    public bool EnableLoopbackDevelopmentPrincipal { get; init; }
    public string DevelopmentPrincipalSubject { get; init; } = string.Empty;
    public IReadOnlyList<AdminPrincipalOptions> Principals { get; init; } = [];

    public bool IsValid(out string error)
    {
        if (!Enabled)
        {
            error = "Administrator authentication cannot be disabled.";
            return false;
        }
        if (!IsSafeHeaderName(ApiKeyHeaderName) || !IsSafeHeaderName(TotpHeaderName) ||
            string.Equals(ApiKeyHeaderName, TotpHeaderName, StringComparison.OrdinalIgnoreCase))
        {
            error = "Administrator API-key and TOTP headers must be distinct valid HTTP header names.";
            return false;
        }
        if (Principals.Count == 0)
        {
            error = "At least one enabled administrator principal is required.";
            return false;
        }

        var subjects = new HashSet<string>(StringComparer.Ordinal);
        var keyHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var principal in Principals)
        {
            if (!principal.Enabled)
            {
                continue;
            }
            if (!IsSafeSubject(principal.Subject) || !subjects.Add(principal.Subject))
            {
                error = "Enabled administrator subjects must be unique, stable, and contain only safe characters.";
                return false;
            }
            if (!IsSha256(principal.ApiKeySha256) || !keyHashes.Add(principal.ApiKeySha256))
            {
                error = "Enabled administrator API-key SHA-256 values must be unique lowercase or uppercase hexadecimal digests.";
                return false;
            }
            if (principal.Roles.Count == 0 || principal.Roles.Any(role => !AdminRoles.All.Contains(role)))
            {
                error = $"Administrator '{principal.Subject}' contains no role or an unsupported role.";
                return false;
            }
            if (AdminRoles.Expand(principal.Roles).Overlaps(
                    [AdminRoles.EconomyAdmin, AdminRoles.SeasonAdmin, AdminRoles.Owner]) &&
                !Totp.TryDecodeSecret(principal.TotpSecretBase32, out _))
            {
                error = $"Privileged administrator '{principal.Subject}' requires a valid Base32 TOTP secret.";
                return false;
            }
        }
        if (subjects.Count == 0)
        {
            error = "At least one enabled administrator principal is required.";
            return false;
        }
        if (EnableLoopbackDevelopmentPrincipal &&
            (string.IsNullOrWhiteSpace(DevelopmentPrincipalSubject) ||
             FindBySubject(DevelopmentPrincipalSubject) is null))
        {
            error = "The loopback development principal must reference one enabled configured subject.";
            return false;
        }
        error = string.Empty;
        return true;
    }

    public AdminPrincipalOptions? FindBySubject(string subject) => Principals.SingleOrDefault(
        principal => principal.Enabled &&
            string.Equals(principal.Subject, subject, StringComparison.Ordinal));

    private static bool IsSafeHeaderName(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 64 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character == '-');

    private static bool IsSafeSubject(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 128 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or ':' or '@');

    private static bool IsSha256(string value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);
}

public sealed class AdminPrincipalOptions
{
    public required string Subject { get; init; }
    public required string ApiKeySha256 { get; init; }
    public IReadOnlyList<string> Roles { get; init; } = [];
    public string TotpSecretBase32 { get; init; } = string.Empty;
    public bool Enabled { get; init; } = true;
}

public sealed class AdminApiKeyAuthenticationHandler(
    IOptionsMonitor<AdminAuthenticationOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AdminAuthenticationOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Options.Enabled)
        {
            return Task.FromResult(AuthenticateResult.Fail(
                "Administrator authentication is disabled by an invalid configuration."));
        }
        var suppliedValues = Request.Headers[Options.ApiKeyHeaderName];
        AdminPrincipalOptions? matched = null;
        var authenticationMethod = "api_key";
        if (suppliedValues.Count == 0 &&
            Options.EnableLoopbackDevelopmentPrincipal &&
            Context.Connection.RemoteIpAddress is { } remoteAddress &&
            IPAddress.IsLoopback(remoteAddress))
        {
            matched = Options.FindBySubject(Options.DevelopmentPrincipalSubject);
            authenticationMethod = "development_loopback";
        }
        else if (suppliedValues.Count != 1 ||
                 suppliedValues[0] is not { Length: >= 24 and <= 512 })
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }
        if (matched is null && suppliedValues.Count == 1 && suppliedValues[0] is { } supplied)
        {
            var suppliedHash = SHA256.HashData(Encoding.UTF8.GetBytes(supplied));
            foreach (var candidate in Options.Principals.Where(principal => principal.Enabled))
            {
                byte[] expectedHash;
                try
                {
                    expectedHash = Convert.FromHexString(candidate.ApiKeySha256);
                }
                catch (FormatException)
                {
                    continue;
                }
                if (expectedHash.Length == suppliedHash.Length &&
                    CryptographicOperations.FixedTimeEquals(suppliedHash, expectedHash))
                {
                    matched = candidate;
                }
                CryptographicOperations.ZeroMemory(expectedHash);
            }
            CryptographicOperations.ZeroMemory(suppliedHash);
        }
        if (matched is null)
        {
            return Task.FromResult(AuthenticateResult.Fail("The administrator credential is invalid."));
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, matched.Subject),
            new(ClaimTypes.Name, matched.Subject),
            new("pal_control_authentication_method", authenticationMethod)
        };
        claims.AddRange(AdminRoles.Expand(matched.Roles).Select(role => new Claim(ClaimTypes.Role, role)));
        var identity = new ClaimsIdentity(claims, Scheme.Name, ClaimTypes.Name, ClaimTypes.Role);
        var principal = new ClaimsPrincipal(identity);
        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(principal, Scheme.Name)));
    }
}

public sealed class AdminTotpRequirement : IAuthorizationRequirement;

public sealed class AdminReasonRequirement : IAuthorizationRequirement;

public sealed class AdminTotpAuthorizationHandler(
    IHttpContextAccessor httpContextAccessor,
    IOptionsMonitor<AdminAuthenticationOptions> options,
    TimeProvider timeProvider)
    : AuthorizationHandler<AdminTotpRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        AdminTotpRequirement requirement)
    {
        var httpContext = httpContextAccessor.HttpContext;
        var subject = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        var configured = options.Get(AdminAuthenticationDefaults.Scheme);
        var principal = subject is null ? null : configured.FindBySubject(subject);
        var values = httpContext is null
            ? Microsoft.Extensions.Primitives.StringValues.Empty
            : httpContext.Request.Headers[configured.TotpHeaderName];
        if (principal is not null && values.Count == 1 && values[0] is { Length: 6 } code &&
            Totp.Validate(principal.TotpSecretBase32, code, timeProvider.GetUtcNow()))
        {
            context.Succeed(requirement);
        }
        return Task.CompletedTask;
    }
}

public sealed class AdminReasonAuthorizationHandler(IHttpContextAccessor httpContextAccessor)
    : AuthorizationHandler<AdminReasonRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        AdminReasonRequirement requirement)
    {
        var values = httpContextAccessor.HttpContext?.Request.Headers["X-Pal-Admin-Reason"];
        if (values?.Count == 1 && values.Value[0] is { } supplied)
        {
            var reason = supplied.Trim();
            if (reason.Length is >= 3 and <= 512 && !reason.Any(char.IsControl))
            {
                context.Succeed(requirement);
            }
        }
        return Task.CompletedTask;
    }
}

public static class AdminIdentity
{
    public static string RequireSubject(HttpContext context) =>
        context.User.FindFirstValue(ClaimTypes.NameIdentifier) ??
        throw new InvalidOperationException("An authenticated administrator subject is required.");

    public static string[] Roles(HttpContext context) => context.User
        .FindAll(ClaimTypes.Role)
        .Select(claim => claim.Value)
        .Distinct(StringComparer.Ordinal)
        .OrderBy(role => role, StringComparer.Ordinal)
        .ToArray();
}

internal static partial class Totp
{
    private const int TimeStepSeconds = 30;

    public static bool Validate(string secretBase32, string suppliedCode, DateTimeOffset now)
    {
        if (!SixDigits().IsMatch(suppliedCode) || !TryDecodeSecret(secretBase32, out var secret))
        {
            return false;
        }
        try
        {
            var currentStep = now.ToUnixTimeSeconds() / TimeStepSeconds;
            for (var offset = -1; offset <= 1; offset++)
            {
                var expected = Compute(secret, currentStep + offset);
                if (CryptographicOperations.FixedTimeEquals(
                        Encoding.ASCII.GetBytes(expected),
                        Encoding.ASCII.GetBytes(suppliedCode)))
                {
                    return true;
                }
            }
            return false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    public static bool TryDecodeSecret(string value, out byte[] bytes)
    {
        bytes = [];
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }
        var normalized = value.Trim().Replace(" ", string.Empty, StringComparison.Ordinal).TrimEnd('=').ToUpperInvariant();
        if (normalized.Length < 16 || normalized.Any(character =>
                !(character is >= 'A' and <= 'Z' || character is >= '2' and <= '7')))
        {
            return false;
        }
        try
        {
            var output = new byte[checked(normalized.Length * 5 / 8)];
            var buffer = 0;
            var bitsLeft = 0;
            var outputIndex = 0;
            foreach (var character in normalized)
            {
                var valuePart = character is >= 'A' and <= 'Z'
                    ? character - 'A'
                    : character - '2' + 26;
                buffer = (buffer << 5) | valuePart;
                bitsLeft += 5;
                if (bitsLeft < 8)
                {
                    continue;
                }
                bitsLeft -= 8;
                output[outputIndex++] = (byte)(buffer >> bitsLeft);
                buffer &= (1 << bitsLeft) - 1;
            }
            bytes = outputIndex == output.Length ? output : output[..outputIndex];
            return bytes.Length >= 10;
        }
        catch (OverflowException)
        {
            bytes = [];
            return false;
        }
    }

    private static string Compute(byte[] secret, long step)
    {
        Span<byte> counter = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(counter, step);
        var digest = HMACSHA1.HashData(secret, counter);
        try
        {
            var offset = digest[^1] & 0x0f;
            var binary = ((digest[offset] & 0x7f) << 24) |
                (digest[offset + 1] << 16) |
                (digest[offset + 2] << 8) |
                digest[offset + 3];
            return (binary % 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(digest);
        }
    }

    [GeneratedRegex("^[0-9]{6}$", RegexOptions.CultureInvariant)]
    private static partial Regex SixDigits();
}
