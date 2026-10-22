namespace ContosoDashboard.Tests.Infrastructure;

public sealed class TemporaryStorageRoot : IDisposable
{
    public TemporaryStorageRoot()
    {
        RootPath = Path.Combine(Path.GetTempPath(), "ContosoDashboard.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(RootPath);
    }

    public string RootPath { get; }

    public void Dispose()
    {
        if (Directory.Exists(RootPath))
        {
            Directory.Delete(RootPath, recursive: true);
        }
    }
}
