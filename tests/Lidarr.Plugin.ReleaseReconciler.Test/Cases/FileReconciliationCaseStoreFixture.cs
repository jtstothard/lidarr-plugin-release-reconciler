extern alias release_reconciler_nlog;

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Logger = release_reconciler_nlog::NLog.Logger;
using LogManager = release_reconciler_nlog::NLog.LogManager;
using NzbDrone.Core.Plugins.ReleaseReconciler.Candidates;
using NzbDrone.Core.Plugins.ReleaseReconciler.Capture;
using NzbDrone.Core.Plugins.ReleaseReconciler.Cases;
using Xunit;

namespace NzbDrone.Core.Plugins.ReleaseReconciler.Test.Cases
{
    public sealed class FileReconciliationCaseStoreFixture : IDisposable
    {
        private static readonly JsonSerializerOptions SnapshotSerializerOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = true
        };

        private readonly string _rootDirectory;
        private readonly Logger _logger;

        public FileReconciliationCaseStoreFixture()
        {
            _rootDirectory = Path.Combine(Path.GetTempPath(), $"release-reconciler-tests-{Guid.NewGuid():N}");
            _logger = LogManager.GetLogger(nameof(FileReconciliationCaseStoreFixture));
        }

        [Fact]
        public void save_should_round_trip_case_across_store_instances()
        {
            var store = CreateStore();
            var capturedAtUtc = new DateTimeOffset(2026, 6, 14, 12, 0, 0, TimeSpan.Zero);
            var reconciliationCase = new ReconciliationCase(
                sourceSeam: "download-client",
                phase: "captured",
                capturedIdentifiers: BuildIdentifiers(),
                structuralEvidence: BuildStructuralEvidence(),
                downloadId: "download-123",
                outputPath: "/music/artist/album",
                diagnosticSummary: "status=missed format=mp3",
                lastError: "Tag mismatch",
                importFailureClassification: new HostImportFailureClassification
                {
                    Actionable = false,
                    RuleVersion = HostImportFailureClassification.CurrentRuleVersion,
                    MatchedRule = "non-actionable-import-failure",
                    SuppressionReason = "import failure did not match track-number or ordering mismatch rules."
                },
                harmonySourceSupport: new HostHarmonySourceSupport
                {
                    Supported = false,
                    RuleVersion = HostHarmonySourceSupport.CurrentRuleVersion,
                    MatchedRule = "provider-unsupported",
                    MatchedProvider = "bandcamp",
                    SuppressionReason = "Provider 'bandcamp' is not supported for Harmony source extraction."
                },
                capturedAtUtc: capturedAtUtc);

            var persisted = store.Save(reconciliationCase);
            var reloadedStore = CreateStore();
            var reloaded = reloadedStore.Get(persisted.CaseId);

            Assert.NotNull(reloaded);
            Assert.Equal(persisted.CaseId, reloaded!.CaseId);
            Assert.Equal("download-client", reloaded.SourceSeam);
            Assert.Equal("captured", reloaded.Phase);
            Assert.Equal(capturedAtUtc, reloaded.CapturedAtUtc);
            Assert.Equal("download-123", reloaded.DownloadId);
            Assert.Equal("/music/artist/album", reloaded.OutputPath);
            Assert.Equal("status=missed format=mp3", reloaded.DiagnosticSummary);
            Assert.Equal("Tag mismatch", reloaded.LastError);
            Assert.NotNull(reloaded.ImportFailureClassification);
            Assert.False(reloaded.ImportFailureClassification!.Actionable);
            Assert.Equal(HostImportFailureClassification.CurrentRuleVersion, reloaded.ImportFailureClassification.RuleVersion);
            Assert.Equal("non-actionable-import-failure", reloaded.ImportFailureClassification.MatchedRule);
            Assert.Equal("import failure did not match track-number or ordering mismatch rules.", reloaded.ImportFailureClassification.SuppressionReason);
            Assert.NotNull(reloaded.HarmonySourceSupport);
            Assert.False(reloaded.HarmonySourceSupport!.Supported);
            Assert.Equal(HostHarmonySourceSupport.CurrentRuleVersion, reloaded.HarmonySourceSupport.RuleVersion);
            Assert.Equal("provider-unsupported", reloaded.HarmonySourceSupport.MatchedRule);
            Assert.Equal("bandcamp", reloaded.HarmonySourceSupport.MatchedProvider);
            Assert.Equal("Provider 'bandcamp' is not supported for Harmony source extraction.", reloaded.HarmonySourceSupport.SuppressionReason);
            Assert.Equal("Artist Name", reloaded.CapturedIdentifiers["artist"]);
            Assert.Equal("Album Title", reloaded.CapturedIdentifiers["album"]);
            Assert.NotNull(reloaded.StructuralEvidence);
            Assert.Equal(1, reloaded.StructuralEvidence!.TrackCount);
            Assert.Equal(new[] { "recording-1" }, reloaded.StructuralEvidence.RecordingMusicBrainzIds);
        }

