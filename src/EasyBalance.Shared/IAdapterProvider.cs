namespace EasyBalance.Shared;

public interface IAdapterProvider
{
    ValueTask<IReadOnlyList<NetworkAdapterInfo>> GetAdaptersAsync(CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<NetworkAdapterInfo>> GetAdapters(CancellationToken cancellationToken = default) =>
        GetAdaptersAsync(cancellationToken);
}
