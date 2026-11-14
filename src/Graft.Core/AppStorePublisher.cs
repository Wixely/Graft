using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Graft.Core;

/// <summary>Where a release is going, with the key that lets it. Never logged, never put in a message.</summary>
public sealed record PublishTarget(Uri Endpoint, string ApplicationId, string ApiKey, string Channel, bool AllowProductionPromotion, string? PackageName);

/// <summary>The release being published: whose it is, its tag, the version that tag names, and its notes.</summary>
public sealed record PublishRelease(string ProjectIdentity, string TagName, string Version, string Notes);

/// <summary>One file of a release, declared, with a way to read it from the start as often as a retry needs.</summary>
public sealed record PublishArtifact(string FileName, long Size, string Sha256, ArtifactDeclaration Declaration, Func<Stream> Open);

/// <summary>
/// What became of one file. <see cref="State"/> is planned, accepted, published, rejected, failed or
/// not-attempted; both hashes are kept because a store that signs a build changes its bytes.
/// </summary>
public sealed record ArtifactOutcome(string FileName, string Format, string Platform, string Architecture, string State,
    string? BuildId, string UploadedSha256, string? StoreSha256, string? Problem);

/// <summary>How a publication ended, in words that are safe to keep and show.</summary>
public sealed record PublicationReport(bool Succeeded, string Summary, IReadOnlyList<ArtifactOutcome> Artifacts, bool? ChannelVerified);

/// <summary>Something that can take a release somewhere else. One kind exists; this is where a second would go.</summary>
public interface IReleasePublisher
{
    string Kind { get; }

    /// <summary>
    /// Publishes, or with <paramref name="dryRun"/> only checks the configuration and what the destination says
    /// it accepts, starting no upload. Never throws for anything the destination did: the report says it.
    /// </summary>
    Task<PublicationReport> PublishAsync(PublishTarget target, PublishRelease release, IReadOnlyList<PublishArtifact> artifacts,
        bool dryRun, Func<string, IReadOnlyList<ArtifactOutcome>, Task>? progress, CancellationToken cancellationToken);
}

public sealed record AppStorePublisherOptions
{
    /// <summary>How long between two looks at a build the store is validating.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(3);
    /// <summary>How long a build may stay in validation before the publication gives up on waiting for it.</summary>
    public TimeSpan ValidationTimeout { get; init; } = TimeSpan.FromMinutes(20);
    /// <summary>How many times one chunk is sent before the upload is called failed.</summary>
    public int ChunkAttempts { get; init; } = 4;
    /// <summary>The first wait between two attempts at a chunk; it doubles.</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(2);
}

/// <summary>
/// Publishes a release to an application store through its upload-session contract (ADR 0052): ask what it
/// accepts, open one resumable upload per file, send the file in hashed chunks, wait for the store to validate
/// it, and only when every file has been accepted put the builds on a channel and read them back.
///
/// <para>
/// The client it is given must not follow redirects. A redirect is treated as a failure either way, because a
/// key sent to wherever a response pointed is a key given away.
/// </para>
///
/// <para>
/// Nothing this class says contains the key, a transfer token, or any path the store issued: every message
/// passes through one place that removes them, including messages the store itself wrote.
/// </para>
/// </summary>
public sealed class AppStorePublisher(HttpClient client, AppStorePublisherOptions? options = null) : IReleasePublisher
{
    private const int MaximumChunkBytes = 64 * 1024 * 1024;
    private const int MaximumResponseBytes = 4 * 1024 * 1024;
    private readonly AppStorePublisherOptions options = options ?? new();

    public string Kind => ReleaseDestinations.AppStoreKind;

