using System.Globalization;
using System.Net;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;

namespace NzbDrone.Core.Plugins.ReleaseReconciler.Actions
{
    public sealed class OperatorActionResultPage : ContentResult
    {
        private OperatorActionResultPage(int statusCode, string title, string message)
        {
            StatusCode = statusCode;
            ContentType = "text/html; charset=utf-8";
            Content = BuildHtml(statusCode, title, message);
        }

        public static OperatorActionResultPage Success(string title, string message)
        {
            return new OperatorActionResultPage(200, title, message);
        }

        public static OperatorActionResultPage Refusal(int statusCode, string title, string message)
        {
            return new OperatorActionResultPage(statusCode, title, message);
        }

        public override Task ExecuteResultAsync(ActionContext context)
        {
            var headers = context.HttpContext.Response.Headers;
            headers["Cache-Control"] = "no-store, no-cache, max-age=0, private";
            headers["Pragma"] = "no-cache";
            headers["Expires"] = "0";
            headers["X-Robots-Tag"] = "noindex, nofollow";
            return base.ExecuteResultAsync(context);
        }

        private static string BuildHtml(int statusCode, string title, string message)
        {
            var safeTitle = WebUtility.HtmlEncode(title);
            var safeMessage = WebUtility.HtmlEncode(message);
            var safeStatus = WebUtility.HtmlEncode(statusCode.ToString(CultureInfo.InvariantCulture));

            return "<!doctype html>\n"
                + "<html lang=\"en\">\n"
                + "<head>\n"
                + "  <meta charset=\"utf-8\" />\n"
                + "  <meta http-equiv=\"Cache-Control\" content=\"no-store, no-cache, max-age=0, private\" />\n"
                + "  <meta http-equiv=\"Pragma\" content=\"no-cache\" />\n"
                + "  <meta name=\"robots\" content=\"noindex,nofollow\" />\n"
                + "  <meta name=\"viewport\" content=\"width=device-width, initial-scale=1\" />\n"
                + "  <title>" + safeTitle + "</title>\n"
                + "  <style>\n"
                + "    body { font-family: -apple-system, BlinkMacSystemFont, sans-serif; margin: 2rem; background: #0f172a; color: #e2e8f0; }\n"
                + "    main { max-width: 34rem; padding: 1.5rem; border: 1px solid #334155; border-radius: 0.75rem; background: #111827; }\n"
                + "    h1 { margin-top: 0; font-size: 1.25rem; }\n"
                + "    p { line-height: 1.5; }\n"
                + "    code { color: #93c5fd; }\n"
                + "  </style>\n"
                + "</head>\n"
                + "<body>\n"
                + "  <main>\n"
                + "    <p><code>Status " + safeStatus + "</code></p>\n"
                + "    <h1>" + safeTitle + "</h1>\n"
                + "    <p>" + safeMessage + "</p>\n"
                + "  </main>\n"
                + "</body>\n"
                + "</html>";
        }
    }
}
