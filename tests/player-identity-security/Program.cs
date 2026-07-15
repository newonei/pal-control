using System.Collections;
using System.Reflection;
using Microsoft.Data.Sqlite;
using PalControl.ControlApi.Extraction;
using PalControl.ControlApi.Infrastructure;

var failures = new List<string>();
var now = DateTimeOffset.UtcNow;
var platformSubject = "steam_76561198000000001";
var otherSubject = "steam_76561198000000002";
var playerUid = "12345678901234567890123456789012";
var tempDirectory = Path.Combine(
    Path.GetTempPath(),
    "pal-control-player-identity-security",
    Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(tempDirectory);

try
{
    var firstRegistry = new PlayerPortalSessionRegistry();
    var first = firstRegistry.Create(platformSubject, playerUid, now, TimeSpan.FromHours(1));
    Check(firstRegistry.Authenticate(first.SessionToken, now) is not null,
        "a newly created session was not accepted");
    Check(firstRegistry.FindSubjects(playerUid).SequenceEqual([platformSubject]),
        "PlayerUID did not resolve to the in-memory session subject");

    var sessionField = typeof(PlayerPortalSessionRegistry).GetField(
        "_sessions",
        BindingFlags.Instance | BindingFlags.NonPublic);
    var storedSessions = sessionField?.GetValue(firstRegistry) as IDictionary;
    var hashMethod = typeof(PlayerPortalSessionRegistry).GetMethod(
        "HashSessionToken",
        BindingFlags.Static | BindingFlags.NonPublic);
    var expectedHash = hashMethod?.Invoke(null, [first.SessionToken]) as string;
    Check(storedSessions is not null, "session registry storage could not be inspected");
    Check(expectedHash is not null, "session-token hash function could not be inspected");
    if (storedSessions is not null)
    {
        Check(!storedSessions.Contains(first.SessionToken),
            "the raw session token was retained as a dictionary key");
        Check(expectedHash is not null && storedSessions.Contains(expectedHash),
            "the session was not keyed by its SHA-256 fingerprint");
    }

    var restartedRegistry = new PlayerPortalSessionRegistry();
    Check(restartedRegistry.Authenticate(first.SessionToken, now) is null,
        "a process restart did not invalidate the old session token");

    var store = new PlayerIdentitySecurityStore(tempDirectory);
    var serviceRegistry = new PlayerPortalSessionRegistry();
    var security = new PlayerIdentitySecurityService(store, serviceRegistry, TimeProvider.System);
    var subjectSessionOne = security.CreateSessionIfAllowed(
        platformSubject, playerUid, TimeSpan.FromHours(1));
    var subjectSessionTwo = security.CreateSessionIfAllowed(
        platformSubject, playerUid, TimeSpan.FromHours(1));
    var otherSession = security.CreateSessionIfAllowed(
        otherSubject, "another-player-uid", TimeSpan.FromHours(1));
    Check(subjectSessionOne is not null && subjectSessionTwo is not null && otherSession is not null,
        "pre-ban sessions were not created");

    var actorFingerprint = PlayerIdentitySecurityStore.FingerprintSubject("owner@example.test");
    var revoked = security.ApplyModeration(
        platformSubject,
        banned: true,
        "test-ban-correlation",
        actorFingerprint);
    Check(revoked == 2, $"ban revoked {revoked} sessions instead of all 2 subject sessions");
    Check(subjectSessionOne is null || security.Authenticate(subjectSessionOne.SessionToken) is null,
        "first banned-player session remained valid");
    Check(subjectSessionTwo is null || security.Authenticate(subjectSessionTwo.SessionToken) is null,
        "second banned-player session remained valid");
    Check(otherSession is not null && security.Authenticate(otherSession.SessionToken) is not null,
        "ban revoked an unrelated player's session");
    Check(security.CreateSessionIfAllowed(
            platformSubject, playerUid, TimeSpan.FromHours(1)) is null,
        "a banned subject could create a new session");

    var restartedStore = new PlayerIdentitySecurityStore(tempDirectory);
    Check(restartedStore.IsBanned(platformSubject),
        "the subject ban did not survive a security-store restart");
    var restartedSecurity = new PlayerIdentitySecurityService(
        restartedStore,
        new PlayerPortalSessionRegistry(),
        TimeProvider.System);
    Check(restartedSecurity.Authenticate(otherSession!.SessionToken) is null,
        "a new process accepted a session token created by the old process");

    security.Audit(
        PlayerIdentitySecurityEvents.CodeVerification,
        "denied",
        "invalid_or_expired_code",
        "suspicious",
        platformSubject,
        "203.0.113.42",
        "test-audit-correlation");
    var audit = restartedStore.List(10);
    var verification = audit.SingleOrDefault(item =>
        item.EventType == PlayerIdentitySecurityEvents.CodeVerification);
    Check(verification is not null, "the failed verification audit was not persisted");
    Check(verification?.SubjectFingerprint ==
        PlayerIdentitySecurityStore.FingerprintSubject(platformSubject),
        "the audit did not contain the subject fingerprint");
    Check(verification?.SourceIpFingerprint ==
        PlayerIdentitySecurityStore.Fingerprint("203.0.113.42"),
        "the audit did not contain the source IP fingerprint");

    VerifyAuditSchemaContainsNoSecrets(tempDirectory, platformSubject, failures);

    security.ApplyModeration(
        platformSubject,
        banned: false,
        "test-unban-correlation",
        actorFingerprint);
    Check(!new PlayerIdentitySecurityStore(tempDirectory).IsBanned(platformSubject),
        "unban did not clear the persisted subject ban");
    var unbannedSession = security.CreateSessionIfAllowed(
        platformSubject, playerUid, TimeSpan.FromMinutes(5));
    Check(unbannedSession is not null,
        "an unbanned subject could not create a session");
    Check(security.RevokeAll(platformSubject) == 1 &&
          unbannedSession is not null &&
          security.Authenticate(unbannedSession.SessionToken) is null,
        "explicit administrator-style subject revocation did not invalidate every session");

    var moderationRegistry = new PlayerPortalSessionRegistry();
    var moderationSecurity = new PlayerIdentitySecurityService(
        new PlayerIdentitySecurityStore(Path.Combine(tempDirectory, "moderation")),
        moderationRegistry,
        TimeProvider.System);
    var moderatedSession = moderationSecurity.CreateSessionIfAllowed(
        platformSubject,
        playerUid,
        TimeSpan.FromMinutes(5));
    var bindingStore = new StubBindingStore(new PlayerIdentityBinding(
        Guid.NewGuid(),
        platformSubject,
        Guid.NewGuid(),
        "world-current",
        playerUid,
        Guid.NewGuid(),
        now,
        now));
    var filterRevoked = await PlayerIdentityModerationFilter.ApplyAcceptedModerationAsync(
        "local",
        playerUid,
        banned: true,
        "filter-ban-correlation",
        "owner@example.test",
        "127.0.0.1",
        bindingStore,
        moderationSecurity,
        CancellationToken.None);
    Check(filterRevoked == 1,
        $"binding-aware moderation revoked {filterRevoked} sessions instead of 1");
    Check(moderatedSession is not null &&
          moderationSecurity.Authenticate(moderatedSession.SessionToken) is null,
        "PlayerUID moderation did not revoke the bound platform session");
}
finally
{
    try
    {
        Directory.Delete(tempDirectory, recursive: true);
    }
    catch (IOException)
    {
        // SQLite handles are disposed above; a delayed antivirus handle must not
        // change the contract result.
    }
}

if (failures.Count > 0)
{
    Console.Error.WriteLine("Player identity security contract failed:");
    foreach (var failure in failures)
    {
        Console.Error.WriteLine($"- {failure}");
    }
    return 1;
}

Console.WriteLine(
    "PASS: hashed process sessions, restart revocation, durable bans, moderation revocation, and redacted login audit.");
return 0;

void Check(bool condition, string message)
{
    if (!condition)
    {
        failures.Add(message);
    }
}

static void VerifyAuditSchemaContainsNoSecrets(
    string dataDirectory,
    string rawSubject,
    List<string> failures)
{
    var databasePath = Path.Combine(dataDirectory, "extraction-commerce.db");
    using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
    connection.Open();
    using var schemaCommand = connection.CreateCommand();
    schemaCommand.CommandText = """
        SELECT sql
        FROM sqlite_master
        WHERE type = 'table'
          AND name IN ('player_identity_bans', 'player_identity_security_events')
        ORDER BY name;
        """;
    var schema = new List<string>();
    using (var reader = schemaCommand.ExecuteReader())
    {
        while (reader.Read())
        {
            schema.Add(reader.GetString(0));
        }
    }
    var schemaText = string.Join("\n", schema).ToLowerInvariant();
    foreach (var forbidden in new[]
             {
                 "session_token", "raw_token", "csrf", "verification_code",
                 "challenge_id", "cookie_value", "source_ip text", "platform_subject text"
             })
    {
        if (schemaText.Contains(forbidden, StringComparison.Ordinal))
        {
            failures.Add($"security schema contains forbidden secret/raw field '{forbidden}'");
        }
    }

    using var valuesCommand = connection.CreateCommand();
    valuesCommand.CommandText = """
        SELECT COALESCE(subject_fingerprint, '') || '|' ||
               COALESCE(source_ip_fingerprint, '') || '|' ||
               event_type || '|' || outcome || '|' || reason_code
        FROM player_identity_security_events;
        """;
    using var values = valuesCommand.ExecuteReader();
    while (values.Read())
    {
        var persisted = values.GetString(0);
        if (persisted.Contains(rawSubject, StringComparison.OrdinalIgnoreCase) ||
            persisted.Contains("203.0.113.42", StringComparison.Ordinal))
        {
            failures.Add("security audit persisted a raw player or IP identifier");
        }
    }
}

sealed class StubBindingStore(PlayerIdentityBinding? binding) : IPlayerIdentityBindingStore
{
    public Task<PlayerIdentityBinding?> FindActivePlayerIdentityBindingAsync(
        string serverId,
        string playerIdentifier,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(
            string.Equals(serverId, "local", StringComparison.Ordinal) &&
            (string.Equals(playerIdentifier, binding?.PlatformSubject, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(playerIdentifier, binding?.PlayerUid, StringComparison.OrdinalIgnoreCase))
                ? binding
                : null);
    }
}
