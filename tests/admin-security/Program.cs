using PalControl.ControlApi.Infrastructure;

const string firstHash = "1111111111111111111111111111111111111111111111111111111111111111";
const string secondHash = "2222222222222222222222222222222222222222222222222222222222222222";
const string rfcSecret = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ";
List<string> failures = [];

CheckInvalid("disabled authentication", new AdminAuthenticationOptions
{
    Enabled = false,
    Principals = [Viewer("viewer", firstHash)]
});
CheckInvalid("no principal", new AdminAuthenticationOptions());
CheckInvalid("duplicate subject", ValidOptions(
    Viewer("same", firstHash),
    Viewer("same", secondHash)));
CheckInvalid("duplicate key hash", ValidOptions(
    Viewer("first", firstHash),
    Viewer("second", firstHash)));
CheckInvalid("unknown role", ValidOptions(new AdminPrincipalOptions
{
    Subject = "unknown-role",
    ApiKeySha256 = firstHash,
    Roles = ["SuperUser"]
}));
CheckInvalid("privileged principal without TOTP", ValidOptions(new AdminPrincipalOptions
{
    Subject = "economy-admin",
    ApiKeySha256 = firstHash,
    Roles = [AdminRoles.EconomyAdmin]
}));
CheckValid("viewer does not require TOTP", ValidOptions(Viewer("viewer", firstHash)));
CheckValid("owner with TOTP", ValidOptions(new AdminPrincipalOptions
{
    Subject = "owner",
    ApiKeySha256 = firstHash,
    Roles = [AdminRoles.Owner],
    TotpSecretBase32 = rfcSecret
}));

var ownerRoles = AdminRoles.Expand([AdminRoles.Owner]);
foreach (var required in AdminRoles.All)
{
    if (!ownerRoles.Contains(required))
    {
        failures.Add($"owner role expansion omitted {required}");
    }
}
var economyRoles = AdminRoles.Expand([AdminRoles.EconomyAdmin]);
if (!economyRoles.SetEquals([AdminRoles.Viewer, AdminRoles.Operator, AdminRoles.EconomyAdmin]))
{
    failures.Add("EconomyAdmin role expansion was not Viewer + Operator + EconomyAdmin.");
}

var rfcTime = DateTimeOffset.FromUnixTimeSeconds(59);
if (!Totp.Validate(rfcSecret, "287082", rfcTime))
{
    failures.Add("RFC 6238 SHA-1 vector did not validate after six-digit truncation.");
}
if (Totp.Validate(rfcSecret, "287083", rfcTime) || Totp.Validate("invalid!", "287082", rfcTime))
{
    failures.Add("TOTP accepted an invalid code or malformed secret.");
}

if (failures.Count > 0)
{
    Console.Error.WriteLine("Admin security contract failed:");
    foreach (var failure in failures)
    {
        Console.Error.WriteLine($"- {failure}");
    }
    return 1;
}

Console.WriteLine("PASS: admin API-key, role hierarchy, configuration, and TOTP contracts.");
return 0;

void CheckValid(string name, AdminAuthenticationOptions options)
{
    if (!options.IsValid(out var error))
    {
        failures.Add($"{name}: expected valid, error={error}");
    }
}

void CheckInvalid(string name, AdminAuthenticationOptions options)
{
    if (options.IsValid(out _))
    {
        failures.Add($"{name}: expected invalid");
    }
}

static AdminAuthenticationOptions ValidOptions(params AdminPrincipalOptions[] principals) => new()
{
    Principals = principals
};

static AdminPrincipalOptions Viewer(string subject, string hash) => new()
{
    Subject = subject,
    ApiKeySha256 = hash,
    Roles = [AdminRoles.Viewer]
};
