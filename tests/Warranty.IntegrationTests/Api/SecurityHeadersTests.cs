using System.Net;
using Warranty.Api.Security;

namespace Warranty.IntegrationTests.Api;

/// <summary>
/// T111: every API response carries the security headers, and CORS grants only the
/// <c>localhost</c>, <c>aurora.localhost</c> and <c>borealis.localhost</c> origins. Needs no
/// backing service, like <see cref="ApiCompositionTests"/>.
/// </summary>
public sealed class SecurityHeadersTests(ApiCompositionTests.ApiFactory factory) : IClassFixture<ApiCompositionTests.ApiFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("/alive", HttpStatusCode.OK)]
    [InlineData("/api/does-not-exist", HttpStatusCode.NotFound)]
    [InlineData("/api/me", HttpStatusCode.Unauthorized)]
    public async Task Every_response_carries_the_security_headers(string path, HttpStatusCode expected)
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(path, Ct);

        response.StatusCode.ShouldBe(expected);
        Header(response, "X-Content-Type-Options").ShouldBe("nosniff");
        Header(response, "X-Frame-Options").ShouldBe("DENY");
        Header(response, "Content-Security-Policy").ShouldBe(SecurityHeaders.ContentSecurityPolicy);
        Header(response, "Content-Security-Policy").ShouldContain("frame-ancestors 'none'");
        Header(response, "Referrer-Policy").ShouldBe("no-referrer");
        Header(response, "Cross-Origin-Opener-Policy").ShouldBe("same-origin");
        response.Headers.Contains("Permissions-Policy").ShouldBeTrue();
        response.Headers.CacheControl!.NoStore.ShouldBeTrue();
    }

    [Theory]
    [InlineData("http://localhost:5173")]
    [InlineData("http://aurora.localhost:5173")]
    [InlineData("https://borealis.localhost")]
    public async Task Allowed_origins_get_a_cors_grant_on_preflight_and_actual_requests(string origin)
    {
        using var client = factory.CreateClient();

        using var preflight = await client.SendAsync(Preflight(origin), Ct);
        using var actual = await client.SendAsync(Get(origin), Ct);

        preflight.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        Header(preflight, "Access-Control-Allow-Origin").ShouldBe(origin);
        Header(preflight, "Access-Control-Allow-Methods").ShouldContain("POST");
        Header(preflight, "Access-Control-Allow-Headers").ShouldContain("authorization", Case.Insensitive);
        preflight.Headers.Contains("Access-Control-Allow-Credentials").ShouldBeFalse();
        Header(actual, "Access-Control-Allow-Origin").ShouldBe(origin);
        Header(actual, "Access-Control-Expose-Headers").ShouldContain("X-Correlation-Id");
    }

    [Theory]
    [InlineData("https://evil.example")]
    [InlineData("http://localhost.evil.example")]
    [InlineData("http://aurora.localhost.evil.example:5173")]
    [InlineData("http://unknown.localhost:5173")]
    [InlineData("null")]
    public async Task A_disallowed_origin_gets_no_cors_grant(string origin)
    {
        using var client = factory.CreateClient();

        using var preflight = await client.SendAsync(Preflight(origin), Ct);
        using var actual = await client.SendAsync(Get(origin), Ct);

        preflight.Headers.Contains("Access-Control-Allow-Origin").ShouldBeFalse();
        preflight.Headers.Contains("Access-Control-Allow-Methods").ShouldBeFalse();
        actual.Headers.Contains("Access-Control-Allow-Origin").ShouldBeFalse();
        actual.Headers.Contains("Access-Control-Expose-Headers").ShouldBeFalse();
    }

    private static HttpRequestMessage Preflight(string origin)
    {
        var request = new HttpRequestMessage(HttpMethod.Options, "/api/public/claims");
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", "POST");
        request.Headers.Add("Access-Control-Request-Headers", "authorization, content-type");
        return request;
    }

    private static HttpRequestMessage Get(string origin)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/alive");
        request.Headers.Add("Origin", origin);
        return request;
    }

    private static string Header(HttpResponseMessage response, string name)
        => response.Headers.GetValues(name).ShouldHaveSingleItem();
}
