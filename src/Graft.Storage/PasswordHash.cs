using System.Globalization;
using System.Security.Cryptography;

namespace Graft.Storage;

internal static class PasswordHash
{
    private const int Iterations = 600_000;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;
    private const string Prefix = "$graft-pbkdf2-sha256$v=1$";

    public static string Create(string password)
    {
        ValidatePassword(password);
        byte[] salt = RandomNumberGenerator.GetBytes(SaltBytes);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, HashBytes);
        return Prefix + "i=" + Iterations.ToString(CultureInfo.InvariantCulture) + "$" +
            Convert.ToBase64String(salt) + "$" + Convert.ToBase64String(hash);
    }

    public static bool Verify(string password, string encoded)
    {
        if (password.Length > 256 || !encoded.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        string[] parts = encoded[Prefix.Length..].Split('$');
        if (parts.Length != 3 || !parts[0].StartsWith("i=", StringComparison.Ordinal) ||
            !int.TryParse(parts[0].AsSpan(2), CultureInfo.InvariantCulture, out int iterations) || iterations != Iterations)
            return false;
        try
        {
            byte[] salt = Convert.FromBase64String(parts[1]);
            byte[] expected = Convert.FromBase64String(parts[2]);
            if (salt.Length != SaltBytes || expected.Length != HashBytes) return false;
            byte[] actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, HashBytes);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException) { return false; }
    }

    public static void ValidatePassword(string password)
    {
        ArgumentNullException.ThrowIfNull(password);
        if (password.Length is < 16 or > 256)
            throw new ArgumentException("Passwords must be between 16 and 256 characters.", nameof(password));
    }
}
