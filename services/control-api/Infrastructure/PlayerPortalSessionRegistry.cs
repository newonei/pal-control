using System.Security.Cryptography;
using System.Text;

namespace PalControl.ControlApi.Infrastructure;

/// <summary>
/// Process-scoped player sessions. Session secrets are never persisted and the
/// raw cookie value is never retained: only its SHA-256 fingerprint is used as
/// the dictionary key. A service restart therefore revokes every session by
/// construction.
/// </summary>
public sealed class PlayerPortalSessionRegistry
{
    private const int SessionTokenBytes = 32;
    private const int CsrfTokenBytes = 32;
    private const int MaximumSessions = 16_384;
    private readonly object _sync = new();
    private readonly Dictionary<string, PlayerPortalSession> _sessions =
        new(StringComparer.Ordinal);

    public PlayerPortalSessionCreation Create(
        string normalizedUserId,
        string? playerUid,
        DateTimeOffset now,
        TimeSpan lifetime)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(normalizedUserId);
        if (lifetime <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(lifetime));
        }

        var sessionToken = CreateOpaqueToken(SessionTokenBytes);
        var csrfToken = CreateOpaqueToken(CsrfTokenBytes);
        var session = new PlayerPortalSession(
            normalizedUserId,
            csrfToken,
            now,
            now.Add(lifetime),
            string.IsNullOrWhiteSpace(playerUid) ? null : playerUid.Trim());
        var tokenHash = HashSessionToken(sessionToken);

        lock (_sync)
        {
            CleanupExpiredLocked(now);
            if (_sessions.Count >= MaximumSessions)
            {
                var oldest = _sessions.MinBy(pair => pair.Value.ExpiresAt).Key;
                _sessions.Remove(oldest);
            }
            _sessions[tokenHash] = session;
        }
        return new PlayerPortalSessionCreation(sessionToken, session);
    }

    public PlayerPortalSession? Authenticate(string rawSessionToken, DateTimeOffset now)
    {
        if (!IsOpaqueToken(rawSessionToken))
        {
            return null;
        }
        var tokenHash = HashSessionToken(rawSessionToken);
        lock (_sync)
        {
            CleanupExpiredLocked(now);
            return _sessions.GetValueOrDefault(tokenHash);
        }
    }

    public bool Revoke(string rawSessionToken)
    {
        if (!IsOpaqueToken(rawSessionToken))
        {
            return false;
        }
        lock (_sync)
        {
            return _sessions.Remove(HashSessionToken(rawSessionToken));
        }
    }

    public int RevokeAll(string normalizedUserId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(normalizedUserId);
        lock (_sync)
        {
            var hashes = _sessions
                .Where(pair => string.Equals(
                    pair.Value.UserId,
                    normalizedUserId,
                    StringComparison.OrdinalIgnoreCase))
                .Select(pair => pair.Key)
                .ToArray();
            foreach (var hash in hashes)
            {
                _sessions.Remove(hash);
            }
            return hashes.Length;
        }
    }

    public IReadOnlyList<string> FindSubjects(string playerIdentifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(playerIdentifier);
        var normalized = playerIdentifier.Trim();
        lock (_sync)
        {
            return _sessions.Values
                .Where(session =>
                    string.Equals(session.UserId, normalized, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(session.PlayerUid, normalized, StringComparison.OrdinalIgnoreCase))
                .Select(session => session.UserId)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
    }

    public int ActiveCount(DateTimeOffset now)
    {
        lock (_sync)
        {
            CleanupExpiredLocked(now);
            return _sessions.Count;
        }
    }

    internal static string HashSessionToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(token)));

    private void CleanupExpiredLocked(DateTimeOffset now)
    {
        foreach (var hash in _sessions
                     .Where(pair => pair.Value.ExpiresAt <= now)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            _sessions.Remove(hash);
        }
    }

    private static bool IsOpaqueToken(string? token) =>
        token is { Length: 43 } && token.All(character =>
            character is >= 'A' and <= 'Z' or
                >= 'a' and <= 'z' or
                >= '0' and <= '9' or '_' or '-');

    private static string CreateOpaqueToken(int byteCount) =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(byteCount))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}
