using System;
using System.Collections.Generic;
using System.Linq;
using global::NzbDrone.Core.Plugins.ReleaseReconciler.Actions;
using global::NzbDrone.Core.Plugins.ReleaseReconciler.Candidates;
using global::NzbDrone.Core.Plugins.ReleaseReconciler.Capture;
using global::NzbDrone.Core.Plugins.ReleaseReconciler.Cases;
using global::NzbDrone.Core.Plugins.ReleaseReconciler.Notifications;
using global::NzbDrone.Core.Plugins.ReleaseReconciler.Switching;
using Xunit;

namespace NzbDrone.Core.Plugins.ReleaseReconciler.Test.Notifications
{
    public sealed class ReconciliationNotificationBuilderFixture
    {
        private static readonly DateTimeOffset Now = new(2026, 6, 14, 18, 30, 0, TimeSpan.Zero);

        [Fact]
        public void should_build_transport_neutral_envelope_with_signed_content_action_links()
        {
            var builder = new ReconciliationNotificationBuilder(() => Now);
            var envelope = builder.Build(CreateCase(ReconciliationClassification.MissingReleaseInCurrentGroup), CreateSettings());

            Assert.Equal("Missing release in current group", envelope.Classification);
            Assert.Equal("signed-content-path", envelope.ActionUrlKind);
            Assert.Equal(Now.AddMinutes(240), envelope.ActionTokenExpiresAtUtc);
            Assert.Equal("signed-readonly-path", envelope.EvidenceUrlKind);
            Assert.Equal(Now.AddMinutes(240), envelope.EvidenceTokenExpiresAtUtc);
            Assert.Equal(2, envelope.Actions.Count);
            Assert.All(envelope.Actions, action => Assert.Contains("/content/release-reconciler/action/", action.Url, StringComparison.Ordinal));
            Assert.All(envelope.Actions, action => Assert.DoesNotContain("token=", action.Url, StringComparison.Ordinal));
            Assert.Contains(envelope.EvidenceLinks, link => link.Label == "Evidence Page" && link.Url.StartsWith("https://lidarr.example/content/release-reconciler/evidence/", StringComparison.Ordinal));
            Assert.Contains(envelope.EvidenceLinks, link => link.Label == "Bandcamp Source" && link.Url == "https://artist.bandcamp.com/album/example");
            Assert.Contains(envelope.EvidenceLinks, link => link.Label == "MusicBrainz Release" && link.Url.Contains("release-mbid-1", StringComparison.Ordinal));
            Assert.Contains(envelope.EvidenceLinks, link => link.Label == "MusicBrainz Release Group" && link.Url.Contains("album-mbid-1", StringComparison.Ordinal));
            Assert.Contains(envelope.EvidenceLinks, link => link.Label == "Harmony Search" && link.Url.Contains("term=https%3A%2F%2Fartist.bandcamp.com%2Falbum%2Fexample", StringComparison.Ordinal));
            Assert.Equal("Example Artist", envelope.Candidates.Single().ArtistName);
            Assert.Contains(envelope.Fields, field => field.Name == "Evidence Page" && field.Value == "Signed link generated");
            Assert.Contains(envelope.Fields, field => field.Name == "Evidence Token Expires" && field.Value == Now.AddMinutes(240).ToString("O"));
            Assert.Contains("Lookup Attempts", envelope.Fields.Select(static field => field.Name));
        }