        [Fact]
        public void save_should_round_trip_notification_and_operator_action_state()
        {
            var store = CreateStore();
            var capturedAtUtc = new DateTimeOffset(2026, 6, 14, 12, 0, 0, TimeSpan.Zero);
            var reconciliationCase = new ReconciliationCase(
                sourceSeam: "download-client",
                phase: "evaluated",
                capturedIdentifiers: BuildIdentifiers(),
                structuralEvidence: BuildStructuralEvidence(),
                capturedAtUtc: capturedAtUtc,
                notificationState: new ReconciliationNotificationState
                {
                    DispatchAttempts =
                    {
                        new ReconciliationNotificationDispatchAttempt
                        {
                            AttemptedAtUtc = new DateTimeOffset(2026, 6, 14, 12, 5, 0, TimeSpan.Zero),
                            Transport = "discord",
                            Succeeded = false,
                            ActionUrlKind = "signed-relative",
                            ActionTokenExpiresAtUtc = new DateTimeOffset(2026, 6, 14, 13, 5, 0, TimeSpan.Zero),
                            FailureKind = "http-429",
                            FailureSummary = "Authorization: Bearer dispatch-secret"
                        },
                        new ReconciliationNotificationDispatchAttempt
                        {
                            AttemptedAtUtc = new DateTimeOffset(2026, 6, 14, 12, 10, 0, TimeSpan.Zero),
                            Transport = "telegram",
                            Succeeded = true,
                            ActionUrlKind = "signed-relative",
                            ActionTokenExpiresAtUtc = new DateTimeOffset(2026, 6, 14, 13, 10, 0, TimeSpan.Zero)
                        }
                    }
                },
                operatorActionState: new ReconciliationOperatorActionState
                {
                    SnoozedUntilUtc = new DateTimeOffset(2026, 6, 15, 12, 0, 0, TimeSpan.Zero),
                    LastAction = new ReconciliationOperatorActionAudit
                    {
                        Action = "snooze",
                        Result = "applied",
                        OccurredAtUtc = new DateTimeOffset(2026, 6, 14, 12, 20, 0, TimeSpan.Zero),
                        Transport = "telegram",
                        ActionedBy = "operator:qa",
                        ActionTokenHash = "sha256:token-1"
                    },
                    ProcessedActionReceipts =
                    {
                        new ReconciliationOperatorActionReceipt
                        {
                            ActionTokenHash = "sha256:token-1",
                            Action = "snooze",
                            Outcome = "applied",
                            ProcessedAtUtc = new DateTimeOffset(2026, 6, 14, 12, 20, 0, TimeSpan.Zero),
                            ExpiresAtUtc = new DateTimeOffset(2026, 6, 14, 13, 20, 0, TimeSpan.Zero),
                            Transport = "telegram"
                        },
                        new ReconciliationOperatorActionReceipt
                        {
                            ActionTokenHash = "sha256:token-1",
                            Action = "snooze",
                            Outcome = "replayed",
                            ProcessedAtUtc = new DateTimeOffset(2026, 6, 14, 12, 21, 0, TimeSpan.Zero),
                            ExpiresAtUtc = new DateTimeOffset(2026, 6, 14, 13, 20, 0, TimeSpan.Zero),
                            Transport = "telegram",
                            FailureSummary = "token=should-not-persist"
                        }
                    }
                });

            var persisted = store.Save(reconciliationCase);
            var reloaded = store.Get(persisted.CaseId);

            Assert.NotNull(reloaded);
            Assert.NotNull(reloaded!.NotificationState);
            Assert.Equal(2, reloaded.NotificationState!.DispatchAttempts.Count);
            Assert.Equal("discord", reloaded.NotificationState.DispatchAttempts[0].Transport);
            Assert.False(reloaded.NotificationState.DispatchAttempts[0].Succeeded);
            Assert.Contains("[REDACTED]", reloaded.NotificationState.DispatchAttempts[0].FailureSummary!, StringComparison.Ordinal);
            Assert.Equal("telegram", reloaded.NotificationState.DispatchAttempts[1].Transport);
            Assert.True(reloaded.NotificationState.DispatchAttempts[1].Succeeded);

            Assert.NotNull(reloaded.OperatorActionState);
            Assert.Equal(new DateTimeOffset(2026, 6, 15, 12, 0, 0, TimeSpan.Zero), reloaded.OperatorActionState!.SnoozedUntilUtc);
            Assert.NotNull(reloaded.OperatorActionState.LastAction);
            Assert.Equal("snooze", reloaded.OperatorActionState.LastAction!.Action);
            Assert.Equal("applied", reloaded.OperatorActionState.LastAction.Result);
            Assert.Equal("sha256:token-1", reloaded.OperatorActionState.LastAction.ActionTokenHash);
            Assert.Equal(2, reloaded.OperatorActionState.ProcessedActionReceipts.Count);
            Assert.Equal("replayed", reloaded.OperatorActionState.ProcessedActionReceipts[1].Outcome);
            Assert.Contains("[REDACTED]", reloaded.OperatorActionState.ProcessedActionReceipts[1].FailureSummary!, StringComparison.Ordinal);
        }

