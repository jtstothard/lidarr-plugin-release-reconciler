using System;
using System.Collections.Generic;
using NzbDrone.Core.Plugins.ReleaseReconciler.Capture;
using Xunit;

namespace NzbDrone.Core.Plugins.ReleaseReconciler.Test.Capture
{
    public sealed class HostHarmonySourceSupportClassifierFixture
    {
        private readonly HostHarmonySourceSupportClassifier _subject = new();

        [Fact]
        public void classify_should_support_harmony_provider_url_and_strip_query_strings()
        {
            var support = _subject.Classify(
                new HostImportFailureClassification
                {
                    Actionable = true,
                    RuleVersion = HostImportFailureClassification.CurrentRuleVersion,
                    MatchedRule = "track-number-mismatch"
                },
                new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["provider"] = "Harmony",
                    ["providerUrl"] = "https://harmony.example/releases/album-123?token=secret&sig=abc123#fragment",
                    ["lookupArtist"] = "Ignored Artist"
                });

            Assert.True(support.Supported);
            Assert.Equal(HostHarmonySourceSupport.CurrentRuleVersion, support.RuleVersion);
            Assert.Equal("harmony-provider-url", support.MatchedRule);
            Assert.Equal("harmony", support.MatchedProvider);
            Assert.Equal("https://harmony.example/releases/album-123", support.ProviderUrl);
            Assert.Null(support.SuppressionReason);
            Assert.Null(support.LookupArtist);
            Assert.Null(support.LookupAlbum);
            Assert.Null(support.LookupQuery);
        }

        [Fact]
        public void classify_should_support_equivalent_lookup_fields_when_url_is_absent()
        {
            var support = _subject.Classify(
                new HostImportFailureClassification
                {
                    Actionable = true,
                    RuleVersion = HostImportFailureClassification.CurrentRuleVersion,
                    MatchedRule = "track-ordering-mismatch"
                },
                new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["providerName"] = "harmony-search",
                    ["lookupArtist"] = "  Soccer Mommy  ",
                    ["lookupAlbum"] = "  color theory  ",
                    ["lookupQuery"] = "Soccer Mommy color theory"
                });

            Assert.True(support.Supported);
            Assert.Equal("harmony-equivalent-lookup", support.MatchedRule);
            Assert.Equal("harmony", support.MatchedProvider);
            Assert.Null(support.ProviderUrl);
            Assert.Equal("Soccer Mommy", support.LookupArtist);
            Assert.Equal("color theory", support.LookupAlbum);
            Assert.Equal("Soccer Mommy color theory", support.LookupQuery);
        }

        [Fact]
        public void classify_should_treat_deezer_metadata_as_lookup_only_harmony_support()
        {
            var support = _subject.Classify(
                new HostImportFailureClassification
                {
                    Actionable = true,
                    RuleVersion = HostImportFailureClassification.CurrentRuleVersion,
                    MatchedRule = "track-ordering-mismatch"
                },
                new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["providerName"] = "Deezer",
                    ["sourceUrl"] = "https://www.deezer.com/us/album/302127",
                    ["lookupArtist"] = "  Daft Punk  ",
                    ["lookupAlbum"] = "  Discovery  "
                });

            Assert.True(support.Supported);
            Assert.Equal("harmony-equivalent-lookup", support.MatchedRule);
            Assert.Equal("harmony", support.MatchedProvider);
            Assert.Null(support.ProviderUrl);
            Assert.Equal("Daft Punk", support.LookupArtist);
            Assert.Equal("Discovery", support.LookupAlbum);
            Assert.Null(support.LookupQuery);
        }

        [Fact]
        public void classify_should_suppress_non_actionable_mismatch_cases()
        {
            var support = HostHarmonySourceSupportClassifier.SuppressNonActionable(
                new HostImportFailureClassification
                {
                    Actionable = false,
                    RuleVersion = HostImportFailureClassification.CurrentRuleVersion,
                    MatchedRule = "non-actionable-import-failure",
                    SuppressionReason = "import failure did not match track-number or ordering mismatch rules."
                });

            Assert.False(support.Supported);
            Assert.Equal("mismatch-not-actionable", support.MatchedRule);
            Assert.Null(support.MatchedProvider);
            Assert.Equal("Harmony source support is suppressed because mismatch classification 'non-actionable-import-failure' was not actionable.", support.SuppressionReason);
        }

        [Fact]
        public void classify_should_suppress_unsupported_providers()
        {
            var support = _subject.Classify(
                new HostImportFailureClassification
                {
                    Actionable = true,
                    RuleVersion = HostImportFailureClassification.CurrentRuleVersion,
                    MatchedRule = "track-number-mismatch"
                },
                new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["provider"] = "Bandcamp",
                    ["providerUrl"] = "https://bandcamp.example/releases/album-123"
                });

            Assert.False(support.Supported);
            Assert.Equal("provider-unsupported", support.MatchedRule);
            Assert.Equal("bandcamp", support.MatchedProvider);
            Assert.Equal("Provider 'bandcamp' is not supported for Harmony source extraction.", support.SuppressionReason);
        }

        [Fact]
        public void classify_should_suppress_header_shaped_provider_url_input()
        {
            var support = _subject.Classify(
                new HostImportFailureClassification
                {
                    Actionable = true,
                    RuleVersion = HostImportFailureClassification.CurrentRuleVersion,
                    MatchedRule = "track-number-mismatch"
                },
                new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["provider"] = "Harmony",
                    ["providerUrl"] = "Authorization: Bearer super-secret-token"
                });

            Assert.False(support.Supported);
            Assert.Equal("unsafe-provider-url", support.MatchedRule);
            Assert.Equal("harmony", support.MatchedProvider);
            Assert.Equal("Host source metadata key 'providerUrl' contained header-shaped or secret-bearing URL input.", support.SuppressionReason);
        }

        [Fact]
        public void classify_should_suppress_secret_bearing_lookup_metadata_for_deezer_compatible_sources()
        {
            var support = _subject.Classify(
                new HostImportFailureClassification
                {
                    Actionable = true,
                    RuleVersion = HostImportFailureClassification.CurrentRuleVersion,
                    MatchedRule = "track-ordering-mismatch"
                },
                new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["provider"] = "Deezer",
                    ["sourceUrl"] = "https://www.deezer.com/us/album/302127",
                    ["lookupArtist"] = "Authorization: Bearer super-secret-token",
                    ["lookupAlbum"] = "Discovery"
                });

            Assert.False(support.Supported);
            Assert.Equal("unsafe-lookup-metadata", support.MatchedRule);
            Assert.Equal("harmony", support.MatchedProvider);
            Assert.Equal("Host source metadata key 'lookupArtist' contained header-shaped or secret-bearing lookup input.", support.SuppressionReason);
        }
    }
}
