using System.Net.Http.Headers;
using System.Text;
using FmsOnboard.Core;

namespace FmsOnboard.Web;

public sealed class AuthMiddleware(RequestDelegate next, AppConfig cfg)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (cfg.AllowedNetworks.Count > 0)
        {
            var remoteIp = context.Connection.RemoteIpAddress;
            if (remoteIp is null || !cfg.AllowedNetworks.Any(net => net.Contains(remoteIp)))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsync("Akses ditolak dari jaringan Anda.");
                return;
            }
        }

        if (!string.IsNullOrEmpty(cfg.DashboardPassword))
        {
            bool authorized = false;
            if (context.Request.Headers.TryGetValue("Authorization", out var authHeader) &&
                AuthenticationHeaderValue.TryParse(authHeader, out var parsed) &&
                "Basic".Equals(parsed.Scheme, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrEmpty(parsed.Parameter))
            {
                try
                {
                    var cred = Encoding.UTF8.GetString(Convert.FromBase64String(parsed.Parameter));
                    authorized = cred == $"{cfg.DashboardUser}:{cfg.DashboardPassword}";
                }
                catch { }
            }

            if (!authorized)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.Headers.WWWAuthenticate = "Basic realm=\"FMS Onboard Monitoring\"";
                await context.Response.WriteAsync("Autentikasi diperlukan.");
                return;
            }
        }

        await next(context);
    }
}
