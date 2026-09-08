namespace Wmsfo.Api.Auth;

// api.md 6: scheme and policy names. Each endpoint's RequireAuthorization uses
// one of these; the pipeline registers both schemes and defers to the endpoint.
public static class AuthSchemes
{
    public const string BeaconKey = "BeaconKey";
    public const string CognitoJwt = "CognitoJwt";
    // Composite that lets a single request produce either principal.
    public const string BeaconOrCognito = "BeaconOrCognito";
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
}