        [Fact]
        public void should_prefer_persisted_harmony_source_support_over_legacy_album_identifier()
        {
            var builder = new ReconciliationNotificationBuilder(() => Now);
            var envelope = builder.Build(
                new ReconciliationCase(
                    sourceSeam: "bandcamp-retag",
                    phase: "evaluated",
                    capturedIdentifiers: new Dictionary<string, string?>
                    {
                        ["artistName"] = "Example Artist",
                        ["albumTitle"] = "Example Album",
                        ["albumUrl"] = "https://legacy.bandcamp.com/album/example"
                    },
                    structuralEvidence: new ReconciliationEvidence(),
                    diagnosticSummary: "releaseSpecificity=preferred-release trackCount=0",
                    harmonySourceSupport: new HostHarmonySourceSupport
                    {
                        Supported = true,
                        MatchedRule = "harmony-provider-url",
                        MatchedProvider = "harmony",
                        ProviderUrl = "https://artist.bandcamp.com/album/example"
                    },
                    caseId: "rcase-structured-source",
                    capturedAtUtc: Now.AddMinutes(-20),
                    updatedAtUtc: Now.AddMinutes(-5),
                    evaluationResult: new ReconciliationEvaluationResult
                    {
                        Classification = ReconciliationClassification.MissingReleaseInCurrentGroup,
                        EvaluatedAtUtc = Now.AddMinutes(-10),
                        RankedCandidates = new List<RankedCandidateRelease>
                        {
                            new()
                            {
                                Rank = 1,
                                AlbumMusicBrainzId = "album-mbid-1",
                                AlbumTitle = "Example Album",
                                ArtistMusicBrainzId = "artist-mbid-1",
                                ArtistName = "Example Artist",
                                ReleaseMusicBrainzId = "release-mbid-1",
                                ReleaseTitle = "Example Album",
                                SameReleaseGroup = true,
                                HasStrongIdentitySignal = true,
                                LookupPaths = new List<CandidateLookupPath> { CandidateLookupPath.RecordingIds }
                            }
                        }
                    }),
                CreateSettings());

            Assert.Contains(envelope.EvidenceLinks, link => link.Label == "Bandcamp Source" && link.Url == "https://artist.bandcamp.com/album/example");
            Assert.DoesNotContain(envelope.EvidenceLinks, link => link.Label == "Bandcamp Source" && link.Url == "https://legacy.bandcamp.com/album/example");
            Assert.Contains(envelope.EvidenceLinks, link => link.Label == "Harmony Search" && link.Url.Contains("term=https%3A%2F%2Fartist.bandcamp.com%2Falbum%2Fexample", StringComparison.Ordinal));
            Assert.DoesNotContain(envelope.EvidenceLinks, link => link.Label == "Harmony Search" && link.Url.Contains("legacy.bandcamp.com", StringComparison.Ordinal));
        }

        [Fact]
        public void should_build_harmony_search_from_persisted_lookup_fields_when_provider_url_is_missing()
        {
            var links = ReconciliationNotificationBuilder.BuildSupplementalEvidenceLinks(
                CreateSettings(),
                new Dictionary<string, string?>
                {
                    ["artistName"] = "Display Artist",
                    ["albumTitle"] = "Display Album",
                    ["preferredReleaseMusicBrainzId"] = "release-mbid-fallback",
                    ["albumMusicBrainzId"] = "album-mbid-fallback"
                },
                new HostHarmonySourceSupport
                {
                    Supported = true,
                    MatchedRule = "harmony-lookup-fields",
                    MatchedProvider = "harmony",
                    LookupArtist = "Lookup Artist",
                    LookupAlbum = "Lookup Album"
                },
                evaluation: null,
                candidates: Array.Empty<ReconciliationNotificationCandidate>());

            Assert.Contains(links, link => link.Label == "Harmony Search" && link.Url.Contains("term=Lookup Artist Lookup Album", StringComparison.Ordinal));
            Assert.DoesNotContain(links, link => link.Label == "Harmony Search" && link.Url.Contains("Display%20Artist%20Display%20Album", StringComparison.Ordinal));
            Assert.DoesNotContain(links, link => link.Label == "Bandcamp Source");
            Assert.Contains(links, link => link.Label == "MusicBrainz Release" && link.Url.Contains("release-mbid-fallback", StringComparison.Ordinal));
            Assert.Contains(links, link => link.Label == "MusicBrainz Release Group" && link.Url.Contains("album-mbid-fallback", StringComparison.Ordinal));
        }

