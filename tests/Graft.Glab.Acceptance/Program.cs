using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Graft.Core;
using Graft.Storage;

if (!OperatingSystem.IsLinux() || args.Length != 2 || !File.Exists(args[0]))
{
    Console.Error.WriteLine("Usage on Linux: Graft.Glab.Acceptance <Graft.Server executable> <pinned glab image>");
    return 2;
}

string serverPath = Path.GetFullPath(args[0]);
string glabImage = args[1];
string root = Path.Combine(Path.GetTempPath(), "graft-glab-" + Guid.NewGuid().ToString("N"));
string databasePath = Path.Combine(root, "graft.db");
string repositoriesPath = Path.Combine(root, "repositories");
string tokenPath = Path.Combine(root, "api-token");
string certificatePath = Path.Combine(root, "server.pfx");
string certificatePasswordPath = Path.Combine(root, "certificate-password");
string caPath = Path.Combine(root, "ca.pem");
const string Username = "glabfixture";
const string ProjectPath = "glab-cli-fixture";
int assertions = 0;
ServerRun? server = null;

Directory.CreateDirectory(root);
Directory.CreateDirectory(repositoriesPath);

try
{
    string password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
    TokenGrant token;
    await using (GraftStore store = await GraftStore.OpenAsync(databasePath))
    {
        await store.BootstrapAdministratorAsync(Username, password);
        SessionGrant session = Required(await store.LoginAsync(Username, password), "fixture login");
        AuthenticatedIdentity identity = Required(await store.ResolveSessionAsync(session.Secret), "fixture identity");
        token = await store.CreateTokenAsync(identity, "glab-acceptance", TokenScope.Api,
            DateTimeOffset.UtcNow.AddHours(1));
    }

    File.WriteAllText(tokenPath, token.Secret, new UTF8Encoding(false));
    WriteCertificate(certificatePath, certificatePasswordPath, caPath);
    Restrict(tokenPath);
    Restrict(certificatePath);
    Restrict(certificatePasswordPath);

    int port = FreePort();
    string origin = $"https://localhost:{port}";
    server = StartServer(serverPath, databasePath, repositoriesPath, certificatePath,
        certificatePasswordPath, port, origin);
    await Ready(origin, server, certificatePath, certificatePasswordPath);
    Check(true, "Graft HTTPS endpoint ready with trusted fixture CA");

    CommandResult version = await Podman([
        "run", "--rm", "--pull=never", "--entrypoint", "/bin/sh", glabImage,
        "-ec", "export GLAB_CHECK_UPDATE=false; glab version"
    ]);
    Check(version.ExitCode == 0, "pinned glab container starts");
    Check(version.Output.Contains("glab 1.58.0 (676a7ced)", StringComparison.Ordinal),
        "exact glab 1.58.0 commit");

    const string host = "localhost";
    string apiHost = $"localhost:{port}";
    string script = $"""
        export SSL_CERT_FILE=/run/secrets/graft-ca.pem
        export GLAB_CHECK_UPDATE=false
        glab auth login --hostname {host} --api-host {apiHost} --api-protocol https --git-protocol https --stdin < /run/secrets/graft-token
        echo PASS:auth
        glab api --silent --hostname {host} user
        echo PASS:user
        glab api --silent --hostname {host} projects
        echo PASS:list
        glab api --silent --hostname {host} projects -X POST -f name={ProjectPath} -f visibility=private
        echo PASS:create
        glab api --silent --hostname {host} projects/{Username}%2F{ProjectPath}
        echo PASS:read
        """;
    CommandResult workflow = await Podman([
        "run", "--rm", "--pull=never", "--network=host",
        "--volume", $"{tokenPath}:/run/secrets/graft-token:ro",
        "--volume", $"{caPath}:/run/secrets/graft-ca.pem:ro",
        "--entrypoint", "/bin/sh", glabImage, "-ec", script
    ]);
    if (workflow.ExitCode != 0)
        throw new InvalidOperationException("glab workflow failed: " +
            workflow.Output.Replace(token.Secret, "<redacted>", StringComparison.Ordinal).Trim());
    Check(true, "glab workflow exits successfully");
    foreach (string marker in new[] { "auth", "user", "list", "create", "read" })
        Check(workflow.Output.Contains("PASS:" + marker, StringComparison.Ordinal), "glab " + marker);

    await Stop(server);
    server = null;
    await using (GraftStore store = await GraftStore.OpenAsync(databasePath))
    {
        ProjectDetails? created = await store.FindReadyProjectDetailsAsync(Username, ProjectPath);
        Check(created?.Project.Name == ProjectPath, "glab-created project persisted in Graft");
    }

    Console.WriteLine($"RESULT: PASS; glab=1.58.0; assertions={assertions}");
    Console.WriteLine("BOUNDARY: glab API user/list/create/read against disposable loopback Graft over a fixture CA.");
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine($"RESULT: FAIL; {error.GetType().Name}: {error.Message}");
    return 1;
}
finally
{
    if (server is not null) await Stop(server);
    if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
}

void Check(bool condition, string description)
{
    if (!condition) throw new InvalidOperationException(description);
    assertions++;
    Console.WriteLine("PASS: " + description);
}

