using System.Net;
using System.Security.Cryptography;

namespace UnVault.Core.Epic;

/// <summary>The signed-in account: keeps a valid access token around, refreshing it when needed.</summary>
public sealed class EpicAccount(EpicAuthClient auth, SessionStore store)
{
    /// <summary>Refresh this long before the access token actually expires.</summary>
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(5);

    /// <summary>Refreshing, and signing out, one at a time: a sign-out mustn't be undone by a refresh finishing after it.</summary>
    private readonly SemaphoreSlim _lock = new(1, 1);

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

        await _lock.WaitAsync(cancellationToken);
        try
        {
            // Another caller may have refreshed while we waited.
            var session = Session ?? throw new NotLoggedInException();
            if (session.AccessExpiresAt - DateTimeOffset.UtcNow > RefreshMargin)
                return session;
            return await RefreshLockedAsync(session, cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// A new access token after Epic turned down <paramref name="rejected"/> before its time (sessions ended elsewhere, a
    /// password change). If another caller already replaced it, that one is used. Throws <see cref="NotLoggedInException"/>
    /// when the session itself is over.
    /// </summary>
    public async Task<EpicAuthSession> RenewAsync(EpicAuthSession rejected, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var session = Session ?? throw new NotLoggedInException();
            if (session.AccessToken != rejected.AccessToken)
                return session;
            return await RefreshLockedAsync(session, cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task LogoutAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (Session is { } session && session.AccessExpiresAt > DateTimeOffset.UtcNow)
                await auth.KillSessionAsync(session.AccessToken, cancellationToken);
        }
        finally
        {
            Forget();
            _lock.Release();
        }
    }

    private async Task<EpicAuthSession> RefreshLockedAsync(EpicAuthSession session, CancellationToken cancellationToken)
    {
        // The app and the CLI share the saved session: the other one may have refreshed it since this one loaded it.
        if (store.Load() is { } saved && saved.AccountID == session.AccountID && saved.AccessToken != session.AccessToken
            && saved.AccessExpiresAt - DateTimeOffset.UtcNow > RefreshMargin)
        {
            return Session = saved;
        }

        if (session.RefreshExpiresAt <= DateTimeOffset.UtcNow)
        {
            Forget();
            throw new NotLoggedInException("Your Epic session has expired; sign in again.");
        }

        EpicAuthSession refreshed;
        try
        {
            refreshed = await auth.RefreshAsync(session.RefreshToken, cancellationToken);
        }
        catch (EpicAPIException ex) when (ex.StatusCode is { } status && (int)status is >= 400 and < 500 && status != HttpStatusCode.TooManyRequests)
        {
            // Epic no longer takes the refresh token: the session was ended on its side. A busy or failing Epic (429,
            // 5xx) or no connection ends nothing, so those keep the session.
            Forget();
            throw new NotLoggedInException("Epic ended this session, for example after a password change. Sign in again.");
        }

        // In use before it's saved: tokens that couldn't be saved still work until the app closes.
        Session = refreshed;
        try
        {
            store.Save(refreshed);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            // Next start, the saved refresh token may be turned down, which only means signing in again.
        }
        return refreshed;
    }

    /// <summary>Signed out here: the saved session is deleted even if Epic couldn't be told.</summary>
    private void Forget()
    {
        Session = null;
        try
        {
            store.Delete();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unreadable to anyone but this Windows user; the next sign-in replaces it.
        }
    }
}
