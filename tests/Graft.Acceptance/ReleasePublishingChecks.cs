using System.Security.Cryptography;
using Graft.Core;
using Graft.TestSupport;

/// <summary>
/// Publishing a release to an application store (ADR 0052), against a store this suite runs on loopback. The
/// rules a destination's settings follow are checked first, then the publisher end to end: what it sends, what
/// it refuses to send, what it does when the store misbehaves, and what its messages never contain.
/// </summary>
static class ReleasePublishingChecks
{
    private static int assertions;
    private static readonly List<string> said = [];

    public static async Task<int> RunAsync()
    {
        assertions = 0;
        Rules();
        await DryRunAndCapabilities();
        await GenericFile();
        await AndroidPackage();
        await MultipleFiles();
        await Rejection();
        await Resuming();
        await Retries();
        await Expiry();
        await Redirects();
        await ForeignChunkAddress();
        await Conflicts();
        await Refusals();
        return assertions;
    }

    private static void Rules()
    {
        Check(ReleaseDestinations.IsName("store") && ReleaseDestinations.IsName("beta-2") && !ReleaseDestinations.IsName("Store") && !ReleaseDestinations.IsName("") &&
            !ReleaseDestinations.IsName("a/b") && !ReleaseDestinations.IsName(new string('a', 41)), "a destination's name is one lower-case word");
        Check(ReleaseDestinations.ParseEndpoint("https://store.example/opaque/api/v1", false, out _) is { Host: "store.example" } &&
            ReleaseDestinations.ParseEndpoint("https://store.example/opaque/api/v1/", false, out _) is not null, "an https endpoint ending in /api/v1 is accepted, with or without a final slash");
        Check(ReleaseDestinations.ParseEndpoint("http://store.example/opaque/api/v1", false, out _) is null &&
            ReleaseDestinations.ParseEndpoint("http://store.example/opaque/api/v1", true, out _) is not null, "plain http is refused unless the operator allowed any address");
        Check(ReleaseDestinations.ParseEndpoint("https://store.example/opaque/api", false, out _) is null &&
            ReleaseDestinations.ParseEndpoint("https://user:secret@store.example/api/v1", false, out _) is null &&
            ReleaseDestinations.ParseEndpoint("https://store.example/api/v1?key=1", false, out _) is null &&
            ReleaseDestinations.ParseEndpoint("store.example/api/v1", false, out _) is null &&
            ReleaseDestinations.ParseEndpoint("", false, out _) is null, "an endpoint with the wrong ending, credentials, a query or no scheme is refused");
        Check(ReleaseDestinations.IsApplicationId("0f8fad5b-d9cb-469f-a165-70867728950e") && !ReleaseDestinations.IsApplicationId("0f8fad5bd9cb469fa16570867728950e") &&
            !ReleaseDestinations.IsApplicationId("example"), "an application is named by a dashed UUID");

        IReadOnlyList<ArtifactRule>? rules = ReleaseDestinations.ParseRules("# comment\n*-win-x64.zip Windows x64 main\n*.sig skip\n\nnotes.txt Unknown\nexample-ppc.tar.gz Linux PPC64LE", out string problem);
        Check(rules is { Count: 4 } && problem.Length == 0 && rules[0] is { Platform: "Windows", Architecture: "x64", Main: true } && rules[1].Skip &&
            rules[2] is { Platform: "Unknown", Architecture: "any", Main: false } && rules[3].Architecture == "ppc64le", "file rules are read: pattern, platform, architecture, main, skip");
        Check(ReleaseDestinations.ParseRules(ReleaseDestinations.FormatRules(rules!), out _) is { Count: 4 }, "file rules survive being written and read back");
        Check(ReleaseDestinations.ParseRules("*.zip Plan9", out problem) is null && problem.Contains("rule 1", StringComparison.Ordinal) &&
            ReleaseDestinations.ParseRules("*.zip", out _) is null && ReleaseDestinations.ParseRules("a/b.zip Windows", out _) is null &&
            ReleaseDestinations.ParseRules("*.zip Windows x64 mian", out _) is null && ReleaseDestinations.ParseRules("*.zip Windows x_64! main", out _) is null &&
            ReleaseDestinations.ParseRules("a.zip Windows x64 main\nb.zip Linux x64 main", out _) is null, "a rule with an unknown platform, no action, a path, a stray word, a bad architecture or a second main is refused by line");

        Check(ReleaseDestinations.Declare("example.apk", []) is { Format: "apk", Platform: "Android", Architecture: "any" }, "an .apk is declared as an Android package");
        Check(ReleaseDestinations.Declare("example-win-x64.zip", []) is { Format: "file", Platform: "Windows", Architecture: "x64" } &&
            ReleaseDestinations.Declare("example-linux-arm64.tar.gz", []) is { Platform: "Linux", Architecture: "arm64" } &&
            ReleaseDestinations.Declare("example_linux_x86_64.tar.gz", []) is { Platform: "Linux", Architecture: "x64" } &&
            ReleaseDestinations.Declare("Example-macOS.dmg", []) is { Platform: "MacOS", Architecture: "any" } &&
            ReleaseDestinations.Declare("setup.exe", []) is { Platform: "Windows" } &&
            ReleaseDestinations.Declare("checksums.txt", []) is { Format: "file", Platform: "Unknown", Architecture: "any" }, "a file no rule names is declared from its own name");
        Check(ReleaseDestinations.Declare("build.sig", rules!) is null && ReleaseDestinations.Declare("example-win-x64.zip", rules!) is { Main: true } &&
            ReleaseDestinations.Declare("NOTES.TXT", rules!) is { Platform: "Unknown" } &&
            ReleaseDestinations.Declare("tool.apk", [new("*.apk", false, "Linux", "any", false)]) is { Format: "file", Platform: "Linux" }, "the first rule that matches decides, without regard to case, and can leave a file out");
        Check(ReleaseDestinations.Matches("*", "x") && ReleaseDestinations.Matches("a*b?c", "aXXbYc") && !ReleaseDestinations.Matches("a*b", "ab.zip") &&
            ReleaseDestinations.Matches("*.tar.gz", "a.tar.gz") && !ReleaseDestinations.Matches("?", ""), "a pattern matches the whole name with * and ?");
        Check(ReleaseDestinations.VersionOf("v1.4.0") == "1.4.0" && ReleaseDestinations.VersionOf("1.4.0") == "1.4.0" && ReleaseDestinations.VersionOf("vNext") == "vNext" &&
            ReleaseDestinations.VersionOf("V2") == "2", "a tag's version drops the v before a number and nothing else");
        string key = ReleaseDestinations.IdempotencyKey("project", "v1", "0F8FAD5B-D9CB-469F-A165-70867728950E", "a.zip", new string('a', 64));
        Check(key.Length <= 200 && key == ReleaseDestinations.IdempotencyKey("project", "v1", "0f8fad5b-d9cb-469f-a165-70867728950e", "a.zip", new string('A', 64)) &&
            key != ReleaseDestinations.IdempotencyKey("project", "v2", "0f8fad5b-d9cb-469f-a165-70867728950e", "a.zip", new string('a', 64)) &&
            key != ReleaseDestinations.IdempotencyKey("project", "v1", "0f8fad5b-d9cb-469f-a165-70867728950e", "b.zip", new string('a', 64)) &&
            key != ReleaseDestinations.IdempotencyKey("project", "v1", "0f8fad5b-d9cb-469f-a165-70867728950e", "a.zip", new string('b', 64)) &&
            key != ReleaseDestinations.IdempotencyKey("other", "v1", "0f8fad5b-d9cb-469f-a165-70867728950e", "a.zip", new string('a', 64)),
            "the idempotency key is the same for the same upload and different when the project, release, file or bytes differ");

        Uri endpoint = new("https://store.example/opaque/api/v1");
        Check(AppStorePublisher.ChunkAddress(endpoint, "/opaque/upload/v1/upload-sessions/x/chunks/{index}", 3).AbsoluteUri == "https://store.example/opaque/upload/v1/upload-sessions/x/chunks/3",
            "a chunk address is the issued path on the store's own origin");
        foreach (string template in new[] { "//elsewhere.example/x/{index}", "https://elsewhere.example/x/{index}", "/x/chunks", "x/{index}", "/\\elsewhere.example/{index}", "/x@elsewhere.example/{index}", "" })
            Check(Refused(() => AppStorePublisher.ChunkAddress(endpoint, template, 0)), "a chunk address that is not a path on the store's origin is refused: '" + template + "'");
    }