        [Fact]
        public void save_should_round_trip_release_switch_approval_state()
        {
            var store = CreateStore();
            var reconciliationCase = new ReconciliationCase(
                sourceSeam: "download-client",
                phase: "evaluated",
                capturedIdentifiers: BuildIdentifiers(),
                structuralEvidence: BuildStructuralEvidence(),
                releaseSwitchApprovalState: new ReconciliationReleaseSwitchApprovalState
                {
                    AttemptedAtUtc = new DateTimeOffset(2026, 6, 14, 12, 30, 0, TimeSpan.Zero),
                    AppliedAtUtc = new DateTimeOffset(2026, 6, 14, 12, 31, 0, TimeSpan.Zero),
                    Outcome = "applied",
                    BeforeAlbumMusicBrainzId = "album-current",
                    BeforeReleaseMusicBrainzId = "release-preferred",
                    RequestedAlbumMusicBrainzId = "album-current",
                    RequestedReleaseMusicBrainzId = "release-candidate",
                    AfterAlbumMusicBrainzId = "album-current",
                    AfterReleaseMusicBrainzId = "release-candidate",
                    Classification = "BetterExistingRelease",
                    ScoringVersion = ReconciliationEvaluationResult.CurrentScoringVersion,
                    CandidateTotalScore = 42,
                    CandidateStrongSignalScore = 24,
                    CandidateSameReleaseGroup = true,
                    CandidateHasStrongIdentitySignal = true,
                    CandidateTitleOnly = false,
                    Transport = "discord",
                    ActionTokenHash = "sha256:switch-token",
                    RefusalSummary = "Authorization: Bearer should-redact"
                });

            var persisted = store.Save(reconciliationCase);
            var reloaded = store.Get(persisted.CaseId);

            Assert.NotNull(reloaded);
            Assert.NotNull(reloaded!.ReleaseSwitchApprovalState);
            Assert.Equal("applied", reloaded.ReleaseSwitchApprovalState!.Outcome);
            Assert.Equal("album-current", reloaded.ReleaseSwitchApprovalState.BeforeAlbumMusicBrainzId);
            Assert.Equal("release-preferred", reloaded.ReleaseSwitchApprovalState.BeforeReleaseMusicBrainzId);
            Assert.Equal("release-candidate", reloaded.ReleaseSwitchApprovalState.AfterReleaseMusicBrainzId);
            Assert.Equal(42, reloaded.ReleaseSwitchApprovalState.CandidateTotalScore);
            Assert.True(reloaded.ReleaseSwitchApprovalState.CandidateSameReleaseGroup);
            Assert.Contains("[REDACTED]", reloaded.ReleaseSwitchApprovalState.RefusalSummary!, StringComparison.Ordinal);
        }

        [Fact]
        public void save_should_overwrite_existing_case_file_and_preserve_original_capture_time()
        {
            var store = CreateStore();
            var capturedAtUtc = new DateTimeOffset(2026, 6, 14, 12, 0, 0, TimeSpan.Zero);
            var initial = new ReconciliationCase(
                sourceSeam: "download-client",
                phase: "captured",
                capturedIdentifiers: BuildIdentifiers(),
                structuralEvidence: BuildStructuralEvidence(),
                diagnosticSummary: "status=queued",
                capturedAtUtc: capturedAtUtc);

            var persistedInitial = store.Save(initial);

            var updated = new ReconciliationCase(
                sourceSeam: "download-client",
                phase: "failed",
                capturedIdentifiers: BuildIdentifiers(),
                structuralEvidence: BuildStructuralEvidence(),
                diagnosticSummary: "status=failed",
                lastError: "Import mismatch");

            var persistedUpdated = store.Save(updated);
            var reloaded = store.Get(persistedUpdated.CaseId);

            Assert.Equal(persistedInitial.CaseId, persistedUpdated.CaseId);
            Assert.NotNull(reloaded);
            Assert.Equal(capturedAtUtc, reloaded!.CapturedAtUtc);
            Assert.Equal("failed", reloaded.Phase);
            Assert.Equal("status=failed", reloaded.DiagnosticSummary);
            Assert.Equal("Import mismatch", reloaded.LastError);
            Assert.True(reloaded.LastUpdatedAtUtc >= capturedAtUtc);
        }

