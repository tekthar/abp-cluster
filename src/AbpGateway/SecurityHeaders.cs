namespace AbpGateway;

/// <summary>
/// The browser's protections, set at the front door for every answer, unless the server or the web
/// set their own:
/// - <c>Strict-Transport-Security</c> on an answer over HTTPS (behind a trusted ingress, its X-Forwarded-Proto says so),
///   a year with subdomains; <c>Gateway:Hsts:MaxAgeDays</c> 0 turns it off;
/// - <c>X-Content-Type-Options: nosniff</c> and <c>Referrer-Policy: strict-origin-when-cross-origin</c>;
/// - a Content-Security-Policy enforced (<c>Gateway:Csp</c>): no plugins of the old kind, no base address from elsewhere;
/// - the full policy (<c>Gateway:CspReportOnly</c>: scripts, styles, frames and connections from the site itself,
///   images from anywhere https) reported only, so a site sees in the browser's console what it would refuse before
///   it moves it to <c>Gateway:Csp</c>. Scripts a web app loads from the site itself (module federation's remotes) are
///   'self'; a framework's start-up scripts are inline; images may be data: or blob: (a chart's export).
/// Either policy empty is left out, and each is the product's to set: an app whose web needs more says so in its own
/// <c>Gateway:Csp</c> and <c>Gateway:CspReportOnly</c>.
/// </summary>
public static class SecurityHeaders
{
    public const string Enforced = "object-src 'none'; base-uri 'self'";

    public const string ReportOnly = "default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob: https:; "
        + "font-src 'self' data:; connect-src 'self'; frame-src 'self' blob:; worker-src 'self' blob:; object-src 'none'; base-uri 'self'";

    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app, IConfiguration configuration)
    {
        int hstsDays = configuration.GetValue("Gateway:Hsts:MaxAgeDays", 365);
        string csp = configuration["Gateway:Csp"] ?? Enforced;
        string report = configuration["Gateway:CspReportOnly"] ?? ReportOnly;
        return app.Use(async (context, next) =>
        {
            context.Response.OnStarting(() =>
            {
                IHeaderDictionary headers = context.Response.Headers;
                if (context.Request.IsHttps && hstsDays > 0)
                {
                    headers.TryAdd("Strict-Transport-Security", $"max-age={hstsDays * 86400}; includeSubDomains");
                }

                headers.TryAdd("X-Content-Type-Options", "nosniff");
                headers.TryAdd("Referrer-Policy", "strict-origin-when-cross-origin");
                if (csp.Length > 0)
                {
                    headers.TryAdd("Content-Security-Policy", csp);
                }

                if (report.Length > 0)
                {
                    headers.TryAdd("Content-Security-Policy-Report-Only", report);
                }

                return Task.CompletedTask;
            });
            await next();
        });
    }
}