        [Fact]
        public void should_prefer_lookup_query_and_omit_harmony_links_for_incomplete_or_suppressed_source_support()
        {
            var settings = CreateSettings();

            var queryLinks = ReconciliationNotificationBuilder.BuildSupplementalEvidenceLinks(
                settings,
                new Dictionary<string, string?>
                {
                    ["artistName"] = "Display Artist",
                    ["albumTitle"] = "Display Album"
                },
                new HostHarmonySourceSupport
                {
                    Supported = true,
                    MatchedRule = "harmony-lookup-query",
                    MatchedProvider = "harmony",
                    LookupQuery = "provider:bandcamp slug:example",
                    LookupArtist = "Ignored Artist",
                    LookupAlbum = "Ignored Album"
                },
                evaluation: null,
                candidates: Array.Empty<ReconciliationNotificationCandidate>());

            Assert.Contains(queryLinks, link => link.Label == "Harmony Search" && link.Url.Contains("term=provider%3Abandcamp slug%3Aexample", StringComparison.Ordinal));
            Assert.DoesNotContain(queryLinks, link => link.Label == "Harmony Search" && link.Url.Contains("Ignored%20Artist%20Ignored%20Album", StringComparison.Ordinal));

            var incompleteLinks = ReconciliationNotificationBuilder.BuildSupplementalEvidenceLinks(
                settings,
                new Dictionary<string, string?>
                {
                    ["artistName"] = "Display Artist",
                    ["albumTitle"] = "Display Album",
                    ["albumUrl"] = "https://legacy.bandcamp.com/album/example"
                },
                new HostHarmonySourceSupport
                {
                    Supported = true,
                    MatchedRule = "harmony-partial-lookup",
                    MatchedProvider = "harmony",
                    LookupArtist = "Only Artist"
                },
                evaluation: null,
                candidates: Array.Empty<ReconciliationNotificationCandidate>());

            Assert.DoesNotContain(incompleteLinks, link => link.Label == "Harmony Search");
            Assert.DoesNotContain(incompleteLinks, link => link.Label == "Bandcamp Source");

            var suppressedLinks = ReconciliationNotificationBuilder.BuildSupplementalEvidenceLinks(
                settings,
                new Dictionary<string, string?>
                {
                    ["artistName"] = "Display Artist",
                    ["albumTitle"] = "Display Album",
                    ["albumUrl"] = "https://legacy.bandcamp.com/album/example"
                },
                new HostHarmonySourceSupport
                {
                    Supported = false,
                    MatchedRule = "harmony-suppressed",
                    SuppressionReason = "unsupported-provider"
                },
                evaluation: null,
                candidates: Array.Empty<ReconciliationNotificationCandidate>());

            Assert.DoesNotContain(suppressedLinks, link => link.Label == "Harmony Search");
            Assert.DoesNotContain(suppressedLinks, link => link.Label == "Bandcamp Source");
        }

        [Fact]
        public void should_include_approve_switch_action_when_feature_flag_enabled_for_safe_winner()
        {
            var builder = new ReconciliationNotificationBuilder(() => Now);
            var envelope = builder.Build(
                CreateCase(ReconciliationClassification.BetterExistingRelease),
                CreateSettings(),
                new ReleaseSwitchSettings { EnableReleaseSwitchApproval = true });

            Assert.Equal(3, envelope.Actions.Count);
            var approveAction = Assert.Single(envelope.Actions.Where(action => action.Action == ReconciliationOperatorActionKinds.ApproveSwitch));
            Assert.Equal("Approve switch", approveAction.Label);
            Assert.Contains("/content/release-reconciler/action/", approveAction.Url, StringComparison.Ordinal);
            Assert.DoesNotContain("token=", approveAction.Url, StringComparison.Ordinal);
        }

        [Fact]
        public void should_omit_evidence_page_link_when_public_base_url_is_missing()
        {
            var builder = new ReconciliationNotificationBuilder(() => Now);
            var settings = CreateSettings();
            settings.PublicBaseUrl = null;

            var envelope = builder.Build(CreateCase(ReconciliationClassification.MissingReleaseInCurrentGroup), settings);

            Assert.Null(envelope.EvidenceUrlKind);
            Assert.Null(envelope.EvidenceTokenExpiresAtUtc);
            Assert.DoesNotContain(envelope.EvidenceLinks, link => link.Label == "Evidence Page");
            Assert.Contains(envelope.Fields, field => field.Name == "Evidence Page" && field.Value == "Omitted (PublicBaseUrl not configured)");
            Assert.DoesNotContain(envelope.Fields, field => field.Name == "Evidence Token Expires");
        }

