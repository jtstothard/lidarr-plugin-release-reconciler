extern alias release_reconciler_nlog;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Logger = release_reconciler_nlog::NLog.Logger;
using LogManager = release_reconciler_nlog::NLog.LogManager;
using NzbDrone.Core.Plugins.ReleaseReconciler.Capture;
using NzbDrone.Core.Plugins.ReleaseReconciler.Cases;
using Xunit;

namespace NzbDrone.Core.Plugins.ReleaseReconciler.Test.Capture
{
    public sealed class BandcampReconciliationCaseCaptureFixture : IDisposable
    {
        private readonly string _rootDirectory;
        private readonly Logger _logger;

        public BandcampReconciliationCaseCaptureFixture()
        {
            _rootDirectory = Path.Combine(Path.GetTempPath(), $"release-reconciler-capture-tests-{Guid.NewGuid():N}");
            _logger = LogManager.GetLogger(nameof(BandcampReconciliationCaseCaptureFixture));
        }

        [Fact]
        public void capture_should_round_trip_soccer_mommy_sample_with_structural_evidence()
        {
            var store = CreateStore();
            var capture = CreateCapture(store);
            var evidence = LoadSoccerMommySample();

            var persisted = capture.Capture(evidence);
            var reloaded = store.Get(persisted.CaseId);

            Assert.NotNull(reloaded);
            Assert.Equal(BandcampReconciliationCaseCapture.BandcampDownloadSourceSeam, reloaded!.SourceSeam);
            Assert.Equal("post-extract-review", reloaded.Phase);
            Assert.Equal("bc-soccer-mommy-color-theory", reloaded.DownloadId);
            Assert.Equal("/music/Soccer Mommy/color theory", reloaded.OutputPath);
            Assert.Equal("Soccer Mommy", reloaded.CapturedIdentifiers["artistName"]);
            Assert.Equal("color theory", reloaded.CapturedIdentifiers["albumTitle"]);
            Assert.Equal("7d4d7c31-8d66-4b90-a312-4310dcd58e9f", reloaded.CapturedIdentifiers["albumMusicBrainzId"]);
            Assert.Equal("b5f7f0bf-4b9e-4f7d-8a6b-7d7ae0ca1b68", reloaded.CapturedIdentifiers["preferredReleaseMusicBrainzId"]);
            Assert.NotNull(reloaded.StructuralEvidence);
            Assert.Equal(3, reloaded.StructuralEvidence!.TrackCount);
            Assert.Equal("b5f7f0bf-4b9e-4f7d-8a6b-7d7ae0ca1b68", reloaded.StructuralEvidence.PreferredReleaseMusicBrainzId);
            Assert.Equal(new[]
            {
                "820cd57e-f7f8-4f1b-a3df-8f13bb5e50dd",
                "46502aa0-6b7e-4e90-8aec-f9d6a6381420",
                "f5379162-79d6-41b6-8cce-37f0f0c435a2"
            }, reloaded.StructuralEvidence.RecordingMusicBrainzIds);
            Assert.Equal(new[]
            {
                "bc3d3319-c329-4ed0-b4d0-7cf30391ec5a",
                "53a59b1d-1957-4cd3-b4b0-5e609b0af377"
            }, reloaded.StructuralEvidence.ReleaseTrackMusicBrainzIds);
            Assert.Equal(new[] { "bloodstream", "circle the drain", "royal screw up" }, reloaded.StructuralEvidence.Tracks.Select(static track => track.Title).ToArray());
            Assert.NotNull(reloaded.StructuralEvidence.PreferredRelease);
            Assert.Equal("official", reloaded.StructuralEvidence.PreferredRelease!.ReleaseStatus);
            Assert.Equal("Loma Vista", reloaded.StructuralEvidence.PreferredRelease.Label);
            Assert.Equal(1, reloaded.StructuralEvidence.PreferredRelease.DiscCount);
            Assert.Equal("Digital Media", reloaded.StructuralEvidence.PreferredRelease.MediaByDisc[1]);
            Assert.Contains("releaseSpecificity=preferred-release", reloaded.DiagnosticSummary!, StringComparison.Ordinal);
            Assert.Contains("trackCount=3", reloaded.DiagnosticSummary!, StringComparison.Ordinal);
            Assert.Contains("releaseTrackMbids=2", reloaded.DiagnosticSummary!, StringComparison.Ordinal);
            Assert.Equal("downloaded files diverged from preferred release track mapping", reloaded.LastError);
            Assert.False(reloaded.CapturedIdentifiers.ContainsKey("albumUrl"));
            Assert.NotNull(reloaded.HarmonySourceSupport);
            Assert.False(reloaded.HarmonySourceSupport!.Supported);
            Assert.Equal(HostHarmonySourceSupport.CurrentRuleVersion, reloaded.HarmonySourceSupport.RuleVersion);
            Assert.Contains("not actionable", reloaded.HarmonySourceSupport.SuppressionReason!, StringComparison.OrdinalIgnoreCase);
            Assert.Null(reloaded.HarmonySourceSupport.ProviderUrl);
        }

