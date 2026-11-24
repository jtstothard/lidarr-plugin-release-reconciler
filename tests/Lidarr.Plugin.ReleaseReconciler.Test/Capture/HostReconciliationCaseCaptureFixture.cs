extern alias release_reconciler_nlog;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
using Moq;
using Logger = release_reconciler_nlog::NLog.Logger;
using LogManager = release_reconciler_nlog::NLog.LogManager;
using NzbDrone.Common.Http;
using NzbDrone.Core.Plugins.ReleaseReconciler.Capture;
using NzbDrone.Core.Plugins.ReleaseReconciler.Cases;
using NzbDrone.Core.Plugins.ReleaseReconciler.Notifications;
using Xunit;

namespace NzbDrone.Core.Plugins.ReleaseReconciler.Test.Capture
{
    public sealed class HostReconciliationCaseCaptureFixture : IDisposable
    {
        private static readonly DateTimeOffset NotificationNow = new(2026, 06, 15, 12, 00, 00, TimeSpan.Zero);
        private readonly string _rootDirectory;
        private readonly Logger _logger;

        public HostReconciliationCaseCaptureFixture()
        {
            _rootDirectory = Path.Combine(Path.GetTempPath(), $"release-reconciler-host-capture-tests-{Guid.NewGuid():N}");
            _logger = LogManager.GetLogger(nameof(HostReconciliationCaseCaptureFixture));
        }

        [Fact]
        public void capture_should_create_and_update_one_case_per_download_id()
        {
            var store = CreateStore();
            var capture = CreateCapture(store);
            var queuedAtUtc = new DateTimeOffset(2026, 06, 15, 10, 00, 00, TimeSpan.Zero);
            var completedAtUtc = queuedAtUtc.AddMinutes(7);

            var created = capture.Capture(new HostReconciliationCaptureRequest
            {
                SourceSeam = "lidarr-host-download-client",
                DownloadId = "lidarr-download-123",
                Phase = "queued",
                Status = "Queued",
                OutputPath = "/music/Artist/Album",
                QueuedAtUtc = queuedAtUtc,
                CapturedIdentifiers = new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["artistMusicBrainzId"] = "artist-mbid-123",
                    ["albumMusicBrainzId"] = "album-mbid-456"
                },
                SourceMetadata = new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["hostEventId"] = "evt-queued-1",
                    ["title"] = "Artist - Album"
                },
                StructuralEvidence = BuildTrackedStructuralEvidence()
            });

