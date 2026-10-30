using System.Security.Cryptography;
using System.Text;

namespace Graft.Ssh;

/// <summary>
/// A client's public key as SSH carries it: the algorithm name and the key blob of RFC 4253 section 6.6 or
/// its successors. <see cref="Fingerprint"/> is OpenSSH's <c>SHA256:</c> form, which is what a person
/// compares against <c>ssh-keygen -lf</c>.
/// </summary>
public sealed class SshPublicKey
{
    private SshPublicKey(string type, byte[] blob)
    {
        Type = type;
        Blob = blob;
        Fingerprint = "SHA256:" + Convert.ToBase64String(SHA256.HashData(blob)).TrimEnd('=');
    }

    /// <summary>The key type as the blob names it: <c>ssh-ed25519</c>, <c>ecdsa-sha2-nistp256</c>, <c>ssh-rsa</c>.</summary>
    public string Type { get; }
    public byte[] Blob { get; }
    public string Fingerprint { get; }

    /// <summary>The key as one line of an <c>authorized_keys</c> file, without a comment.</summary>
    public string ToAuthorizedKeyLine() => Type + " " + Convert.ToBase64String(Blob);

    /// <summary>The key types this server will authenticate.</summary>
    public static IReadOnlyList<string> SupportedTypes { get; } =
        ["ssh-ed25519", "ecdsa-sha2-nistp256", "ecdsa-sha2-nistp384", "ecdsa-sha2-nistp521", "ssh-rsa"];

    /// <summary>
    /// The signature algorithms this server accepts, advertised as <c>server-sig-algs</c>. An RSA key signs
    /// with SHA-2; the SHA-1 form OpenSSH itself disabled in 8.8 is not here, by decision.
    /// </summary>
    public static IReadOnlyList<string> SignatureAlgorithms { get; } =
        ["ssh-ed25519", "ecdsa-sha2-nistp256", "ecdsa-sha2-nistp384", "ecdsa-sha2-nistp521", "rsa-sha2-512", "rsa-sha2-256"];

    /// <summary>Largest key blob accepted, which admits a 16384-bit RSA key and refuses a request body.</summary>
    public const int MaximumBlobBytes = 4096;

    /// <summary>
    /// Parses one line as a person would paste it: <c>type base64 [comment]</c>, the format of
    /// <c>id_ed25519.pub</c>. Returns null for anything that is not a supported, well-formed key.
    /// </summary>
    public static SshPublicKey? ParseAuthorizedKey(string line)
    {
        if (line is null) return null;
        string[] parts = line.Trim().Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || parts[1].Length > MaximumBlobBytes * 2) return null;
        byte[] blob;
        try { blob = Convert.FromBase64String(parts[1]); }
        catch (FormatException) { return null; }
        SshPublicKey? key = FromBlob(parts[0], blob);
        return key;
    }

    /// <summary>
    /// Parses a key blob whose declared type is <paramref name="type"/>, checking that the blob names the
    /// same type and that its contents decode as a key of that type. Returns null otherwise.
    /// </summary>
    public static SshPublicKey? FromBlob(string type, byte[] blob)
    {
        if (blob.Length > MaximumBlobBytes || !SupportedTypes.Contains(type, StringComparer.Ordinal)) return null;
        try
        {
            SshReader reader = new(blob);
            if (reader.Text() != type) return null;
            switch (type)
            {
                case "ssh-ed25519":
                    if (reader.String().Length != 32) return null;
                    break;
                case "ssh-rsa":
                {
                    byte[] exponent = reader.MPInt();
                    byte[] modulus = reader.MPInt();
                    // Below 2048 bits, OpenSSH itself refuses the key; above the blob bound it cannot arrive.
                    if (exponent.Length is 0 or > 8 || modulus.Length < 256) return null;
                    break;
                }
                default:
                {
                    string curve = reader.Text();
                    if (curve != type["ecdsa-sha2-".Length..]) return null;
                    ReadOnlyMemory<byte> point = reader.String();
                    int coordinate = CoordinateBytes(curve);
                    if (point.Length != 1 + 2 * coordinate || point.Span[0] != 4) return null;
                    break;
                }
            }
            reader.ExpectEnd();
            SshPublicKey key = new(type, blob);
            // Decoding into the platform's own type is the real check that the numbers form a key.
            if (type != "ssh-ed25519") using (AsymmetricAlgorithm algorithm = key.Import()) { }
            return key;
        }
        catch (SshProtocolException) { return null; }
        catch (CryptographicException) { return null; }
    }

    /// <summary>
    /// Verifies <paramref name="signature"/>, an SSH signature blob (string algorithm, string bytes), over
    /// <paramref name="data"/>. The algorithm must be one this key can produce: a key of one type signing
    /// under another's name is refused, as is an RSA signature with SHA-1.
    /// </summary>
    public bool Verify(ReadOnlySpan<byte> data, ReadOnlyMemory<byte> signature)
    {
        try
        {
            SshReader reader = new(signature);
            string algorithm = reader.Text();
            ReadOnlyMemory<byte> bytes = reader.String();
            reader.ExpectEnd();
            switch (Type)
            {
                case "ssh-ed25519":
                {
                    if (algorithm != "ssh-ed25519") return false;
                    SshReader key = new(Blob);
                    key.Text();
                    return Ed25519.Verify(key.String().Span, data, bytes.Span);
                }
                case "ssh-rsa":
                {
                    HashAlgorithmName hash;
                    if (algorithm == "rsa-sha2-256") hash = HashAlgorithmName.SHA256;
                    else if (algorithm == "rsa-sha2-512") hash = HashAlgorithmName.SHA512;
                    else return false;
                    using RSA rsa = (RSA)Import();
                    return rsa.VerifyData(data, bytes.Span, hash, RSASignaturePadding.Pkcs1);
                }
                default:
                {
                    if (algorithm != Type) return false;
                    // The signature is two multiple-precision integers; the platform wants them fixed-size
                    // and concatenated.
                    SshReader rs = new(bytes);
                    byte[] r = rs.MPInt();
                    byte[] s = rs.MPInt();
                    rs.ExpectEnd();
                    int size = CoordinateBytes(Type["ecdsa-sha2-".Length..]);
                    if (r.Length > size || s.Length > size) return false;
                    byte[] fixedSize = new byte[2 * size];
                    r.CopyTo(fixedSize, size - r.Length);
                    s.CopyTo(fixedSize, 2 * size - s.Length);
                    using ECDsa ecdsa = (ECDsa)Import();
                    return ecdsa.VerifyData(data, fixedSize, HashFor(Type));
                }
            }
        }
        catch (SshProtocolException) { return false; }
        catch (CryptographicException) { return false; }
    }

    private static HashAlgorithmName HashFor(string type) => type switch
    {
        "ecdsa-sha2-nistp256" => HashAlgorithmName.SHA256,
        "ecdsa-sha2-nistp384" => HashAlgorithmName.SHA384,
        _ => HashAlgorithmName.SHA512
    };

    private static int CoordinateBytes(string curve) => curve switch
    {
        "nistp256" => 32,
        "nistp384" => 48,
        "nistp521" => 66,
        _ => throw new SshProtocolException("unknown curve")
    };

    private AsymmetricAlgorithm Import()
    {
        SshReader reader = new(Blob);
        reader.Text();
        if (Type == "ssh-rsa")
        {
            RSAParameters parameters = new() { Exponent = reader.MPInt(), Modulus = reader.MPInt() };
            RSA rsa = RSA.Create();
            rsa.ImportParameters(parameters);
            return rsa;
        }
        string curve = reader.Text();
        ReadOnlySpan<byte> point = reader.String().Span;
        int size = CoordinateBytes(curve);
        ECParameters ec = new()
        {
            Curve = curve switch
            {
                "nistp256" => ECCurve.NamedCurves.nistP256,
                "nistp384" => ECCurve.NamedCurves.nistP384,
                _ => ECCurve.NamedCurves.nistP521
            },
            Q = new ECPoint { X = point.Slice(1, size).ToArray(), Y = point.Slice(1 + size, size).ToArray() }
        };
        ECDsa ecdsa = ECDsa.Create();
        ecdsa.ImportParameters(ec);
        return ecdsa;
    }
}

