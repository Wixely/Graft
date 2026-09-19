using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

if (args.Length != 4 || !Uri.TryCreate(args[0], UriKind.Absolute, out Uri? baseAddress)
    || !File.Exists(args[1]) || !File.Exists(args[2]) || !File.Exists(args[3]))
{
    Console.Error.WriteLine("Usage: Graft.GitLab.Reference <base URL> <api token file> <read_api token file> <fixture JSON>");
    return 2;
}

string apiToken = (await File.ReadAllTextAsync(args[1])).Trim();
string readApiToken = (await File.ReadAllTextAsync(args[2])).Trim();
Fixture fixture = JsonSerializer.Deserialize(await File.ReadAllTextAsync(args[3]), ReferenceJsonContext.Default.Fixture)
    ?? throw new InvalidDataException("The fixture is empty.");

if (apiToken.Length == 0 || readApiToken.Length == 0)
    throw new InvalidDataException("A token file is empty.");

using HttpClient client = new() { BaseAddress = baseAddress, Timeout = TimeSpan.FromMinutes(2) };
int assertions = 0;

using (HttpRequestMessage request = Request(HttpMethod.Get, "api/v4/version", apiToken))
using (HttpResponseMessage response = await client.SendAsync(request))
{
    Check(response.IsSuccessStatusCode, "authenticated version endpoint");
    VersionResponse? version = await response.Content.ReadFromJsonAsync(ReferenceJsonContext.Default.VersionResponse);
    Check(version?.Version == "18.0.0", "exact GitLab version 18.0.0");
}

foreach (CreationCase creationCase in fixture.CreationCases)
{
    string? token = creationCase.Token switch
    {
        "api" => apiToken,
        "read_api" => readApiToken,
        "none" => null,
        _ => throw new InvalidDataException($"Unknown token kind for {creationCase.Id}.")
    };

    using HttpRequestMessage request = Request(HttpMethod.Post, "api/v4/projects", token);
    request.Content = JsonContent.Create(creationCase.Body, ReferenceJsonContext.Default.CreationBody);
    using HttpResponseMessage response = await client.SendAsync(request);
    Check((int)response.StatusCode == creationCase.UpstreamStatus,
        $"{creationCase.Id} status {(int)response.StatusCode}");

    if (creationCase.UpstreamStatus == 201)
    {
        ProjectResponse? project = await response.Content.ReadFromJsonAsync(ReferenceJsonContext.Default.ProjectResponse);
        Check(project?.Name == creationCase.ExpectedName, $"{creationCase.Id} response name");
        Check(project?.Path == creationCase.ExpectedPath, $"{creationCase.Id} response path");
    }
}

Console.WriteLine($"RESULT: PASS; baseline: {fixture.Baseline}; assertions: {assertions}; cases: {fixture.CreationCases.Length}");
return 0;

HttpRequestMessage Request(HttpMethod method, string path, string? token)
{
    HttpRequestMessage request = new(method, path);
    if (token is not null)
        request.Headers.Add("PRIVATE-TOKEN", token);
    return request;
}

void Check(bool condition, string description)
{
    if (!condition)
        throw new InvalidOperationException($"FAIL: {description}");
    assertions++;
    Console.WriteLine($"PASS: {description}");
}

internal sealed record Fixture(
    string Baseline,
    [property: JsonPropertyName("creation_cases")] CreationCase[] CreationCases);

internal sealed record CreationCase(
    string Id,
    string Token,
    CreationBody Body,
    [property: JsonPropertyName("upstream_status")] int UpstreamStatus,
    [property: JsonPropertyName("expected_name")] string? ExpectedName,
    [property: JsonPropertyName("expected_path")] string? ExpectedPath);

internal sealed record CreationBody(string? Name, string? Path, string? Visibility);
internal sealed record VersionResponse(string Version);
internal sealed record ProjectResponse(string Name, string Path);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(Fixture))]
[JsonSerializable(typeof(CreationBody))]
[JsonSerializable(typeof(VersionResponse))]
[JsonSerializable(typeof(ProjectResponse))]
internal sealed partial class ReferenceJsonContext : JsonSerializerContext;