    private static async Task DryRunAndCapabilities()
    {
        using FakeAppStore store = new();
        PublicationReport report = await Publish(store, "none", [File("example-win-x64.zip", 3000)], dryRun: true);
        Check(report.Succeeded && store.UploadSessionsOpened == 0 && report.Artifacts is [{ State: "planned", Platform: "Windows", Architecture: "x64" }] &&
            store.Requests.Count == 1, "a dry run asks the store what it accepts, reports what would be sent, and starts no upload");
        report = await Publish(store, "none", [], dryRun: true);
        Check(report.Succeeded && store.UploadSessionsOpened == 0, "a dry run with no files still checks the endpoint and the key");

        store.MaximumArtifactBytes = 2000;
        report = await Publish(store, "none", [File("example-win-x64.zip", 3000)]);
        Check(!report.Succeeded && store.UploadSessionsOpened == 0 && report.Summary.Contains("more than", StringComparison.Ordinal), "a file larger than the store accepts fails before any upload is opened");
        store.MaximumArtifactBytes = 8 * 1024 * 1024;
        store.Platforms = ["Android", "Windows"];
        report = await Publish(store, "none", [File("example-win-x64.zip", 3000), File("example-linux-x64.tar.gz", 3000)]);
        Check(!report.Succeeded && store.UploadSessionsOpened == 0 && report.Summary.Contains("Linux", StringComparison.Ordinal), "a platform the store does not list fails the whole release before any upload is opened");
        store.Formats = ["apk"];
        report = await Publish(store, "none", [File("example-win-x64.zip", 3000)]);
        Check(!report.Succeeded && store.UploadSessionsOpened == 0 && report.Summary.Contains("format", StringComparison.Ordinal), "a format the store does not list fails before any upload is opened");

        using FakeAppStore closed = new() { ProductionPromotionAvailable = false };
        report = await Publish(closed, "stable", [File("example-win-x64.zip", 3000)], promotion: true);
        Check(!report.Succeeded && closed.UploadSessionsOpened == 0, "a store that offers no production promotion fails a channel publication before any upload");
        report = await Publish(closed, "stable", [File("example-win-x64.zip", 3000)], promotion: false);
        Check(!report.Succeeded && closed.Requests.Count == 1 && report.Summary.Contains("not enabled", StringComparison.Ordinal), "a destination without production promotion enabled sends nothing at all for a channel");
        report = await Publish(closed, "none", [File("example-win-x64.zip", 3000)], notes: new string('n', 8001));
        Check(!report.Succeeded && closed.Requests.Count == 1, "release notes longer than the store takes fail before anything is sent");
        report = await Publish(closed, "none", [File("example.apk", 3000)], package: null);
        Check(!report.Succeeded && closed.Requests.Count == 1 && report.Summary.Contains("package name", StringComparison.Ordinal), "an Android package needs the destination's package name");
        report = await Publish(closed, "none", []);
        Check(!report.Succeeded && closed.Requests.Count == 1, "publishing a release with no files fails without a request");
    }

