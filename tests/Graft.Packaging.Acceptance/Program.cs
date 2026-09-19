using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Graft.Operations;

if (args.Length < 2)
{
    Usage();
    return 2;
}

return args[0] switch
{
    "interactive" when args.Length == 2 => await Interactive(Path.GetFullPath(args[1])),
    "tls" when args.Length == 2 => await Tls(Path.GetFullPath(args[1])),
    "windows-service" when args.Length == 2 => await WindowsService(Path.GetFullPath(args[1])),
    "systemd" when args.Length == 3 => await Systemd(Path.GetFullPath(args[1]), Path.GetFullPath(args[2])),
    "container" when args.Length == 3 => await Container(Path.GetFullPath(args[1]), Path.GetFullPath(args[2])),
    _ => Invalid()
};

static async Task<int> Tls(string serverPath)
{
    RequireFile(serverPath);
    string root = Temporary("tls");
    string certificatePath = Path.Combine(root, "tls.pfx");
    string passwordPath = Path.Combine(root, "tls-password");
    string password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
    File.WriteAllText(passwordPath, password + Environment.NewLine, new UTF8Encoding(false));
    int port = FreePort();
    string origin = $"https://127.0.0.1:{port}";
    try
    {
        byte[] first = WriteCertificate(certificatePath, password);
        for (int restart = 0; restart < 2; restart++)
        {
            using Process server = StartServer(serverPath, Path.Combine(root, "graft.db"),
                Path.Combine(root, "repositories"), port, certificatePath, passwordPath);
            try
            {
                using HttpClient client = Client(origin, first);
                await Ready(client, server, TimeSpan.FromSeconds(20));
            }
            finally { await StopProcess(server); }
        }

        byte[] renewed = WriteCertificate(certificatePath, password);
        Require(!renewed.SequenceEqual(first), "renewal creates a distinct certificate");
        using (Process server = StartServer(serverPath, Path.Combine(root, "graft.db"),
            Path.Combine(root, "repositories"), port, certificatePath, passwordPath))
        {
            try
            {
                using HttpClient client = Client(origin, renewed);
                await Ready(client, server, TimeSpan.FromSeconds(20));
            }
            finally { await StopProcess(server); }
        }
        using (Process refused = StartServer(serverPath, Path.Combine(root, "refused.db"),
            Path.Combine(root, "refused-repositories"), FreePort(), listenAddress: "0.0.0.0"))
        {
            await refused.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Require(refused.ExitCode == 2, "non-loopback listener requires an operator certificate");
        }
        _ = WriteCertificate(certificatePath, password, "different.example", addLoopback: false);
        using (Process mismatch = StartServer(serverPath, Path.Combine(root, "mismatch.db"),
            Path.Combine(root, "mismatch-repositories"), FreePort(), certificatePath, passwordPath))
        {
            await mismatch.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Require(mismatch.ExitCode != 0, "certificate subject alternative name must match the origin");
        }
        Console.WriteLine("RESULT: PASS; mode=tls; assertions=6");
        return 0;
    }
    finally { DeleteTree(root); }
}

