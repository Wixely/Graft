namespace Graft.Git;

public sealed class TemporaryStorage : IDisposable
{
    public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "graft-transfer-" + Guid.NewGuid().ToString("N"));

    public TemporaryStorage()
    {
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(DirectoryPath);
        else Directory.CreateDirectory(DirectoryPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    public FileStream CreateFile() => new(Path.Combine(DirectoryPath, Guid.NewGuid().ToString("N")),
        FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 64 * 1024, FileOptions.SequentialScan);

    public void Dispose() => Directory.Delete(DirectoryPath, recursive: true);
}