    private static async Task GenericFile()
    {
        using FakeAppStore store = new();
        PublishArtifact file = File("example-win-x64.zip", 3500);
        List<string> stages = [];
        PublicationReport report = await Publish(store, "none", [file], stages: stages);
        var build = store.Builds.SingleOrDefault();
        Check(report.Succeeded && report.Artifacts is [{ State: "accepted", BuildId: { Length: 36 } }] && build.Id == report.Artifacts[0].BuildId, "a generic file is uploaded and accepted as a build");
        Check(build is { Format: "file", Platform: "Windows", Architecture: "x64", Version: "1.4.0", Published: false, Channel: null } && build.Sha256 == file.Sha256,
            "the build carries the declared platform, architecture and version, holds the bytes that were sent, and is not published");
        Check(Enumerable.Range(0, 4).All(index => store.Arrivals(file.FileName, index) == 1) && store.Arrivals(file.FileName, 4) == 0, "the file went up in the store's chunk size, each chunk once");
        Check(store.Violations.IsEmpty, "nothing the contract forbids was sent: " + string.Join("; ", store.Violations));
        Check(store.ReleaseRequests == 0 && store.Channel("stable") is null, "with the channel none, nothing is published");
        Check(stages.Count >= 2 && stages.Any(stage => stage.Contains(file.FileName, StringComparison.Ordinal)), "progress is reported while it runs");

        int opened = store.UploadSessionsOpened;
        report = await Publish(store, "none", [file]);
        Check(report.Succeeded && store.UploadSessionsOpened == opened && store.Builds.Count == 1 && report.Artifacts[0].BuildId == build.Id && store.Arrivals(file.FileName, 0) == 1,
            "publishing the same file again finds the accepted upload and sends nothing twice");
    }