        [Fact]
        public void should_omit_evidence_page_link_when_public_base_url_is_invalid()
        {
            var builder = new ReconciliationNotificationBuilder(() => Now);
            var settings = CreateSettings();
            settings.PublicBaseUrl = "not-a-valid-url";
            settings.EvidencePagePath = "release-reconciler/evidence/";

            var envelope = builder.Build(CreateCase(ReconciliationClassification.MissingReleaseInCurrentGroup), settings);

            Assert.Null(envelope.EvidenceUrlKind);
            Assert.Null(envelope.EvidenceTokenExpiresAtUtc);
            Assert.DoesNotContain(envelope.EvidenceLinks, link => link.Label == "Evidence Page");
            Assert.Contains(envelope.Fields, field => field.Name == "Evidence Page" && field.Value == "Omitted (PublicBaseUrl invalid)");
            Assert.DoesNotContain(envelope.Fields, field => field.Name == "Evidence Token Expires");
        }

        [Fact]
        public void should_suppress_approve_switch_when_flag_disabled_or_evaluation_is_unsafe()
        {
            var builder = new ReconciliationNotificationBuilder(() => Now);
            var disabledEnvelope = builder.Build(CreateCase(ReconciliationClassification.BetterExistingRelease), CreateSettings());
            var refusalEnvelope = builder.Build(
                CreateCase(ReconciliationClassification.NoSafeMatch, ReconciliationRefusalReason.TitleOnlyMatchNotAllowed, titleOnlyCandidate: true),
                CreateSettings(),
                new ReleaseSwitchSettings { EnableReleaseSwitchApproval = true });
            var missingWinnerEnvelope = builder.Build(
                CreateCase(ReconciliationClassification.MissingReleaseInCurrentGroup),
                CreateSettings(),
                new ReleaseSwitchSettings { EnableReleaseSwitchApproval = true });

            Assert.DoesNotContain(disabledEnvelope.Actions, action => action.Action == ReconciliationOperatorActionKinds.ApproveSwitch);
            Assert.DoesNotContain(refusalEnvelope.Actions, action => action.Action == ReconciliationOperatorActionKinds.ApproveSwitch);
            Assert.DoesNotContain(missingWinnerEnvelope.Actions, action => action.Action == ReconciliationOperatorActionKinds.ApproveSwitch);
        }

        [Fact]
        public void should_require_cross_group_policy_before_emitting_approve_switch_for_different_group_winner()
        {
            var builder = new ReconciliationNotificationBuilder(() => Now);
            var policyDisabled = builder.Build(
                CreateCase(ReconciliationClassification.DifferentReleaseGroup),
                CreateSettings(),
                new ReleaseSwitchSettings { EnableReleaseSwitchApproval = true });
            var policyEnabled = builder.Build(
                CreateCase(ReconciliationClassification.DifferentReleaseGroup),
                CreateSettings(),
                new ReleaseSwitchSettings
                {
                    EnableReleaseSwitchApproval = true,
                    EnableDifferentGroupReleaseSwitchApproval = true
                });

            Assert.DoesNotContain(policyDisabled.Actions, action => action.Action == ReconciliationOperatorActionKinds.ApproveSwitch);
            Assert.Contains(policyEnabled.Actions, action => action.Action == ReconciliationOperatorActionKinds.ApproveSwitch);
        }

