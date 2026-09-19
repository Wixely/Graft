using System.Text.Json.Serialization;

namespace Graft.Ui;

public sealed record SessionView(bool Authenticated, string? User, string Csrf, DateTimeOffset Expires);
public sealed record LoginRequest(string Username, string Password);
public sealed record ProjectView(string Name, string Description);
public sealed record ErrorView(string Error);

[JsonSerializable(typeof(SessionView))]
[JsonSerializable(typeof(LoginRequest))]
[JsonSerializable(typeof(ProjectView[]))]
[JsonSerializable(typeof(ErrorView))]
public partial class UiJson : JsonSerializerContext;