    private static async Task AndroidPackage()
    {
        using FakeAppStore store = new();
        PublishArtifact apk = File("example.apk", 2500);
        PublicationReport report = await Publish(store, "stable", [apk], promotion: true);
        var build = store.Builds.SingleOrDefault();
        Check(report.Succeeded && report.ChannelVerified == true && report.Artifacts is [{ State: "published" }], "an Android package is uploaded, published to stable and read back");
        Check(build is { Format: "apk", Platform: "Android", Published: true, Channel: "stable", PackageName: "com.example.application" } &&
            store.Channel("stable") == (build.Id, "1.4.0"), "the stable channel reports the build and its version");
        Check(report.Artifacts[0].UploadedSha256 == apk.Sha256 && report.Artifacts[0].StoreSha256 == build.Sha256 && build.Sha256 != apk.Sha256,
            "a store that signs the package changes its hash, and both hashes are recorded");
        Check(store.Violations.IsEmpty, "nothing the contract forbids was sent: " + string.Join("; ", store.Violations));

        int releases = store.ReleaseRequests;
        report = await Publish(store, "stable", [apk], promotion: true);
        Check(report.Succeeded && store.ReleaseRequests == releases, "publishing again finds the build already on the channel and does not publish it twice");
    }

    private static async Task MultipleFiles()
    {
        using FakeAppStore store = new();
        IReadOnlyList<ArtifactRule> rules = ReleaseDestinations.ParseRules("example-windows-x64.zip Windows x64 main\n*.sig skip", out _)!;
        PublishArtifact[] files = [File("example-windows-x64.zip", 2100, rules), File("example-linux-x64.tar.gz", 1500, rules), File("example-linux-arm64.tar.gz", 900, rules), File("checksums.txt", 200, rules)];
        Check(ReleaseDestinations.Declare("example.sig", rules) is null, "a skipped file is not part of what is published");
        PublicationReport report = await Publish(store, "beta", files, promotion: true);
        var builds = store.Builds;
        Check(report.Succeeded && builds.Count == 4 && builds.All(build => build is { Published: true, Channel: "beta", Version: "1.4.0" }) &&
            builds.Select(build => build.Id).Distinct().Count() == 4, "each file of a release becomes its own published build, with the same version");
        Check(builds.Single(build => build.FileName == "example-linux-arm64.tar.gz") is { Platform: "Linux", Architecture: "arm64" } &&
            builds.Single(build => build.FileName == "checksums.txt") is { Platform: "Unknown", Architecture: "any" }, "each file carries its own platform and architecture");
        Check(report.ChannelVerified == true && store.Channel("beta")?.BuildId == builds.Single(build => build.FileName == "example-windows-x64.zip").Id && store.Channel("stable") is null,
            "the beta channel is left on the main file's build, and stable is untouched");
        Check(store.UploadSessionsOpened == 4 && store.ReleaseRequests == 4, "four independent uploads, four publications");
    }

