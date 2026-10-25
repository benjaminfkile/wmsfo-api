using Amazon.CognitoIdentityProvider;
using Amazon.CognitoIdentityProvider.Model;
using Wmsfo.Api.Config;

namespace Wmsfo.Api.Auth;

// Contracts 3.1: the person upsert asks the people pool whether a sub it has
// no row for is still a user there. True when the pool has the user, false
// when it answers UserNotFoundException; any other failure throws.
public interface IPersonDirectory
{
    Task<bool> ExistsAsync(string sub, CancellationToken ct);
}

// AdminGetUser on the people pool (WMSFO_COGNITO_USER_POOL_ID) with the sub as
// the username. Needs cognito-idp:AdminGetUser on the people pool for the
// instance role (platform.md 6.1).
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
}

// api.md 20: the dev static token mode has no pool to ask, so every sub counts
// as a user of the pool.
public sealed class AlwaysKnownPersonDirectory : IPersonDirectory
{
    public Task<bool> ExistsAsync(string sub, CancellationToken ct) => Task.FromResult(true);
}