static async Task<int> Interactive(string serverPath)
{
    RequireFile(serverPath);
    string root = Temporary("interactive");
    int assertions = 0;
    int port = FreePort();
    string origin = $"https://127.0.0.1:{port}";
    Stopwatch startup = Stopwatch.StartNew();
    using Process server = StartServer(serverPath, Path.Combine(root, "graft.db"), Path.Combine(root, "repositories"), port);
    try
    {
        using HttpClient client = Client(origin);
        await Ready(client, server, TimeSpan.FromSeconds(20));
        startup.Stop();
        assertions++;
        using HttpResponseMessage shell = await client.GetAsync("/");
        Require(shell.IsSuccessStatusCode && shell.Content.Headers.ContentType?.MediaType == "text/html", "embedded shell");
        assertions++;
        for (int index = 0; index < 10; index++) await Health(client);
        List<double> latency = [];
        for (int index = 0; index < 100; index++)
        {
            Stopwatch timer = Stopwatch.StartNew();
            await Health(client);
            latency.Add(timer.Elapsed.TotalMilliseconds);
        }
        Stopwatch load = Stopwatch.StartNew();
        await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            using HttpClient concurrent = Client(origin);
            for (int request = 0; request < 25; request++) await Health(concurrent);
        }));
        load.Stop();
        assertions += 200;
        server.Refresh();
        long workingSet = server.WorkingSet64;
        long executableBytes = new FileInfo(serverPath).Length;
        long publishedBytes = Directory.EnumerateFiles(Path.GetDirectoryName(serverPath)!, "*", SearchOption.AllDirectories)
            .Sum(path => new FileInfo(path).Length);
        double p50 = Percentile(latency, 0.50);
        double p95 = Percentile(latency, 0.95);
        Require(startup.Elapsed < TimeSpan.FromSeconds(10), "startup budget");
        Require(workingSet < 256L * 1024 * 1024, "idle working-set budget");
        Require(p95 < 250, "loopback p95 budget");
        Require(load.Elapsed < TimeSpan.FromSeconds(10), "parallel health budget");
        assertions += 4;
        Console.WriteLine($"METRICS: startup_ms={startup.Elapsed.TotalMilliseconds:F1}; working_set_bytes={workingSet}; executable_bytes={executableBytes}; published_bytes={publishedBytes}; health_p50_ms={p50:F2}; health_p95_ms={p95:F2}; parallel_200_ms={load.Elapsed.TotalMilliseconds:F1}");
        Console.WriteLine($"RESULT: PASS; mode=interactive; assertions={assertions}");
        return 0;
    }
    finally
    {
        await StopProcess(server);
        DeleteTree(root);
    }
}

static async Task<int> WindowsService(string serverPath)
{
    if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows Service acceptance requires Windows.");
    RequireFile(serverPath);
    string service = "GraftAcceptance" + Guid.NewGuid().ToString("N");
    string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), service);
    string install = Path.Combine(root, "app");
    string data = Path.Combine(root, "data");
    int port = FreePort();
    Directory.CreateDirectory(data);
    (string certificatePath, string passwordPath) = WriteCertificateFiles(data);
    CopyTree(Path.GetDirectoryName(serverPath)!, install);
    string executable = Path.Combine(install, Path.GetFileName(serverPath));
    string binPath = $"\"{executable}\" --database \"{Path.Combine(data, "graft.db")}\" --repositories \"{Path.Combine(data, "repositories")}\" --listen https://127.0.0.1:{port} --origin https://127.0.0.1:{port} --certificate \"{certificatePath}\" --certificate-password-file \"{passwordPath}\"";
    bool created = false;
    try
    {
        CommandResult create = await Run("sc.exe", ["create", service, "binPath=", binPath, "start=", "demand", "DisplayName=", service]);
        if (create.ExitCode != 0 && create.Output.Contains("Access is denied", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("RESULT: BLOCKED; mode=windows-service; reason=administrator privileges are required");
            return 3;
        }
        Require(create.ExitCode == 0, "service creation: " + create.Output);
        created = true;
        Require((await Run("sc.exe", ["start", service])).ExitCode == 0, "service start");
        using HttpClient client = Client($"https://127.0.0.1:{port}");
        await Ready(client, null, TimeSpan.FromSeconds(30));
        Require((await Run("sc.exe", ["query", service])).Output.Contains("RUNNING", StringComparison.Ordinal), "service running state");
        Require((await Run("sc.exe", ["stop", service])).ExitCode == 0, "service stop request");
        await WaitService(service, "STOPPED", TimeSpan.FromSeconds(30));
        using (MaintenanceLease.Acquire(Path.Combine(data, "graft.db"), Path.Combine(data, "repositories"))) { }
        Console.WriteLine("RESULT: PASS; mode=windows-service; assertions=5");
        return 0;
    }
    finally
    {
        if (created)
        {
            _ = await Run("sc.exe", ["stop", service]);
            _ = await Run("sc.exe", ["delete", service]);
        }
        DeleteTree(root);
    }
}