        [Fact]
        public void capture_should_persist_harmony_source_support_for_actionable_bandcamp_track_mismatch()
        {
            var store = CreateStore();
            var capture = CreateCapture(store);
            var evidence = BuildActionableBandcampEvidence();

            var persisted = capture.Capture(evidence);
            var reloaded = store.Get(persisted.CaseId);

            Assert.NotNull(reloaded);
            Assert.Equal("failed", reloaded!.Phase);
            Assert.False(reloaded.CapturedIdentifiers.ContainsKey("albumUrl"));
            Assert.NotNull(reloaded.HarmonySourceSupport);
            Assert.True(reloaded.HarmonySourceSupport!.Supported);
            Assert.Equal(HostHarmonySourceSupport.CurrentRuleVersion, reloaded.HarmonySourceSupport.RuleVersion);
            Assert.Equal("harmony-provider-url", reloaded.HarmonySourceSupport.MatchedRule);
            Assert.Equal("harmony", reloaded.HarmonySourceSupport.MatchedProvider);
            Assert.Equal("https://soccermommy.bandcamp.com/album/color-theory", reloaded.HarmonySourceSupport.ProviderUrl);
            Assert.Null(reloaded.HarmonySourceSupport.LookupArtist);
            Assert.Null(reloaded.HarmonySourceSupport.LookupAlbum);
            Assert.Null(reloaded.HarmonySourceSupport.LookupQuery);
            Assert.Null(reloaded.HarmonySourceSupport.SuppressionReason);
        }

        [Fact]
        public void capture_should_keep_case_identity_stable_when_only_bandcamp_source_hints_change()
        {
            var store = CreateStore();
            var capture = CreateCapture(store);
            var baselineEvidence = BuildActionableBandcampEvidence();
            var updatedEvidence = BuildActionableBandcampEvidence();
            updatedEvidence.AlbumUrl = "https://soccermommy.bandcamp.com/album/color-theory-deluxe";
            updatedEvidence.Title = "Soccer Mommy - color theory deluxe";

            var baseline = capture.Capture(baselineEvidence);
            var updated = capture.Capture(updatedEvidence);
            var reloaded = store.Get(baseline.CaseId);
            var snapshots = store.List();

            Assert.Equal(baseline.CaseId, updated.CaseId);
            Assert.Single(snapshots);
            Assert.NotNull(reloaded);
            Assert.False(reloaded!.CapturedIdentifiers.ContainsKey("albumUrl"));
            Assert.NotNull(reloaded.HarmonySourceSupport);
            Assert.True(reloaded.HarmonySourceSupport!.Supported);
            Assert.Equal("https://soccermommy.bandcamp.com/album/color-theory-deluxe", reloaded.HarmonySourceSupport.ProviderUrl);
        }