    private static async Task Rejection()
    {
        using FakeAppStore store = new();
        store.Reject.Add("example-linux-x64.tar.gz");
        PublishArtifact[] files = [File("example-windows-x64.zip", 2100), File("example-linux-x64.tar.gz", 1500), File("example-linux-arm64.tar.gz", 900)];
        PublicationReport report = await Publish(store, "stable", files, promotion: true);
        Check(!report.Succeeded && report.Artifacts.Select(artifact => artifact.State).SequenceEqual(["accepted", "rejected", "not-attempted"]) &&
            report.Artifacts[1].Problem!.Contains("malware_scan", StringComparison.Ordinal), "a rejected file stops the release and reports the store's own code");
        Check(store.ReleaseRequests == 0 && store.Builds.All(build => !build.Published) && store.Channel("stable") is null, "nothing is published when one file of the set was rejected");
    }

    private static async Task Resuming()
    {
        using FakeAppStore store = new();
        PublishArtifact file = File("example-win-x64.zip", 4500);
        store.DropChunk[2] = 100;
        PublicationReport report = await Publish(store, "none", [file]);
        Check(!report.Succeeded && report.Artifacts is [{ State: "failed" }] && store.Builds.Count == 0 && store.Arrivals(file.FileName, 0) == 1 && store.Arrivals(file.FileName, 1) == 1,
            "a connection that keeps failing fails the publication after the chunks before it arrived");
        store.DropChunk.Clear();
        report = await Publish(store, "none", [file]);
        Check(report.Succeeded && store.UploadSessionsOpened == 1 && store.Arrivals(file.FileName, 0) == 1 && store.Arrivals(file.FileName, 1) == 1 &&
            Enumerable.Range(2, 3).All(index => store.Arrivals(file.FileName, index) == 1) && store.Builds.Single().Sha256 == file.Sha256,
            "publishing again carries on in the same upload from what the store holds, and the build is the whole file");
    }

    private static async Task Retries()
    {
        using FakeAppStore store = new();
        PublishArtifact file = File("example-win-x64.zip", 3000);
        store.DropChunk[1] = 1;
        store.LoseAnswer[2] = 1;
        store.BusyChunks = 1;
        PublicationReport report = await Publish(store, "none", [file]);
        Check(report.Succeeded && store.Builds.Single().Sha256 == file.Sha256, "a dropped connection, a lost answer and a busy store are each retried within one publication");
        Check(store.Arrivals(file.FileName, 2) == 2, "a chunk the store already held is sent again byte for byte, and accepted");
    }

    private static async Task Expiry()
    {
        using FakeAppStore store = new();
        PublishArtifact file = File("example-win-x64.zip", 3000);
        store.DropChunk[1] = 100;
        await Publish(store, "stable", [file], promotion: true);
        store.DropChunk.Clear();
        store.ExpireOnResume = true;
        PublicationReport report = await Publish(store, "stable", [file], promotion: true);
        Check(!report.Succeeded && report.Artifacts is [{ State: "failed" }] && report.Artifacts[0].Problem!.Contains("expired", StringComparison.Ordinal) &&
            store.Builds.Count == 0 && store.ReleaseRequests == 0, "an upload session that expired is reported as expired, and nothing is published");
    }

