using Wmsfo.Api.Auth;

namespace Wmsfo.Api.Tests;

// A145: SignupBlocks.Matches (contracts 3.1, sql.md 3.12a). An address pattern
// matches by equality, a domain pattern `*@d` matches the part after the
// address's last `@`, case and surrounding space never count, and a pattern
// outside the rules matches nothing.
public class SignupBlocksTests
{
    [Theory]
    [InlineData("jane@example.com", "jane@example.com")]
    [InlineData("Jane@Example.COM", "jane@example.com")]
    [InlineData("  jane@example.com  ", "jane@example.com")]
    [InlineData("jane@example.com", "JANE@EXAMPLE.COM")]
    public void An_address_pattern_matches_the_same_address_whatever_the_case(string email, string pattern)
    {
        Assert.True(SignupBlocks.Matches(email, new[] { pattern }));
    }

    [Fact]
    public void An_address_pattern_matches_no_other_address()
    {
        Assert.False(SignupBlocks.Matches("john@example.com", new[] { "jane@example.com" }));
        Assert.False(SignupBlocks.Matches("jane@example.co", new[] { "jane@example.com" }));
    }

    [Theory]
    [InlineData("anyone@somedomain.com")]
    [InlineData("Someone.Else@SOMEDOMAIN.com")]
    [InlineData("a@b@somedomain.com")]
    public void A_domain_pattern_matches_every_address_at_the_domain(string email)
    {
        Assert.True(SignupBlocks.Matches(email, new[] { "*@somedomain.com" }));
    }

    [Theory]
    [InlineData("jane@mail.somedomain.com")]
    [InlineData("jane@notsomedomain.com")]
    [InlineData("jane@somedomain.com.au")]
    [InlineData("somedomain.com")]
    public void A_domain_pattern_does_not_match_a_subdomain_or_another_domain(string email)
    {
        Assert.False(SignupBlocks.Matches(email, new[] { "*@somedomain.com" }));
    }

    [Theory]
    [InlineData("*")]
    [InlineData("*.com")]
    [InlineData("*@*.com")]
    [InlineData("j*@example.com")]
    [InlineData("*example.com")]
    [InlineData("@example.com")]
    [InlineData("jane@example")]
    [InlineData("")]
    public void A_pattern_outside_the_rules_matches_nothing(string pattern)
    {
        Assert.Null(SignupBlocks.Kind(SignupBlocks.Normalize(pattern)));
        Assert.False(SignupBlocks.Matches("jane@example.com", new[] { pattern }));
        Assert.False(SignupBlocks.Matches("*@example.com", new[] { pattern }));
    }

    [Fact]
    public void A_question_mark_is_a_literal_character_not_a_wildcard()
    {
        Assert.True(SignupBlocks.Matches("?ane@example.com", new[] { "?ane@example.com" }));
        Assert.False(SignupBlocks.Matches("jane@example.com", new[] { "?ane@example.com" }));
    }

    [Fact]
    public void Any_matching_pattern_in_the_list_is_enough()
    {
        var patterns = new[] { "other@example.org", "*@example.com" };
        Assert.True(SignupBlocks.Matches("jane@example.com", patterns));
        Assert.False(SignupBlocks.Matches("jane@example.net", patterns));
        Assert.False(SignupBlocks.Matches("jane@example.com", Array.Empty<string>()));
        Assert.False(SignupBlocks.Matches("", patterns));
    }

    [Theory]
    [InlineData("jane@example.com", "address")]
    [InlineData(" Jane@Example.com ", "address")]
    [InlineData("*@example.com", "domain")]
    [InlineData("*@MAIL.Example.com", "domain")]
    public void Kind_names_an_address_or_a_domain(string pattern, string kind)
    {
        Assert.Equal(kind, SignupBlocks.Kind(SignupBlocks.Normalize(pattern)));
    }

    [Fact]
    public void Kind_refuses_a_pattern_longer_than_254_characters()
    {
        var local = new string('a', 250);
        Assert.Null(SignupBlocks.Kind(local + "@b.co"));
        Assert.Equal("address", SignupBlocks.Kind(new string('a', 249) + "@b.co"));
    }
}