/// <summary>
/// The server's own identity: an ECDSA P-256 key, created on first start and kept as PKCS#8 PEM beside the
/// database. One key type, because every OpenSSH client since 5.7 accepts it and the platform signs with
/// it everywhere Graft runs.
/// </summary>
public sealed class SshHostKey : IDisposable
{
    private SshHostKey(ECDsa key)
    {
        Key = key;
        ECParameters parameters = key.ExportParameters(false);
        Blob = new SshWriter().String("ecdsa-sha2-nistp256").String("nistp256")
            .String([4, .. parameters.Q.X!, .. parameters.Q.Y!]).ToArray();
        Fingerprint = "SHA256:" + Convert.ToBase64String(SHA256.HashData(Blob)).TrimEnd('=');
    }

    public ECDsa Key { get; }
    public const string Algorithm = "ecdsa-sha2-nistp256";
    public byte[] Blob { get; }
    public string Fingerprint { get; }

    /// <summary>The key as a <c>known_hosts</c> entry would carry it.</summary>
    public string PublicKeyLine => Algorithm + " " + Convert.ToBase64String(Blob);

    /// <summary>Loads the key at <paramref name="path"/>, or creates it there when there is none.</summary>
    public static SshHostKey LoadOrCreate(string path)
    {
        ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        if (File.Exists(path))
        {
            key.ImportFromPem(File.ReadAllText(path));
            return new(key);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string pem = key.ExportPkcs8PrivateKeyPem();
        FileStreamOptions options = new() { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (FileStream stream = new(path, options))
            stream.Write(Encoding.ASCII.GetBytes(pem));
        return new(key);
    }

    /// <summary>Signs the exchange hash in the wire form: string algorithm, string (mpint r, mpint s).</summary>
    public byte[] Sign(ReadOnlySpan<byte> exchangeHash)
    {
        byte[] raw = Key.SignData(exchangeHash, HashAlgorithmName.SHA256);
        return new SshWriter().String(Algorithm)
            .String(new SshWriter().MPInt(raw.AsSpan(0, 32)).MPInt(raw.AsSpan(32, 32)).ToArray()).ToArray();
    }

    public void Dispose() => Key.Dispose();
}
