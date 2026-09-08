using System.Security.Cryptography;
using System.Text;
using Wmsfo.Api.Security;

namespace Wmsfo.Api.Tests;

// A8 / api.md 14 unit tests for Security/Keys.cs: minting, hashing, AES-GCM round trip.
public sealed class KeysTests
{
    [Fact]
    public void MintKey_returns_47_characters_with_prefix_and_43_body()
    {
        var k = Keys.MintKey();
        Assert.StartsWith("wbk_", k.Token);
        Assert.Equal(4 + 43, k.Token.Length);
        // Body is base64url (unpadded).
        for (var i = 4; i < k.Token.Length; i++)
        {
            var c = k.Token[i];
            Assert.True((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-' || c == '_',
                $"unexpected char '{c}'");
        }
        Assert.Equal(12, k.Prefix.Length);
        Assert.Equal(32, k.Hash.Length);
        Assert.Equal(SHA256.HashData(Encoding.UTF8.GetBytes(k.Token)), k.Hash);
    }

    [Theory]
    [InlineData("wbk_")]
    [InlineData("wet_")]
    [InlineData("wsv_")]
    [InlineData("wsu_")]
    [InlineData("wpv_")]
    public void MintKey_families_carry_the_expected_prefix(string prefix)
    {
        var k = prefix switch
        {
            "wbk_" => Keys.MintKey(),
            "wet_" => Keys.MintEnrollmentToken(),
            "wsv_" => Keys.MintVerifyToken(),
            "wsu_" => Keys.MintUnsubscribeToken(),
            "wpv_" => Keys.MintPreviewToken(),
            _ => throw new InvalidOperationException(),
        };
        Assert.StartsWith(prefix, k.Token);
    }

    [Fact]
    public void Hash_matches_sha256_of_utf8()
    {
        var value = "hello world";
        Assert.Equal(SHA256.HashData(Encoding.UTF8.GetBytes(value)), Keys.Hash(value));
    }

    [Fact]
    public void Aes_gcm_round_trip_returns_plaintext()
    {
        var key = new byte[32];
        RandomNumberGenerator.Fill(key);
        var plaintext = "wbk_" + new string('x', 43);
        var blob = Keys.Encrypt(key, plaintext);
        Assert.Equal(Keys.NonceSize + Encoding.UTF8.GetByteCount(plaintext) + Keys.TagSize, blob.Length);
        var round = Keys.Decrypt(key, blob);
        Assert.Equal(plaintext, round);
    }

    [Fact]
    public void Aes_gcm_wrong_key_throws()
    {
        var key = new byte[32];
        RandomNumberGenerator.Fill(key);
        var wrong = new byte[32];
        RandomNumberGenerator.Fill(wrong);
        var blob = Keys.Encrypt(key, "abc");
        var ex = Assert.ThrowsAny<CryptographicException>(() => Keys.Decrypt(wrong, blob));
        Assert.NotNull(ex);
    }
}