static async Task<int> Systemd(string serverPath, string templatePath)
{
    if (!OperatingSystem.IsLinux() || Environment.UserName != "root")
        throw new InvalidOperationException("systemd acceptance must run as root on Linux.");
    RequireFile(serverPath);
    RequireFile(templatePath);
    string suffix = Guid.NewGuid().ToString("N");
    string service = "graft-acceptance-" + suffix;
    string install = "/opt/" + service;
    string data = "/var/lib/" + service;
    string unitPath = "/etc/systemd/system/" + service + ".service";
    int assertions = 0;
    try
    {
        CopyTree(Path.GetDirectoryName(serverPath)!, install);
        string executable = Path.Combine(install, Path.GetFileName(serverPath));
        File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        Directory.CreateDirectory(data);
        (string certificatePath, string passwordPath) = WriteCertificateFiles(data);
        string unit = File.ReadAllText(templatePath)
            .Replace("@USER@", "root", StringComparison.Ordinal)
            .Replace("@GROUP@", "root", StringComparison.Ordinal)
            .Replace("@EXECUTABLE@", executable, StringComparison.Ordinal)
            .Replace("@DATABASE@", Path.Combine(data, "graft.db"), StringComparison.Ordinal)
            .Replace("@REPOSITORIES@", Path.Combine(data, "repositories"), StringComparison.Ordinal)
            .Replace("@DATA_DIRECTORY@", data, StringComparison.Ordinal)
            .Replace("@INSTALL_DIRECTORY@", install, StringComparison.Ordinal)
            .Replace("@LISTEN@", "https://127.0.0.1:7443", StringComparison.Ordinal)
            .Replace("@ORIGIN@", "https://127.0.0.1:7443", StringComparison.Ordinal);
        unit = unit.Replace("@CERTIFICATE@", certificatePath, StringComparison.Ordinal)
            .Replace("@CERTIFICATE_PASSWORD_FILE@", passwordPath, StringComparison.Ordinal);
        File.WriteAllText(unitPath, unit);
        Require((await Run("systemctl", ["daemon-reload"])).ExitCode == 0, "systemd daemon reload");
        Require((await Run("systemctl", ["start", service])).ExitCode == 0, "systemd start");
        assertions += 2;
        using HttpClient client = Client("https://127.0.0.1:7443");
        await Ready(client, null, TimeSpan.FromSeconds(30));
        assertions++;
        CommandResult state = await Run("systemctl", ["show", service, "--property=ActiveState", "--property=SubState", "--property=Type"]);
        Require(state.ExitCode == 0 && state.Output.Contains("ActiveState=active", StringComparison.Ordinal) &&
            state.Output.Contains("SubState=running", StringComparison.Ordinal) && state.Output.Contains("Type=notify", StringComparison.Ordinal),
            "systemd notify running state");
        assertions++;
        Require((await Run("systemctl", ["stop", service])).ExitCode == 0, "systemd stop");
        assertions++;
        using (MaintenanceLease.Acquire(Path.Combine(data, "graft.db"), Path.Combine(data, "repositories"))) { }
        assertions++;
        Console.WriteLine($"RESULT: PASS; mode=systemd; assertions={assertions}");
        return 0;
    }
    catch
    {
        CommandResult journal = await Run("journalctl", ["-u", service, "--no-pager", "-n", "50"]);
        Console.Error.WriteLine(journal.Output);
        throw;
    }
    finally
    {
        _ = await Run("systemctl", ["stop", service]);
        if (File.Exists(unitPath)) File.Delete(unitPath);
        _ = await Run("systemctl", ["daemon-reload"]);
        DeleteTree(install);
        DeleteTree(data);
    }
}

