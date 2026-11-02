using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Graft.Git;

public static class SelfChecks
{
    public static int Run()
    {
        var passed = 0;
        void Check(string name, Action action)
        {
            action(); passed++; Console.WriteLine("PASS: " + name);
        }
        void Reject(string name, Action action) => Check(name, () =>
        {
            try { action(); }
            catch (GitInputException) { return; }
            throw new InvalidOperationException("Accepted invalid input: " + name);
        });
        // A command refused on its own merits is reported by name in the status report rather than thrown, as Git does.
        void Refused(string name, string reference, Func<byte[]> action) => Check(name, () =>
        {
            if (!Encoding.ASCII.GetString(action()).Contains("ng " + reference + " ")) throw new InvalidOperationException("Accepted invalid input: " + name);
        });
        try
        {
            var empty = new Dictionary<string, GitObject>();
            var blob = new GitObject(3, "hello"u8.ToArray());
            var target = new GitObject(3, "hello!"u8.ToArray());
            byte[] delta = [5, 6, 0x90, 5, 1, (byte)'!'];
            var packed = Pack.Write([blob]);
            Check("plain pack round trip", () => Require(Pack.Read(packed, empty).Objects[blob.Id].Data.SequenceEqual(blob.Data)));
            Check("empty pack", () => Require(Pack.Read(Pack.Write([]), empty).Objects.Count == 0));
            Check("OFS delta", () =>
            {
                var result = Pack.Read(FixturePack([(3, blob.Data, null), (6, delta, null)]), empty);
                Require(result.OffsetDeltas == 1 && result.Objects[target.Id].Data.SequenceEqual(target.Data));
            });
            Check("REF delta forward base", () =>
            {
                var result = Pack.Read(FixturePack([(7, delta, blob.Id), (3, blob.Data, null)]), empty);
                Require(result.ReferenceDeltas == 1 && result.Objects.ContainsKey(target.Id));
            });
            Check("thin REF delta existing base", () =>
            {
                var result = Pack.Read(FixturePack([(7, delta, blob.Id)]), new Dictionary<string, GitObject> { [blob.Id] = blob });
                Require(result.Objects.ContainsKey(target.Id));
            });
            Check("delta copy default 64KiB", () =>
            {
                var basis = new byte[65536];
                Require(Pack.ApplyDelta(basis, [0x80, 0x80, 4, 0x80, 0x80, 4, 0x80]).Length == 65536);
            });
            Reject("truncated packet", () => new Cursor("0008ab"u8.ToArray()).Packet());
            Reject("invalid packet length", () => new Cursor("0001"u8.ToArray()).Packet());
            Reject("invalid packet hex", () => new Cursor("zzzz"u8.ToArray()).Packet());
            Reject("truncated pack", () => Pack.Read(packed[..^1], empty));
            Reject("bad pack checksum", () => { var data = (byte[])packed.Clone(); data[^1] ^= 1; Pack.Read(data, empty); });
            Reject("unsupported pack version", () =>
            {
                var data = (byte[])packed.Clone(); data[7] = 3; Rehash(data); Pack.Read(data, empty);
            });
            Reject("object count limit", () =>
            {
                var data = (byte[])packed.Clone(); BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(8), Limits.ObjectCount + 1); Rehash(data); Pack.Read(data, empty);
            });
            Reject("pack trailing data", () =>
            {
                var data = new byte[packed.Length + 1]; packed.AsSpan(0, packed.Length - 20).CopyTo(data); Rehash(data); Pack.Read(data, empty);
            });
            Reject("zlib corruption", () =>
            {
                var data = (byte[])packed.Clone(); data[13] = 0; Rehash(data); Pack.Read(data, empty);
            });
            Reject("missing zlib trailer despite valid pack checksum", () =>
            {
                var data = new byte[packed.Length - 4];
                packed.AsSpan(0, packed.Length - 24).CopyTo(data);
                Rehash(data); Pack.Read(data, empty);
            });
            Reject("inflated size mismatch", () =>
            {
                var data = (byte[])packed.Clone(); data[12] = 0x34; Rehash(data); Pack.Read(data, empty);
            });
            Reject("missing delta base", () => Pack.Read(FixturePack([(7, delta, blob.Id)]), empty));
            Reject("zero delta opcode", () => Pack.ApplyDelta(blob.Data, [5, 0, 0]));
            Reject("delta source mismatch", () => Pack.ApplyDelta(blob.Data, [4, 0]));
            Reject("delta copy out of bounds", () => Pack.ApplyDelta(blob.Data, [5, 6, 0x90, 6]));
            Reject("delta result underflow", () => Pack.ApplyDelta(blob.Data, [5, 1]));
            Reject("delta allocation limit", () => Pack.ApplyDelta(blob.Data, [5, 0xff, 0xff, 0xff, 0x7f]));
            Reject("oversized inflated object", () =>
                Pack.Read(FixturePack([(3, new byte[Limits.ObjectBytes + 1], null)]), empty));
            Reject("total expanded budget", () =>
            {
                using MemoryStream input = new(FixturePack(Enumerable.Range(0, 9)
                    .Select(_ => (3, new byte[1024 * 1024], (string?)null)).ToArray()));
                Pack.Read(input, empty, expandedBudget: 8 * 1024 * 1024);
            });
            Check("a delta chain as deep as Git writes by default is read", () =>
            {
                var entries = new List<(int, byte[], string?)> { (3, [1], null) };
                for (var i = 0; i < 50; i++) entries.Add((6, [1, 1, 1, (byte)(i + 2)], null));
                Require(Pack.Read(FixturePack(entries.ToArray()), empty).Objects.Count == 51);
            });
            Reject("delta depth limit", () =>
            {
                var entries = new List<(int, byte[], string?)> { (3, [1], null) };
                for (var i = 0; i <= Limits.DeltaDepth; i++) entries.Add((6, [1, 1, 1, (byte)(i + 2)], null));
                Pack.Read(FixturePack(entries.ToArray()), empty);
            });
            Check("cancellation", () =>
            {
                using var cts = new CancellationTokenSource(); cts.Cancel();
                try { Pack.Read(packed, empty, cts.Token); }
                catch (OperationCanceledException) { return; }
                throw new InvalidOperationException("Cancellation ignored");
            });
            Check("safe ref validation", () =>
            {
                Require(Limits.IsRef("refs/heads/feature/topic"));
                foreach (var bad in new[] { "refs/heads/../bad", "refs/heads/a.lock", "refs/heads/a\\b", "refs/heads/a b", "refs/heads/.hidden", "refs/heads/a@{b", "refs/heads/a/", "refs/heads/a..b" })
                    Require(!Limits.IsRef(bad));
            });
            var tree = new GitObject(2, []);
            var commit = Commit(tree.Id, null, "first");
            var repository = new Repository();
            var firstPack = Pack.Write([tree, commit]);
            Check("receive complete commit", () =>
            {
                repository.Receive(Push([(Limits.Zero, commit.Id, "refs/heads/main")], firstPack));
                Require(repository.Refs["refs/heads/main"] == commit.Id);
            });
            Refused("stale ref", "refs/heads/main", () => repository.Receive(Push([(Limits.Zero, commit.Id, "refs/heads/main")], Pack.Write([]))));
            Reject("duplicate ref", () => repository.Receive(Push([(commit.Id, commit.Id, "refs/heads/main"), (commit.Id, commit.Id, "refs/heads/main")], Pack.Write([]))));
            Refused("branch points at blob", "refs/heads/blob", () => repository.Receive(Push([(Limits.Zero, blob.Id, "refs/heads/blob")], packed)));
            Reject("traversal ref", () => repository.Receive(Push([(Limits.Zero, commit.Id, "refs/heads/../escape")], Pack.Write([]))));
            Refused("ref prefix collision", "refs/heads/main/topic", () => repository.Receive(Push([(Limits.Zero, commit.Id, "refs/heads/main/topic")], Pack.Write([]))));
            var missing = Commit(Limits.Zero, commit.Id, "missing tree");
            Reject("missing reachable tree", () => repository.Receive(Push([(commit.Id, missing.Id, "refs/heads/main")], Pack.Write([missing]))));
            var unrelated = Commit(tree.Id, null, "unrelated");
            Refused("non fast forward", "refs/heads/main", () => repository.Receive(Push([(commit.Id, unrelated.Id, "refs/heads/main")], Pack.Write([unrelated]))));
            Refused("deletion off unless the host allows it", "refs/heads/main", () => repository.Receive(Push([(commit.Id, Limits.Zero, "refs/heads/main")], [])));
            Check("rejected pushes leave objects and refs untouched", () => Require(repository.Refs.Count == 1 && repository.Objects.Count == 2 && repository.Refs["refs/heads/main"] == commit.Id));
            Check("a refused command does not hold back the others, as in Git without atomic", () =>
            {
                var batch = new Repository();
                batch.Receive(Push([(Limits.Zero, commit.Id, "refs/heads/main")], firstPack));
                var report = Encoding.ASCII.GetString(batch.Receive(Push([(Limits.Zero, commit.Id, "refs/heads/good"), (Limits.Zero, blob.Id, "refs/heads/bad")], packed)));
                Require(report.Contains("ok refs/heads/good\n") && report.Contains("ng refs/heads/bad ") && batch.Refs.ContainsKey("refs/heads/good") && !batch.Refs.ContainsKey("refs/heads/bad"));
            });
            Check("deletion by push when allowed, never the default branch", () =>
            {
                var pruned = new Repository { AcceptsDeletion = true };
                pruned.Receive(Push([(Limits.Zero, commit.Id, "refs/heads/main"), (Limits.Zero, commit.Id, "refs/heads/done")], firstPack));
                var report = Encoding.ASCII.GetString(pruned.Receive(Push([(commit.Id, Limits.Zero, "refs/heads/done"), (commit.Id, Limits.Zero, "refs/heads/main")], [])));
                Require(report.Contains("ok refs/heads/done\n") && report.Contains("ng refs/heads/main ") && !pruned.Refs.ContainsKey("refs/heads/done") && pruned.Refs.ContainsKey("refs/heads/main"));
            });
            Reject("unadvertised upload want", () =>
            {
                using var request = new MemoryStream(); Packet.Write(request, "want " + tree.Id + "\n"); Packet.Flush(request); Packet.Write(request, "done\n"); repository.Upload(request.ToArray());
            });
            Reject("unsupported shallow command", () =>
            {
                using var request = new MemoryStream(); Packet.Write(request, "deepen 1\n"); repository.Upload(request.ToArray());
            });
            Check("gitlink not traversed", () =>
            {
                using var data = new MemoryStream(); data.Write("160000 sub\0"u8); data.Write(new byte[20]);
                Require(Graph.Links(new GitObject(2, data.ToArray())).Count == 0);
            });
            Check("symlink stored as blob", () =>
            {
                using var data = new MemoryStream(); data.Write("120000 link\0"u8); data.Write(Convert.FromHexString(blob.Id));
                Require(Graph.Links(new GitObject(2, data.ToArray())).Single() == (blob.Id, 3));
            });
            // Import by address (ADR 0042): the fetch client against this engine's own server half, over both protocol versions,
            // and the mirror rules: follow additions and fast-forwards, keep what the upstream deletes, preserve what it rewrites.
            foreach (bool version2 in new[] { true, false })
            {
                string label = version2 ? " (protocol v2)" : " (protocol v0)";
                var upstream = new Repository { AcceptsDeletion = true };
                var second = Commit(tree.Id, commit.Id, "second");
                var tag = new GitObject(4, Encoding.UTF8.GetBytes("object " + commit.Id + "\ntype commit\ntag v1\ntagger Fixture <fixture@example.invalid> 1700000000 +0000\n\nv1\n"));
                upstream.Receive(Push([(Limits.Zero, second.Id, "refs/heads/main"), (Limits.Zero, commit.Id, "refs/heads/old"), (Limits.Zero, tag.Id, "refs/tags/v1")],
                    Pack.Write([tree, commit, second, tag])));
                using var client = new HttpClient(new ServedRepository(upstream, version2));
                var remote = new GitRemote(client);
                var mirror = new Repository();
                MirrorResult Sync(DateTimeOffset at)
                {
                    RemoteAdvertisement listed = remote.ListAsync(new Uri("https://upstream.invalid/owner/repo.git"), CancellationToken.None).GetAwaiter().GetResult();
                    Require(listed.ProtocolVersion == (version2 ? 2 : 0) && listed.HeadBranch == "main");
                    var wants = listed.Refs.Values.Distinct().Where(id => !mirror.Objects.ContainsKey(id)).ToList();
                    var haves = mirror.Refs.Values.Distinct().Where(id => mirror.Objects.TryGetValue(id, out var value) && value.Type == 1).ToList();
                    if (wants.Count == 0) return RepositoryMirror.Apply(mirror, null, listed.Refs, at);
                    using var pack = new MemoryStream();
                    remote.FetchAsync(listed, wants, haves, pack, CancellationToken.None).GetAwaiter().GetResult();
                    pack.Position = 0;
                    return RepositoryMirror.Apply(mirror, pack, listed.Refs, at);
                }
                Check("an import copies every branch and tag" + label, () =>
                {
                    MirrorResult first = Sync(DateTimeOffset.UnixEpoch);
                    Require(first.Changed && mirror.Refs.Count == 3 && mirror.Refs["refs/heads/main"] == second.Id && mirror.Refs["refs/tags/v1"] == tag.Id &&
                        mirror.Objects.ContainsKey(tree.Id) && first.References.All(reference => reference.Change == MirrorRefChange.Created));
                });
                Check("an update with nothing new changes nothing" + label, () => Require(!Sync(DateTimeOffset.UnixEpoch.AddHours(1)).Changed));
                var third = Commit(tree.Id, second.Id, "third");
                var rewrite = Commit(tree.Id, null, "rewritten old");
                upstream.Receive(Push([(second.Id, third.Id, "refs/heads/main"), (commit.Id, Limits.Zero, "refs/heads/old")], Pack.Write([third])));
                upstream.Receive(Push([(Limits.Zero, rewrite.Id, "refs/heads/old"), (tag.Id, Limits.Zero, "refs/tags/v1")], Pack.Write([rewrite])));
                Check("an update follows a fast-forward, preserves a rewrite, and keeps a deleted tag" + label, () =>
                {
                    MirrorResult update = Sync(DateTimeOffset.UnixEpoch.AddHours(2));
                    string preserved = RepositoryMirror.PreservedPrefix + "19700101T020000Z/heads/old";
                    Require(mirror.Refs["refs/heads/main"] == third.Id && mirror.Refs["refs/heads/old"] == rewrite.Id && mirror.Refs[preserved] == commit.Id &&
                        mirror.Refs["refs/tags/v1"] == tag.Id &&
                        update.References.Single(reference => reference.Name == "refs/heads/main").Change == MirrorRefChange.Updated &&
                        update.References.Single(reference => reference.Name == "refs/heads/old").Preserved == preserved &&
                        update.References.Single(reference => reference.Name == "refs/tags/v1").Change == MirrorRefChange.Kept);
                });
                Check("a preserved reference is fetchable and not pushable" + label, () =>
                {
                    string preserved = RepositoryMirror.PreservedPrefix + "19700101T020000Z/heads/old";
                    Require(Limits.IsRef(preserved) && !Limits.IsClientRef(preserved) && Encoding.ASCII.GetString(mirror.Advertise("git-upload-pack")).Contains(preserved));
                });
                Reject("a client cannot push into the preserved namespace" + label,
                    () => mirror.Receive(Push([(Limits.Zero, commit.Id, RepositoryMirror.PreservedPrefix + "x/heads/main")], [])));
                Check("an upstream that is gone answers as unavailable" + label, () =>
                {
                    using var gone = new HttpClient(new ServedRepository(upstream, version2) { Gone = true });
                    try { new GitRemote(gone).ListAsync(new Uri("https://upstream.invalid/owner/repo.git"), CancellationToken.None).GetAwaiter().GetResult(); }
                    catch (GitRemoteException error) when (error.Failure == RemoteFailure.Unavailable) { return; }
                    throw new InvalidOperationException("a missing upstream was not reported as unavailable");
                });
            }
            Check("an address that is not a Git server is refused as such", () =>
            {
                using var html = new HttpClient(new StaticAnswer());
                try { new GitRemote(html).ListAsync(new Uri("https://example.invalid/page"), CancellationToken.None).GetAwaiter().GetResult(); }
                catch (GitRemoteException error) when (error.Failure == RemoteFailure.Protocol) { return; }
                throw new InvalidOperationException("an HTML page was taken for a repository");
            });
            Console.WriteLine($"SELF-CHECKS: PASS ({passed})");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"SELF-CHECKS: FAIL after {passed}: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    public static GitObject Commit(string tree, string? parent, string message) => new(1, Encoding.UTF8.GetBytes(
        "tree " + tree + "\n" + (parent is null ? "" : "parent " + parent + "\n") +
        "author Fixture <fixture@example.invalid> 1700000000 +0000\ncommitter Fixture <fixture@example.invalid> 1700000000 +0000\n\n" + message + "\n"));

    public static byte[] Push((string Old, string New, string Name)[] commands, byte[] pack)
    {
        using var output = new MemoryStream();
        for (var i = 0; i < commands.Length; i++)
        {
            var c = commands[i]; Packet.Write(output, c.Old + " " + c.New + " " + c.Name + (i == 0 ? "\0report-status ofs-delta" : ""));
        }
        Packet.Flush(output); output.Write(pack); return output.ToArray();
    }

    // Test fixtures only; production writes complete, non-delta packs.
    public static byte[] FixturePack((int Type, byte[] Data, string? BaseId)[] entries)
    {
        using var output = new MemoryStream(); output.Write("PACK"u8);
        Span<byte> number = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(number, 2); output.Write(number);
        BinaryPrimitives.WriteUInt32BigEndian(number, (uint)entries.Length); output.Write(number);
        var previousOffset = 12;
        foreach (var entry in entries)
        {
            var at = (int)output.Position;
            Pack.WriteHeader(output, entry.Type, entry.Data.Length);
            if (entry.Type == 7) output.Write(Convert.FromHexString(entry.BaseId!));
            if (entry.Type == 6)
            {
                var distance = at - previousOffset;
                var encoded = new List<byte> { (byte)(distance & 127) };
                while ((distance >>= 7) > 0) { distance--; encoded.Add((byte)(128 | (distance & 127))); }
                encoded.Reverse(); output.Write(encoded.ToArray());
            }
            Pack.Compress(output, entry.Data);
            previousOffset = at;
        }
        output.Write(SHA1.HashData(output.GetBuffer().AsSpan(0, (int)output.Length)));
        return output.ToArray();
    }

    /// <summary>Smart HTTP served from an in-memory repository, as the forge serves it, for the fetch client to talk to.</summary>
    private sealed class ServedRepository(Repository repository, bool version2) : HttpMessageHandler
    {
        public bool Gone { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Gone) return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound) { RequestMessage = request };
            bool asksV2 = version2 && request.Headers.TryGetValues("Git-Protocol", out var values) && values.Contains("version=2");
            string path = request.RequestUri!.AbsolutePath;
            byte[] body;
            string type;
            if (request.Method == HttpMethod.Get && path.EndsWith("/info/refs", StringComparison.Ordinal))
            {
                body = asksV2 ? repository.AdvertiseV2("git-upload-pack") : repository.Advertise("git-upload-pack");
                type = "application/x-git-upload-pack-advertisement";
            }
            else if (request.Method == HttpMethod.Post && path.EndsWith("/git-upload-pack", StringComparison.Ordinal))
            {
                using var input = new MemoryStream(await request.Content!.ReadAsByteArrayAsync(cancellationToken));
                using var output = new MemoryStream();
                if (asksV2) repository.ServeV2(input, output, cancellationToken); else repository.Upload(input, output, cancellationToken);
                body = output.ToArray();
                type = "application/x-git-upload-pack-result";
            }
            else return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound) { RequestMessage = request };
            var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK) { RequestMessage = request, Content = new ByteArrayContent(body) };
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(type);
            return response;
        }
    }

    private sealed class StaticAnswer : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK) { RequestMessage = request, Content = new StringContent("<html></html>") };
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/html");
            return Task.FromResult(response);
        }
    }

    private static void Rehash(byte[] bytes) => SHA1.HashData(bytes.AsSpan(0, bytes.Length - 20)).CopyTo(bytes.AsSpan(bytes.Length - 20));
    private static void Require(bool value)
    {
        if (!value) throw new InvalidOperationException("Self-check assertion failed");
    }
}