    public async Task<PublicationReport> PublishAsync(PublishTarget target, PublishRelease release, IReadOnlyList<PublishArtifact> artifacts,
        bool dryRun, Func<string, IReadOnlyList<ArtifactOutcome>, Task>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(artifacts);
        Redactor redactor = new(target);
        ArtifactOutcome[] outcomes = [.. artifacts.Select(artifact => Outcome(artifact, "planned"))];
        bool? channelVerified = null;

        async Task Say(string stage)
        {
            if (progress is not null) await progress(redactor.Clean(stage), [.. outcomes]);
        }

        try
        {
            bool promoting = target.Channel != "none";
            if (!ReleaseDestinations.Channels.Contains(target.Channel)) throw new PublishFailure("The channel is none, stable or beta.");
            if (promoting && !target.AllowProductionPromotion)
                throw new PublishFailure("This destination publishes to the " + target.Channel + " channel, and production promotion is not enabled for it. Nothing was sent.");
            if (release.Version.Length is 0 or > ReleaseDestinations.MaximumVersionLength)
                throw new PublishFailure("The version the tag names is empty or longer than " + ReleaseDestinations.MaximumVersionLength + " characters.");
            if (release.Notes.Length > ReleaseDestinations.MaximumNotesLength)
                throw new PublishFailure("The release notes are longer than the " + ReleaseDestinations.MaximumNotesLength + " characters the store accepts. Shorten them and publish again.");
            if (!dryRun && artifacts.Count == 0) throw new PublishFailure("The release has no files to publish to this destination.");
            foreach (PublishArtifact artifact in artifacts)
            {
                if (artifact.FileName.Length > ReleaseDestinations.MaximumFileNameLength)
                    throw new PublishFailure("'" + artifact.FileName + "' has a name longer than " + ReleaseDestinations.MaximumFileNameLength + " characters.");
                if (artifact.Size <= 0) throw new PublishFailure("'" + artifact.FileName + "' is empty.");
                if (artifact.Declaration.Format == "apk" && string.IsNullOrEmpty(target.PackageName))
                    throw new PublishFailure("'" + artifact.FileName + "' is an Android package, and this destination has no package name.");
            }

            await Say("Asking the store what it accepts.");
            Capabilities capabilities = await ReadCapabilitiesAsync(target, cancellationToken);
            if (promoting && !capabilities.ProductionPromotionAvailable)
                throw new PublishFailure("The store does not offer production promotion, and this destination publishes to " + target.Channel + ". Nothing was sent.");
            foreach (PublishArtifact artifact in artifacts)
            {
                if (artifact.Size > capabilities.MaximumArtifactBytes)
                    throw new PublishFailure("'" + artifact.FileName + "' is " + artifact.Size.ToString(CultureInfo.InvariantCulture) + " bytes, more than the " +
                        capabilities.MaximumArtifactBytes.ToString(CultureInfo.InvariantCulture) + " the store accepts. Nothing was sent.");
                if (!capabilities.Formats.Contains(artifact.Declaration.Format, StringComparer.Ordinal))
                    throw new PublishFailure("The store does not accept the '" + artifact.Declaration.Format + "' format '" + artifact.FileName + "' would be sent as. Nothing was sent.");
                if (!capabilities.Platforms.Contains(artifact.Declaration.Platform, StringComparer.Ordinal))
                    throw new PublishFailure("The store does not accept the platform '" + artifact.Declaration.Platform + "' declared for '" + artifact.FileName + "'. Nothing was sent.");
            }
            if (dryRun)
                return new(true, artifacts.Count == 0
                    ? "The store answered and accepts this key's request for its capabilities. No upload was started."
                    : "The store answered, and it accepts every one of the " + Count(artifacts.Count, "file") + " as declared. No upload was started.",
                    outcomes, null);

            for (int index = 0; index < artifacts.Count; index++)
            {
                await Say("Uploading " + artifacts[index].FileName + ".");
                try { outcomes[index] = await UploadAsync(target, release, artifacts[index], redactor, cancellationToken); }
                catch (Exception error) when (Describe(error, cancellationToken) is { } problem)
                { outcomes[index] = Outcome(artifacts[index], "failed") with { Problem = redactor.Clean(problem) }; }
                if (outcomes[index].State == "accepted") continue;
                for (int rest = index + 1; rest < artifacts.Count; rest++) outcomes[rest] = Outcome(artifacts[rest], "not-attempted");
                return new(false, redactor.Clean("'" + artifacts[index].FileName + "' was not accepted: " + outcomes[index].Problem +
                    " Nothing was published" + (index > 0 ? "; the files accepted before it stay in the store as unpublished builds." : ".")), outcomes, null);
            }

            if (!promoting)
                return new(true, Count(artifacts.Count, "file") + " accepted by the store as " + (artifacts.Count == 1 ? "an unpublished build." : "unpublished builds."), outcomes, null);

            // The file the channel should report goes last, so it is the one the channel is left on.
            int[] order = [.. Enumerable.Range(0, artifacts.Count).OrderBy(index => artifacts[index].Declaration.Main ? 1 : 0)];
            Dictionary<string, JsonElement> builds = await ReadBuildsAsync(target, cancellationToken);
            foreach (int index in order)
            {
                string buildId = outcomes[index].BuildId!;
                if (builds.TryGetValue(buildId.ToLowerInvariant(), out JsonElement existing) && Flag(existing, "published") &&
                    string.Equals(Text(existing, "channel"), target.Channel, StringComparison.OrdinalIgnoreCase))
                {
                    outcomes[index] = outcomes[index] with { State = "published" };
                    continue;
                }
                await Say("Publishing " + artifacts[index].FileName + " to " + target.Channel + ".");
                try { await PromoteAsync(target, release, buildId, cancellationToken); }
                catch (StoreRefusal refusal) when (refusal.Status == 409)
                {
                    int done = outcomes.Count(outcome => outcome.State == "published");
                    outcomes[index] = outcomes[index] with { Problem = "The application changed in the store while this was being published." };
                    return new(false, "The store reported a revision conflict: something else changed the application while this release was being published. " +
                        done.ToString(CultureInfo.InvariantCulture) + " of " + Count(artifacts.Count, "build") + " had been published. " +
                        "Graft did not try again with a newer revision. Look at the application in the store, then publish again.", outcomes, null);
                }
                outcomes[index] = outcomes[index] with { State = "published" };
            }

            await Say("Reading the builds back.");
            List<string> doubts = [];
            builds = await ReadBuildsAsync(target, cancellationToken);
            for (int index = 0; index < artifacts.Count; index++)
            {
                PublishArtifact artifact = artifacts[index];
                if (!builds.TryGetValue(outcomes[index].BuildId!.ToLowerInvariant(), out JsonElement build))
                { doubts.Add("the store does not list the build of '" + artifact.FileName + "'"); continue; }
                string? stored = Text(build, "sha256")?.ToLowerInvariant();
                outcomes[index] = outcomes[index] with { StoreSha256 = stored };
                if (!Flag(build, "published")) doubts.Add("'" + artifact.FileName + "' is not marked published");
                if (Text(build, "channel") is { } channel && !string.Equals(channel, target.Channel, StringComparison.OrdinalIgnoreCase))
                    doubts.Add("'" + artifact.FileName + "' is on the " + channel + " channel");
                if (Text(build, "applicationId") is { } application && !string.Equals(application, target.ApplicationId, StringComparison.OrdinalIgnoreCase))
                    doubts.Add("'" + artifact.FileName + "' belongs to another application");
                if (Text(build, "version") is { } version && version != release.Version)
                    doubts.Add("'" + artifact.FileName + "' is listed as version " + version);
                if (Text(build, "packageName") is { } package && target.PackageName is not null && artifact.Declaration.Format == "apk" && package != target.PackageName)
                    doubts.Add("'" + artifact.FileName + "' is listed under another package name");
                // A store that signs an Android package changes its bytes, so a different hash there is expected and
                // recorded. For any other file it means what is in the store is not what was sent.
                if (stored is not null && stored != artifact.Sha256.ToLowerInvariant() && artifact.Declaration.Format != "apk")
                    doubts.Add("the store's copy of '" + artifact.FileName + "' has a different SHA-256 from the file that was sent");
            }

            int mainIndex = artifacts.Count == 1 ? 0 : -1;
            for (int index = 0; index < artifacts.Count && mainIndex < 0; index++)
                if (artifacts[index].Declaration.Main) mainIndex = index;
            JsonElement? current = (await ReadListAsync(target, "channels", "channels", cancellationToken))
                .Where(entry => string.Equals(Text(entry, "applicationId"), target.ApplicationId, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(Text(entry, "name"), target.Channel, StringComparison.OrdinalIgnoreCase))
                .Select(entry => (JsonElement?)entry).FirstOrDefault();
            string? currentBuild = current is null ? null : Text(current.Value, "currentBuildId");
            if (current is null) { channelVerified = false; doubts.Add("the store lists no " + target.Channel + " channel for the application"); }
            else if (mainIndex >= 0)
            {
                channelVerified = string.Equals(currentBuild, outcomes[mainIndex].BuildId, StringComparison.OrdinalIgnoreCase) &&
                    (Text(current.Value, "currentVersion") is not { } currentVersion || currentVersion == release.Version);
                if (channelVerified == false) doubts.Add("the " + target.Channel + " channel does not report the build of '" + artifacts[mainIndex].FileName + "'");
            }
            else
            {
                // Several files and none marked main: the channel can only be asked whether it is on one of them.
                channelVerified = outcomes.Any(outcome => string.Equals(outcome.BuildId, currentBuild, StringComparison.OrdinalIgnoreCase));
                if (channelVerified == false) doubts.Add("the " + target.Channel + " channel reports a build that is not one of this release's");
            }

            if (doubts.Count > 0)
                return new(false, redactor.Clean("Published, but reading the store back did not confirm it: " + string.Join("; ", doubts) + "."), outcomes, channelVerified);
            return new(true, Count(artifacts.Count, "build") + " published to the " + target.Channel + " channel as version " + release.Version +
                ", and read back from the store.", outcomes, channelVerified);
        }
        catch (Exception error) when (Describe(error, cancellationToken) is { } problem)
        {
            return new(false, redactor.Clean(problem), outcomes, channelVerified);
        }
    }

    /// <summary>
    /// The words for a failure, or null for one that is not this class's to explain: a cancellation the caller
    /// asked for, or a defect. A network failure's own message names a host at most, never a path.
    /// </summary>
    private static string? Describe(Exception error, CancellationToken cancellationToken) => error switch
    {
        PublishFailure failure => failure.Message,
        StoreRefusal refusal => refusal.Message,
        OperationCanceledException when cancellationToken.IsCancellationRequested => null,
        OperationCanceledException => "The store did not answer in time.",
        HttpRequestException request => "The store could not be reached: " + request.Message,
        IOException io => "The connection to the store was lost: " + io.Message,
        JsonException => "The store answered with something that is not the JSON this contract describes.",
        _ => null
    };

    private async Task<ArtifactOutcome> UploadAsync(PublishTarget target, PublishRelease release, PublishArtifact artifact,
        Redactor redactor, CancellationToken cancellationToken)
    {
        UploadSession session = await OpenSessionAsync(target, release, artifact, redactor, cancellationToken);
        if (session.State == "Uploading")
        {
            // Chunks are sent in order, so what the store says it holds is where to carry on from. If that turns out
            // not to be so, the finalize says it, and everything is sent again: a chunk repeated byte for byte is
            // accepted, so sending too much is safe and sending too little is not.
            int skip = (int)Math.Min(session.TotalChunks, Math.Max(0, session.ReceivedBytes) / session.ChunkSize);
            session = await SendChunksAsync(target, release, artifact, session, skip, session.TotalChunks, redactor, cancellationToken);
            try { session = session with { State = await FinalizeAsync(target, session.Id, cancellationToken) }; }
            catch (StoreRefusal refusal) when (refusal.Status == 409 && skip > 0)
            {
                session = await SendChunksAsync(target, release, artifact, session, 0, skip, redactor, cancellationToken);
                session = session with { State = await FinalizeAsync(target, session.Id, cancellationToken) };
            }
        }

        DateTimeOffset deadline = DateTimeOffset.UtcNow + options.ValidationTimeout;
        JsonElement status = default;
        string state = session.State;
        string? buildId = session.BuildId;
        string? code = session.ErrorCode, detail = session.ErrorDetail;
        while (state is "Validating" or "Uploading")
        {
            if (DateTimeOffset.UtcNow > deadline)
                throw new PublishFailure("The store was still validating the file after " + ((int)options.ValidationTimeout.TotalMinutes).ToString(CultureInfo.InvariantCulture) +
                    " minutes. Publish again later: the upload is kept and will not be sent twice.");
            await Task.Delay(options.PollInterval, cancellationToken);
            status = await SendJsonAsync(target, HttpMethod.Get, "upload-sessions/" + session.Id, null, "upload session", cancellationToken);
            state = Text(status, "state") ?? "";
            buildId = Text(status, "buildId");
            code = Text(status, "errorCode");
            detail = Text(status, "errorDetail");
        }

        return state switch
        {
            "Accepted" when buildId is { Length: > 0 } => Outcome(artifact, "accepted") with { BuildId = buildId },
            "Accepted" => throw new PublishFailure("The store accepted the file and named no build for it."),
            "Rejected" => Outcome(artifact, "rejected") with { Problem = redactor.Clean("The store rejected it" + (code is null ? "" : " (" + code + ")") + (detail is null ? "." : ": " + detail)) },
            "Expired" => Outcome(artifact, "failed") with { Problem = "The store's upload session for this file expired before it was complete, and the store keeps the expired session under the same key." },
            "Aborted" => Outcome(artifact, "failed") with { Problem = "The store's upload session for this file was aborted." },
            _ => throw new PublishFailure("The store reported an upload state this contract does not describe.")
        };
    }

    private async Task<UploadSession> OpenSessionAsync(PublishTarget target, PublishRelease release, PublishArtifact artifact,
        Redactor redactor, CancellationToken cancellationToken)
    {
        using MemoryStream body = new();
        using (Utf8JsonWriter json = new(body))
        {
            json.WriteStartObject();
            json.WriteString("applicationId", target.ApplicationId);
            json.WriteString("idempotencyKey", ReleaseDestinations.IdempotencyKey(release.ProjectIdentity, release.TagName, target.ApplicationId, artifact.FileName, artifact.Sha256));
            json.WriteString("originalFileName", artifact.FileName);
            json.WriteNumber("expectedSize", artifact.Size);
            json.WriteString("expectedSha256", artifact.Sha256.ToLowerInvariant());
            if (artifact.Declaration.Format == "apk") json.WriteString("packageName", target.PackageName);
            else json.WriteNull("packageName");
            json.WriteString("version", release.Version);
            json.WriteString("releaseNotes", release.Notes);
            json.WriteString("format", artifact.Declaration.Format);
            json.WriteString("platform", artifact.Declaration.Platform);
            json.WriteString("architecture", artifact.Declaration.Architecture);
            json.WriteEndObject();
        }
        JsonElement answer;
        try { answer = await SendJsonAsync(target, HttpMethod.Post, "upload-sessions", body.ToArray(), "application", cancellationToken); }
        catch (StoreRefusal refusal) when (refusal.Status == 409)
        {
            throw new PublishFailure("The store already holds an upload under this file's key with different details (" + refusal.Code +
                "). A file that was changed after an earlier attempt needs a new name or a new release.");
        }
        string id = Text(answer, "id") is { Length: > 0 } text && Guid.TryParse(text, out _) ? text
            : throw new PublishFailure("The store opened an upload session without a usable identifier.");
        string state = Text(answer, "state") ?? "";
        UploadSession session = new(id, state, Number(answer, "receivedBytes"), Number(answer, "chunkSize"), (int)Number(answer, "totalChunks"),
            Text(answer, "chunkPathTemplate"), Text(answer, "transferToken"), Text(answer, "buildId"), Text(answer, "errorCode"), Text(answer, "errorDetail"));
        redactor.Add(session.TransferToken);
        redactor.AddPath(session.ChunkPathTemplate);
        if (state != "Uploading") return session;
        if (Number(answer, "expectedSize") != artifact.Size)
            throw new PublishFailure("The store's upload session expects a different size from the file's.");
        if (session.ChunkSize is <= 0 or > MaximumChunkBytes || session.TotalChunks != (artifact.Size + session.ChunkSize - 1) / session.ChunkSize)
            throw new PublishFailure("The store's upload session describes chunks that do not add up to the file.");
        if (string.IsNullOrEmpty(session.TransferToken)) throw new PublishFailure("The store opened an upload session without a transfer token.");
        ChunkAddress(target.Endpoint, session.ChunkPathTemplate, 0);
        return session;
    }

    /// <summary>
    /// Where one chunk goes: the path the store issued, on the store's own origin and nowhere else. A path that
    /// resolves to another scheme, host or port, or that carries credentials, is refused before anything is sent.
    /// </summary>
    public static Uri ChunkAddress(Uri endpoint, string? template, int index)
    {
        const string refusal = "The store issued an upload address that is not a path on its own origin. Nothing was sent to it.";
        if (template is not { Length: > 1 and <= 2048 } || template[0] != '/' || template[1] is '/' or '\\' ||
            !template.Contains("{index}", StringComparison.Ordinal) || template.Any(character => char.IsControl(character) || character is '\\' or '@' or '#'))
            throw new PublishFailure(refusal);
        string path = template.Replace("{index}", index.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
        Uri origin = new(endpoint.GetLeftPart(UriPartial.Authority));
        if (!Uri.TryCreate(origin, path, out Uri? resolved) || resolved.Scheme != endpoint.Scheme ||
            !string.Equals(resolved.Host, endpoint.Host, StringComparison.OrdinalIgnoreCase) || resolved.Port != endpoint.Port ||
            !string.IsNullOrEmpty(resolved.UserInfo))
            throw new PublishFailure(refusal);
        return resolved;
    }

    private async Task<UploadSession> SendChunksAsync(PublishTarget target, PublishRelease release, PublishArtifact artifact, UploadSession session,
        int from, int until, Redactor redactor, CancellationToken cancellationToken)
    {
        if (from >= until) return session;
        byte[] buffer = new byte[session.ChunkSize];
        await using Stream source = artifact.Open();
        source.Position = from * session.ChunkSize;
        for (int index = from; index < until; index++)
        {
            int length = (int)Math.Min(session.ChunkSize, artifact.Size - (long)index * session.ChunkSize);
            await source.ReadExactlyAsync(buffer.AsMemory(0, length), cancellationToken);
            string digest = Convert.ToHexStringLower(SHA256.HashData(buffer.AsSpan(0, length)));
            bool renewed = false;
            for (int attempt = 1; ; attempt++)
            {
                TimeSpan wait = options.RetryDelay * Math.Pow(2, attempt - 1);
                string problem;
                try
                {
                    Uri address = ChunkAddress(target.Endpoint, session.ChunkPathTemplate, index);
                    using HttpRequestMessage request = new(HttpMethod.Put, address);
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.TransferToken);
                    request.Headers.TryAddWithoutValidation("X-Chunk-SHA256", digest);
                    request.Content = new ByteArrayContent(buffer, 0, length);
                    request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                    using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                    RefuseRedirect(address, response);
                    int status = (int)response.StatusCode;
                    if (response.IsSuccessStatusCode) break;
                    if (status == 409) throw new PublishFailure("The store already holds different bytes for part of this file. The upload was stopped.");
                    if (status is 401 or 403 or 404 or 410 && !renewed)
                    {
                        // The transfer token is short-lived and the session may have gone with it. Opening the session
                        // again under the same key gives a new token for the same upload, or says what became of it.
                        renewed = true;
                        session = await OpenSessionAsync(target, release, artifact, redactor, cancellationToken);
                        if (session.State != "Uploading")
                            throw new PublishFailure(session.State == "Expired"
                                ? "The store's upload session expired while the file was being sent."
                                : "The store's upload session stopped accepting chunks while the file was being sent (" + session.State + ").");
                        attempt--;
                        continue;
                    }
                    if (status is 400 or 401 or 403 or 404 or 410 or 413 or 422) throw await RefusalAsync(response, "upload session", cancellationToken);
                    if (status == 429 && response.Headers.RetryAfter?.Delta is { } asked) wait = asked < TimeSpan.FromMinutes(1) ? asked : TimeSpan.FromMinutes(1);
                    problem = status == 429 ? "The store's upload capacity is exhausted." : "The store answered " + status.ToString(CultureInfo.InvariantCulture) + " to part of the file.";
                }
                catch (HttpRequestException error) { problem = "The connection to the store failed while sending part of the file: " + error.Message; }
                catch (IOException error) { problem = "The connection to the store was lost while sending part of the file: " + error.Message; }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { problem = "The store did not answer in time while part of the file was being sent."; }
                if (attempt >= options.ChunkAttempts)
                    throw new PublishFailure(problem + " Publish again to carry on from what the store holds.");
                await Task.Delay(wait, cancellationToken);
            }
        }
        return session;
    }

    private async Task<string> FinalizeAsync(PublishTarget target, string sessionId, CancellationToken cancellationToken)
    {
        JsonElement answer = await SendJsonAsync(target, HttpMethod.Post, "upload-sessions/" + sessionId + "/finalize", null, "upload session", cancellationToken);
        return Text(answer, "state") ?? "Validating";
    }

    private async Task PromoteAsync(PublishTarget target, PublishRelease release, string buildId, CancellationToken cancellationToken)
    {
        // Read immediately before, every time: accepting a build changes the revision, and so does publishing one.
        JsonElement application = await SendJsonAsync(target, HttpMethod.Get, "applications/" + target.ApplicationId, null, "application", cancellationToken);
        string revision = application.ValueKind == JsonValueKind.Object && application.TryGetProperty("revision", out JsonElement value) &&
            value.ValueKind is JsonValueKind.String or JsonValueKind.Number
            ? (value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText())
            : throw new PublishFailure("The store described the application without a revision, so nothing was published.");
        using MemoryStream body = new();
        using (Utf8JsonWriter json = new(body))
        {
            json.WriteStartObject();
            json.WriteBoolean("published", true);
            json.WriteBoolean("beta", target.Channel == "beta");
            json.WriteString("releaseNotes", release.Notes);
            json.WriteString("expectedRevision", revision);
            json.WriteString("confirmation", "SET BUILD RELEASE " + buildId);
            json.WriteEndObject();
        }
        await SendJsonAsync(target, HttpMethod.Put, "builds/" + buildId + "/release", body.ToArray(), "build", cancellationToken);
    }

    private async Task<Capabilities> ReadCapabilitiesAsync(PublishTarget target, CancellationToken cancellationToken)
    {
        JsonElement answer = await SendJsonAsync(target, HttpMethod.Get, "capabilities", null, "API", cancellationToken);
        if (answer.ValueKind != JsonValueKind.Object) throw new PublishFailure("The store did not describe its capabilities. Is the endpoint the store's /api/v1?");
        long maximum = Number(answer, "maxArtifactBytes");
        if (maximum <= 0) throw new PublishFailure("The store did not say how large a file it accepts. Is the endpoint the store's /api/v1?");
        return new(Strings(answer, "formats"), maximum, Strings(answer, "platforms"), Flag(answer, "productionPromotionAvailable"));
    }

    private async Task<Dictionary<string, JsonElement>> ReadBuildsAsync(PublishTarget target, CancellationToken cancellationToken)
    {
        Dictionary<string, JsonElement> builds = new(StringComparer.Ordinal);
        foreach (JsonElement build in await ReadListAsync(target, "builds?applicationId=" + Uri.EscapeDataString(target.ApplicationId), "builds", cancellationToken))
            if (Text(build, "id") is { } id) builds[id.ToLowerInvariant()] = build;
        return builds;
    }

    /// <summary>A list, whether the store answers with the array itself or with an object that holds it.</summary>
    private async Task<IReadOnlyList<JsonElement>> ReadListAsync(PublishTarget target, string path, string name, CancellationToken cancellationToken)
    {
        JsonElement answer = await SendJsonAsync(target, HttpMethod.Get, path, null, "application", cancellationToken);
        if (answer.ValueKind == JsonValueKind.Object)
            answer = answer.TryGetProperty(name, out JsonElement named) ? named : answer.TryGetProperty("items", out JsonElement items) ? items : answer;
        return answer.ValueKind == JsonValueKind.Array ? [.. answer.EnumerateArray().Where(entry => entry.ValueKind == JsonValueKind.Object)] : [];
    }

    private async Task<JsonElement> SendJsonAsync(PublishTarget target, HttpMethod method, string path, byte[]? body, string subject, CancellationToken cancellationToken)
    {
        Uri address = new(target.Endpoint.AbsoluteUri.TrimEnd('/') + "/" + path);
        using HttpRequestMessage request = new(method, address);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", target.ApiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (body is not null)
        {
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }
        using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        RefuseRedirect(address, response);
        if (!response.IsSuccessStatusCode) throw await RefusalAsync(response, subject, cancellationToken);
        byte[] bytes = await ReadBoundedAsync(response, cancellationToken);
        if (bytes.Length == 0) return default;
        using JsonDocument document = JsonDocument.Parse(bytes);
        return document.RootElement.Clone();
    }

    /// <summary>
    /// A redirect is a failure whether the client followed it or not: the key goes where it was addressed, or
    /// nowhere. The address is the one asked for, kept apart, because a client that follows a redirect rewrites
    /// the request it was given.
    /// </summary>
    private static void RefuseRedirect(Uri asked, HttpResponseMessage response)
    {
        int status = (int)response.StatusCode;
        if (status is >= 300 and < 400 || (response.RequestMessage?.RequestUri is { } answered && answered != asked))
            throw new PublishFailure("The store answered with a redirect. Graft does not follow one with a key; check the endpoint.");
    }

    private static async Task<StoreRefusal> RefusalAsync(HttpResponseMessage response, string subject, CancellationToken cancellationToken)
    {
        string? code = null, detail = null;
        try
        {
            byte[] bytes = await ReadBoundedAsync(response, cancellationToken);
            if (bytes.Length > 0)
            {
                using JsonDocument document = JsonDocument.Parse(bytes);
                code = Text(document.RootElement, "error");
                detail = Text(document.RootElement, "detail");
            }
        }
        catch (JsonException) { }
        catch (PublishFailure) { }
        int status = (int)response.StatusCode;
        string said = (code is null ? "" : " (" + Short(code, 80) + ")") + (detail is null ? "." : ": " + Short(detail, 400));
        string message = status switch
        {
            400 or 422 => "The store refused the request as invalid" + said,
            401 => "The store did not accept the API key" + said,
            403 => "The API key lacks a scope this needs (read, upload:test, and promote:production to publish to a channel)" + said,
            404 => "The store does not know this " + subject + said,
            409 => "The store reported a conflict" + said,
            413 => "The store refused the request as too large" + said,
            429 => "The store's upload capacity is exhausted. Try again later" + said,
            _ => "The store answered " + status.ToString(CultureInfo.InvariantCulture) + said
        };
        return new(status, code ?? "", message);
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using MemoryStream buffer = new();
        byte[] block = new byte[16 * 1024];
        int count;
        while ((count = await stream.ReadAsync(block, cancellationToken)) > 0)
        {
            if (buffer.Length + count > MaximumResponseBytes) throw new PublishFailure("The store answered with more than this contract's responses hold.");
            buffer.Write(block, 0, count);
        }
        return buffer.ToArray();
    }

    private static ArtifactOutcome Outcome(PublishArtifact artifact, string state) =>
        new(artifact.FileName, artifact.Declaration.Format, artifact.Declaration.Platform, artifact.Declaration.Architecture, state,
            null, artifact.Sha256.ToLowerInvariant(), null, null);

    private static string Count(int count, string noun) => count.ToString(CultureInfo.InvariantCulture) + " " + noun + (count == 1 ? "" : "s");

    private static string Short(string text, int length)
    {
        string clean = new([.. text.Select(character => char.IsControl(character) ? ' ' : character)]);
        return clean.Length <= length ? clean : clean[..length] + "…";
    }

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static long Number(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt64(out long number) ? number : 0;

    private static bool Flag(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.True;

    private static string[] Strings(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Array
            ? [.. value.EnumerateArray().Where(entry => entry.ValueKind == JsonValueKind.String).Select(entry => entry.GetString()!)]
            : [];

    private sealed record Capabilities(string[] Formats, long MaximumArtifactBytes, string[] Platforms, bool ProductionPromotionAvailable);

    private sealed record UploadSession(string Id, string State, long ReceivedBytes, long ChunkSize, int TotalChunks,
        string? ChunkPathTemplate, string? TransferToken, string? BuildId, string? ErrorCode, string? ErrorDetail);

    /// <summary>A failure whose message was written here and is safe to show.</summary>
    private sealed class PublishFailure(string message) : Exception(message);

    /// <summary>The store said no. The message is built from its status and its own safe fields.</summary>
    private sealed class StoreRefusal(int status, string code, string message) : Exception(message)
    {
        public int Status { get; } = status;
        public string Code { get; } = code;
    }

    /// <summary>
    /// Removes what must never be kept or shown from anything about to be: the key, transfer tokens, the
    /// endpoint and its path, and every path the store issued. Applied to this class's own messages too, so a
    /// mistake in one of them cannot leak what this exists to keep.
    /// </summary>
    private sealed class Redactor
    {
        private readonly List<string> secrets = [];

        public Redactor(PublishTarget target)
        {
            Add(target.ApiKey);
            Add(target.Endpoint.AbsoluteUri);
            Add(target.Endpoint.AbsoluteUri.TrimEnd('/'));
            AddPath(target.Endpoint.AbsolutePath);
        }

        public void Add(string? secret)
        {
            if (secret is { Length: >= 4 } && !secrets.Contains(secret)) secrets.Add(secret);
        }

        /// <summary>A path, and each segment of it long enough to be an opaque name and not a word of the contract.</summary>
        public void AddPath(string? path)
        {
            if (string.IsNullOrEmpty(path)) return;
            Add(path);
            Add(path.Replace("{index}", "", StringComparison.Ordinal));
            foreach (string segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
                if (segment.Length >= 8 && segment is not ("upload-sessions" or "{index}")) Add(segment);
        }

        public string Clean(string text)
        {
            foreach (string secret in secrets.OrderByDescending(secret => secret.Length))
                text = text.Replace(secret, "[removed]", StringComparison.OrdinalIgnoreCase);
            return text.Length <= 1000 ? text : text[..1000] + "…";
        }
    }
}
