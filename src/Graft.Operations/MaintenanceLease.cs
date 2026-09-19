namespace Graft.Operations;

public sealed class MaintenanceLease : IDisposable
{
    private readonly FileStream[] streams;
    private MaintenanceLease(FileStream[] streams) => this.streams = streams;

    public static MaintenanceLease Acquire(string databasePath, string repositoriesPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoriesPath);
        string[] lockPaths = [Path.GetFullPath(databasePath) + ".maintenance.lock",
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoriesPath)) + ".maintenance.lock"];
        List<FileStream> acquired = [];
        try
        {
            foreach (string lockPath in lockPaths.Distinct(PathComparer).OrderBy(path => path, PathComparer))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
                acquired.Add(new FileStream(lockPath, FileMode.OpenOrCreate,
                    FileAccess.ReadWrite, FileShare.None, 1, FileOptions.WriteThrough));
            }
            return new(acquired.ToArray());
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            foreach (FileStream stream in acquired) stream.Dispose();
            throw new InvalidOperationException("Graft is running or another maintenance operation is active.", error);
        }
    }

    public void Dispose()
    {
        foreach (FileStream stream in streams.Reverse()) stream.Dispose();
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
}
