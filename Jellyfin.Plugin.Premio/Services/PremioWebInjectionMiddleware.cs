using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace Jellyfin.Plugin.Premio.Services;

/// <summary>
/// ASP.NET Core middleware that injects the Premio client script tag into Jellyfin Web's index.html.
/// </summary>
public sealed class PremioWebInjectionMiddleware
{
    private static readonly string ScriptTag = $"<script defer src=\"/Premio/Web/premio.js?v={typeof(PremioWebInjectionMiddleware).Assembly.GetName().Version}\"></script></body>";
    private readonly RequestDelegate _next;

    /// <summary>
    /// Initializes a new instance of the <see cref="PremioWebInjectionMiddleware"/> class.
    /// </summary>
    /// <param name="next">Next request delegate.</param>
    public PremioWebInjectionMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    /// <summary>
    /// Invokes the middleware for each HTTP request.
    /// </summary>
    /// <param name="context">HTTP context.</param>
    /// <returns>A task representing the completion of request processing.</returns>
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var path = context.Request.Path.Value ?? string.Empty;
        var isHtmlPage = path.Equals("/web/index.html", StringComparison.OrdinalIgnoreCase) ||
                         path.Equals("/web/", StringComparison.OrdinalIgnoreCase) ||
                         path.Equals("/web", StringComparison.OrdinalIgnoreCase) ||
                         path.EndsWith("/index.html", StringComparison.OrdinalIgnoreCase);

        if (!isHtmlPage)
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        // Strip caching validation headers so static file middleware cannot return 304 Not Modified
        context.Request.Headers.Remove("If-None-Match");
        context.Request.Headers.Remove("If-Modified-Since");

        var originalBodyFeature = context.Features.Get<IHttpResponseBodyFeature>();
        using var newBodyStream = new MemoryStream();
        context.Features.Set<IHttpResponseBodyFeature>(new StreamResponseBodyFeature(newBodyStream));
        context.Response.Body = newBodyStream;

        try
        {
            await _next(context).ConfigureAwait(false);

            newBodyStream.Seek(0, SeekOrigin.Begin);
            var contentType = context.Response.ContentType ?? string.Empty;

            if (context.Response.StatusCode == StatusCodes.Status200OK &&
                (string.IsNullOrWhiteSpace(contentType) || contentType.Contains("text/html", StringComparison.OrdinalIgnoreCase)))
            {
                using var reader = new StreamReader(newBodyStream, Encoding.UTF8, leaveOpen: true);
                var html = await reader.ReadToEndAsync().ConfigureAwait(false);

                if (html.Contains("</body>", StringComparison.OrdinalIgnoreCase) &&
                    !html.Contains("premio.js", StringComparison.OrdinalIgnoreCase))
                {
                    html = html.Replace("</body>", ScriptTag, StringComparison.OrdinalIgnoreCase);
                    var modifiedBytes = Encoding.UTF8.GetBytes(html);
                    context.Response.Headers.Remove("ETag");
                    context.Response.Headers.ContentLength = modifiedBytes.Length;
                    if (originalBodyFeature is not null)
                    {
                        await originalBodyFeature.Stream.WriteAsync(modifiedBytes, context.RequestAborted).ConfigureAwait(false);
                    }
                    return;
                }
            }

            newBodyStream.Seek(0, SeekOrigin.Begin);
            if (originalBodyFeature is not null)
            {
                await newBodyStream.CopyToAsync(originalBodyFeature.Stream, context.RequestAborted).ConfigureAwait(false);
            }
        }
        finally
        {
            if (originalBodyFeature is not null)
            {
                context.Features.Set<IHttpResponseBodyFeature>(originalBodyFeature);
                context.Response.Body = originalBodyFeature.Stream;
            }
        }
    }
}