static T Required<T>(T? value, string description) where T : class =>
    value ?? throw new InvalidOperationException(description + " was not created.");

static int FreePort()
{
    using TcpListener listener = new(IPAddress.Loopback, 0);
    listener.Start();
    int port = ((IPEndPoint)listener.LocalEndpoint).Port;
    listener.Stop();
    return port;
}

static ServerRun StartServer(string path, string database, string repositories, string certificate,
    string certificatePassword, int port, string origin)
{
    ProcessStartInfo start = new(path)
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true
    };
    foreach (string argument in new[]
    {
        "--database", database, "--repositories", repositories,
        "--listen", $"https://127.0.0.1:{port}", "--origin", origin,
        "--certificate", certificate, "--certificate-password-file", certificatePassword
    }) start.ArgumentList.Add(argument);
    Process process = Process.Start(start) ?? throw new InvalidOperationException("Graft did not start.");
    return new(process, process.StandardOutput.ReadToEndAsync(), process.StandardError.ReadToEndAsync());
}

static async Task Ready(string origin, ServerRun server, string certificatePath, string certificatePasswordPath)
{
    using X509Certificate2 expected = X509CertificateLoader.LoadPkcs12FromFile(
        certificatePath, File.ReadAllText(certificatePasswordPath));
    byte[] expectedHash = expected.GetCertHash(HashAlgorithmName.SHA256);
    using HttpClientHandler handler = new()
    {
        UseProxy = false,
        ServerCertificateCustomValidationCallback = (_, certificate, _, _) => certificate is not null &&
            certificate.GetCertHash(HashAlgorithmName.SHA256).SequenceEqual(expectedHash)
    };
    using HttpClient client = new(handler) { BaseAddress = new(origin), Timeout = TimeSpan.FromSeconds(2) };
    Stopwatch timer = Stopwatch.StartNew();
    while (timer.Elapsed < TimeSpan.FromSeconds(30))
    {
        if (server.Process.HasExited)
        {
            string error = (await server.StandardError).Trim();
            throw new InvalidOperationException("Graft exited before becoming ready: " + error);
        }
        try
        {
            using HttpResponseMessage response = await client.GetAsync("/healthz");
            if (response.StatusCode == HttpStatusCode.OK) return;
        }
        catch (HttpRequestException) { }
        catch (TaskCanceledException) { }
        await Task.Delay(100);
    }
    throw new TimeoutException("Graft did not become ready.");
}

static async Task<CommandResult> Podman(string[] arguments)
{
    ProcessStartInfo start = new("podman")
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true
    };
    foreach (string argument in arguments) start.ArgumentList.Add(argument);
    using Process process = Process.Start(start) ?? throw new InvalidOperationException("Podman did not start.");
    Task<string> output = process.StandardOutput.ReadToEndAsync();
    Task<string> error = process.StandardError.ReadToEndAsync();
    try
    {
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(2));
    }
    catch (TimeoutException)
    {
        if (!process.HasExited) process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync();
        throw;
    }
    return new(process.ExitCode, (await output) + (await error));
}

static async Task Stop(ServerRun server)
{
    if (!server.Process.HasExited) server.Process.Kill(entireProcessTree: true);
    await server.Process.WaitForExitAsync();
    await Task.WhenAll(server.StandardOutput, server.StandardError);
    server.Process.Dispose();
}

static void WriteCertificate(string certificatePath, string passwordPath, string caPath)
{
    using RSA caKey = RSA.Create(2048);
    CertificateRequest caRequest = new("CN=Graft glab acceptance CA", caKey, HashAlgorithmName.SHA256,
        RSASignaturePadding.Pkcs1);
    caRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
    caRequest.CertificateExtensions.Add(new X509KeyUsageExtension(
        X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
    caRequest.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(caRequest.PublicKey, false));
    using X509Certificate2 ca = caRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5),
        DateTimeOffset.UtcNow.AddDays(1));

    using RSA serverKey = RSA.Create(2048);
    CertificateRequest serverRequest = new("CN=localhost", serverKey, HashAlgorithmName.SHA256,
        RSASignaturePadding.Pkcs1);
    SubjectAlternativeNameBuilder names = new();
    names.AddDnsName("localhost");
    names.AddIpAddress(IPAddress.Loopback);
    serverRequest.CertificateExtensions.Add(names.Build());
    serverRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
    serverRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
    serverRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
        new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
    byte[] serial = RandomNumberGenerator.GetBytes(16);
    using X509Certificate2 signed = serverRequest.Create(ca, DateTimeOffset.UtcNow.AddMinutes(-5),
        DateTimeOffset.UtcNow.AddHours(2), serial);
    using X509Certificate2 server = signed.CopyWithPrivateKey(serverKey);
    string password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
    File.WriteAllBytes(certificatePath, server.Export(X509ContentType.Pkcs12, password));
    File.WriteAllText(passwordPath, password, new UTF8Encoding(false));
    File.WriteAllText(caPath, ca.ExportCertificatePem(), new UTF8Encoding(false));
}

static void Restrict(string path) =>
    File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);

internal sealed record CommandResult(int ExitCode, string Output);
internal sealed record ServerRun(Process Process, Task<string> StandardOutput, Task<string> StandardError);