        [Fact]
        public void should_render_refusal_guidance_for_title_only_no_safe_match_cases()
        {
            var builder = new ReconciliationNotificationBuilder(() => Now);
            var envelope = builder.Build(CreateCase(ReconciliationClassification.NoSafeMatch, ReconciliationRefusalReason.TitleOnlyMatchNotAllowed, titleOnlyCandidate: true), CreateSettings());

            Assert.Equal("No safe match", envelope.Classification);
            Assert.NotNull(envelope.RefusalContext);
            Assert.Contains("title-level evidence", envelope.RefusalContext!, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Title/artist fallback", envelope.RefusalContext!, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("No safe match", envelope.BuildPlainTextBody(), StringComparison.Ordinal);
            Assert.Contains(envelope.Candidates, candidate => candidate.TitleOnlyCandidate);
        }

        private static ReconciliationCase CreateCase(ReconciliationClassification classification, ReconciliationRefusalReason? refusalReason = null, bool titleOnlyCandidate = false)
        {
            var canPersistWinner = classification is ReconciliationClassification.BetterExistingRelease or ReconciliationClassification.DifferentReleaseGroup;

            return new ReconciliationCase(
                sourceSeam: "bandcamp-retag",
                phase: "evaluated",
                capturedIdentifiers: new Dictionary<string, string?>
                {
                    ["artistName"] = "Example Artist",
                    ["albumTitle"] = "Example Album"
                },
                structuralEvidence: new ReconciliationEvidence(),
                downloadId: "download-123",
                outputPath: "/music/example-artist/example-album",
                diagnosticSummary: "releaseSpecificity=preferred-release trackCount=0",
                harmonySourceSupport: new HostHarmonySourceSupport
                {
                    Supported = true,
                    MatchedRule = "harmony-provider-url",
                    MatchedProvider = "harmony",
                    ProviderUrl = "https://artist.bandcamp.com/album/example"
                },
                caseId: "rcase-123",
                capturedAtUtc: Now.AddMinutes(-20),
                updatedAtUtc: Now.AddMinutes(-5),
                evaluationResult: new ReconciliationEvaluationResult
                {
                    Classification = classification,
                    RefusalReason = refusalReason,
                    UsedTitleArtistFallback = refusalReason.HasValue,
                    WinnerAlbumMusicBrainzId = canPersistWinner ? "album-mbid-1" : null,
                    WinnerReleaseMusicBrainzId = canPersistWinner ? "release-mbid-1" : null,
                    EvaluatedAtUtc = Now.AddMinutes(-10),
                    RankedCandidates = new List<RankedCandidateRelease>
                    {
                        new()
                        {
                            Rank = 1,
                            AlbumMusicBrainzId = "album-mbid-1",
                            AlbumTitle = "Example Album",
                            ArtistMusicBrainzId = "artist-mbid-1",
                            ArtistName = "Example Artist",
                            ReleaseMusicBrainzId = "release-mbid-1",
                            ReleaseTitle = "Example Album",
                            ReleaseDisambiguation = "2024 remaster",
                            SameReleaseGroup = classification != ReconciliationClassification.DifferentReleaseGroup,
                            HasStrongIdentitySignal = !titleOnlyCandidate,
                            TitleOnlyCandidate = titleOnlyCandidate,
                            LookupPaths = new List<CandidateLookupPath> { CandidateLookupPath.RecordingIds }
                        }
                    }
                });
        }

        private static global::NzbDrone.Core.Plugins.ReleaseReconciler.Notifications.ReconciliationNotificationSettings CreateSettings()
        {
            return new global::NzbDrone.Core.Plugins.ReleaseReconciler.Notifications.ReconciliationNotificationSettings
            {
                PublicBaseUrl = "https://lidarr.example",
                HarmonySearchBaseUrl = "https://api.lidarr.audio/api/v0.4/search",
                Discord = new global::NzbDrone.Core.Plugins.ReleaseReconciler.Notifications.ReconciliationNotificationSettings.DiscordNotificationSettings
                {
                    Enabled = true,
                    WebHookUrl = "https://discord.example/webhook"
                },
                Telegram = new global::NzbDrone.Core.Plugins.ReleaseReconciler.Notifications.ReconciliationNotificationSettings.TelegramNotificationSettings
                {
                    Enabled = true,
                    BotToken = "bot-token",
                    ChatId = "12345"
                }
            };
        }
    }
}