        [Fact]
        public void capture_should_persist_guidance_needed_case_when_release_specificity_is_missing()
        {
            var store = CreateStore();
            var capture = CreateCapture(store);
            var evidence = BuildDegradedEvidence();

            var persisted = capture.Capture(evidence);
            var reloaded = store.Get(persisted.CaseId);

            Assert.NotNull(reloaded);
            Assert.Equal("guidance-needed", reloaded!.Phase);
            Assert.Equal("Bandcamp Sampler", reloaded.CapturedIdentifiers["artistName"]);
            Assert.Equal("loose singles", reloaded.CapturedIdentifiers["albumTitle"]);
            Assert.False(reloaded.CapturedIdentifiers.ContainsKey("albumMusicBrainzId"));
            Assert.False(reloaded.CapturedIdentifiers.ContainsKey("preferredReleaseMusicBrainzId"));
            Assert.NotNull(reloaded.StructuralEvidence);
            Assert.Equal(1, reloaded.StructuralEvidence!.TrackCount);
            Assert.Equal(new[] { "mystery song" }, reloaded.StructuralEvidence.Tracks.Select(static track => track.Title).ToArray());
            Assert.Empty(reloaded.StructuralEvidence.RecordingMusicBrainzIds);
            Assert.Empty(reloaded.StructuralEvidence.ReleaseTrackMusicBrainzIds);
            Assert.Contains("releaseSpecificity=guidance-needed", reloaded.DiagnosticSummary!, StringComparison.Ordinal);
            Assert.Equal("cookies=[REDACTED]", reloaded.LastError);
        }

        [Fact]
        public void capture_should_suppress_harmony_source_support_for_unsafe_bandcamp_provider_url()
        {
            var store = CreateStore();
            var capture = CreateCapture(store);
            var evidence = BuildActionableBandcampEvidence();
            evidence.AlbumUrl = "authorization=Bearer bandcamp-provider-secret";

            var persisted = capture.Capture(evidence);
            var reloaded = store.Get(persisted.CaseId);

            Assert.NotNull(reloaded);
            Assert.False(reloaded!.CapturedIdentifiers.ContainsKey("albumUrl"));
            Assert.NotNull(reloaded.HarmonySourceSupport);
            Assert.False(reloaded.HarmonySourceSupport!.Supported);
            Assert.Equal(HostHarmonySourceSupport.CurrentRuleVersion, reloaded.HarmonySourceSupport.RuleVersion);
            Assert.Equal("unsafe-provider-url", reloaded.HarmonySourceSupport.MatchedRule);
            Assert.Equal("harmony", reloaded.HarmonySourceSupport.MatchedProvider);
            Assert.Equal("Host source metadata key 'providerUrl' contained header-shaped or secret-bearing URL input.", reloaded.HarmonySourceSupport.SuppressionReason);
            Assert.Null(reloaded.HarmonySourceSupport.ProviderUrl);
        }

        [Fact]
        public void capture_should_suppress_harmony_source_support_when_bandcamp_lookup_metadata_is_incomplete()
        {
            var store = CreateStore();
            var capture = CreateCapture(store);
            var evidence = BuildActionableBandcampEvidence();
            evidence.AlbumUrl = string.Empty;
            evidence.Title = string.Empty;
            evidence.RetagContext.ArtistName = string.Empty;
            evidence.RetagContext.AlbumTitle = string.Empty;

            var persisted = capture.Capture(evidence);
            var reloaded = store.Get(persisted.CaseId);

            Assert.NotNull(reloaded);
            Assert.False(reloaded!.CapturedIdentifiers.ContainsKey("albumUrl"));
            Assert.NotNull(reloaded.HarmonySourceSupport);
            Assert.False(reloaded.HarmonySourceSupport!.Supported);
            Assert.Equal(HostHarmonySourceSupport.CurrentRuleVersion, reloaded.HarmonySourceSupport.RuleVersion);
            Assert.Equal("lookup-metadata-missing", reloaded.HarmonySourceSupport.MatchedRule);
            Assert.Equal("harmony", reloaded.HarmonySourceSupport.MatchedProvider);
            Assert.Equal("Harmony source metadata did not include a safe provider URL or equivalent lookup fields.", reloaded.HarmonySourceSupport.SuppressionReason);
            Assert.Null(reloaded.HarmonySourceSupport.ProviderUrl);
            Assert.Null(reloaded.HarmonySourceSupport.LookupArtist);
            Assert.Null(reloaded.HarmonySourceSupport.LookupAlbum);
            Assert.Null(reloaded.HarmonySourceSupport.LookupQuery);
        }