        [Fact]
        public void find_by_download_id_should_return_existing_snapshot_for_matching_source_seam()
        {
            var store = CreateStore();
            var reconciliationCase = new ReconciliationCase(
                sourceSeam: "download-client",
                phase: "captured",
                capturedIdentifiers: BuildIdentifiers(),
                structuralEvidence: BuildStructuralEvidence(),
                downloadId: "download-123",
                outputPath: "/music/artist/album");

            var persisted = store.Save(reconciliationCase);

            var found = store.FindByDownloadId("download-client", "download-123");

            Assert.NotNull(found);
            Assert.Equal(persisted.CaseId, found!.CaseId);
            Assert.Equal("captured", found.Phase);
        }

        [Fact]
        public void save_should_update_existing_case_by_download_id_and_preserve_original_identity()
        {
            var store = CreateStore();
            var capturedAtUtc = new DateTimeOffset(2026, 6, 14, 12, 0, 0, TimeSpan.Zero);
            var initial = new ReconciliationCase(
                sourceSeam: "download-client",
                phase: "captured",
                capturedIdentifiers: BuildIdentifiers(),
                structuralEvidence: BuildStructuralEvidence(),
                downloadId: "download-123",
                outputPath: "/music/artist/album",
                diagnosticSummary: "status=queued",
                capturedAtUtc: capturedAtUtc);

            var persistedInitial = store.Save(initial);
            var laterPhase = new ReconciliationCase(
                sourceSeam: "download-client",
                phase: "failed",
                capturedIdentifiers: new Dictionary<string, string?>
                {
                    ["artist"] = "Artist Name",
                    ["albumTitle"] = "Album Title",
                    ["failureReason"] = "Import mismatch"
                },
                structuralEvidence: BuildStructuralEvidence(),
                downloadId: "download-123",
                outputPath: "/music/artist/album-failed",
                diagnosticSummary: "status=failed",
                lastError: "Authorization: Bearer host-secret",
                harmonySourceSupport: new HostHarmonySourceSupport
                {
                    Supported = true,
                    RuleVersion = HostHarmonySourceSupport.CurrentRuleVersion,
                    MatchedRule = "harmony-provider-url",
                    MatchedProvider = "harmony",
                    ProviderUrl = "https://harmony.example/releases/artist-album"
                });

            Assert.NotEqual(persistedInitial.CaseId, laterPhase.CaseId);

            var persistedUpdated = store.Save(laterPhase);
            var reloaded = store.Get(persistedInitial.CaseId);

            Assert.Equal(persistedInitial.CaseId, persistedUpdated.CaseId);
            Assert.NotEqual(laterPhase.CaseId, persistedUpdated.CaseId);
            Assert.NotNull(reloaded);
            Assert.Equal(capturedAtUtc, reloaded!.CapturedAtUtc);
            Assert.Equal("failed", reloaded.Phase);
            Assert.Equal("download-123", reloaded.DownloadId);
            Assert.Equal("/music/artist/album-failed", reloaded.OutputPath);
            Assert.Equal("status=failed", reloaded.DiagnosticSummary);
            Assert.Contains("[REDACTED]", reloaded.LastError!, StringComparison.Ordinal);
            Assert.NotNull(reloaded.HarmonySourceSupport);
            Assert.True(reloaded.HarmonySourceSupport!.Supported);
            Assert.Equal("harmony-provider-url", reloaded.HarmonySourceSupport.MatchedRule);
            Assert.Equal("https://harmony.example/releases/artist-album", reloaded.HarmonySourceSupport.ProviderUrl);
            Assert.True(reloaded.LastUpdatedAtUtc >= reloaded.CapturedAtUtc);
            Assert.Single(Directory.EnumerateFiles(store.StorageDirectory, "*.json", SearchOption.TopDirectoryOnly));
        }