    private static async Task Redirects()
    {
        using FakeAppStore elsewhere = new();
        using FakeAppStore store = new() { RedirectApiTo = elsewhere.Origin + "/collect" };
        PublicationReport report = await Publish(store, "none", [File("example-win-x64.zip", 3000)]);
        Check(!report.Succeeded && report.Summary.Contains("redirect", StringComparison.Ordinal) && elsewhere.Requests.IsEmpty, "a redirect from the API is a failure, and nothing follows it");

        using FakeAppStore chunky = new() { RedirectChunksTo = elsewhere.Origin + "/collect" };
        report = await Publish(chunky, "none", [File("example-win-x64.zip", 3000)]);
        Check(!report.Succeeded && report.Artifacts[0].Problem!.Contains("redirect", StringComparison.Ordinal) && elsewhere.Requests.IsEmpty && chunky.Builds.Count == 0,
            "a redirect from a chunk address is a failure, and no bytes follow it");

        // A client that would follow redirects is refused all the same, by where the answer came from.
        using HttpClient following = new(new SocketsHttpHandler { AllowAutoRedirect = true, UseProxy = false });
        report = await Publish(store, "none", [File("example-win-x64.zip", 3000)], client: following);
        Check(!report.Succeeded && report.Summary.Contains("redirect", StringComparison.Ordinal) && elsewhere.Requests.All(request => !request.Contains("upload-sessions", StringComparison.Ordinal)),
            "a client configured to follow redirects still ends in a refusal, with no upload opened anywhere");
    }

    private static async Task ForeignChunkAddress()
    {
        using FakeAppStore elsewhere = new();
        foreach (string template in new[] { elsewhere.Origin + "/collect/{index}", "//127.0.0.1:" + elsewhere.Port + "/collect/{index}" })
        {
            using FakeAppStore store = new() { ChunkPathTemplate = template };
            PublicationReport report = await Publish(store, "none", [File("example-win-x64.zip", 3000)]);
            Check(!report.Succeeded && report.Artifacts[0].Problem!.Contains("own origin", StringComparison.Ordinal) && elsewhere.Requests.IsEmpty,
                "an upload address on another origin is refused and nothing is sent to it");
        }
    }

    private static async Task Conflicts()
    {
        using FakeAppStore store = new();
        PublishArtifact file = File("example-win-x64.zip", 3000);
        await Publish(store, "none", [file]);
        PublicationReport report = await Publish(store, "none", [file], notes: "Different notes for the same file.");
        Check(!report.Succeeded && report.Artifacts[0].Problem!.Contains("different details", StringComparison.Ordinal) && store.Builds.Count == 1,
            "the same key with a different declaration is an idempotency conflict, reported and not worked around");

        using FakeAppStore moving = new() { ChangeApplicationAfterRead = true };
        PublishArtifact[] files = [File("example-windows-x64.zip", 2100), File("example-linux-x64.tar.gz", 1500)];
        report = await Publish(moving, "stable", files, promotion: true);
        Check(!report.Succeeded && report.Summary.Contains("revision conflict", StringComparison.Ordinal) && moving.ReleaseRequests == 1 &&
            moving.Builds.All(build => !build.Published) && moving.Channel("stable") is null,
            "a revision conflict stops the publication: one attempt, no second try with a newer revision, nothing published");
        report = await Publish(moving, "stable", files, promotion: true);
        Check(report.Succeeded && moving.Builds.All(build => build.Published) && moving.UploadSessionsOpened == 2, "asked again by a person, the same builds are published without being uploaded again");
    }