        [Fact]
        public void capture_should_preserve_redacted_last_error_for_failed_phase()
        {
            var store = CreateStore();
            var capture = CreateCapture(store);
            var evidence = new BandcampMismatchEvidence
            {
                DownloadId = "bc-bandcamp-sampler-failed-capture",
                AlbumUrl = "https://example.bandcamp.com/album/failed-capture",
                Title = "Bandcamp Sampler - failed capture",
                OutputPath = "/music/Bandcamp Sampler/failed capture",
                QueuePhase = string.Empty,
                QueueStatus = "Failed",
                ErrorMessage = "cookies=session-cookie-123 token=provider-secret-456 import aborted",
                CompletedAtUtc = new DateTimeOffset(2026, 6, 14, 10, 9, 0, TimeSpan.Zero),
                RetagContext = new BandcampRetagEvidence
                {
                    ArtistName = "Bandcamp Sampler",
                    AlbumTitle = "failed capture",
                    AlbumMusicBrainzId = "album-failed-1",
                    PreferredRelease = new BandcampPreferredReleaseEvidence
                    {
                        ReleaseMusicBrainzId = "release-failed-1",
                        ReleaseArtistMusicBrainzId = "artist-failed-1"
                    },
                    Genres = new[] { "indie" },
                    Tracks =
                    {
                        new BandcampTrackEvidence
                        {
                            AbsoluteTrackNumber = 1,
                            MediumNumber = 1,
                            Title = "mystery song",
                            RecordingMusicBrainzId = "recording-failed-1",
                            ReleaseTrackMusicBrainzId = "release-track-failed-1"
                        }
                    }
                }
            };

            var persisted = capture.Capture(evidence);
            var reloaded = store.Get(persisted.CaseId);

            Assert.NotNull(reloaded);
            Assert.Equal("failed", reloaded!.Phase);
            Assert.NotNull(reloaded.ImportFailureClassification);
            Assert.False(reloaded.ImportFailureClassification!.Actionable);
            Assert.Equal(HostImportFailureClassification.CurrentRuleVersion, reloaded.ImportFailureClassification.RuleVersion);
            Assert.Equal("non-actionable-import-failure", reloaded.ImportFailureClassification.MatchedRule);
            Assert.Equal("import failure did not match track-number or ordering mismatch rules.", reloaded.ImportFailureClassification.SuppressionReason);
            Assert.DoesNotContain("session-cookie-123", reloaded.LastError ?? string.Empty, StringComparison.Ordinal);
            Assert.DoesNotContain("provider-secret-456", reloaded.LastError ?? string.Empty, StringComparison.Ordinal);
            Assert.Contains("[REDACTED]", reloaded.LastError ?? string.Empty, StringComparison.Ordinal);
        }

