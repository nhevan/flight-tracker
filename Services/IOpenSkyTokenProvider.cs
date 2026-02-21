namespace FlightTracker.Services;

public interface IOpenSkyTokenProvider
{
    Task<string> GetAccessTokenAsync(CancellationToken cancellationToken);

    /// <summary>Invalidates the cached token, forcing a fresh fetch on the next call.</summary>
    void Invalidate();
}