    private static async Task Refusals()
    {
        using FakeAppStore store = new();
        PublishArtifact file = File("example-win-x64.zip", 3000);
        PublicationReport report = await Publish(store, "none", [file], key: "not-the-key-at-all");
        Check(!report.Succeeded && report.Summary.Contains("did not accept the API key", StringComparison.Ordinal) && store.UploadSessionsOpened == 0, "a wrong key is reported as a wrong key");
        report = await Publish(store, "none", [file], application: Guid.NewGuid().ToString("D"));
        Check(!report.Succeeded && report.Artifacts[0].Problem!.Contains("does not know this application", StringComparison.Ordinal), "an application the store does not have is reported as that");
        store.KeyMayPromote = false;
        report = await Publish(store, "stable", [file], promotion: true);
        Check(!report.Succeeded && report.Summary.Contains("scope", StringComparison.Ordinal) && store.Builds.Single() is { Published: false }, "a key without the promotion scope uploads and is then refused, with the scopes named");

        using HttpClient client = Client();
        Uri gone = new("http://127.0.0.1:" + FreePort() + "/opaque-path-that-is-private/api/v1");
        report = await new AppStorePublisher(client, Options).PublishAsync(new(gone, store.ApplicationId, "key-that-is-private", "none", false, null),
            new("project", "v1.4.0", "1.4.0", "notes"), [file], false, null, CancellationToken.None);
        Check(!report.Succeeded && report.Summary.Contains("could not be reached", StringComparison.Ordinal) &&
            !report.Summary.Contains("opaque-path-that-is-private", StringComparison.Ordinal) && !report.Summary.Contains("key-that-is-private", StringComparison.Ordinal),
            "a store that cannot be reached is reported without its path or the key");

        // Everything this suite was told, across every scenario above, searched for what must never be said.
        string everything = string.Join('\n', said);
        Check(said.Count > 30 && !everything.Contains("key-", StringComparison.Ordinal) && !everything.Contains("opaque-", StringComparison.Ordinal) &&
            !everything.Contains("transfer-", StringComparison.Ordinal) && !everything.Contains("/upload/", StringComparison.Ordinal) &&
            !everything.Contains("/api/v1", StringComparison.Ordinal) && !everything.Contains("/collect", StringComparison.Ordinal) &&
            !everything.Contains("not-the-key-at-all", StringComparison.Ordinal),
            "no summary, problem or progress message contains a key, a transfer token, the endpoint's path or an upload address");
    }

    private static readonly AppStorePublisherOptions Options = new()
    {
        PollInterval = TimeSpan.FromMilliseconds(5),
        RetryDelay = TimeSpan.FromMilliseconds(5),
        ChunkAttempts = 3,
        ValidationTimeout = TimeSpan.FromSeconds(30)
    };

    private static HttpClient Client() => new(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false }) { Timeout = TimeSpan.FromSeconds(30) };

    private static async Task<PublicationReport> Publish(FakeAppStore store, string channel, IReadOnlyList<PublishArtifact> artifacts, bool dryRun = false,
        bool promotion = false, string? package = "com.example.application", string notes = "Release notes for this version.", string? key = null,
        string? application = null, List<string>? stages = null, HttpClient? client = null)
    {
        using HttpClient own = Client();
        PublishTarget target = new(new Uri(store.Endpoint), application ?? store.ApplicationId, key ?? store.ApiKey, channel, promotion, package);
        PublicationReport report = await new AppStorePublisher(client ?? own, Options).PublishAsync(target, new("project-identity", "v1.4.0", "1.4.0", notes), artifacts, dryRun,
            (stage, _) => { said.Add(stage); stages?.Add(stage); return Task.CompletedTask; }, CancellationToken.None);
        said.Add(report.Summary);
        said.AddRange(report.Artifacts.Where(artifact => artifact.Problem is not null).Select(artifact => artifact.Problem!));
        return report;
    }

    private static PublishArtifact File(string name, int size, IReadOnlyList<ArtifactRule>? rules = null)
    {
        byte[] bytes = RandomNumberGenerator.GetBytes(size);
        return new(name, size, Convert.ToHexStringLower(SHA256.HashData(bytes)), ReleaseDestinations.Declare(name, rules ?? [])!, () => new MemoryStream(bytes, writable: false));
    }

    private static int FreePort()
    {
        using System.Net.Sockets.TcpListener probe = new(System.Net.IPAddress.Loopback, 0);
        probe.Start();
        return ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
    }

    private static bool Refused(Action action)
    {
        try { action(); return false; }
        catch (Exception) { return true; }
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException("Assertion failed: " + name);
        assertions++;
    }
}
