using System.Security.Cryptography;
using System.Text;

namespace Wmsfo.Api.Tests;

// Contracts 13: `CONTRACTS_VERSION` is an integer bumped on every change under `contracts/`.
// The map below records the version-to-hash pairs; bump the integer and add an entry when the
// hash changes. The test fails when the directory hash changes and the version does not.
public class ContractsVersionTests
{
    // Add a new entry every time you bump CONTRACTS_VERSION.
    private static readonly IReadOnlyDictionary<int, string> KnownHashes = new Dictionary<int, string>
    {
        { 1, "b0886551ff8f90ea1ff7389a5b6b6b4d87033d51bbdff6647abc82acbd730ae2" },
        { 2, "22bf1982b10c9298e1775beef60db2168f18d11fa2ded7a264aaf1383e4341ce" },
        { 3, "dfd2bb18bc84d70aa6f71cad7d189bfe70aa8e54bf7d7e6c0f2c1dac9f024c2b" },
        { 4, "4670e8cebc4b85437ce184b19f277ad320890ae5c01b7480a1e05f29b8fc509a" },
        { 5, "5bbe5defbbb6b1c42bab831d6ed516d89e78c57ccee551ea9a237a263536fa62" },
        { 6, "8f73adf31084954549ea4010f34201ff725bd9b218346280c198c575e0efc18e" },
        { 7, "464168042a138edea4f3113e5e17d85f62de39dfca290b46d042c4ddee084744" },
        { 8, "fa3b812f683c37452d56f9f4c1dc03104040e8939e8df2e4d7c2c35fbe3dca0a" },
        { 9, "871f7106402ac4295ecebf22c1bdfa043d22d7f41df86a306d737a0cfb2fcd29" },
        { 10, "9e7702a422f18542345718c7c433ce7b90085e28bfb00f3a7b8f3a00fe6e69fd" },
        { 11, "c629f83edc6d19f2f1db04c15e6bfd61df4a97d34d85850b5381e91c1d195c9f" },
        { 12, "517fd0b4d2c56a58a60fcca25b98e4560c5dde98b1390d85cc0b7d4d854a6f67" },
        { 13, "a80462cd0c79ef9a87fca2942e51bd01f26562e0af4602010b16f4cec1c5330e" },
        { 14, "cff19ff3271e3dda4332acd937e1f1045f8f1578639990315b9d9164eaa87060" },
        { 15, "9a85e0684937f8bcaa6a1da0b459d405d547b3e3021b621e9c262819223179eb" },
        { 16, "09bff0d06edb3ac4aa4187a249873ccdc3d0f0be1f1be6a8315bb69d1ef4564b" },
        { 17, "e0dd73110e898a67262e066294f0901dc8259d8191aae8aa36f1bba58db923e1" },
        { 18, "a40dd819dbea384ce2bbc1e5a4d2a097520d29f71fc67237c043d61ba38ebd1d" },
        { 19, "736778e4f315a027089b163b8986275bf04d982fc14d240a22bff9e8d0937732" },
        { 20, "317ee1d266d3485db9cc8bbe1a37d6f799f9481be5c260569e4cf419fb021911" },
        { 21, "b9dcd38ec32fbb041f63243cb848dbf799d3629f314509cb28096dff8c257820" },
        { 22, "5f25d2c2a60fdfefcb0365b851d384c816291f96438c9efcec6091a740b89ff4" },
        { 23, "e86da5e0b9ef845f7cbdc20fa7fa166f9aebfa519f0a128d5006cde0c104017c" },
        { 24, "5aa24f24af79a5d2866683b85607d116d1da27a0c2245ad358ed0b4a16ffd699" },
        { 25, "ccadcde46c35f06dc02af30100905aa17b8c31cd7f190014fd82a25b58005eaa" },
        { 26, "c7cf3d6b17172252808178d977e734ea13a336ba372f3c306e6336012298b36f" },
        { 27, "3d6d43cf89f3bdd48d11cba1c83fb6b473de09e67c3f7a6ff06447b323ba69dd" },
        { 28, "58681c1b47aa8c33cc288e14c37346076ba03d0b035d949fee66a37f79d237aa" },
        { 29, "aab42ca14ead31d80ff8c6bca3720e3fc50d7c79a06896e0cd856d19e59c3928" },
        { 30, "16d916fff456451296372d2466dbdb7b450996f4ca0d8d6736303af82abc4cd0" },
        { 31, "d0f9a39d7b8f711c8567e4651c4486b419a50d673dc53967a56c4ac1d53267a8" },
        { 32, "afb3f2841abb98712a5a158a05c1fd86792bd85580ec82f1f010866d1c0e584f" },
        { 33, "193adb85756457aeb519904ed6f6e01a1bb271e8e09c656d10503147a5d7a1c5" },
        { 34, "8ff763c64a8863b39084bc8a655de854b45fb6fc01dedb9a1426efb3bfe701e7" },
        { 35, "667791a2f6d77a2ad61516d2e0f3d50e83d7a682698fbc0c11ea1a8f19a287de" },
        { 36, "cd174b7566d5dd2015d99baff7ebe84a23087ff3ab9fd48335b43face6c3ec68" },
        { 37, "493026285cda8ec3b1c2280af188796599ef96d437f0bb8f0b84ba24e04d7ee3" },
        { 38, "7656820adc91490be32e72b17b81d5ee43c14b7dcf2f97ea37cfb5dfde7627b9" },
        { 39, "86557d04eeb250be28696e529fa57744131164d2f3d4325fed6e2b45edea1fa5" },
        { 40, "a2e5e31a2a6b6b2b3e1ef0afc5e9a25903b3631e6350a7b56f55081c9a818a6b" },
        { 41, "b921cb6ed749c609964e21ab570971c6fc006fa89201befc14649bfcc0651334" },
        { 42, "c698511d0cfe0ea8ca566ae50e03a6916981e7dbda8090eb985c6a3d454267ca" },
        { 43, "e1b107c69b5d761cb79e65a1977f0174b8c09435c7fd15c47c066968887fd7d8" },
        { 44, "b334178aed83523c421f5e8a026358b051e294e58e22e8e5e8c79b3124a73e0b" },
        { 45, "7285508ae07c6f4bf0b9f10ec6a2a72468bbdac2f6af787726bc734cc74f2453" },
        { 46, "5ac58b13a2bf9beb39f9e66c8adcd3aca522c3e4f43b9feea82dbe876aa76f4c" },
        { 47, "2836b9b679f93f68c748ec6e0cddba36ac374715572cf069f3f6dcbd807a5ee6" },
        { 48, "5ac58b13a2bf9beb39f9e66c8adcd3aca522c3e4f43b9feea82dbe876aa76f4c" },
        { 49, "01c0dc52d83fbc57ab62c4481588c9568e6a57dca3dc680dc25452c172222ef6" },
        { 50, "c9ead52e46fb10edaf9ba41bf660b604d62582332c67d3e667e993a7e1b7766f" },
        { 51, "0d67e8581b0db56ad2a45df3129d26d1544b2825ba094fe6e2029c106f60a307" },
        { 52, "8db0e45e5f06687497a54f6803c60c80133b2325233243b57212cf062b2bfb8a" },
        { 53, "8743b33a2463e92eb9eb2ddf16742b15ef6a0bdedc3f3944b65912b616ded1bc" },
        { 54, "70f52c30d5ecafd6dd05cca52623705c485b4904129dc56957d2b9e31121b4c8" },
        { 55, "b3fbb90081abbf49f8aac40ecdd31ad10b9c38f9eee085288799c7d56f464326" },
        { 56, "b9d2f7b4b3568d0e5693e73cfed71cca2d6c4968be92c0c2dcdc89a2bf27e8ce" },
        { 57, "ad981de98a2a47cd891daff006d63432b8a95bdc498327a041ea00f11a71a7e3" },
        { 58, "9a9326240660520ed084f412c1fd7785f24b0de338b2c8d2ce76ef7e1caf00a2" },
        { 59, "fa0c8dc3ae1b4d64d9708e945e69e9cb7946ac10ab59307303a52a5c2987767b" },
        { 60, "47a1bda93c058c6c10daaca55f3865c84b08810914cf550b59b9ac2612f5102a" },
        { 61, "0c8b4ca5169cf18fa276272e6fd964e82fd17c7b7190fbf05238becdd3e34ebe" },
    };

