using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Wmsfo.Api.Auth;
using Wmsfo.Api.Endpoints;

namespace Wmsfo.Api.Tests;

// A34 unit test: the actor string for an API-key caller is `key:<name>` with
// the key's full name as stored - names carrying spaces or punctuation are
// preserved verbatim, not truncated to the first word (contracts 4.5 Audit).
public sealed class AuditRecorderActorTests
{
    [Fact]
    public void Actor_for_api_key_uses_the_full_name_including_spaces()
    {
        var name = "claude-code (all capabilities, no expiry)";
        var recorder = MakeRecorder(BuildApiKeyPrincipal(name));

        var actor = recorder.Actor();

        Assert.Equal("key:claude-code (all capabilities, no expiry)", actor);
    }

    [Fact]
    public void Actor_for_person_uses_email_claim()
    {
        var recorder = MakeRecorder(BuildPersonPrincipal("editor@wmsfo.test"));

        Assert.Equal("person:editor@wmsfo.test", recorder.Actor());
    }

    private static AuditRecorder MakeRecorder(ClaimsPrincipal principal)
    {
        var accessor = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext { User = principal },
        };
        return new AuditRecorder(accessor);
    }

    private static ClaimsPrincipal BuildApiKeyPrincipal(string name)
    {
        var identity = new ClaimsIdentity(AuthSchemes.ApiKey);
        identity.AddClaim(new Claim(ApiKeyClaims.ApiKeyId, "1"));
        identity.AddClaim(new Claim(ApiKeyClaims.ApiKeyName, name));
        identity.AddClaim(new Claim(ApiKeyClaims.ApiKeyAll, "true"));
        return new ClaimsPrincipal(identity);
    }

    private static ClaimsPrincipal BuildPersonPrincipal(string email)
    {
        var identity = new ClaimsIdentity(AuthSchemes.CognitoJwt);
        identity.AddClaim(new Claim(PersonClaims.Email, email));
        return new ClaimsPrincipal(identity);
    }
}
