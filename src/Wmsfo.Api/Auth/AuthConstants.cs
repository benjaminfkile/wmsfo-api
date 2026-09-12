namespace Wmsfo.Api.Auth;

// api.md 6: scheme and policy names. Each endpoint's RequireAuthorization uses
// one of these; the pipeline registers both schemes and defers to the endpoint.
public static class AuthSchemes
{
    public const string BeaconKey = "BeaconKey";
    // Selects by the token's iss: the admin pool's bearer scheme or the people
    // pool's (api.md 6.2). Under dev static tokens it is the static handler.
    public const string CognitoJwt = "CognitoJwt";
    public const string CognitoPeopleJwt = "CognitoPeopleJwt";
    public const string CognitoAdminJwt = "CognitoAdminJwt";
    // Composite that lets a single request produce either principal.
    public const string BeaconOrCognito = "BeaconOrCognito";
    // 6.4: wak_ bearer scheme, selected by prefix on the Authorization header.
    public const string ApiKey = "ApiKey";
    // Composite that hands a wak_ bearer to ApiKey and anything else to CognitoJwt.
    public const string CognitoOrApiKey = "CognitoOrApiKey";
}

public static class AuthPolicies
{
    // 6.1 beacon key scheme
    public const string Beacon = "Beacon";              // any beacon
    public const string BeaconAdmin = "BeaconAdmin";    // beacon_role = admin

    // 6.2 Cognito ID token scheme
    public const string Person = "Person";              // any valid ID token
    public const string Editor = "Editor";              // cognito:groups contains editor or admin
    public const string Admin = "Admin";                // cognito:groups contains admin
}

public static class BeaconClaims
{
    public const string BeaconId = "beacon_id";
    public const string BeaconRole = "beacon_role";
    public const string BeaconActive = "beacon_active";
    public const string KeyVersion = "key_version";
    public const string RoleBeacon = "beacon";
    public const string RoleAdmin = "admin";
}

public static class PersonClaims
{
    public const string PersonId = "wmsfo_person_id";
    public const string Sub = "sub";
    public const string Email = "email";
    public const string Groups = "cognito:groups";
    public const string TokenUse = "token_use";
    // Stamped by the scheme that validated the token: "admin" for the admin
    // pool, "people" for the people pool. Only "admin" reaches /admin/*.
    public const string Pool = "wmsfo_pool";
    public const string PoolAdmin = "admin";
    public const string PoolPeople = "people";
}

// api.md 6.4 principal claims for an API key request.
public static class ApiKeyClaims
{
    public const string ApiKeyId = "api_key_id";
    public const string ApiKeyName = "api_key_name";
    public const string ApiKeyAll = "api_key_all";           // "true" for all-capabilities keys
    public const string ApiKeyCapability = "api_key_capability";  // one per capability entry
}

// contracts 3.6: one per endpoint group of 4.5.
public static class ApiKeyCapabilities
{
    public const string Events = "events";
    public const string Routes = "routes";
    public const string Beacons = "beacons";
    public const string Sponsors = "sponsors";
    public const string CookieTypes = "cookie_types";
    public const string Pages = "pages";
    public const string Sections = "sections";
    public const string SiteSettings = "site_settings";
    public const string Content = "content";
    public const string Media = "media";
    public const string Icons = "icons";
    public const string Cookies = "cookies";
    public const string Settings = "settings";
    public const string ContactMessages = "contact_messages";
    public const string Subscribers = "subscribers";
    public const string People = "people";
    public const string Diagnostics = "diagnostics";

    public static readonly IReadOnlyList<string> All = new[]
    {
        Events, Routes, Beacons, Sponsors, CookieTypes, Pages, Sections, SiteSettings,
        Content, Media, Icons, Cookies, Settings, ContactMessages, Subscribers, People,
        Diagnostics,
    };

    public static bool IsKnown(string value) => All.Contains(value, StringComparer.Ordinal);
}
