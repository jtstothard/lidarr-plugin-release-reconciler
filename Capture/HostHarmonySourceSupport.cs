using System;

namespace NzbDrone.Core.Plugins.ReleaseReconciler.Capture
{
    public sealed class HostHarmonySourceSupport
    {
        public const string CurrentRuleVersion = "host-harmony-source-support-v1";

        public bool Supported { get; set; }

        public string RuleVersion { get; set; } = CurrentRuleVersion;

        public string MatchedRule { get; set; } = string.Empty;

        public string? MatchedProvider { get; set; }

        public string? SuppressionReason { get; set; }

        public string? ProviderUrl { get; set; }

        public string? LookupArtist { get; set; }

        public string? LookupAlbum { get; set; }

        public string? LookupQuery { get; set; }

        public HostHarmonySourceSupport Normalize()
        {
            return new HostHarmonySourceSupport
            {
                Supported = Supported,
                RuleVersion = NormalizeRequiredValue(RuleVersion, nameof(RuleVersion)),
                MatchedRule = NormalizeRequiredValue(MatchedRule, nameof(MatchedRule)),
                MatchedProvider = NormalizeOptionalValue(MatchedProvider),
                SuppressionReason = NormalizeOptionalValue(SuppressionReason),
                ProviderUrl = NormalizeOptionalValue(ProviderUrl),
                LookupArtist = NormalizeOptionalValue(LookupArtist),
                LookupAlbum = NormalizeOptionalValue(LookupAlbum),
                LookupQuery = NormalizeOptionalValue(LookupQuery)
            };
        }

        public void ValidatePersistedShape()
        {
            if (string.IsNullOrWhiteSpace(RuleVersion))
            {
                throw new InvalidOperationException("Harmony source support rule version is required.");
            }

            if (string.IsNullOrWhiteSpace(MatchedRule))
            {
                throw new InvalidOperationException("Harmony source support matched rule is required.");
            }

            if (Supported)
            {
                if (!string.IsNullOrWhiteSpace(SuppressionReason))
                {
                    throw new InvalidOperationException("Supported Harmony source support cannot persist a suppression reason.");
                }

                if (string.IsNullOrWhiteSpace(MatchedProvider))
                {
                    throw new InvalidOperationException("Supported Harmony source support must persist a matched provider.");
                }

                var hasLookupUrl = !string.IsNullOrWhiteSpace(ProviderUrl);
                var hasLookupQuery = !string.IsNullOrWhiteSpace(LookupQuery);
                var hasLookupArtistAlbum = !string.IsNullOrWhiteSpace(LookupArtist) && !string.IsNullOrWhiteSpace(LookupAlbum);
                if (!hasLookupUrl && !hasLookupQuery && !hasLookupArtistAlbum)
                {
                    throw new InvalidOperationException("Supported Harmony source support must persist a provider URL or equivalent lookup fields.");
                }
            }
            else if (string.IsNullOrWhiteSpace(SuppressionReason))
            {
                throw new InvalidOperationException("Suppressed Harmony source support must persist a suppression reason.");
            }

            if (!string.IsNullOrWhiteSpace(ProviderUrl))
            {
                if (!Uri.TryCreate(ProviderUrl, UriKind.Absolute, out var providerUri)
                    || !string.Equals(providerUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(providerUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("Harmony source support provider URL must be an absolute HTTP or HTTPS URL.");
                }

                if (!string.IsNullOrEmpty(providerUri.Query) || !string.IsNullOrEmpty(providerUri.Fragment) || !string.IsNullOrEmpty(providerUri.UserInfo))
                {
                    throw new InvalidOperationException("Harmony source support provider URL must be normalized without query strings, fragments, or userinfo.");
                }
            }
        }

        private static string NormalizeRequiredValue(string value, string paramName)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException("Value is required.", paramName);
            }

            return value.Trim();
        }

        private static string? NormalizeOptionalValue(string? value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
    }
}
