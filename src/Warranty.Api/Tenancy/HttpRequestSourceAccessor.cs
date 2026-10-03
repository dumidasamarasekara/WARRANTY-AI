using Warranty.Infrastructure.Audit;

namespace Warranty.Api.Tenancy;

/// <summary>Supplies the client IP of the current request to security events.</summary>
internal sealed class HttpRequestSourceAccessor(IHttpContextAccessor accessor) : IRequestSourceAccessor
{
    public string? SourceIp => accessor.HttpContext?.Connection.RemoteIpAddress?.ToString();
}
