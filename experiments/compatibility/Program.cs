using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Dapper;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Data.Sqlite;

[module: DapperAot]

Console.WriteLine("Graft compatibility probe v1 (2026-09-18)");
Console.WriteLine($"Runtime: {RuntimeInformation.FrameworkDescription}; architecture: {RuntimeInformation.ProcessArchitecture}");
Console.WriteLine($"Dynamic code supported: {RuntimeFeature.IsDynamicCodeSupported}");
Console.WriteLine($"Invariant globalization requested: {Environment.GetEnvironmentVariable("DOTNET_SYSTEM_GLOBALIZATION_INVARIANT") ?? "0"}");
var failures = 0;
var folder = Path.Combine(Path.GetTempPath(), "graft-probe-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(folder);
try
{
    await Check("Secure randomness and SHA-256", () =>
    {
        var first = RandomNumberGenerator.GetBytes(64);
        var second = RandomNumberGenerator.GetBytes(64);
        Require(!first.SequenceEqual(second) && SHA256.HashData(first).Length == 32);
        return Task.CompletedTask;
    });
    await Check("Thread pool and timers", async () =>
    {
        Require(await Task.Run(() => 6 * 7).WaitAsync(TimeSpan.FromSeconds(5)) == 42);
        await Task.Delay(20);
    });
    await Check("Globalization (non-invariant cultures)", () =>
    {
        Require(CultureInfo.GetCultureInfo("en-GB").Name == "en-GB");
        Require(CultureInfo.GetCultureInfo("tr-TR").TextInfo.ToUpper("i") == "\u0130");
        return Task.CompletedTask;
    });
    await Check("File write, flush, rename and reopen", () =>
    {
        var source = Path.Combine(folder, "write.tmp");
        var destination = Path.Combine(folder, "persist.txt");
        using (var stream = new FileStream(source, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            stream.Write("graft-probe"u8);
            stream.Flush(true);
        }
        File.Move(source, destination);
        Require(File.ReadAllText(destination) == "graft-probe");
        return Task.CompletedTask;
    });
    await Check("SQLite and Dapper.AOT CRUD, rollback, WAL, contention and reopen", () =>
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(folder, "probe.db"), Pooling = false, DefaultTimeout = 1
        }.ToString();
        using (var db = new SqliteConnection(connectionString))
        {
            db.Open();
            Require(db.ExecuteScalar<string>("PRAGMA journal_mode=WAL;") == "wal");
            db.Execute("CREATE TABLE Probe (Id INTEGER PRIMARY KEY, Name TEXT NOT NULL);");
            db.Execute("INSERT INTO Probe (Id, Name) VALUES (@Id, @Name);", new { Id = 1, Name = "Graft-\u03bb" });
            var row = db.QuerySingle<ProbeRow>("SELECT Id, Name FROM Probe WHERE Id = @Id;", new { Id = 1 });
            Require(row.Id == 1 && row.Name == "Graft-\u03bb");
            using (var tx = db.BeginTransaction())
            {
                db.Execute("UPDATE Probe SET Name = @Name WHERE Id = @Id;", new { Id = 1, Name = "rollback" }, transaction: tx);
                using var contender = new SqliteConnection(connectionString);
                contender.Open();
                var busy = false;
                try { contender.Execute("INSERT INTO Probe (Id, Name) VALUES (2, 'contender');", commandTimeout: 1); }
                catch (SqliteException ex) when (ex.SqliteErrorCode is 5 or 6) { busy = true; }
                Require(busy);
                tx.Rollback();
            }
            db.Execute("INSERT INTO Probe (Id, Name) VALUES (3, 'delete');");
            Require(db.Execute("DELETE FROM Probe WHERE Id = 3;") == 1);
        }
        using var reopened = new SqliteConnection(connectionString);
        reopened.Open();
        Require(reopened.QuerySingle<string>("SELECT Name FROM Probe WHERE Id = 1;") == "Graft-\u03bb");
        Require(reopened.ExecuteScalar<long>("SELECT COUNT(Id) FROM Probe;") == 1);
        Require(reopened.QuerySingle<string>("PRAGMA integrity_check;") == "ok");
        return Task.CompletedTask;
    });
    await Check("ASP.NET Core loopback HTTPS, RSA certificate and TLS handshake", async () =>
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        using var generatedCertificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));
        // Schannel cannot use the ephemeral key returned by CreateSelfSigned.
        // DefaultKeySet imports a temporary user key; disposal removes it because
        // PersistKeySet is not requested. No certificate/trust-store installation.
        using var windowsCertificate = OperatingSystem.IsWindows()
            ? X509CertificateLoader.LoadPkcs12(generatedCertificate.Export(X509ContentType.Pkcs12), null, X509KeyStorageFlags.DefaultKeySet)
            : null;
        var certificate = windowsCertificate ?? generatedCertificate;
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], ContentRootPath = folder });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(certificate)));
        await using var app = builder.Build();
        app.MapGet("/probe", () => "graft-ok");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await app.StartAsync(timeout.Token);
        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var handler = new HttpClientHandler
            {
                UseProxy = false,
                ServerCertificateCustomValidationCallback = (_, cert, _, _) => cert?.Thumbprint == certificate.Thumbprint
            };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
            Require(await client.GetStringAsync(address + "/probe", timeout.Token) == "graft-ok");
        }
        finally { await app.StopAsync(CancellationToken.None); }
    });
}
finally
{
    try { Directory.Delete(folder, recursive: true); }
    catch { Console.WriteLine("WARN: temporary probe directory could not be removed."); }
}
Console.WriteLine($"RESULT: {(failures == 0 ? "PASS" : "FAIL")}; failed checks: {failures}");
Console.WriteLine("LIMITS: no Graft app, Blazor, Git transport, runner, upgrade, external CA trust, cross-process locking or crash recovery validation.");
Console.WriteLine(RuntimeFeature.IsDynamicCodeSupported
    ? "NativeAOT host compatibility is established only by running a separately published NativeAOT probe."
    : "This run used NativeAOT. Passing validates this probe only, not the complete Graft dependency stack.");
return failures == 0 ? 0 : 1;

async Task Check(string name, Func<Task> action)
{
    Console.WriteLine("CHECK: " + name);
    try { await action(); Console.WriteLine("PASS: " + name); }
    catch (Exception ex)
    {
        failures++;
        // Avoid paths, hostnames, SQL contents and stack traces in the shareable report.
        Console.WriteLine($"FAIL: {name}; {ex.GetType().Name}; HResult=0x{ex.HResult:X8}");
        for (var inner = ex.InnerException; inner is not null; inner = inner.InnerException)
            Console.WriteLine($"CAUSE: {inner.GetType().Name}; HResult=0x{inner.HResult:X8}");
        if (ex is SqliteException sqlite) Console.WriteLine($"SQLite error: {sqlite.SqliteErrorCode}/{sqlite.SqliteExtendedErrorCode}");
    }
}
static void Require(bool condition) { if (!condition) throw new InvalidOperationException("Probe assertion failed."); }
public sealed class ProbeRow { public long Id { get; set; } public string Name { get; set; } = ""; }
