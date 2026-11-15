using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Factory.Core;
using Factory.Store;

namespace Factory.UnitTests;

public class GitHubJwtTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);

    private static byte[] B64(string s) => Convert.FromBase64String(s.Replace('-', '+').Replace('_', '/').PadRight(s.Length + (4 - s.Length % 4) % 4, '='));

    [Fact]
    public void Is_RS256_issued_by_the_app_60_s_ago_for_9_minutes_and_verifies_with_its_key()
    {
        using var rsa = RSA.Create(2048);

        var jwt = GitHubJwt.Create("12345", rsa.ExportRSAPrivateKeyPem(), Now);

        var parts = jwt.Split('.');
        Assert.Equal(3, parts.Length);
        Assert.Equal("""{"alg":"RS256","typ":"JWT"}""", Encoding.UTF8.GetString(B64(parts[0])));
        var claims = JsonNode.Parse(B64(parts[1]))!;
        Assert.Equal((Now.ToUnixTimeSeconds() - 60, Now.ToUnixTimeSeconds() + 540, "12345"),
            ((long)claims["iat"]!, (long)claims["exp"]!, (string)claims["iss"]!));
        Assert.True(rsa.VerifyData(Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"), B64(parts[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }

    [Fact]
    public void A_key_that_is_not_PEM_is_refused_without_echoing_it()
    {
        var e = Assert.Throws<FactoryException>(() => GitHubJwt.Create("1", "not a key", Now));

        Assert.Equal("GITHUB_APP_PRIVATE_KEY is not a PEM private key; run factory init again", e.Message);
    }
}
