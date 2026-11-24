using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace NzbDrone.Core.Plugins.ReleaseReconciler.Capture
{
    public sealed class HostHarmonySourceSupportClassifier
    {
        private static readonly Regex HeaderSecretPattern = new(
            "(?<label>authorization|cookie|cookies|set-cookie)\\s*[:=]\\s*(?<value>[^\\r\\n;]+)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex BearerSecretPattern = new(
            "bearer\\s+[A-Za-z0-9._~+/=-]+",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex TokenSecretPattern = new(
            "(?<label>token|api[_-]?key|apikey|password|passwd|secret|session(?:id)?|signature|sig)\\s*[:=]\\s*(?<value>[^\\r\\n;]+)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly string[] ProviderKeys =
        {
            "provider",
            "providerName",
            "providerId",
            "sourceProvider",
            "releaseSource",
            "lookupProvider",
            "indexer",
            "indexerName",
            "downloadClient",
            "source"
        };

        private static readonly string[] UrlKeys =
        {
            "providerUrl",
            "lookupUrl",
            "sourceUrl",
            "releaseUrl",
            "detailsUrl",
            "infoUrl",
            "guid",
            "downloadUrl"
        };

        private static readonly string[] ArtistKeys =
        {
            "lookupArtist",
            "providerArtist",
            "artist",
            "artistName"
        };

        private static readonly string[] AlbumKeys =
        {
            "lookupAlbum",
            "providerAlbum",
            "album",
            "albumTitle",
            "releaseTitle"
        };

        private static readonly string[] QueryKeys =
        {
            "lookupQuery",
            "searchQuery",
            "query",
            "title"
        };

        public HostHarmonySourceSupport Classify(
            HostImportFailureClassification importFailureClassification,
            IReadOnlyDictionary<string, string?>? sourceMetadata)
        {
            ArgumentNullException.ThrowIfNull(importFailureClassification);

            var normalizedMismatch = importFailureClassification.Normalize();
            normalizedMismatch.ValidatePersistedShape();
            if (!normalizedMismatch.Actionable)
            {
                return SuppressNonActionable(normalizedMismatch);
            }

            var normalizedMetadata = NormalizeMetadata(sourceMetadata);
            var matchedProvider = ResolveProvider(normalizedMetadata, out var allowProviderUrl);
            if (matchedProvider == null)
            {
                return Suppress(
                    matchedRule: "provider-missing",
                    matchedProvider: null,
                    suppressionReason: "Host source metadata did not include a provider identifier or Harmony URL.");
            }

            if (!string.Equals(matchedProvider, "harmony", StringComparison.Ordinal))
            {
                return Suppress(
                    matchedRule: "provider-unsupported",
                    matchedProvider: matchedProvider,
                    suppressionReason: $"Provider '{matchedProvider}' is not supported for Harmony source extraction.");
            }

            if (TryExtractProviderUrl(normalizedMetadata, out var providerUrl, out var providerUrlSuppressionReason)
                && allowProviderUrl)
            {
                return Support(
                    matchedRule: "harmony-provider-url",
                    matchedProvider: matchedProvider,
                    providerUrl: providerUrl,
                    lookupArtist: null,
                    lookupAlbum: null,
                    lookupQuery: null);
            }

            if (providerUrlSuppressionReason != null)
            {
                return Suppress(
                    matchedRule: "unsafe-provider-url",
                    matchedProvider: matchedProvider,
                    suppressionReason: providerUrlSuppressionReason);
            }

            var hasArtist = TryExtractLookupValue(normalizedMetadata, ArtistKeys, out var lookupArtist, out var lookupArtistSuppressionReason);
            if (lookupArtistSuppressionReason != null)
            {
                return Suppress(
                    matchedRule: "unsafe-lookup-metadata",
                    matchedProvider: matchedProvider,
                    suppressionReason: lookupArtistSuppressionReason);
            }

            var hasAlbum = TryExtractLookupValue(normalizedMetadata, AlbumKeys, out var lookupAlbum, out var lookupAlbumSuppressionReason);
            if (lookupAlbumSuppressionReason != null)
            {
                return Suppress(
                    matchedRule: "unsafe-lookup-metadata",
                    matchedProvider: matchedProvider,
                    suppressionReason: lookupAlbumSuppressionReason);
            }

            var hasQuery = TryExtractLookupValue(normalizedMetadata, QueryKeys, out var lookupQuery, out var lookupQuerySuppressionReason);
            if (lookupQuerySuppressionReason != null)
            {
                return Suppress(
                    matchedRule: "unsafe-lookup-metadata",
                    matchedProvider: matchedProvider,
                    suppressionReason: lookupQuerySuppressionReason);
            }

            if ((hasArtist && hasAlbum) || hasQuery)
            {
                return Support(
                    matchedRule: "harmony-equivalent-lookup",
                    matchedProvider: matchedProvider,
                    providerUrl: null,
                    lookupArtist: lookupArtist,
                    lookupAlbum: lookupAlbum,
                    lookupQuery: lookupQuery);
            }

            return Suppress(
                matchedRule: "lookup-metadata-missing",
                matchedProvider: matchedProvider,
                suppressionReason: "Harmony source metadata did not include a safe provider URL or equivalent lookup fields.");
        }

        public static HostHarmonySourceSupport SuppressNonActionable(HostImportFailureClassification importFailureClassification)
        {
            ArgumentNullException.ThrowIfNull(importFailureClassification);

            var normalizedMismatch = importFailureClassification.Normalize();
            normalizedMismatch.ValidatePersistedShape();

            return Suppress(
                matchedRule: "mismatch-not-actionable",
                matchedProvider: null,
                suppressionReason: $"Harmony source support is suppressed because mismatch classification '{normalizedMismatch.MatchedRule}' was not actionable.");
        }

        private static bool TryExtractProviderUrl(
            IReadOnlyDictionary<string, string> normalizedMetadata,
            out string? providerUrl,
            out string? suppressionReason)
        {
            providerUrl = null;
            suppressionReason = null;

            foreach (var key in UrlKeys)
            {
                if (!normalizedMetadata.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                if (ContainsUnsafeUrlInput(value))
                {
                    suppressionReason = $"Host source metadata key '{key}' contained header-shaped or secret-bearing URL input.";
                    return false;
                }

                if (!Uri.TryCreate(value, UriKind.Absolute, out var providerUri)
                    || !IsHttpUri(providerUri))
                {
                    continue;
                }

                providerUrl = BuildNormalizedUrl(providerUri);
                return true;
            }

            return false;
        }

        private static bool TryExtractLookupValue(
            IReadOnlyDictionary<string, string> normalizedMetadata,
            IEnumerable<string> keys,
            out string? normalizedValue,
            out string? suppressionReason)
        {
            normalizedValue = null;
            suppressionReason = null;

            foreach (var key in keys)
            {
                if (!normalizedMetadata.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                if (ContainsUnsafeLookupInput(value))
                {
                    suppressionReason = $"Host source metadata key '{key}' contained header-shaped or secret-bearing lookup input.";
                    return false;
                }

                normalizedValue = NormalizeLookupValue(value);
                return true;
            }

            return false;
        }

        private static string? ResolveProvider(IReadOnlyDictionary<string, string> normalizedMetadata, out bool allowProviderUrl)
        {
            allowProviderUrl = false;

            foreach (var key in ProviderKeys)
            {
                if (normalizedMetadata.TryGetValue(key, out var rawProvider) && !string.IsNullOrWhiteSpace(rawProvider))
                {
                    var normalizedProvider = NormalizeProvider(rawProvider);
                    switch (normalizedProvider)
                    {
                        case "harmony":
                            allowProviderUrl = true;
                            return "harmony";
                        case "deezer":
                            // Host-owned Deezer metadata is only Harmony-compatible when we can rebuild
                            // a safe Harmony lookup from preserved artist/album/query fields. Deezer URLs
                            // are provenance only and must not be handed to Harmony as direct provider URLs.
                            return "harmony";
                        case not null:
                            return normalizedProvider;
                    }
                }
            }

            foreach (var key in UrlKeys)
            {
                if (!normalizedMetadata.TryGetValue(key, out var rawUrl) || string.IsNullOrWhiteSpace(rawUrl))
                {
                    continue;
                }

                if (ContainsUnsafeUrlInput(rawUrl))
                {
                    continue;
                }

                if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out var providerUri)
                    || !IsHttpUri(providerUri))
                {
                    continue;
                }

                if (providerUri.Host.Contains("harmony", StringComparison.OrdinalIgnoreCase))
                {
                    allowProviderUrl = true;
                    return "harmony";
                }

                if (providerUri.Host.Contains("deezer", StringComparison.OrdinalIgnoreCase))
                {
                    return "harmony";
                }
            }

            return null;
        }

        private static string? NormalizeProvider(string rawProvider)
        {
            var normalized = NormalizeOptionalValue(rawProvider);
            if (normalized == null)
            {
                return null;
            }

            if (normalized.Contains("harmony", StringComparison.OrdinalIgnoreCase))
            {
                return "harmony";
            }

            normalized = normalized.ToLowerInvariant();
            normalized = normalized.Replace('_', '-').Replace(' ', '-');
            normalized = Regex.Replace(normalized, "[^a-z0-9-]+", string.Empty, RegexOptions.CultureInvariant);
            return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
        }

        private static string NormalizeLookupValue(string value)
        {
            var redacted = RedactSensitiveText(value);
            return Regex.Replace(redacted, "\\s+", " ", RegexOptions.CultureInvariant).Trim();
        }

        private static string BuildNormalizedUrl(Uri providerUri)
        {
            var builder = new UriBuilder(providerUri)
            {
                Query = string.Empty,
                Fragment = string.Empty,
                UserName = string.Empty,
                Password = string.Empty
            };

            var normalized = builder.Uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
            return string.IsNullOrWhiteSpace(normalized)
                ? builder.Uri.GetLeftPart(UriPartial.Authority)
                : normalized;
        }

        private static bool IsHttpUri(Uri uri)
        {
            return string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                || string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
        }

        private static IReadOnlyDictionary<string, string> NormalizeMetadata(IReadOnlyDictionary<string, string?>? sourceMetadata)
        {
            var normalized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (sourceMetadata == null)
            {
                return normalized;
            }

            foreach (var pair in sourceMetadata)
            {
                var key = NormalizeOptionalValue(pair.Key);
                var value = NormalizeOptionalValue(pair.Value);
                if (key != null && value != null)
                {
                    normalized[key] = value;
                }
            }

            return normalized;
        }

        private static HostHarmonySourceSupport Support(
            string matchedRule,
            string matchedProvider,
            string? providerUrl,
            string? lookupArtist,
            string? lookupAlbum,
            string? lookupQuery)
        {
            var support = new HostHarmonySourceSupport
            {
                Supported = true,
                RuleVersion = HostHarmonySourceSupport.CurrentRuleVersion,
                MatchedRule = matchedRule,
                MatchedProvider = matchedProvider,
                ProviderUrl = providerUrl,
                LookupArtist = lookupArtist,
                LookupAlbum = lookupAlbum,
                LookupQuery = lookupQuery
            }.Normalize();

            support.ValidatePersistedShape();
            return support;
        }

        private static HostHarmonySourceSupport Suppress(
            string matchedRule,
            string? matchedProvider,
            string suppressionReason)
        {
            var support = new HostHarmonySourceSupport
            {
                Supported = false,
                RuleVersion = HostHarmonySourceSupport.CurrentRuleVersion,
                MatchedRule = matchedRule,
                MatchedProvider = matchedProvider,
                SuppressionReason = suppressionReason
            }.Normalize();

            support.ValidatePersistedShape();
            return support;
        }

        private static bool ContainsUnsafeUrlInput(string value)
        {
            return value.IndexOf('\r') >= 0
                || value.IndexOf('\n') >= 0
                || HeaderSecretPattern.IsMatch(value)
                || BearerSecretPattern.IsMatch(value);
        }

        private static bool ContainsUnsafeLookupInput(string value)
        {
            return value.IndexOf('\r') >= 0
                || value.IndexOf('\n') >= 0
                || HeaderSecretPattern.IsMatch(value)
                || BearerSecretPattern.IsMatch(value);
        }

        private static string RedactSensitiveText(string value)
        {
            var redacted = HeaderSecretPattern.Replace(value, static match => $"{match.Groups["label"].Value}=[REDACTED]");
            redacted = TokenSecretPattern.Replace(redacted, static match => $"{match.Groups["label"].Value}=[REDACTED]");
            return BearerSecretPattern.Replace(redacted, "Bearer [REDACTED]");
        }

        private static string? NormalizeOptionalValue(string? value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
    }
}