            var updated = capture.Capture(new HostReconciliationCaptureRequest
            {
                SourceSeam = "lidarr-host-download-client",
                DownloadId = "lidarr-download-123",
                Phase = "failed",
                Status = "Failed",
                OutputPath = "/music/Artist/Album",
                QueuedAtUtc = queuedAtUtc,
                CompletedAtUtc = completedAtUtc,
                LastError = "track number mismatch while importing Track Two; authorization=Bearer super-secret-token; token=abc123",
                CapturedIdentifiers = new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["artistMusicBrainzId"] = "artist-mbid-123",
                    ["albumMusicBrainzId"] = "album-mbid-456"
                },
                SourceMetadata = new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["hostEventId"] = "evt-failed-2",
                    ["provider"] = "Harmony",
                    ["providerUrl"] = "https://harmony.example/releases/album-123?token=secret-session",
                    ["title"] = "Artist - Album"
                },
                StructuralEvidence = BuildTrackedStructuralEvidence()
            });

            var reloaded = store.Get(created.CaseId);
            var snapshots = store.List();

            Assert.NotNull(reloaded);
            Assert.Equal(created.CaseId, updated.CaseId);
            Assert.Single(snapshots);
            Assert.Equal("failed", reloaded!.Phase);
            Assert.Equal("lidarr-download-123", reloaded.DownloadId);
            Assert.Equal(queuedAtUtc, reloaded.CapturedAtUtc);
            Assert.Equal(completedAtUtc, reloaded.LastUpdatedAtUtc);
            Assert.Equal(2, reloaded.StructuralEvidence!.TrackCount);
            Assert.Equal(new[] { "Track One", "Track Two" }, reloaded.StructuralEvidence.Tracks.Select(static track => track.Title).ToArray());
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
            Assert.Equal("https://harmony.example/releases/album-123", reloaded.HarmonySourceSupport.ProviderUrl);
            Assert.Null(reloaded.HarmonySourceSupport.SuppressionReason);
        }

        [Fact]
        public void capture_should_backfill_actionable_album_musicbrainz_context_from_preferred_release_evidence()
        {
            var store = CreateStore();
            var resolver = new RecordingHostMusicBrainzContextResolver(
                HostMusicBrainzContextResolution.Resolved(
                    "release-preferred",
                    "release-preferred",
                    "album-current"));
            var capture = CreateCapture(store, resolver);

            var persisted = capture.Capture(new HostReconciliationCaptureRequest
            {
                SourceSeam = "lidarr-host-download-client",
                DownloadId = "lidarr-download-actionable-ctx",
                Phase = "failed",
                Status = "Failed",
                LastError = "track number mismatch while importing Track One",
                CapturedIdentifiers = new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["artistMusicBrainzId"] = "artist-mbid-ctx"
                },
                StructuralEvidence = BuildTrackedStructuralEvidence(" release-preferred ")
            });

            var reloaded = store.Get(persisted.CaseId);

            Assert.NotNull(reloaded);
            Assert.True(reloaded!.ImportFailureClassification!.Actionable);
            Assert.Equal("release-preferred", reloaded.CapturedIdentifiers["preferredReleaseMusicBrainzId"]);
            Assert.Equal("album-current", reloaded.CapturedIdentifiers["albumMusicBrainzId"]);
            Assert.Equal(1, resolver.CallCount);
            Assert.Equal("release-preferred", resolver.LastPreferredReleaseMusicBrainzId);
        }

        [Fact]
        public void capture_should_skip_album_musicbrainz_backfill_for_non_actionable_cases_but_preserve_preferred_release_context()
        {
            var store = CreateStore();
            var resolver = new RecordingHostMusicBrainzContextResolver(
                HostMusicBrainzContextResolution.Resolved(
                    "release-preferred",
                    "release-preferred",
                    "album-current"));
            var capture = CreateCapture(store, resolver);

            var persisted = capture.Capture(new HostReconciliationCaptureRequest
            {
                SourceSeam = "lidarr-host-download-client",
                DownloadId = "lidarr-download-non-actionable-ctx",
                Phase = "failed",
                Status = "Failed",
                LastError = "permission denied while moving imported files",
                CapturedIdentifiers = new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["artistMusicBrainzId"] = "artist-mbid-ctx"
                },
                StructuralEvidence = BuildTrackedStructuralEvidence(" release-preferred ")
            });

            var reloaded = store.Get(persisted.CaseId);

            Assert.NotNull(reloaded);
            Assert.False(reloaded!.ImportFailureClassification!.Actionable);
            Assert.Equal("release-preferred", reloaded.CapturedIdentifiers["preferredReleaseMusicBrainzId"]);
            Assert.False(reloaded.CapturedIdentifiers.ContainsKey("albumMusicBrainzId"));
            Assert.Equal(0, resolver.CallCount);
        }

        [Fact]
        public void capture_should_preserve_existing_album_musicbrainz_id_without_requerying_host_context()
        {
            var store = CreateStore();
            var resolver = new RecordingHostMusicBrainzContextResolver(
                HostMusicBrainzContextResolution.Resolved(
                    "release-preferred",
                    "release-preferred",
                    "album-overwrite-attempt"));
            var capture = CreateCapture(store, resolver);

            var persisted = capture.Capture(new HostReconciliationCaptureRequest
            {
                SourceSeam = "lidarr-host-download-client",
                DownloadId = "lidarr-download-preserve-album-ctx",
                Phase = "failed",
                Status = "Failed",
                LastError = "track number mismatch while importing Track One",
                CapturedIdentifiers = new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["artistMusicBrainzId"] = "artist-mbid-ctx",
                    ["albumMusicBrainzId"] = "album-host-supplied"
                },
                StructuralEvidence = BuildTrackedStructuralEvidence(" release-preferred ")
            });

            var reloaded = store.Get(persisted.CaseId);

            Assert.NotNull(reloaded);
            Assert.True(reloaded!.ImportFailureClassification!.Actionable);
            Assert.Equal("album-host-supplied", reloaded.CapturedIdentifiers["albumMusicBrainzId"]);
            Assert.Equal("release-preferred", reloaded.CapturedIdentifiers["preferredReleaseMusicBrainzId"]);
            Assert.Equal(0, resolver.CallCount);
        }

        [Fact]
        public void capture_should_suppress_ordering_mismatches_without_persisted_track_evidence()
        {
            var store = CreateStore();
            var capture = CreateCapture(store);

            var persisted = capture.Capture(new HostReconciliationCaptureRequest
            {
                SourceSeam = "lidarr-host-download-client",
                DownloadId = "lidarr-download-789",
                Phase = "failed",
                Status = "Failed",
                DiagnosticSummary = "import failure: track ordering mismatch detected during host validation",
                CapturedIdentifiers = new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["artistMusicBrainzId"] = "artist-mbid-321"
                }
            });

            var reloaded = store.Get(persisted.CaseId);

            Assert.NotNull(reloaded);
            Assert.NotNull(reloaded!.ImportFailureClassification);
            Assert.False(reloaded.ImportFailureClassification!.Actionable);
            Assert.Equal(HostImportFailureClassification.CurrentRuleVersion, reloaded.ImportFailureClassification.RuleVersion);
            Assert.Equal("insufficient-structural-evidence", reloaded.ImportFailureClassification.MatchedRule);
            Assert.Equal("track-ordering mismatch was suppressed because fewer than two persisted tracks were captured.", reloaded.ImportFailureClassification.SuppressionReason);
            Assert.NotNull(reloaded.HarmonySourceSupport);
            Assert.False(reloaded.HarmonySourceSupport!.Supported);
            Assert.Equal("mismatch-not-actionable", reloaded.HarmonySourceSupport.MatchedRule);
            Assert.Equal("Harmony source support is suppressed because mismatch classification 'insufficient-structural-evidence' was not actionable.", reloaded.HarmonySourceSupport.SuppressionReason);
        }

        [Fact]
        public void capture_should_update_existing_download_id_with_suppressed_failure_and_preserve_redaction()
        {
            var store = CreateStore();
            var capture = CreateCapture(store);
            var queuedAtUtc = new DateTimeOffset(2026, 06, 15, 11, 00, 00, TimeSpan.Zero);
            var completedAtUtc = queuedAtUtc.AddMinutes(9);

            var actionable = capture.Capture(new HostReconciliationCaptureRequest
            {
                SourceSeam = "lidarr-host-download-client",
                DownloadId = "lidarr-download-900",
                Phase = "failed",
                Status = "Failed",
                QueuedAtUtc = queuedAtUtc,
                LastError = "track ordering mismatch detected during host validation; authorization=Bearer actionable-secret",
                CapturedIdentifiers = new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["artistMusicBrainzId"] = "artist-mbid-900",
                    ["albumMusicBrainzId"] = "album-mbid-900"
                },
                StructuralEvidence = BuildTrackedStructuralEvidence()
            });

            var suppressed = capture.Capture(new HostReconciliationCaptureRequest
            {
                SourceSeam = "lidarr-host-download-client",
                DownloadId = "lidarr-download-900",
                Phase = "failed",
                Status = "Failed",
                QueuedAtUtc = queuedAtUtc,
                CompletedAtUtc = completedAtUtc,
                DiagnosticSummary = "queueStatus=Failed; authorization=Bearer updated-secret; token=session-123",
                LastError = "cookie=session-cookie-123 permission denied while moving imported files",
                CapturedIdentifiers = new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["artistMusicBrainzId"] = "artist-mbid-900",
                    ["albumMusicBrainzId"] = "album-mbid-900"
                },
                StructuralEvidence = BuildTrackedStructuralEvidence()
            });

            var reloaded = store.Get(actionable.CaseId);
            var snapshots = store.List();

            Assert.True(actionable.ImportFailureClassification!.Actionable);
            Assert.NotNull(reloaded);
            Assert.Equal(actionable.CaseId, suppressed.CaseId);
            Assert.Single(snapshots);
            Assert.Equal(completedAtUtc, reloaded!.LastUpdatedAtUtc);
            Assert.NotNull(reloaded.ImportFailureClassification);
            Assert.False(reloaded.ImportFailureClassification!.Actionable);
            Assert.Equal(HostImportFailureClassification.CurrentRuleVersion, reloaded.ImportFailureClassification.RuleVersion);
            Assert.Equal("non-actionable-import-failure", reloaded.ImportFailureClassification.MatchedRule);
            Assert.Equal("import failure did not match track-number or ordering mismatch rules.", reloaded.ImportFailureClassification.SuppressionReason);
            Assert.NotNull(reloaded.HarmonySourceSupport);
            Assert.False(reloaded.HarmonySourceSupport!.Supported);
            Assert.Equal("mismatch-not-actionable", reloaded.HarmonySourceSupport.MatchedRule);
            Assert.Equal("Harmony source support is suppressed because mismatch classification 'non-actionable-import-failure' was not actionable.", reloaded.HarmonySourceSupport.SuppressionReason);
            Assert.Contains("[REDACTED]", reloaded.DiagnosticSummary!, StringComparison.Ordinal);
            Assert.Contains("[REDACTED]", reloaded.LastError!, StringComparison.Ordinal);
            Assert.DoesNotContain("updated-secret", reloaded.DiagnosticSummary!, StringComparison.Ordinal);
            Assert.DoesNotContain("session-123", reloaded.DiagnosticSummary!, StringComparison.Ordinal);
            Assert.DoesNotContain("session-cookie-123", reloaded.LastError!, StringComparison.Ordinal);
        }

        [Fact]
        public void capture_should_preserve_redaction_on_failed_phases()
        {
            var store = CreateStore();
            var capture = CreateCapture(store);

            var persisted = capture.Capture(new HostReconciliationCaptureRequest
            {
                SourceSeam = "lidarr-host-download-client",
                DownloadId = "lidarr-download-456",
                Phase = "failed",
                Status = "Failed",
                DiagnosticSummary = "queueStatus=Failed; authorization=Bearer my-token; token=secret-session",
                LastError = "cookie=super-secret-cookie; token=ultra-secret-token",
                CapturedIdentifiers = new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["artistMusicBrainzId"] = "artist-mbid-789"
                }
            });

            var reloaded = store.Get(persisted.CaseId);

            Assert.NotNull(reloaded);
            Assert.Contains("[REDACTED]", reloaded!.DiagnosticSummary!, StringComparison.Ordinal);
            Assert.Contains("[REDACTED]", reloaded.LastError!, StringComparison.Ordinal);
            Assert.DoesNotContain("my-token", reloaded.DiagnosticSummary!, StringComparison.Ordinal);
            Assert.DoesNotContain("secret-session", reloaded.DiagnosticSummary!, StringComparison.Ordinal);
            Assert.DoesNotContain("super-secret-cookie", reloaded.LastError!, StringComparison.Ordinal);
            Assert.DoesNotContain("ultra-secret-token", reloaded.LastError!, StringComparison.Ordinal);
        }

        [Fact]
        public void capture_should_reject_requests_without_stable_identifiers()
        {
            var store = CreateStore();
            var capture = CreateCapture(store);

            var error = Assert.Throws<ArgumentException>(() => capture.Capture(new HostReconciliationCaptureRequest
            {
                SourceSeam = "lidarr-host-download-client",
                DownloadId = "lidarr-download-789",
                Phase = "queued",
                Status = "Queued"
            }));

            Assert.Contains("stable captured identifier", error.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void capture_should_skip_notification_dispatch_when_transports_are_disabled()
        {
            var store = CreateStore();
            var httpClient = new Mock<IHttpClient>(MockBehavior.Strict);
            var notificationSettingsStore = CreateNotificationSettingsStore("disabled-settings.json");
            notificationSettingsStore.Save(CreateDiscordSettings(enabled: false));
            var capture = CreateCapture(store, notificationSettingsStore: notificationSettingsStore, notificationDispatcher: CreateDiscordDispatcher(store, httpClient.Object));

            var persisted = capture.Capture(BuildActionableNotificationRequest("lidarr-download-disabled"));
            var reloaded = store.Get(persisted.CaseId);

            Assert.NotNull(reloaded);
            Assert.Null(reloaded!.NotificationState);
            httpClient.VerifyNoOtherCalls();
        }

        [Fact]
        public void capture_should_skip_notification_dispatch_for_unsupported_harmony_sources()
        {
            var store = CreateStore();
            var httpClient = new Mock<IHttpClient>(MockBehavior.Strict);
            var notificationSettingsStore = CreateNotificationSettingsStore("unsupported-settings.json");
            notificationSettingsStore.Save(CreateDiscordSettings());
            var capture = CreateCapture(store, notificationSettingsStore: notificationSettingsStore, notificationDispatcher: CreateDiscordDispatcher(store, httpClient.Object));

            var persisted = capture.Capture(BuildActionableNotificationRequest(
                "lidarr-download-unsupported",
                sourceMetadata: new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["title"] = "Example Artist - Example Album"
                }));
            var reloaded = store.Get(persisted.CaseId);

            Assert.NotNull(reloaded);
            Assert.NotNull(reloaded!.HarmonySourceSupport);
            Assert.False(reloaded.HarmonySourceSupport!.Supported);
            Assert.Null(reloaded.NotificationState);
            httpClient.VerifyNoOtherCalls();
        }

        [Fact]
        public void capture_should_persist_first_successful_notification_dispatch()
        {
            var store = CreateStore();
            var httpClient = new Mock<IHttpClient>(MockBehavior.Strict);
            HttpRequest? capturedRequest = null;
            httpClient
                .Setup(client => client.Post(It.IsAny<HttpRequest>()))
                .Callback<HttpRequest>(request => capturedRequest = request)
                .Returns((HttpRequest request) => new HttpResponse(request, new HttpHeader(), "ok", HttpStatusCode.OK));
            var notificationSettingsStore = CreateNotificationSettingsStore("success-settings.json");
            notificationSettingsStore.Save(CreateDiscordSettings());
            var capture = CreateCapture(store, notificationSettingsStore: notificationSettingsStore, notificationDispatcher: CreateDiscordDispatcher(store, httpClient.Object));

            var persisted = capture.Capture(BuildActionableNotificationRequest("lidarr-download-first-success"));
            var reloaded = store.Get(persisted.CaseId);

            Assert.NotNull(reloaded);
            Assert.NotNull(capturedRequest);
            Assert.Equal(System.Net.Http.HttpMethod.Post, capturedRequest!.Method);
            Assert.Contains("discord.example/webhook", capturedRequest.Url.FullUri, StringComparison.Ordinal);
            var attempt = Assert.Single(reloaded!.NotificationState!.DispatchAttempts);
            Assert.True(attempt.Succeeded);
            Assert.Equal("discord", attempt.Transport);
            Assert.Equal("signed-content-path", attempt.ActionUrlKind);
            Assert.Equal(NotificationNow.AddMinutes(240), attempt.ActionTokenExpiresAtUtc);
            httpClient.Verify(client => client.Post(It.IsAny<HttpRequest>()), Times.Once);
        }

        [Fact]
        public void capture_should_suppress_duplicate_notification_dispatch_after_prior_success()
        {
            var store = CreateStore();
            var httpClient = new Mock<IHttpClient>(MockBehavior.Strict);
            httpClient
                .Setup(client => client.Post(It.IsAny<HttpRequest>()))
                .Returns((HttpRequest request) => new HttpResponse(request, new HttpHeader(), "ok", HttpStatusCode.OK));
            var notificationSettingsStore = CreateNotificationSettingsStore("duplicate-settings.json");
            notificationSettingsStore.Save(CreateDiscordSettings());
            var capture = CreateCapture(store, notificationSettingsStore: notificationSettingsStore, notificationDispatcher: CreateDiscordDispatcher(store, httpClient.Object));
            var queuedAtUtc = NotificationNow.AddMinutes(-15);

            var first = capture.Capture(BuildActionableNotificationRequest(
                "lidarr-download-duplicate",
                updatedAtUtc: queuedAtUtc));
            var second = capture.Capture(BuildActionableNotificationRequest(
                "lidarr-download-duplicate",
                updatedAtUtc: queuedAtUtc.AddMinutes(5)));
            var reloaded = store.Get(first.CaseId);

            Assert.Equal(first.CaseId, second.CaseId);
            Assert.NotNull(reloaded);
            var attempt = Assert.Single(reloaded!.NotificationState!.DispatchAttempts);
            Assert.True(attempt.Succeeded);
            httpClient.Verify(client => client.Post(It.IsAny<HttpRequest>()), Times.Once);
        }

        [Fact]
        public void capture_should_retry_notification_dispatch_after_failed_transport_attempt()
        {
            var store = CreateStore();
            var httpClient = new Mock<IHttpClient>(MockBehavior.Strict);
            var callCount = 0;
            httpClient
                .Setup(client => client.Post(It.IsAny<HttpRequest>()))
                .Callback<HttpRequest>(_ =>
                {
                    callCount++;
                    if (callCount == 1)
                    {
                        var failedRequest = new HttpRequest("https://discord.example/webhook?token=super-secret")
                        {
                            Method = System.Net.Http.HttpMethod.Post
                        };
                        throw new HttpException(
                            failedRequest,
                            new HttpResponse(failedRequest, new HttpHeader(), string.Empty, HttpStatusCode.BadGateway),
                            "HTTP request failed: [502:BadGateway] [POST] at [https://discord.example/webhook?token=super-secret]");
                    }
                })
                .Returns((HttpRequest request) => new HttpResponse(request, new HttpHeader(), "ok", HttpStatusCode.OK));
            var notificationSettingsStore = CreateNotificationSettingsStore("retry-settings.json");
            notificationSettingsStore.Save(CreateDiscordSettings());
            var capture = CreateCapture(store, notificationSettingsStore: notificationSettingsStore, notificationDispatcher: CreateDiscordDispatcher(store, httpClient.Object));
            var firstUpdatedAtUtc = NotificationNow.AddMinutes(-10);

            var first = capture.Capture(BuildActionableNotificationRequest(
                "lidarr-download-retry",
                updatedAtUtc: firstUpdatedAtUtc));
            var second = capture.Capture(BuildActionableNotificationRequest(
                "lidarr-download-retry",
                updatedAtUtc: firstUpdatedAtUtc.AddMinutes(3)));
            var reloaded = store.Get(first.CaseId);

            Assert.Equal(first.CaseId, second.CaseId);
            Assert.NotNull(reloaded);
            var attempts = reloaded!.NotificationState!.DispatchAttempts;
            Assert.Equal(2, attempts.Count);
            Assert.False(attempts[0].Succeeded);
            Assert.Equal("http-error", attempts[0].FailureKind);
            Assert.Contains("BadGateway", attempts[0].FailureSummary ?? string.Empty, StringComparison.Ordinal);
            Assert.True(attempts[1].Succeeded);
            httpClient.Verify(client => client.Post(It.IsAny<HttpRequest>()), Times.Exactly(2));
        }

        [Fact]
        public void capture_should_persist_invalid_notification_settings_failure_without_transport_send()
        {
            var store = CreateStore();
            var httpClient = new Mock<IHttpClient>(MockBehavior.Strict);
            var notificationSettingsStore = CreateNotificationSettingsStore("invalid-base-settings.json");
            notificationSettingsStore.Save(CreateDiscordSettings(publicBaseUrl: "not-a-valid-url"));
            var capture = CreateCapture(store, notificationSettingsStore: notificationSettingsStore, notificationDispatcher: CreateDiscordDispatcher(store, httpClient.Object));

            var persisted = capture.Capture(BuildActionableNotificationRequest("lidarr-download-invalid-base"));
            var reloaded = store.Get(persisted.CaseId);

            Assert.NotNull(reloaded);
            var attempt = Assert.Single(reloaded!.NotificationState!.DispatchAttempts);
            Assert.False(attempt.Succeeded);
            Assert.Equal("discord", attempt.Transport);
            Assert.Equal("invalid-settings", attempt.FailureKind);
            httpClient.VerifyNoOtherCalls();
        }

        [Fact]
        public void bandcamp_adapter_should_preserve_existing_sample_semantics()
        {
            var store = CreateStore();
            var capture = new BandcampReconciliationCaseCapture(store, _logger);
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
            Assert.Equal(3, reloaded.StructuralEvidence!.TrackCount);
            Assert.Equal("b5f7f0bf-4b9e-4f7d-8a6b-7d7ae0ca1b68", reloaded.StructuralEvidence.PreferredRelease!.ReleaseMusicBrainzId);
            Assert.Contains("queuePhase=post-extract-review", reloaded.DiagnosticSummary!, StringComparison.Ordinal);
            Assert.Contains("releaseSpecificity=preferred-release", reloaded.DiagnosticSummary!, StringComparison.Ordinal);
            Assert.Equal("downloaded files diverged from preferred release track mapping", reloaded.LastError);
            Assert.NotNull(reloaded.HarmonySourceSupport);
            Assert.False(reloaded.HarmonySourceSupport!.Supported);
            Assert.Equal("mismatch-not-actionable", reloaded.HarmonySourceSupport.MatchedRule);
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

        private HostReconciliationCaseCapture CreateCapture(
            IReconciliationCaseStore store,
            IHostMusicBrainzContextResolver? hostMusicBrainzContextResolver = null,
            NotificationSettingsStore? notificationSettingsStore = null,
            ReconciliationNotificationDispatcher? notificationDispatcher = null)
        {
            return new HostReconciliationCaseCapture(store, _logger, hostMusicBrainzContextResolver, notificationSettingsStore, notificationDispatcher);
        }

        private NotificationSettingsStore CreateNotificationSettingsStore(string fileName)
        {
            return new NotificationSettingsStore(Path.Combine(_rootDirectory, fileName), _logger);
        }

        private ReconciliationNotificationDispatcher CreateDiscordDispatcher(IReconciliationCaseStore store, IHttpClient httpClient)
        {
            return new ReconciliationNotificationDispatcher(
                new ReconciliationNotificationBuilder(() => NotificationNow),
                store,
                new INotificationTransport[] { new DiscordWebhookTransport(httpClient, _logger) },
                _logger);
        }

        private static ReconciliationNotificationSettings CreateDiscordSettings(bool enabled = true, string publicBaseUrl = "https://lidarr.example")
        {
            return new ReconciliationNotificationSettings
            {
                PublicBaseUrl = publicBaseUrl,
                Discord = new ReconciliationNotificationSettings.DiscordNotificationSettings
                {
                    Enabled = enabled,
                    WebHookUrl = "https://discord.example/webhook?token=super-secret",
                    Username = "Release Reconciler"
                }
            };
        }

        private static HostReconciliationCaptureRequest BuildActionableNotificationRequest(
            string downloadId,
            DateTimeOffset? updatedAtUtc = null,
            IReadOnlyDictionary<string, string?>? sourceMetadata = null)
        {
            return new HostReconciliationCaptureRequest
            {
                SourceSeam = "lidarr-host-download-client",
                DownloadId = downloadId,
                Phase = "failed",
                Status = "Failed",
                UpdatedAtUtc = updatedAtUtc,
                LastError = "track number mismatch while importing Track One",
                CapturedIdentifiers = new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["artistMusicBrainzId"] = "artist-mbid-dispatch"
                },
                SourceMetadata = sourceMetadata == null
                    ? BuildSupportedHarmonySourceMetadata()
                    : new Dictionary<string, string?>(sourceMetadata, StringComparer.Ordinal),
                StructuralEvidence = BuildTrackedStructuralEvidence("preferred-release-mbid")
            };
        }

        private static Dictionary<string, string?> BuildSupportedHarmonySourceMetadata()
        {
            return new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["provider"] = "Harmony",
                ["providerUrl"] = "https://harmony.example/releases/album-123?token=secret-session",
                ["lookupArtist"] = "Example Artist",
                ["lookupAlbum"] = "Example Album",
                ["lookupQuery"] = "Example Artist Example Album",
                ["title"] = "Example Artist - Example Album"
            };
        }

        private static HostReconciliationStructuralEvidence BuildTrackedStructuralEvidence(string? preferredReleaseMusicBrainzId = null)
        {
            return new HostReconciliationStructuralEvidence
            {
                TrackCount = 2,
                PreferredRelease = preferredReleaseMusicBrainzId == null
                    ? null
                    : new HostPreferredReleaseEvidence
                    {
                        ReleaseMusicBrainzId = preferredReleaseMusicBrainzId
                    },
                Tracks = new List<HostTrackEvidence>
                {
                    new() { AbsoluteTrackNumber = 1, MediumNumber = 1, Title = "Track One", RecordingMusicBrainzId = "recording-1", ReleaseTrackMusicBrainzId = "release-track-1" },
                    new() { AbsoluteTrackNumber = 2, MediumNumber = 1, Title = "Track Two", RecordingMusicBrainzId = "recording-2", ReleaseTrackMusicBrainzId = "release-track-2" }
                }
            };
        }

        private static BandcampMismatchEvidence LoadSoccerMommySample()
        {
            var samplePath = LocateTrackedFile(
                "tests",
                "Lidarr.Plugin.ReleaseReconciler.Test",
                "SampleData",
                "soccer-mommy-mismatch.json");

            using var stream = File.OpenRead(samplePath);
            var evidence = JsonSerializer.Deserialize<BandcampMismatchEvidence>(stream, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            return evidence ?? throw new InvalidDataException($"Failed to deserialize sample at {samplePath}.");
        }

        private static string LocateTrackedFile(params string[] relativeSegments)
        {
            var current = new DirectoryInfo(AppContext.BaseDirectory);
            while (current != null)
            {
                var candidate = Path.Combine(new[] { current.FullName }.Concat(relativeSegments).ToArray());
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                current = current.Parent;
            }

            throw new FileNotFoundException($"Could not locate tracked file: {Path.Combine(relativeSegments)}");
        }

        private sealed class RecordingHostMusicBrainzContextResolver : IHostMusicBrainzContextResolver
        {
            private readonly HostMusicBrainzContextResolution _result;

            public RecordingHostMusicBrainzContextResolver(HostMusicBrainzContextResolution result)
            {
                _result = result;
            }

            public int CallCount { get; private set; }

            public string? LastPreferredReleaseMusicBrainzId { get; private set; }

            public HostMusicBrainzContextResolution Resolve(string? preferredReleaseMusicBrainzId)
            {
                CallCount++;
                LastPreferredReleaseMusicBrainzId = preferredReleaseMusicBrainzId;
                return _result;
            }
        }
    }
}