        [Fact]
        public void save_should_round_trip_actionable_import_failure_classification_when_download_id_update_reuses_case_id()
        {
            var store = CreateStore();
            var capturedAtUtc = new DateTimeOffset(2026, 6, 14, 12, 0, 0, TimeSpan.Zero);
            var initial = new ReconciliationCase(
                sourceSeam: "download-client",
                phase: "captured",
                capturedIdentifiers: BuildIdentifiers(),
                structuralEvidence: BuildStructuralEvidence(),
                downloadId: "download-456",
                outputPath: "/music/artist/album",
                diagnosticSummary: "status=queued",
                capturedAtUtc: capturedAtUtc);

            var persistedInitial = store.Save(initial);
            var updated = new ReconciliationCase(
                sourceSeam: "download-client",
                phase: "failed",
                capturedIdentifiers: new Dictionary<string, string?>
                {
                    ["artist"] = "Artist Name",
                    ["album"] = "Album Title",
                    ["preferredReleaseMusicBrainzId"] = "release-preferred",
                    ["albumMusicBrainzId"] = "album-current"
                },
                structuralEvidence: BuildStructuralEvidence(),
                downloadId: "download-456",
                outputPath: "/music/artist/album-failed",
                diagnosticSummary: "status=failed",
                lastError: "track number mismatch while importing Track One",
                importFailureClassification: new HostImportFailureClassification
                {
                    Actionable = true,
                    RuleVersion = HostImportFailureClassification.CurrentRuleVersion,
                    MatchedRule = "track-number-mismatch"
                },
                harmonySourceSupport: new HostHarmonySourceSupport
                {
                    Supported = true,
                    RuleVersion = HostHarmonySourceSupport.CurrentRuleVersion,
                    MatchedRule = "harmony-provider-url",
                    MatchedProvider = "harmony",
                    ProviderUrl = "https://harmony.example/releases/album-456"
                });

            var persistedUpdated = store.Save(updated);
            var reloaded = store.Get(persistedInitial.CaseId);
            var storedJson = File.ReadAllText(Path.Combine(store.StorageDirectory, $"{persistedInitial.CaseId}.json"));

            Assert.Equal(persistedInitial.CaseId, persistedUpdated.CaseId);
            Assert.NotNull(reloaded);
            Assert.Equal(capturedAtUtc, reloaded!.CapturedAtUtc);
            Assert.Equal("failed", reloaded.Phase);
            Assert.Equal("download-456", reloaded.DownloadId);
            Assert.Equal("/music/artist/album-failed", reloaded.OutputPath);
            Assert.Equal("status=failed", reloaded.DiagnosticSummary);
            Assert.NotNull(reloaded.ImportFailureClassification);
            Assert.True(reloaded.ImportFailureClassification!.Actionable);
            Assert.Equal(HostImportFailureClassification.CurrentRuleVersion, reloaded.ImportFailureClassification.RuleVersion);
            Assert.Equal("track-number-mismatch", reloaded.ImportFailureClassification.MatchedRule);
            Assert.Null(reloaded.ImportFailureClassification.SuppressionReason);
            Assert.NotNull(reloaded.HarmonySourceSupport);
            Assert.True(reloaded.HarmonySourceSupport!.Supported);
            Assert.Equal(HostHarmonySourceSupport.CurrentRuleVersion, reloaded.HarmonySourceSupport.RuleVersion);
            Assert.Equal("harmony-provider-url", reloaded.HarmonySourceSupport.MatchedRule);
            Assert.Equal("harmony", reloaded.HarmonySourceSupport.MatchedProvider);
            Assert.Equal("https://harmony.example/releases/album-456", reloaded.HarmonySourceSupport.ProviderUrl);
            Assert.Equal("release-preferred", reloaded.CapturedIdentifiers["preferredReleaseMusicBrainzId"]);
            Assert.Equal("album-current", reloaded.CapturedIdentifiers["albumMusicBrainzId"]);
            Assert.Contains("\"matchedRule\": \"track-number-mismatch\"", storedJson, StringComparison.Ordinal);
            Assert.Contains("\"matchedRule\": \"harmony-provider-url\"", storedJson, StringComparison.Ordinal);
            Assert.Contains("\"preferredReleaseMusicBrainzId\": \"release-preferred\"", storedJson, StringComparison.Ordinal);
            Assert.Contains("\"albumMusicBrainzId\": \"album-current\"", storedJson, StringComparison.Ordinal);
            Assert.DoesNotContain("suppressionReason", storedJson, StringComparison.Ordinal);
            Assert.Single(Directory.EnumerateFiles(store.StorageDirectory, "*.json", SearchOption.TopDirectoryOnly));
        }

        [Fact]
        public void save_should_not_match_existing_case_when_download_id_is_blank()
        {
            var store = CreateStore();
            var initial = new ReconciliationCase(
                sourceSeam: "download-client",
                phase: "captured",
                capturedIdentifiers: BuildIdentifiers(),
                structuralEvidence: BuildStructuralEvidence(),
                downloadId: "download-123",
                outputPath: "/music/artist/album");

            var persistedInitial = store.Save(initial);
            var blankDownloadIdUpdate = new ReconciliationCase(
                sourceSeam: "download-client",
                phase: "failed",
                capturedIdentifiers: new Dictionary<string, string?>
                {
                    ["artist"] = "Artist Name",
                    ["albumTitle"] = "Album Title",
                    ["failureReason"] = "Import mismatch"
                },
                structuralEvidence: BuildStructuralEvidence(),
                downloadId: "   ",
                outputPath: "/music/artist/album-failed");

            var persistedBlank = store.Save(blankDownloadIdUpdate);

            Assert.NotEqual(persistedInitial.CaseId, persistedBlank.CaseId);
            Assert.Null(store.FindByDownloadId("download-client", "   "));
            Assert.Equal(2, Directory.GetFiles(store.StorageDirectory, "*.json", SearchOption.TopDirectoryOnly).Length);
        }

