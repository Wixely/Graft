using System.Security.Cryptography;
using System.Text;

namespace Graft.Ui.Server;

public sealed class SessionLimitException : Exception;

public sealed class Sessions
{
    public const string CookieName = "__Host-graft-probe";
    private readonly Dictionary<string, SessionView> entries = new(StringComparer.Ordinal);
    private readonly object gate = new();
    private readonly Func<DateTimeOffset> now;
    private readonly byte[] salt = RandomNumberGenerator.GetBytes(16);
    private readonly byte[] passwordHash;
    private DateTimeOffset attemptWindow;
    private int attempts;

    public Sessions(string password, Func<DateTimeOffset>? clock = null)
    {
        if (password.Length is < 16 or > 256) throw new ArgumentException("Provide a fixture password between 16 and 256 characters.");
        now = clock ?? (() => DateTimeOffset.UtcNow);
        passwordHash = HashPassword(password);
    }

    public (string Token, SessionView View) Issue(bool authenticated = false)
    {
        lock (gate)
        {
            foreach (var key in entries.Where(e => e.Value.Expires <= now()).Select(e => e.Key).ToArray()) entries.Remove(key);
            if (entries.Count >= 128) throw new SessionLimitException();
            var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
            var session = new SessionView(authenticated, authenticated ? "fixture" : null,
                Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)), now().AddMinutes(authenticated ? 15 : 5));
            entries.Add(Key(token), session);
            return (token, session);
        }
    }

    public SessionView? Find(string? token)
    {
        if (!ValidToken(token)) return null;
        lock (gate)
        {
            var key = Key(token!);
            if (!entries.TryGetValue(key, out var entry)) return null;
            if (entry.Expires > now()) return entry;
            entries.Remove(key); return null;
        }
    }

    public bool VerifyCsrf(SessionView session, string? token) => ValidToken(token) &&
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(session.Csrf), Encoding.ASCII.GetBytes(token!));

    public (string Token, SessionView View)? Login(string token, string username, string password)
    {
        lock (gate)
        {
            if (Find(token) is null) return null;
            if (now() >= attemptWindow.AddMinutes(1)) { attemptWindow = now(); attempts = 0; }
            if (++attempts > 10) throw new SessionLimitException();
            var matches = CryptographicOperations.FixedTimeEquals(HashPassword(password), passwordHash);
            if (!matches || username != "fixture") return null;
            entries.Remove(Key(token));
            return Issue(authenticated: true);
        }
    }

    public void Revoke(string token)
    {
        if (ValidToken(token)) lock (gate) entries.Remove(Key(token));
    }

    private byte[] HashPassword(string password) => Rfc2898DeriveBytes.Pbkdf2(password, salt, 600_000, HashAlgorithmName.SHA256, 32);
    private static string Key(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(token)));
    private static bool ValidToken(string? token) => token is { Length: 64 } && token.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    public static void SelfCheck()
    {
        var instant = DateTimeOffset.Parse("2026-09-18T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var store = new Sessions(password, () => instant);
        var anonymous = store.Issue();
        Require(!anonymous.View.Authenticated && store.Find(anonymous.Token) is not null);
        Require(!store.VerifyCsrf(anonymous.View, new string('0', 64)));
        Require(store.VerifyCsrf(anonymous.View, anonymous.View.Csrf));
        var signedIn = store.Login(anonymous.Token, "fixture", password)!.Value;
        Require(signedIn.View.Authenticated && store.Find(anonymous.Token) is null && signedIn.Token != anonymous.Token);
        Require(!store.VerifyCsrf(signedIn.View, anonymous.View.Csrf));
        store.Revoke(signedIn.Token); Require(store.Find(signedIn.Token) is null);
        var expiring = store.Issue(true); instant = instant.AddMinutes(16); Require(store.Find(expiring.Token) is null);
        for (var i = 0; i < 128; i++) store.Issue();
        try { store.Issue(); throw new InvalidOperationException("Session bound ignored."); }
        catch (SessionLimitException) { }
        instant = instant.AddMinutes(6); store.Issue();
        Console.WriteLine("SESSION CHECKS: PASS (CSRF binding, rotation, revocation, expiry, capacity and expiry cleanup)");
    }

    private static void Require(bool value) { if (!value) throw new InvalidOperationException("Session check failed."); }
}
