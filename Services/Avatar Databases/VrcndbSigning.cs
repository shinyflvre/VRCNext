using System.Net.Http;
using System.Security.Cryptography;
using System.Text;

namespace VRCNext;

internal sealed class VrcndbSigningHandler : DelegatingHandler
{
    public VrcndbSigningHandler() : base(new HttpClientHandler()) { }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        VrcndbSigning.Sign(request);
        return base.SendAsync(request, cancellationToken);
    }
}

internal static class VrcndbSigning
{
    private const string Host = "db.vrcnext.com";
    private static readonly byte[] Key = Encoding.UTF8.GetBytes(BuildSecrets.VrcndbKey);

    public static void Sign(HttpRequestMessage req)
    {
        if (Key.Length == 0 || req.RequestUri == null) return;
        if (!req.RequestUri.Host.Equals(Host, StringComparison.OrdinalIgnoreCase)) return;

        var ts    = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        var msg   = ts + "\n" + nonce + "\n" + req.Method.Method.ToUpperInvariant() + "\n" + req.RequestUri.PathAndQuery + "\n" + AppInfo.UserAgent;
        var sig   = Convert.ToHexString(HMACSHA256.HashData(Key, Encoding.UTF8.GetBytes(msg))).ToLowerInvariant();

        req.Headers.Remove("X-VRCN-Ts");
        req.Headers.Remove("X-VRCN-Nonce");
        req.Headers.Remove("X-VRCN-Sig");
        req.Headers.TryAddWithoutValidation("X-VRCN-Ts", ts);
        req.Headers.TryAddWithoutValidation("X-VRCN-Nonce", nonce);
        req.Headers.TryAddWithoutValidation("X-VRCN-Sig", sig);
    }
}