    [Fact]
    public void Contracts_version_matches_directory_hash()
    {
        var version = int.Parse(File.ReadAllText(ContractsPaths.VersionPath).Trim());
        var currentHash = HashDirectory(ContractsPaths.ContractsDir);

        Assert.True(KnownHashes.ContainsKey(version),
            $"CONTRACTS_VERSION={version} has no recorded hash; add it to KnownHashes in ContractsVersionTests. Current hash: {currentHash}");

        var expected = KnownHashes[version];
        Assert.True(string.Equals(expected, currentHash, StringComparison.Ordinal),
            $"contracts/ contents changed (current hash {currentHash}) but CONTRACTS_VERSION is still {version}. " +
            $"Bump CONTRACTS_VERSION to {version + 1} and add the new entry {{ {version + 1}, \"{currentHash}\" }} in ContractsVersionTests.KnownHashes.");
    }

    // A deterministic hash of every file under contracts/ except CONTRACTS_VERSION itself.
    // Byte contents plus the repo-relative POSIX path go into the hash, so a rename or a
    // content edit both count as a change. Bytes are normalized from CRLF to LF first so a
    // checkout under `core.autocrlf=true` (Windows) produces the same hash as a plain LF one.
    private static string HashDirectory(string root)
    {
        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(f => !string.Equals(Path.GetFileName(f), "CONTRACTS_VERSION", StringComparison.Ordinal))
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + ".gitkeep", StringComparison.Ordinal)
                        && !f.EndsWith(".gitkeep", StringComparison.Ordinal))
            .Select(f => (Path: f, Rel: Path.GetRelativePath(root, f).Replace(Path.DirectorySeparatorChar, '/')))
            .OrderBy(p => p.Rel, StringComparer.Ordinal)
            .ToList();

        using var sha = SHA256.Create();
        foreach (var (path, rel) in files)
        {
            var pathBytes = Encoding.UTF8.GetBytes(rel + "\n");
            sha.TransformBlock(pathBytes, 0, pathBytes.Length, null, 0);
            var content = NormalizeNewlineBytes(File.ReadAllBytes(path));
            sha.TransformBlock(content, 0, content.Length, null, 0);
            sha.TransformBlock(new byte[] { 0 }, 0, 1, null, 0);
        }
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Convert.ToHexStringLower(sha.Hash!);
    }

    private static byte[] NormalizeNewlineBytes(byte[] bytes)
    {
        var output = new List<byte>(bytes.Length);
        for (var i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] == 0x0D && i + 1 < bytes.Length && bytes[i + 1] == 0x0A)
            {
                continue;
            }
            output.Add(bytes[i]);
        }
        return output.ToArray();
    }
}