        [Fact]
        public void find_by_download_id_should_skip_malformed_snapshot_files()
        {
            var store = CreateStore();
            var persisted = store.Save(new ReconciliationCase(
                sourceSeam: "download-client",
                phase: "captured",
                capturedIdentifiers: BuildIdentifiers(),
                structuralEvidence: BuildStructuralEvidence(),
                downloadId: "download-123",
                outputPath: "/music/artist/album"));

            Directory.CreateDirectory(store.StorageDirectory);
            File.WriteAllText(Path.Combine(store.StorageDirectory, "broken.json"), "{ definitely-not-json }");

            var found = store.FindByDownloadId("download-client", "download-123");

            Assert.NotNull(found);
            Assert.Equal(persisted.CaseId, found!.CaseId);
        }

        [Fact]
        public void save_should_fail_loudly_on_duplicate_download_id_without_corrupting_existing_case()
        {
            var store = CreateStore();
            var initial = new ReconciliationCase(
                sourceSeam: "download-client",
                phase: "captured",
                capturedIdentifiers: BuildIdentifiers(),
                structuralEvidence: BuildStructuralEvidence(),
                downloadId: "download-123",
                outputPath: "/music/artist/album",
                diagnosticSummary: "status=queued");

            var persistedInitial = store.Save(initial);
            var duplicateCaseId = "rcase-duplicate-download123";
            Directory.CreateDirectory(store.StorageDirectory);
            File.WriteAllText(
                Path.Combine(store.StorageDirectory, $"{duplicateCaseId}.json"),
                JsonSerializer.Serialize(new
                {
                    schemaVersion = ReconciliationCaseSnapshot.CurrentSchemaVersion,
                    caseId = duplicateCaseId,
                    sourceSeam = "download-client",
                    phase = "failed",
                    capturedIdentifiers = new Dictionary<string, string?>
                    {
                        ["artist"] = "Artist Name",
                        ["albumTitle"] = "Album Title"
                    },
                    structuralEvidence = BuildStructuralEvidence(),
                    downloadId = "download-123",
                    outputPath = "/music/artist/album-failed",
                    diagnosticSummary = "status=duplicate-shadow",
                    capturedAtUtc = new DateTimeOffset(2026, 6, 14, 12, 10, 0, TimeSpan.Zero),
                    lastUpdatedAtUtc = new DateTimeOffset(2026, 6, 14, 12, 11, 0, TimeSpan.Zero)
                }, SnapshotSerializerOptions));

            var exception = Assert.Throws<InvalidOperationException>(() => store.Save(new ReconciliationCase(
                sourceSeam: "download-client",
                phase: "imported",
                capturedIdentifiers: new Dictionary<string, string?>
                {
                    ["artist"] = "Artist Name",
                    ["albumTitle"] = "Album Title",
                    ["failureReason"] = "Import mismatch"
                },
                structuralEvidence: BuildStructuralEvidence(),
                downloadId: "download-123",
                outputPath: "/music/artist/album-imported",
                diagnosticSummary: "status=imported")));

            Assert.Contains("Multiple reconciliation cases matched source seam 'download-client' and downloadId 'download-123'", exception.Message, StringComparison.Ordinal);
            Assert.Equal("status=queued", store.Get(persistedInitial.CaseId)!.DiagnosticSummary);
            Assert.Equal(2, Directory.GetFiles(store.StorageDirectory, "*.json", SearchOption.TopDirectoryOnly).Length);
        }

        [Fact]
        public void get_should_load_legacy_snapshot_without_notification_or_operator_action_state()
        {
            var store = CreateStore();
            var caseId = "rcase-legacy-snapshot";
            Directory.CreateDirectory(store.StorageDirectory);
            File.WriteAllText(
                Path.Combine(store.StorageDirectory, $"{caseId}.json"),
                JsonSerializer.Serialize(new
                {
                    schemaVersion = ReconciliationCaseSnapshot.CurrentSchemaVersion,
                    caseId,
                    sourceSeam = "download-client",
                    phase = "evaluated",
                    capturedIdentifiers = BuildIdentifiers(),
                    structuralEvidence = BuildStructuralEvidence(),
                    diagnosticSummary = "status=legacy",
                    capturedAtUtc = new DateTimeOffset(2026, 6, 14, 12, 0, 0, TimeSpan.Zero),
                    lastUpdatedAtUtc = new DateTimeOffset(2026, 6, 14, 12, 5, 0, TimeSpan.Zero)
                }, SnapshotSerializerOptions));

            var reloaded = store.Get(caseId);

            Assert.NotNull(reloaded);
            Assert.Equal(caseId, reloaded!.CaseId);
            Assert.Null(reloaded.ImportFailureClassification);
            Assert.Null(reloaded.HarmonySourceSupport);
            Assert.Null(reloaded.NotificationState);
            Assert.Null(reloaded.OperatorActionState);
            Assert.Null(reloaded.ReleaseSwitchApprovalState);
            Assert.Equal("status=legacy", reloaded.DiagnosticSummary);
        }

