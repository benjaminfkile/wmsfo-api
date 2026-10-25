using Amazon.CognitoIdentityProvider;
using Amazon.CognitoIdentityProvider.Model;
using Wmsfo.Api.Config;

namespace Wmsfo.Api.Auth;

// Contracts 3.1: the person upsert asks the people pool whether a sub it has
// no row for is still a user there. True when the pool has the user, false
// when it answers UserNotFoundException; any other failure throws.
// Contracts 4.5: the person delete removes the user's sign-in first. Removed
// when the pool had the user, Absent when it answered UserNotFoundException;
// any other failure throws. `username` is a sub or an address: the people
// pools use the email as the username, so the pool accepts either.
public interface IPersonDirectory
{
    Task<bool> ExistsAsync(string sub, CancellationToken ct);

    Task<SignInRemoval> RemoveAsync(string username, CancellationToken ct);
}

public enum SignInRemoval
{
    Removed,
    Absent,
}

// AdminGetUser on the people pool (WMSFO_COGNITO_USER_POOL_ID) with the sub as
// the username; AdminUserGlobalSignOut then AdminDeleteUser for a removal.
// Needs cognito-idp:AdminGetUser, AdminUserGlobalSignOut, and AdminDeleteUser
// on the people pool for the instance role (platform.md 6.1).
public sealed class CognitoPersonDirectory : IPersonDirectory
{
    private readonly IAmazonCognitoIdentityProvider _cognito;
    private readonly WmsfoOptions _options;

    public CognitoPersonDirectory(IAmazonCognitoIdentityProvider cognito, WmsfoOptions options)
    {
        _cognito = cognito;
        _options = options;
    }

    public async Task<bool> ExistsAsync(string sub, CancellationToken ct)
    {
        try
        {
            await _cognito.AdminGetUserAsync(new AdminGetUserRequest
            {
                UserPoolId = _options.CognitoUserPoolId,
                Username = sub,
            }, ct);
            return true;
        }
        catch (UserNotFoundException)
        {
            return false;
        }
    }

    // The global sign-out revokes every refresh token the user holds; the
    // delete then removes the user. UserNotFoundException on either call means
    // the user is already gone, which counts as done.
    public async Task<SignInRemoval> RemoveAsync(string username, CancellationToken ct)
    {
        try
        {
            await _cognito.AdminUserGlobalSignOutAsync(new AdminUserGlobalSignOutRequest
            {
                UserPoolId = _options.CognitoUserPoolId,
                Username = username,
            }, ct);
            await _cognito.AdminDeleteUserAsync(new AdminDeleteUserRequest
            {
                UserPoolId = _options.CognitoUserPoolId,
                Username = username,
            }, ct);
            return SignInRemoval.Removed;
        }
        catch (UserNotFoundException)
        {
            return SignInRemoval.Absent;
        }
    }
}

// api.md 20: the dev static token mode has no pool to ask, so every sub counts
// as a user of the pool and a removal finds no user to remove.
public sealed class AlwaysKnownPersonDirectory : IPersonDirectory
{
    public Task<bool> ExistsAsync(string sub, CancellationToken ct) => Task.FromResult(true);

    public Task<SignInRemoval> RemoveAsync(string username, CancellationToken ct) =>
        Task.FromResult(SignInRemoval.Absent);
}