        [Fact]
        public void capture_should_update_existing_bandcamp_download_id_and_refresh_import_failure_classification()
        {
            var store = CreateStore();
            var capture = CreateCapture(store);
            var actionableEvidence = LoadSoccerMommySample();
            actionableEvidence.QueuePhase = "failed";
            actionableEvidence.QueueStatus = "Failed";
            actionableEvidence.ErrorMessage = "track number mismatch while importing bloodstream; authorization=Bearer bandcamp-actionable-secret";

            var actionable = capture.Capture(actionableEvidence);
            var actionableReloaded = store.Get(actionable.CaseId);

            var suppressedEvidence = LoadSoccerMommySample();
            suppressedEvidence.QueuePhase = "failed";
            suppressedEvidence.QueueStatus = "Failed";
            suppressedEvidence.ErrorMessage = "cookies=session-cookie-999 permission denied while moving imported files";

            var suppressed = capture.Capture(suppressedEvidence);
            var reloaded = store.Get(actionable.CaseId);
            var snapshots = store.List();

            Assert.NotNull(actionableReloaded);
            Assert.NotNull(actionableReloaded!.ImportFailureClassification);
            Assert.True(actionableReloaded.ImportFailureClassification!.Actionable);
            Assert.Equal("track-number-mismatch", actionableReloaded.ImportFailureClassification.MatchedRule);
            Assert.Contains("[REDACTED]", actionableReloaded.LastError ?? string.Empty, StringComparison.Ordinal);
            Assert.DoesNotContain("bandcamp-actionable-secret", actionableReloaded.LastError ?? string.Empty, StringComparison.Ordinal);

            Assert.NotNull(reloaded);
            Assert.Equal(actionable.CaseId, suppressed.CaseId);
            Assert.Single(snapshots);
            Assert.NotNull(reloaded!.ImportFailureClassification);
            Assert.False(reloaded.ImportFailureClassification!.Actionable);
            Assert.Equal(HostImportFailureClassification.CurrentRuleVersion, reloaded.ImportFailureClassification.RuleVersion);
            Assert.Equal("non-actionable-import-failure", reloaded.ImportFailureClassification.MatchedRule);
            Assert.Equal("import failure did not match track-number or ordering mismatch rules.", reloaded.ImportFailureClassification.SuppressionReason);
            Assert.DoesNotContain("session-cookie-999", reloaded.LastError ?? string.Empty, StringComparison.Ordinal);
            Assert.Contains("[REDACTED]", reloaded.LastError ?? string.Empty, StringComparison.Ordinal);
        }

        [Fact]
        public void capture_should_clear_last_error_for_evaluated_phase_even_when_error_message_is_present()
        {
            var store = CreateStore();
            var capture = CreateCapture(store);
            var evidence = LoadSoccerMommySample();
            evidence.QueuePhase = "evaluated";
            evidence.ErrorMessage = "downloaded files diverged from preferred release track mapping";

            var persisted = capture.Capture(evidence);
            var reloaded = store.Get(persisted.CaseId);

            Assert.NotNull(reloaded);
            Assert.Equal("evaluated", reloaded!.Phase);
            Assert.Contains("queuePhase=evaluated", reloaded.DiagnosticSummary!, StringComparison.Ordinal);
            Assert.Null(reloaded.LastError);
        }

