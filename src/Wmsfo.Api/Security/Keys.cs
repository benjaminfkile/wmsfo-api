using System.Security.Cryptography;
using System.Text;

namespace Wmsfo.Api.Security;

// api.md 14: minting, hashing, and AES-GCM for the four token families
// (wbk_ beacon keys, wet_ enrollment tokens, wsv_ subscription-verify tokens,
// wsu_ subscription-unsubscribe tokens, wpv_ preview tokens). 32 random bytes
// from RandomNumberGenerator, base64url without padding, prefixed.
public static class Keys
{
    public const string BeaconKeyPrefix = "wbk_";
    public const string EnrollmentTokenPrefix = "wet_";
    public const string SubscribeVerifyPrefix = "wsv_";
    public const string SubscribeUnsubscribePrefix = "wsu_";
    public const string PreviewTokenPrefix = "wpv_";

    public const int TokenBodyChars = 43;   // 32 bytes as unpadded url-safe base64.
    public const int DisplayPrefixChars = 12;

    public static MintedKey MintKey() => Mint(BeaconKeyPrefix);
    public static MintedKey MintEnrollmentToken() => Mint(EnrollmentTokenPrefix);
    public static MintedKey MintVerifyToken() => Mint(SubscribeVerifyPrefix);
    public static MintedKey MintUnsubscribeToken() => Mint(SubscribeUnsubscribePrefix);
    public static MintedKey MintPreviewToken() => Mint(PreviewTokenPrefix);

    public static byte[] Hash(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));

    // api.md 14 / contracts 3.3: AES-256-GCM under WMSFO_ENROLLMENT_ENCRYPTION_KEY.
    // 12-byte random nonce prepended, 16-byte tag appended.
    public const int NonceSize = 12;
    public const int TagSize = 16;

    public static byte[] Encrypt(byte[] key, string plaintext)
    {
        if (key.Length != 32) throw new ArgumentException("key must be 32 bytes", nameof(key));
        var plainBytes = Encoding.UTF8.GetBytes(plaintext);
        var nonce = new byte[NonceSize];
        RandomNumberGenerator.Fill(nonce);
        var cipher = new byte[plainBytes.Length];
        var tag = new byte[TagSize];
        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plainBytes, cipher, tag);
        var blob = new byte[NonceSize + cipher.Length + TagSize];
        Buffer.BlockCopy(nonce, 0, blob, 0, NonceSize);
        Buffer.BlockCopy(cipher, 0, blob, NonceSize, cipher.Length);
        Buffer.BlockCopy(tag, 0, blob, NonceSize + cipher.Length, TagSize);
        return blob;
    }

    public static string Decrypt(byte[] key, byte[] blob)
    {
        if (key.Length != 32) throw new ArgumentException("key must be 32 bytes", nameof(key));
        if (blob.Length < NonceSize + TagSize) throw new CryptographicException("blob too short");
        var cipherLength = blob.Length - NonceSize - TagSize;
        var nonce = new byte[NonceSize];
        var cipher = new byte[cipherLength];
        var tag = new byte[TagSize];
        Buffer.BlockCopy(blob, 0, nonce, 0, NonceSize);
        Buffer.BlockCopy(blob, NonceSize, cipher, 0, cipherLength);
        Buffer.BlockCopy(blob, NonceSize + cipherLength, tag, 0, TagSize);
        var plain = new byte[cipherLength];
        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(nonce, cipher, tag, plain);
        return Encoding.UTF8.GetString(plain);
    }

    private static MintedKey Mint(string prefix)
    {
        Span<byte> random = stackalloc byte[32];
        RandomNumberGenerator.Fill(random);
        var body = Base64UrlNoPad(random);
        var token = prefix + body;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        var display = token[..DisplayPrefixChars];
        return new MintedKey(token, hash, display);
    }

    // Base64Url without padding — the format used by every WMSFO token.
    private static string Base64UrlNoPad(ReadOnlySpan<byte> bytes)
    {
        var s = Convert.ToBase64String(bytes);
        var withoutPad = s.TrimEnd('=');
        return withoutPad.Replace('+', '-').Replace('/', '_');
    }
}

public readonly record struct MintedKey(string Token, byte[] Hash, string Prefix);
