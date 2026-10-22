namespace ContosoDashboard.Services;

public interface ILocalFileStorage
{
    Task<StagedFile> StageAsync(
        Stream source,
        IProgress<long>? bytesCopied = null,
        CancellationToken cancellationToken = default);

    Task<string> PublishAsync(string stagingPath, CancellationToken cancellationToken = default);

    Task<Stream> OpenReadAsync(string relativePath, CancellationToken cancellationToken = default);

    Task DeleteAsync(string relativePath, CancellationToken cancellationToken = default);
}

public sealed record StagedFile(string RelativePath, long Length);