static async Task<int> Container(string contextPath, string containerfilePath)
{
    if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Container acceptance runs in Linux.");
    RequireFile(containerfilePath);
    string suffix = Guid.NewGuid().ToString("N");
    string image = "graft-acceptance:" + suffix;
    string container = "graft-acceptance-" + suffix;
    string volume = "graft-acceptance-" + suffix;
    string tlsRoot = Temporary("container-tls");
    (string certificatePath, string passwordPath) = WriteCertificateFiles(tlsRoot);
    int port = FreePort();
    int assertions = 0;
    try
    {
        CommandResult build = await Run("podman", ["build", "--file", containerfilePath, "--tag", image, contextPath], TimeSpan.FromMinutes(10));
        Require(build.ExitCode == 0, "container image build: " + build.Output);
        assertions++;
        Require((await Run("podman", ["volume", "create", volume])).ExitCode == 0, "container volume create");
        assertions++;
        CommandResult run = await Run("podman", ["run", "--detach", "--name", container,
            "--publish", $"127.0.0.1:{port}:8443", "--mount", $"type=volume,source={volume},target=/data",
            "--mount", $"type=bind,source={certificatePath},target=/run/secrets/graft-tls.pfx,readonly",
            "--mount", $"type=bind,source={passwordPath},target=/run/secrets/graft-tls-password,readonly",
            "--read-only", "--tmpfs", "/tmp:size=64m", image,
            "--database", "/data/graft.db", "--repositories", "/data/repositories",
            "--listen", "https://0.0.0.0:8443", "--origin", "https://localhost:8443",
            "--certificate", "/run/secrets/graft-tls.pfx",
            "--certificate-password-file", "/run/secrets/graft-tls-password"]);
        Require(run.ExitCode == 0, "container run: " + run.Output);
        assertions++;
        using HttpClient client = Client($"https://127.0.0.1:{port}");
        await Ready(client, null, TimeSpan.FromSeconds(30));
        assertions++;
        CommandResult inspect = await Run("podman", ["inspect", container, "--format", "{{.State.Status}} {{.Config.User}}"]);
        Require(inspect.ExitCode == 0 && inspect.Output.Contains("running", StringComparison.Ordinal) &&
            inspect.Output.Contains("1654", StringComparison.Ordinal), "container running as non-root user");
        assertions++;
        Require((await Run("podman", ["restart", container])).ExitCode == 0, "container restart");
        await Ready(client, null, TimeSpan.FromSeconds(30));
        assertions += 2;
        Console.WriteLine($"RESULT: PASS; mode=container; assertions={assertions}");
        return 0;
    }
    catch
    {
        CommandResult logs = await Run("podman", ["logs", container]);
        Console.Error.WriteLine(logs.Output);
        throw;
    }
    finally
    {
        _ = await Run("podman", ["rm", "--force", container]);
        _ = await Run("podman", ["volume", "rm", "--force", volume]);
        _ = await Run("podman", ["image", "rm", "--force", image]);
        DeleteTree(tlsRoot);
    }
}

static Process StartServer(string path, string database, string repositories, int port,
    string? certificate = null, string? certificatePasswordFile = null, string listenAddress = "127.0.0.1")
{
    bool dll = path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
    ProcessStartInfo start = new(dll ? "dotnet" : path)
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true
    };
    if (dll) start.ArgumentList.Add(path);
    foreach (string value in new[] { "--database", database, "--repositories", repositories,
        "--listen", $"https://{listenAddress}:{port}", "--origin", $"https://127.0.0.1:{port}" })
        start.ArgumentList.Add(value);
    if (certificate is not null)
    {
        start.ArgumentList.Add("--certificate");
        start.ArgumentList.Add(certificate);
    }
    if (certificatePasswordFile is not null)
    {
        start.ArgumentList.Add("--certificate-password-file");
        start.ArgumentList.Add(certificatePasswordFile);
    }
    Process process = Process.Start(start)!;
    _ = Task.Run(async () => { while (await process.StandardOutput.ReadLineAsync() is not null) { } });
    _ = Task.Run(async () => { while (await process.StandardError.ReadLineAsync() is not null) { } });
    return process;
}

static HttpClient Client(string origin, byte[]? certificateHash = null)
{
    HttpClientHandler handler = new()
    {
        ServerCertificateCustomValidationCallback = certificateHash is null
            ? HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
            : (_, certificate, _, _) => certificate is not null &&
                certificate.GetCertHash(HashAlgorithmName.SHA256).SequenceEqual(certificateHash),
        UseProxy = false
    };
    return new(handler) { BaseAddress = new(origin), Timeout = TimeSpan.FromSeconds(10) };
}

static async Task Ready(HttpClient client, Process? process, TimeSpan timeout)
{
    Stopwatch timer = Stopwatch.StartNew();
    while (timer.Elapsed < timeout)
    {
        if (process?.HasExited == true) throw new InvalidOperationException("Server exited before becoming ready.");
        try { await Health(client); return; }
        catch (HttpRequestException) { }
        catch (TaskCanceledException) { }
        await Task.Delay(100);
    }
    throw new TimeoutException("HTTPS endpoint did not become ready.");
}

