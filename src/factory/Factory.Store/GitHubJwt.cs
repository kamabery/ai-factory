using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Factory.Core;

namespace Factory.Store;

/// <summary>The GitHub App's JWT (contract, "Secrets, init and doctor"): RS256, issued 60 s ago, 9 minutes long.</summary>
public static class GitHubJwt
{
    public static string Create(string appId, string privateKeyPem, DateTimeOffset now)
    {
        using var rsa = RSA.Create();
        try { rsa.ImportFromPem(privateKeyPem); }
        catch (ArgumentException) { throw new FactoryException("GITHUB_APP_PRIVATE_KEY is not a PEM private key; run factory init again"); }
        var header = B64(Encoding.UTF8.GetBytes("""{"alg":"RS256","typ":"JWT"}"""));
        // Issued a minute early, as GitHub advises, against clock drift; GitHub refuses one longer than ten minutes.
        var claims = new JsonObject { ["iat"] = now.ToUnixTimeSeconds() - 60, ["exp"] = now.ToUnixTimeSeconds() + 540, ["iss"] = appId };
        var payload = B64(Encoding.UTF8.GetBytes(claims.ToJsonString()));
        var signature = rsa.SignData(Encoding.ASCII.GetBytes($"{header}.{payload}"), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return $"{header}.{payload}.{B64(signature)}";
    }

    private static string B64(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
