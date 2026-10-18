using System.Text;
using System.Text.Json;
using Wmsfo.Api.Endpoints;
using Wmsfo.Api.Http;

namespace Wmsfo.Api.Tests;

// A111: the PUT /beacons/me/state body rules (contracts 4.2). The state is a
// JSON object nested at most 8 levels; expectedVersion an int64 of 0 or more;
// both required, no other keys.
public class BeaconStateRulesTests
{
    private static BeaconStateRules.Parsed Parse(string body) => BeaconStateRules.Parse(Encoding.UTF8.GetBytes(body));

    private static IReadOnlyDictionary<string, string> Refused(string body)
    {
        var ex = Assert.Throws<ApiException>(() => Parse(body));
        Assert.Equal(400, ex.StatusCode);
        Assert.Equal(ApiErrorCodes.ValidationFailed, ex.Code);
        return ex.Details is ValidationDetails v ? v.Fields : new Dictionary<string, string>();
    }

    // Objects nested `levels` deep, counted as the heartbeat's debug is: the
    // innermost object holds a scalar, which is one level more.
    private static string Nested(int levels) =>
        string.Concat(Enumerable.Repeat("{\"a\":", levels - 1)) + "1" + new string('}', levels - 1);

    [Fact]
    public void An_object_and_a_version_pass_and_the_state_is_kept_as_sent()
    {
        var parsed = Parse("{\"state\":{\"b\":[1,2,{\"c\":null}],\"a\":\"x\"},\"expectedVersion\":3}");
        Assert.Equal(3, parsed.ExpectedVersion);
        Assert.Equal("{\"b\":[1,2,{\"c\":null}],\"a\":\"x\"}", parsed.StateJson);
    }

    [Fact]
    public void Keys_in_either_order_and_an_empty_object_pass()
    {
        var parsed = Parse("{\"expectedVersion\":0,\"state\":{}}");
        Assert.Equal(0, parsed.ExpectedVersion);
        Assert.Equal("{}", parsed.StateJson);
    }

    [Theory]
    [InlineData("[1,2]")]
    [InlineData("null")]
    [InlineData("\"text\"")]
    [InlineData("12")]
    [InlineData("true")]
    public void A_state_that_is_not_an_object_is_refused(string state)
    {
        var fields = Refused("{\"state\":" + state + ",\"expectedVersion\":0}");
        Assert.Equal("must be an object", fields["state"]);
    }

    [Fact]
    public void Eight_levels_pass_and_nine_are_refused()
    {
        Assert.Equal(8, BeaconEndpoints.JsonDepth(JsonDocument.Parse(Nested(8)).RootElement));
        Parse("{\"state\":" + Nested(8) + ",\"expectedVersion\":0}");

        var fields = Refused("{\"state\":" + Nested(9) + ",\"expectedVersion\":0}");
        Assert.Contains("8-level", fields["state"]);
    }

    [Fact]
    public void Arrays_count_toward_the_depth()
    {
        // object > array > array > ... 9 levels with the scalar.
        var deep = "{\"a\":" + string.Concat(Enumerable.Repeat("[", 7)) + "1" + new string(']', 7) + "}";
        Refused("{\"state\":" + deep + ",\"expectedVersion\":0}");
    }

    [Fact]
    public void Missing_keys_are_required()
    {
        Assert.Equal("required", Refused("{\"expectedVersion\":0}")["state"]);
        Assert.Equal("required", Refused("{\"state\":{}}")["expectedVersion"]);
    }

    [Fact]
    public void Any_other_key_is_refused()
    {
        var fields = Refused("{\"state\":{},\"expectedVersion\":0,\"extra\":1}");
        Assert.True(fields.ContainsKey("extra"));
    }

    [Fact]
    public void A_repeated_key_is_refused()
    {
        Refused("{\"state\":{},\"state\":{},\"expectedVersion\":0}");
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("\"3\"")]
    [InlineData("null")]
    [InlineData("99999999999999999999")]
    public void A_version_that_is_not_an_int64_of_0_or_more_is_refused(string version)
    {
        var fields = Refused("{\"state\":{},\"expectedVersion\":" + version + "}");
        Assert.True(fields.ContainsKey("expectedVersion"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"state\":{}")]
    [InlineData("[]")]
    [InlineData("null")]
    public void A_body_that_is_not_one_json_object_is_refused(string body)
    {
        Refused(body);
    }
}