static async Task Health(HttpClient client)
{
    using HttpResponseMessage response = await client.GetAsync("/healthz");
    if (response.StatusCode != HttpStatusCode.OK || await response.Content.ReadAsStringAsync() != "healthy")
        throw new InvalidOperationException("Health response was not successful.");
}

static async Task StopProcess(Process process)
{
    if (!process.HasExited) process.Kill(entireProcessTree: true);
    await process.WaitForExitAsync();
}

static async Task WaitService(string name, string expected, TimeSpan timeout)
{
    Stopwatch timer = Stopwatch.StartNew();
    while (timer.Elapsed < timeout)
    {
        CommandResult query = await Run("sc.exe", ["query", name]);
        if (query.Output.Contains(expected, StringComparison.Ordinal)) return;
        await Task.Delay(250);
    }
    throw new TimeoutException("Service did not reach " + expected + ".");
}

static async Task<CommandResult> Run(string fileName, string[] arguments, TimeSpan? timeout = null)
{
    ProcessStartInfo start = new(fileName) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (string argument in arguments) start.ArgumentList.Add(argument);
    using Process process = Process.Start(start)!;
    Task<string> output = process.StandardOutput.ReadToEndAsync();
    Task<string> error = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync().WaitAsync(timeout ?? TimeSpan.FromSeconds(60));
    return new(process.ExitCode, (await output) + (await error));
}

static int FreePort()
{
    using TcpListener listener = new(IPAddress.Loopback, 0);
    listener.Start();
    int port = ((IPEndPoint)listener.LocalEndpoint).Port;
    listener.Stop();
    return port;
}

static double Percentile(List<double> values, double percentile)
{
    double[] ordered = values.Order().ToArray();
    return ordered[(int)Math.Ceiling(percentile * ordered.Length) - 1];
}

static string Temporary(string mode)
{
    string path = Path.Combine(Path.GetTempPath(), "graft-mvp08-" + mode + "-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(path);
    return path;
}

static (string Certificate, string PasswordFile) WriteCertificateFiles(string root)
{
    Directory.CreateDirectory(root);
    string certificate = Path.Combine(root, "tls.pfx");
    string passwordFile = Path.Combine(root, "tls-password");
    string password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
    File.WriteAllText(passwordFile, password + Environment.NewLine, new UTF8Encoding(false));
    _ = WriteCertificate(certificate, password);
    return (certificate, passwordFile);
}

static byte[] WriteCertificate(string path, string password, string dnsName = "localhost", bool addLoopback = true)
{
    using RSA rsa = RSA.Create(2048);
    CertificateRequest request = new("CN=" + dnsName, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    SubjectAlternativeNameBuilder names = new();
    names.AddDnsName(dnsName);
    if (addLoopback) names.AddIpAddress(IPAddress.Loopback);
    request.CertificateExtensions.Add(names.Build());
    OidCollection serverUsage = new() { new Oid("1.3.6.1.5.5.7.3.1") };
    request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(serverUsage, critical: true));
    request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
    using X509Certificate2 certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(30));
    File.WriteAllBytes(path, certificate.Export(X509ContentType.Pkcs12, password));
    return certificate.GetCertHash(HashAlgorithmName.SHA256);
}

static void CopyTree(string source, string destination)
{
    Directory.CreateDirectory(destination);
    foreach (string directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
    foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
    {
        string target = Path.Combine(destination, Path.GetRelativePath(source, file));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(file, target);
    }
}

static void DeleteTree(string path)
{
    if (!Directory.Exists(path)) return;
    foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
    Directory.Delete(path, recursive: true);
}

static void RequireFile(string path)
{
    if (!File.Exists(path)) throw new FileNotFoundException("Required file was not found.", path);
}

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException("Assertion failed: " + message);
}

static int Invalid() { Usage(); return 2; }
static void Usage() => Console.Error.WriteLine(
    "Usage: Graft.Packaging.Acceptance interactive <server>\n" +
    "       Graft.Packaging.Acceptance tls <server>\n" +
    "       Graft.Packaging.Acceptance windows-service <server.exe>\n" +
    "       Graft.Packaging.Acceptance systemd <linux-server> <graft.service>\n" +
    "       Graft.Packaging.Acceptance container <repository-root> <Containerfile>");

sealed record CommandResult(int ExitCode, string Output);
