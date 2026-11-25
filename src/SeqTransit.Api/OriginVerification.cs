using System.Security.Cryptography;
using System.Text;

namespace SeqTransit.Api;

/// <summary>
/// Rejects requests that did not come through CloudFront. The API Gateway URL is public, and a
/// request sent straight to it would skip the edge cache and reach the database every time.
/// </summary>
/// <remarks>
/// CloudFront adds <see cref="HeaderName"/> to every /api/* origin request; the CDK stack passes the
/// same value to this function as <c>OriginVerify__Secret</c>. Without that setting (local runs,
/// tests) every request is allowed.
/// </remarks>
public static class OriginVerification
{
    public const string HeaderName = "X-Origin-Verify";
    public const string SettingName = "OriginVerify:Secret";

    public static IApplicationBuilder UseOriginVerification(this IApplicationBuilder app, IConfiguration configuration)
    {
        var secret = configuration[SettingName];
        if (string.IsNullOrWhiteSpace(secret))
        {
            return app;
        }

        var expected = Encoding.UTF8.GetBytes(secret);
        return app.Use(async (context, next) =>
        {
            if (!IsAllowed(context.Request.Headers[HeaderName].ToString(), expected))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                context.Response.Headers.CacheControl = "no-store";
                return;
            }

            await next(context);
        });
    }

    /// <summary>Constant-time comparison, so response timing reveals nothing about the value.</summary>
    public static bool IsAllowed(string? presented, byte[] expected) =>
        !string.IsNullOrEmpty(presented)
        && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(presented), expected);
}
