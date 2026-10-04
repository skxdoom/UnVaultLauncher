namespace UnVault.Core.Epic;

/// <summary>The signed-in account: keeps a valid access token around, refreshing it when needed.</summary>
public sealed class EpicAccount(EpicAuthClient auth, SessionStore store)
{
    /// <summary>Refresh this long before the access token actually expires.</summary>
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(5);

    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    public EpicAuthSession? Session { get; private set; } = store.Load();

    public bool IsLoggedIn => Session is { } session && session.RefreshExpiresAt > DateTimeOffset.UtcNow;

    public async Task<EpicAuthSession> LoginWithAuthorizationCodeAsync(string authorizationCode, CancellationToken cancellationToken = default)
    {
        var session = await auth.ExchangeAuthorizationCodeAsync(authorizationCode, cancellationToken);
        store.Save(session);
        Session = session;
        return session;
    }

    /// <summary>Returns a session whose access token is good for at least a few minutes.</summary>
    public async Task<EpicAuthSession> GetValidSessionAsync(CancellationToken cancellationToken = default)
    {
        if (Session is { } current && current.AccessExpiresAt - DateTimeOffset.UtcNow > RefreshMargin)
            return current;

        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            // Another caller may have refreshed while we waited.
            var session = Session ?? throw new NotLoggedInException();
            if (session.AccessExpiresAt - DateTimeOffset.UtcNow > RefreshMargin)
                return session;
            if (session.RefreshExpiresAt <= DateTimeOffset.UtcNow)
                throw new NotLoggedInException("Your Epic session has expired; sign in again.");

            var refreshed = await auth.RefreshAsync(session.RefreshToken, cancellationToken);
            store.Save(refreshed);
            Session = refreshed;
            return refreshed;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    public async Task LogoutAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (Session is { } session && session.AccessExpiresAt > DateTimeOffset.UtcNow)
                await auth.KillSessionAsync(session.AccessToken, cancellationToken);
        }
        finally
        {
            store.Delete();
            Session = null;
        }
    }
}
