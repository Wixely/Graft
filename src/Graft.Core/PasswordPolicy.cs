namespace Graft.Core;

/// <summary>
/// Credential length policy. Both the API surface and the password hasher enforce it, so there is one floor
/// rather than a literal repeated at each call site.
///
/// <para>
/// <b>Debug builds relax the minimum so local fixtures can seed throwaway credentials such as
/// <c>admin</c>/<c>admin</c> without anyone typing a 16-character password.</b> Release builds keep the full
/// 16-character minimum, and every packaged or deployed artifact is published Release, so no deployment can
/// accept a short password. A server running with the relaxed floor says so on startup.
/// </para>
///
/// <para>
/// These are deliberately <c>static readonly</c> rather than <c>const</c>: a constant would be inlined into
/// each referencing assembly at compile time, so a Debug build of this project could bake a relaxed floor
/// into an otherwise Release assembly. Reading the field keeps one assembly authoritative at runtime.
/// </para>
/// </summary>
public static class PasswordPolicy
{
#if DEBUG
    public static readonly int MinimumLength = 5;
    public static readonly bool RelaxedForLocalDebug = true;
#else
    public static readonly int MinimumLength = 16;
    public static readonly bool RelaxedForLocalDebug = false;
#endif

    public static readonly int MaximumLength = 256;

    /// <summary>True when a candidate password is an acceptable length for the current build.</summary>
    public static bool IsAcceptableLength(string? password) =>
        password is not null && password.Length >= MinimumLength && password.Length <= MaximumLength;
}