        [Fact]
        public void get_should_reject_malformed_import_failure_classification_state()
        {
            var store = CreateStore();
            var caseId = "rcase-malformed-import-failure-classification";
            Directory.CreateDirectory(store.StorageDirectory);
            File.WriteAllText(
                Path.Combine(store.StorageDirectory, $"{caseId}.json"),
                JsonSerializer.Serialize(new
                {
                    schemaVersion = ReconciliationCaseSnapshot.CurrentSchemaVersion,
                    caseId,
                    sourceSeam = "download-client",
                    phase = "failed",
                    capturedIdentifiers = BuildIdentifiers(),
                    structuralEvidence = BuildStructuralEvidence(),
                    capturedAtUtc = new DateTimeOffset(2026, 6, 14, 12, 0, 0, TimeSpan.Zero),
                    lastUpdatedAtUtc = new DateTimeOffset(2026, 6, 14, 12, 5, 0, TimeSpan.Zero),
                    importFailureClassification = new
                    {
                        actionable = false,
                        ruleVersion = HostImportFailureClassification.CurrentRuleVersion,
                        matchedRule = "non-actionable-import-failure"
                    }
                }, SnapshotSerializerOptions));

            var exception = Assert.Throws<InvalidDataException>(() => store.Get(caseId));

            Assert.Contains("must persist a suppression reason", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void get_should_reject_malformed_harmony_source_support_state()
        {
            var store = CreateStore();
            var caseId = "rcase-malformed-harmony-source-support";
            Directory.CreateDirectory(store.StorageDirectory);
            File.WriteAllText(
                Path.Combine(store.StorageDirectory, $"{caseId}.json"),
                JsonSerializer.Serialize(new
                {
                    schemaVersion = ReconciliationCaseSnapshot.CurrentSchemaVersion,
                    caseId,
                    sourceSeam = "download-client",
                    phase = "failed",
                    capturedIdentifiers = BuildIdentifiers(),
                    structuralEvidence = BuildStructuralEvidence(),
                    capturedAtUtc = new DateTimeOffset(2026, 6, 14, 12, 0, 0, TimeSpan.Zero),
                    lastUpdatedAtUtc = new DateTimeOffset(2026, 6, 14, 12, 5, 0, TimeSpan.Zero),
                    harmonySourceSupport = new
                    {
                        supported = true,
                        ruleVersion = HostHarmonySourceSupport.CurrentRuleVersion,
                        matchedRule = "harmony-provider-url",
                        matchedProvider = "harmony"
                    }
                }, SnapshotSerializerOptions));

            var exception = Assert.Throws<InvalidDataException>(() => store.Get(caseId));

            Assert.Contains("must persist a provider URL or equivalent lookup fields", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void get_should_reject_malformed_operator_action_state()
        {
            var store = CreateStore();
            var caseId = "rcase-malformed-action-state";
            Directory.CreateDirectory(store.StorageDirectory);
            File.WriteAllText(
                Path.Combine(store.StorageDirectory, $"{caseId}.json"),
                JsonSerializer.Serialize(new
                {
                    schemaVersion = ReconciliationCaseSnapshot.CurrentSchemaVersion,
                    caseId,
                    sourceSeam = "download-client",
                    phase = "notified",
                    capturedIdentifiers = BuildIdentifiers(),
                    structuralEvidence = BuildStructuralEvidence(),
                    capturedAtUtc = new DateTimeOffset(2026, 6, 14, 12, 0, 0, TimeSpan.Zero),
                    lastUpdatedAtUtc = new DateTimeOffset(2026, 6, 14, 12, 5, 0, TimeSpan.Zero),
                    operatorActionState = new
                    {
                        snoozedUntilUtc = new DateTimeOffset(2026, 6, 15, 12, 0, 0, TimeSpan.Zero),
                        ignoredAtUtc = new DateTimeOffset(2026, 6, 14, 12, 10, 0, TimeSpan.Zero)
                    }
                }, SnapshotSerializerOptions));

            var exception = Assert.Throws<InvalidDataException>(() => store.Get(caseId));

            Assert.Contains("cannot be both snoozed and ignored", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void list_should_skip_malformed_snapshot_files()
        {
            var store = CreateStore();
            var validCase = new ReconciliationCase(
                sourceSeam: "download-client",
                phase: "captured",
                capturedIdentifiers: BuildIdentifiers(),
                structuralEvidence: BuildStructuralEvidence(),
                diagnosticSummary: "ok");

            var persisted = store.Save(validCase);
            Directory.CreateDirectory(store.StorageDirectory);
            File.WriteAllText(Path.Combine(store.StorageDirectory, "broken.json"), "{ definitely-not-json }");

            var snapshots = store.List();

            Assert.Single(snapshots);
            Assert.Equal(persisted.CaseId, snapshots[0].CaseId);
        }

        [Fact]
        public void save_should_reject_invalid_case_identifiers()
        {
            var store = CreateStore();

            var exception = Assert.Throws<ArgumentException>(() => store.Save(new ReconciliationCase(
                sourceSeam: "download-client",
                phase: "captured",
                capturedIdentifiers: new Dictionary<string, string?>(),
                structuralEvidence: BuildStructuralEvidence())));

            Assert.Contains("capturedIdentifiers", exception.ParamName);
        }

        [Fact]
        public void save_should_redact_sensitive_diagnostics_errors_and_operator_metadata_in_persisted_json()
        {
            var store = CreateStore();
            var reconciliationCase = new ReconciliationCase(
                sourceSeam: "download-client",
                phase: "captured",
                capturedIdentifiers: BuildIdentifiers(),
                structuralEvidence: BuildStructuralEvidence(),
                diagnosticSummary: "Authorization: Bearer secret-token Cookie=session=abc123 api-key: xyz987",
                lastError: "cookies=session=abc123 token=xyz987",
                notificationState: new ReconciliationNotificationState
                {
                    DispatchAttempts =
                    {
                        new ReconciliationNotificationDispatchAttempt
                        {
                            AttemptedAtUtc = new DateTimeOffset(2026, 6, 14, 12, 5, 0, TimeSpan.Zero),
                            Transport = "discord",
                            Succeeded = false,
                            ActionUrlKind = "signed-relative",
                            FailureKind = "http-401",
                            FailureSummary = "Authorization: Bearer dispatch-secret signature=abc987"
                        }
                    }
                },
                operatorActionState: new ReconciliationOperatorActionState
                {
                    LastAction = new ReconciliationOperatorActionAudit
                    {
                        Action = "ignore",
                        Result = "rejected",
                        OccurredAtUtc = new DateTimeOffset(2026, 6, 14, 12, 6, 0, TimeSpan.Zero),
                        Transport = "telegram",
                        FailureSummary = "token=xyz987"
                    },
                    ProcessedActionReceipts =
                    {
                        new ReconciliationOperatorActionReceipt
                        {
                            ActionTokenHash = "sha256:token-2",
                            Action = "ignore",
                            Outcome = "expired",
                            ProcessedAtUtc = new DateTimeOffset(2026, 6, 14, 12, 7, 0, TimeSpan.Zero),
                            Transport = "telegram",
                            FailureSummary = "Cookie=session=abc123 sig=abc987"
                        }
                    }
                });

            var persisted = store.Save(reconciliationCase);
            var storedJson = File.ReadAllText(Path.Combine(store.StorageDirectory, $"{persisted.CaseId}.json"));

            Assert.DoesNotContain("secret-token", storedJson, StringComparison.Ordinal);
            Assert.DoesNotContain("abc123", storedJson, StringComparison.Ordinal);
            Assert.DoesNotContain("xyz987", storedJson, StringComparison.Ordinal);
            Assert.DoesNotContain("dispatch-secret", storedJson, StringComparison.Ordinal);
            Assert.DoesNotContain("abc987", storedJson, StringComparison.Ordinal);
            Assert.Contains("[REDACTED]", storedJson, StringComparison.Ordinal);
        }

        [Fact]
        public void save_should_redact_sensitive_release_switch_audit_metadata_in_persisted_json()
        {
            var store = CreateStore();
            var reconciliationCase = new ReconciliationCase(
                sourceSeam: "download-client",
                phase: "evaluated",
                capturedIdentifiers: BuildIdentifiers(),
                structuralEvidence: BuildStructuralEvidence(),
                releaseSwitchApprovalState: new ReconciliationReleaseSwitchApprovalState
                {
                    AttemptedAtUtc = new DateTimeOffset(2026, 6, 14, 12, 40, 0, TimeSpan.Zero),
                    RefusedAtUtc = new DateTimeOffset(2026, 6, 14, 12, 41, 0, TimeSpan.Zero),
                    Outcome = "refused",
                    RefusalSummary = "Authorization: Bearer switch-secret Cookie=session=abc123"
                });

            var persisted = store.Save(reconciliationCase);
            var storedJson = File.ReadAllText(Path.Combine(store.StorageDirectory, $"{persisted.CaseId}.json"));

            Assert.DoesNotContain("switch-secret", storedJson, StringComparison.Ordinal);
            Assert.DoesNotContain("abc123", storedJson, StringComparison.Ordinal);
            Assert.Contains("[REDACTED]", storedJson, StringComparison.Ordinal);
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

        private static IReadOnlyDictionary<string, string?> BuildIdentifiers()
        {
            return new Dictionary<string, string?>
            {
                ["artist"] = "Artist Name",
                ["album"] = "Album Title"
            };
        }

        private static ReconciliationEvidence BuildStructuralEvidence()
        {
            return new ReconciliationEvidence
            {
                TrackCount = 1,
                Tracks =
                {
                    new ReconciliationTrackEvidence
                    {
                        AbsoluteTrackNumber = 1,
                        MediumNumber = 1,
                        Title = "Track One",
                        RecordingMusicBrainzId = "recording-1",
                        ReleaseTrackMusicBrainzId = "release-track-1"
                    }
                }
            };
        }
    }
}
