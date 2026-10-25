using System.Security.Claims;
using Wmsfo.Api.Auth;
using Wmsfo.Api.Http;

namespace Wmsfo.Api.Tests;

// A143: the person upsert filter maps the upsert's answer onto the request.
// Found attaches the person id claim once; UnknownUser is 401 unknown_user.
public class PersonUpsertFilterTests
{
    [Fact]
    public void Found_attaches_the_person_id()
    {
        var user = Principal();
        CognitoAuth.ApplyUpsertResult(new PersonUpsertResult.Found(42), user);
        Assert.Equal(42L, CognitoAuth.TryGetPersonId(user));
    }

    [Fact]
    public void Found_keeps_a_person_id_already_attached()
    {
        var user = Principal();
        CognitoAuth.ApplyUpsertResult(new PersonUpsertResult.Found(42), user);
        CognitoAuth.ApplyUpsertResult(new PersonUpsertResult.Found(7), user);
        Assert.Single(user.FindAll(PersonClaims.PersonId));
        Assert.Equal(42L, CognitoAuth.TryGetPersonId(user));
    }

    [Fact]
    public void Unknown_user_is_401_unknown_user_and_attaches_nothing()
    {
        var user = Principal();
        var ex = Assert.Throws<ApiException>(() => CognitoAuth.ApplyUpsertResult(PersonUpsertResult.Unknown, user));
        Assert.Equal(401, ex.StatusCode);
        Assert.Equal("unknown_user", ex.Code);
        Assert.Equal("the token's user no longer exists; sign in again", ex.Message);
        Assert.Null(CognitoAuth.TryGetPersonId(user));
    }

    private static ClaimsPrincipal Principal() =>
        new(new ClaimsIdentity(new[] { new Claim(PersonClaims.Sub, DevStaticTokens.PersonSub) }, "test"));
}