        [Fact]
        public void capture_should_reject_evidence_without_any_stable_identifiers()
        {
            var store = CreateStore();
            var capture = CreateCapture(store);
            var evidence = new BandcampMismatchEvidence
            {
                DownloadId = "bc-nameless-release",
                RetagContext = new BandcampRetagEvidence
                {
                    Tracks =
                    {
                        new BandcampTrackEvidence
                        {
                            AbsoluteTrackNumber = 1,
                            MediumNumber = 1,
                            Title = "nameless"
                        }
                    }
                }
            };

            var exception = Assert.Throws<ArgumentException>(() => capture.Capture(evidence));

            Assert.Equal("request", exception.ParamName);
            Assert.Contains("stable captured identifier", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void get_should_fail_legacy_snapshots_with_actionable_structural_evidence_message()
        {
            var store = CreateStore();
            Directory.CreateDirectory(store.StorageDirectory);
            var legacyPath = Path.Combine(store.StorageDirectory, "rcase-legacy-structure.json");

            File.WriteAllText(legacyPath, """
            {
              "schemaVersion": 1,
              "caseId": "rcase-legacy-structure",
              "sourceSeam": "bandcamp-download-client",
              "phase": "captured",
              "capturedIdentifiers": {
                "artistName": "Soccer Mommy",
                "albumTitle": "color theory"
              },
              "capturedAtUtc": "2026-06-14T09:12:00Z",
              "lastUpdatedAtUtc": "2026-06-14T09:12:00Z"
            }
            """);

            var exception = Assert.Throws<InvalidDataException>(() => store.Get("rcase-legacy-structure"));

            Assert.Contains("missing structural evidence required for deterministic scoring", exception.Message, StringComparison.Ordinal);
            Assert.Contains("Re-capture the case with the current plugin build", exception.Message, StringComparison.Ordinal);
        }

        public void Dispose()
        {
            if (Directory.Exists(_rootDirectory))
            {
                Directory.Delete(_rootDirectory, recursive: true);
            }
        }

        private FileReconciliationCaseStore CreateStore()
        {
            return new FileReconciliationCaseStore(Path.Combine(_rootDirectory, "store"), _logger);
        }

        private BandcampReconciliationCaseCapture CreateCapture(IReconciliationCaseStore store)
        {
            return new BandcampReconciliationCaseCapture(store, _logger);
        }

        private static BandcampMismatchEvidence BuildDegradedEvidence()
        {
            return new BandcampMismatchEvidence
            {
                DownloadId = "bc-bandcamp-sampler-loose-singles",
                AlbumUrl = "https://example.bandcamp.com/album/loose-singles",
                Title = "Bandcamp Sampler - loose singles",
                OutputPath = "/music/Bandcamp Sampler/loose singles",
                QueuePhase = "extracting",
                QueueStatus = "Failed",
                ErrorMessage = "cookies=session-cookie-123 token=provider-secret-456 missing canonical release match",
                QueuedAtUtc = new DateTimeOffset(2026, 6, 14, 10, 0, 0, TimeSpan.Zero),
                CompletedAtUtc = new DateTimeOffset(2026, 6, 14, 10, 4, 0, TimeSpan.Zero),
                RetagContext = new BandcampRetagEvidence
                {
                    ArtistName = "Bandcamp Sampler",
                    AlbumTitle = "loose singles",
                    Genres = new[] { "indie" },
                    Tracks =
                    {
                        new BandcampTrackEvidence
                        {
                            AbsoluteTrackNumber = 1,
                            MediumNumber = 1,
                            Title = "mystery song"
                        }
                    }
                }
            };
        }

        private static BandcampMismatchEvidence BuildActionableBandcampEvidence()
        {
            var evidence = LoadSoccerMommySample();
            evidence.QueuePhase = "failed";
            evidence.QueueStatus = "Failed";
            evidence.ErrorMessage = "track number mismatch while importing bloodstream";
            return evidence;
        }

        private static BandcampMismatchEvidence LoadSoccerMommySample()
        {
            var path = ResolveTrackedFilePath("tests", "Lidarr.Plugin.ReleaseReconciler.Test", "SampleData", "soccer-mommy-mismatch.json");
            var json = File.ReadAllText(path);
            var evidence = JsonSerializer.Deserialize<BandcampMismatchEvidence>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            Assert.NotNull(evidence);
            return evidence!;
        }

        private static string ResolveTrackedFilePath(params string[] relativeSegments)
        {
            var current = new DirectoryInfo(AppContext.BaseDirectory);
            var relativePath = Path.Combine(relativeSegments);

            while (current != null)
            {
                var candidate = Path.Combine(current.FullName, relativePath);
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                current = current.Parent;
            }

            throw new FileNotFoundException($"Unable to locate tracked fixture '{relativePath}'.", relativePath);
        }
    }
}
