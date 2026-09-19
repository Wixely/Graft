using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Graft.ManagedGit;

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
                Pack.Read(FixturePack(Enumerable.Range(0, 9).Select(_ => (3, new byte[Limits.ObjectBytes], (string?)null)).ToArray()), empty));
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
            Reject("stale ref", () => repository.Receive(Push([(Limits.Zero, commit.Id, "refs/heads/main")], Pack.Write([]))));
            Reject("duplicate ref", () => repository.Receive(Push([(commit.Id, commit.Id, "refs/heads/main"), (commit.Id, commit.Id, "refs/heads/main")], Pack.Write([]))));
            Reject("branch points at blob", () => repository.Receive(Push([(Limits.Zero, blob.Id, "refs/heads/blob")], packed)));
            Reject("traversal ref", () => repository.Receive(Push([(Limits.Zero, commit.Id, "refs/heads/../escape")], Pack.Write([]))));
            Reject("ref prefix collision", () => repository.Receive(Push([(Limits.Zero, commit.Id, "refs/heads/main/topic")], Pack.Write([]))));
            var missing = Commit(Limits.Zero, commit.Id, "missing tree");
            Reject("missing reachable tree", () => repository.Receive(Push([(commit.Id, missing.Id, "refs/heads/main")], Pack.Write([missing]))));
            var unrelated = Commit(tree.Id, null, "unrelated");
            Reject("non fast forward", () => repository.Receive(Push([(commit.Id, unrelated.Id, "refs/heads/main")], Pack.Write([unrelated]))));
            Reject("batch all or nothing", () => repository.Receive(Push([(Limits.Zero, commit.Id, "refs/heads/good"), (Limits.Zero, blob.Id, "refs/heads/bad")], packed)));
            Check("rejected pushes leave objects and refs untouched", () => Require(repository.Refs.Count == 1 && repository.Objects.Count == 2 && repository.Refs["refs/heads/main"] == commit.Id));
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

    private static void Rehash(byte[] bytes) => SHA1.HashData(bytes.AsSpan(0, bytes.Length - 20)).CopyTo(bytes.AsSpan(bytes.Length - 20));
    private static void Require(bool value)
    {
        if (!value) throw new InvalidOperationException("Self-check assertion failed");
    }
}
