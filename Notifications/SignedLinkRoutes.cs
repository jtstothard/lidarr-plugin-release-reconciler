using System;
using System.Text.RegularExpressions;

namespace NzbDrone.Core.Plugins.ReleaseReconciler.Notifications
{
    /// <summary>
    /// The single definition of the signed-link URL shape shared by the notification
    /// link builder and the request mappers that serve those links.
    /// </summary>
    /// <remarks>
    /// Lidarr hands every non-API GET to exactly one <c>IMapHttpRequestsToDisk</c>
    /// (<c>SingleOrDefault</c>). A path without a dot is also claimed by
    /// <c>IndexHtmlMapper</c>, and anything under <c>/content</c> by
    /// <c>StaticResourceMapper</c>, so signed links end in <c>/index.html</c> and live
    /// outside <c>/content</c>. The canonical shape is <c>{prefix}/{token}/index.html</c>.
    /// Plugin 0.1.2 sent <c>{prefix}/index.html/{token}</c>; the mappers still accept
    /// that so links already delivered keep working until their tokens expire.
    /// </remarks>
    public static class SignedLinkRoutes
    {
        public const string OperatorActionPrefix = "/release-reconciler/action";

        public const string EvidencePagePrefix = "/release-reconciler/evidence";

        private const string PageName = "index.html";

        private const string TokenPattern = "[A-Za-z0-9_-]+";

        public static string BuildPath(string prefix, string token)
        {
            return $"{prefix.TrimEnd('/')}/{Uri.EscapeDataString(token)}/{PageName}";
        }

        public static Regex CreatePathRegex(string prefix)
        {
            var escapedPrefix = Regex.Escape(prefix.TrimEnd('/'));
            var escapedPage = Regex.Escape(PageName);

            return new Regex(
                $"^{escapedPrefix}/(?:(?<token>{TokenPattern})/{escapedPage}|{escapedPage}/(?<token>{TokenPattern}))$",
                RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        }

        /// <summary>
        /// Maps an older configured path (the pre-0.1.2 <c>/content/...</c> path, or the
        /// 0.1.2 default that ended in <c>/index.html</c>) to its link prefix.
        /// </summary>
        public static string NormalizePrefix(string path)
        {
            var normalized = path.TrimEnd('/');

            if (normalized.EndsWith("/" + PageName, StringComparison.OrdinalIgnoreCase))
            {
                normalized = normalized.Substring(0, normalized.Length - PageName.Length - 1);
            }

            if (string.Equals(normalized, "/content" + OperatorActionPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return OperatorActionPrefix;
            }

            if (string.Equals(normalized, "/content" + EvidencePagePrefix, StringComparison.OrdinalIgnoreCase))
            {
                return EvidencePagePrefix;
            }

            return normalized;
        }
    }
}
