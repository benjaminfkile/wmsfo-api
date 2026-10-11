using Wmsfo.Api.Config;

namespace Wmsfo.Api.Tests;

// api.md 3: WMSFO_CONTACT_NOTIFY_EMAIL holds one or more addresses, comma
// separated. The list trims each entry and skips empty ones; the validator
// names the key unless the list has at least one entry and every entry is
// an address.
public sealed class ContactNotifyEmailOptionTests
{
    [Fact]
    public void List_splits_on_commas_and_trims()
    {
        var o = new WmsfoOptions { ContactNotifyEmail = " inbox@example.com ,records@example.org,, " };
        Assert.Equal(["inbox@example.com", "records@example.org"], o.ContactNotifyEmailList());
    }

    [Fact]
    public void One_address_is_a_list_of_one()
    {
        var o = new WmsfoOptions { ContactNotifyEmail = "inbox@example.com" };
        Assert.Equal(["inbox@example.com"], o.ContactNotifyEmailList());
    }

    [Theory]
    [InlineData("inbox@example.com", true)]
    [InlineData("inbox@example.com,records@example.org", true)]
    [InlineData("", false)]
    [InlineData(" , ", false)]
    [InlineData("inbox@example.com,not-an-address", false)]
    public void Validator_accepts_only_a_list_of_addresses(string value, bool valid)
    {
        var result = new WmsfoOptionsValidator().Validate(null, new WmsfoOptions { ContactNotifyEmail = value });
        var failures = result.Failures ?? Array.Empty<string>();
        Assert.Equal(!valid, failures.Any(f => f.Contains(WmsfoOptions.Keys.ContactNotifyEmail)));
    }
}
