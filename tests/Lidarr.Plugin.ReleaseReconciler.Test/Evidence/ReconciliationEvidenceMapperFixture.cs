using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Lidarr.Http.Frontend;
using Lidarr.Http.Frontend.Mappers;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using NLog;
using NzbDrone.Core.Plugins.ReleaseReconciler.Actions;
using NzbDrone.Core.Plugins.ReleaseReconciler.Candidates;
using NzbDrone.Core.Plugins.ReleaseReconciler.Cases;
using NzbDrone.Core.Plugins.ReleaseReconciler.Evidence;
using NzbDrone.Core.Plugins.ReleaseReconciler.Notifications;
using Xunit;

namespace NzbDrone.Core.Plugins.ReleaseReconciler.Test.Evidence
{
    public sealed class ReconciliationEvidenceMapperFixture : IDisposable
    {
        private static readonly DateTimeOffset Now = new(2026, 6, 14, 22, 0, 0, TimeSpan.Zero);
        private readonly string _rootDirectory;
        private readonly string _keyDirectory;
        private readonly Logger _logger;

        public ReconciliationEvidenceMapperFixture()
        {
            _rootDirectory = Path.Combine(Path.GetTempPath(), "lidarr-plugin-work", "tests", Guid.NewGuid().ToString("N"));
            _keyDirectory = Path.Combine(_rootDirectory, "keys");
            Directory.CreateDirectory(_rootDirectory);
            Directory.CreateDirectory(_keyDirectory);
            _logger = LogManager.GetLogger(typeof(ReconciliationEvidenceMapperFixture).FullName);
        }

        public void Dispose()
        {
            if (Directory.Exists(_rootDirectory))
            {
                Directory.Delete(_rootDirectory, recursive: true);
            }
        }

        [Fact]
        public async Task should_dispatch_signed_evidence_through_static_resource_controller_without_mutating_case_state()
        {
            var store = CreateStore();
            var settings = CreateNotificationSettings();
            var tokenService = CreateEvidenceTokenService(() => Now);
            var snapshot = store.Save(CreateCase(caseId: "case-evidence-200"));
            var token = tokenService.Issue(snapshot.CaseId, Now.AddMinutes(30)).Token;
            var controller = CreateController(new ReconciliationEvidenceMapper(store, tokenService, settings, _logger));

            var result = await controller.IndexContent(BuildEvidencePath(token));

            var page = Assert.IsType<ReconciliationEvidencePage>(result);
            Assert.Equal(200, page.StatusCode);
            Assert.Contains("Read-only evidence", page.Content);
            Assert.Contains("Bandcamp Source", page.Content);
            Assert.Contains("Harmony Search", page.Content);
            Assert.Contains("Ranked candidates", page.Content);
            Assert.Contains("Notification audit", page.Content);
            Assert.Contains("Operator action audit", page.Content);

            var actionContext = CreateActionContext();
            await page.ExecuteResultAsync(actionContext);
            Assert.Equal("no-store, no-cache, max-age=0, must-revalidate", actionContext.HttpContext.Response.Headers["Cache-Control"].ToString());
            Assert.Equal("no-cache", actionContext.HttpContext.Response.Headers["Pragma"].ToString());

            var after = store.Get(snapshot.CaseId)!;
            Assert.Equal(snapshot.NotificationState?.DispatchAttempts.Count ?? 0, after.NotificationState?.DispatchAttempts.Count ?? 0);
            Assert.Equal(snapshot.OperatorActionState?.ProcessedActionReceipts.Count ?? 0, after.OperatorActionState?.ProcessedActionReceipts.Count ?? 0);
        }

        [Fact]
        public async Task should_refuse_invalid_signature_without_mutating_case_state()
        {
            var store = CreateStore();
            var settings = CreateNotificationSettings();
            var tokenService = CreateEvidenceTokenService(() => Now);
            var snapshot = store.Save(CreateCase(caseId: "case-evidence-invalid"));
            var issued = tokenService.Issue(snapshot.CaseId, Now.AddMinutes(30)).Token;
            var tamperedToken = issued[..^1] + (issued.EndsWith("A", StringComparison.Ordinal) ? "B" : "A");
            var controller = CreateController(new ReconciliationEvidenceMapper(store, tokenService, settings, _logger));

            var result = await controller.IndexContent(BuildEvidencePath(tamperedToken));

            var page = Assert.IsType<ReconciliationEvidencePage>(result);
            Assert.Equal(400, page.StatusCode);
            Assert.Contains("Evidence link is invalid", page.Content);

            var after = store.Get(snapshot.CaseId)!;
            Assert.Equal(snapshot.OperatorActionState?.ProcessedActionReceipts.Count ?? 0, after.OperatorActionState?.ProcessedActionReceipts.Count ?? 0);
        }

        [Fact]
        public async Task should_refuse_expired_token_without_mutating_case_state()
        {
            var store = CreateStore();
            var settings = CreateNotificationSettings();
            var tokenService = CreateEvidenceTokenService(() => Now);
            var snapshot = store.Save(CreateCase(caseId: "case-evidence-expired"));
            var token = tokenService.Issue(snapshot.CaseId, Now.AddMinutes(-5)).Token;
            var controller = CreateController(new ReconciliationEvidenceMapper(store, tokenService, settings, _logger));

            var result = await controller.IndexContent(BuildEvidencePath(token));

            var page = Assert.IsType<ReconciliationEvidencePage>(result);
            Assert.Equal(410, page.StatusCode);
            Assert.Contains("Evidence link expired", page.Content);

            var after = store.Get(snapshot.CaseId)!;
            Assert.Equal(snapshot.NotificationState?.DispatchAttempts.Count ?? 0, after.NotificationState?.DispatchAttempts.Count ?? 0);
        }

        [Fact]
        public async Task should_refuse_missing_case_without_creating_state()
        {
            var store = CreateStore();
            var settings = CreateNotificationSettings();
            var tokenService = CreateEvidenceTokenService(() => Now);
            var token = tokenService.Issue("missing-case", Now.AddMinutes(30)).Token;
            var controller = CreateController(new ReconciliationEvidenceMapper(store, tokenService, settings, _logger));

            var result = await controller.IndexContent(BuildEvidencePath(token));

            var page = Assert.IsType<ReconciliationEvidencePage>(result);
            Assert.Equal(404, page.StatusCode);
            Assert.Contains("Evidence case not found", page.Content);
            Assert.Empty(store.List());
        }

        [Fact]
        public async Task should_keep_evidence_and_action_routes_non_overlapping()
        {
            var store = CreateStore();
            var settings = CreateNotificationSettings();
            var evidenceTokenService = CreateEvidenceTokenService(() => Now);
            var actionTokenService = CreateActionTokenService(() => Now);
            var snapshot = store.Save(CreateCase(caseId: "case-route-separation"));
            var evidenceToken = evidenceTokenService.Issue(snapshot.CaseId, Now.AddMinutes(30)).Token;
            var actionToken = actionTokenService.Issue(snapshot.CaseId, ReconciliationOperatorActionKinds.Snooze, Now.AddMinutes(30)).Token;
            var evidenceMapper = new ReconciliationEvidenceMapper(store, evidenceTokenService, settings, _logger);
            var actionMapper = new ReconciliationOperatorActionMapper(store, actionTokenService, _logger, () => Now.AddMinutes(1));
            var controller = CreateController(evidenceMapper, actionMapper);

            Assert.True(evidenceMapper.CanHandle("/Content/" + BuildEvidencePath(evidenceToken)));
            Assert.False(evidenceMapper.CanHandle("/Content/" + BuildActionPath(actionToken)));
            Assert.True(actionMapper.CanHandle("/Content/" + BuildActionPath(actionToken)));
            Assert.False(actionMapper.CanHandle("/Content/" + BuildEvidencePath(evidenceToken)));

            var evidenceResult = await controller.IndexContent(BuildEvidencePath(evidenceToken));
            var actionResult = await controller.IndexContent(BuildActionPath(actionToken));

            Assert.IsType<ReconciliationEvidencePage>(evidenceResult);
            Assert.IsType<OperatorActionResultPage>(actionResult);
        }

        private FileReconciliationCaseStore CreateStore()
        {
            return new FileReconciliationCaseStore(Path.Combine(_rootDirectory, "cases"), _logger);
        }

        private ReconciliationEvidenceTokenService CreateEvidenceTokenService(Func<DateTimeOffset> clock)
        {
            return new ReconciliationEvidenceTokenService(
                DataProtectionProvider.Create(new DirectoryInfo(_keyDirectory)),
                LogManager.GetLogger("EvidenceTokenService"),
                clock);
        }

        private OperatorActionTokenService CreateActionTokenService(Func<DateTimeOffset> clock)
        {
            return new OperatorActionTokenService(
                DataProtectionProvider.Create(new DirectoryInfo(_keyDirectory)),
                LogManager.GetLogger("OperatorActionTokenService"),
                clock);
        }

        private StaticResourceController CreateController(params IMapHttpRequestsToDisk[] mappers)
        {
            return new StaticResourceController(mappers, _logger)
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext()
                }
            };
        }

        private static ActionContext CreateActionContext()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddMvcCore();

            var httpContext = new DefaultHttpContext
            {
                RequestServices = services.BuildServiceProvider()
            };

            return new ActionContext(httpContext, new RouteData(), new ActionDescriptor());
        }

        private static string BuildEvidencePath(string token)
        {
            return $"release-reconciler/evidence/{token}";
        }

        private static string BuildActionPath(string token)
        {
            return $"release-reconciler/action/{token}";
        }

        private static ReconciliationNotificationSettings CreateNotificationSettings()
        {
            return new ReconciliationNotificationSettings
            {
                PublicBaseUrl = "https://operator.example.test/",
                OperatorActionPath = "/content/release-reconciler/action",
                EvidencePagePath = "/content/release-reconciler/evidence",
                HarmonySearchBaseUrl = "https://api.lidarr.audio/api/v0.4/search"
            };
        }

        private static ReconciliationCase CreateCase(string caseId)
        {
            return new ReconciliationCase(
                sourceSeam: "DownloadDecisionMaker",
                phase: "post-import",
                capturedIdentifiers: new Dictionary<string, string?>
                {
                    ["artistName"] = "Boards of Canada",
                    ["albumTitle"] = "Music Has the Right to Children",
                    ["albumUrl"] = "https://artist.example.test/music-has-the-right-to-children"
                },
                structuralEvidence: new ReconciliationEvidence
                {
                    TrackCount = 2,
                    PreferredRelease = new PreferredReleaseReconciliationEvidence
                    {
                        ReleaseMusicBrainzId = "release-123",
                        ReleaseArtistMusicBrainzId = "artist-123",
                        ReleaseStatus = "official",
                        Label = "Warp Records",
                        ReleaseDateUtc = Now.AddYears(-28),
                        DiscCount = 1,
                        MediaByDisc = new Dictionary<int, string>
                        {
                            [1] = "CD"
                        }
                    },
                    Tracks = new List<ReconciliationTrackEvidence>
                    {
                        new()
                        {
                            AbsoluteTrackNumber = 1,
                            MediumNumber = 1,
                            Title = "Wildlife Analysis",
                            RecordingMusicBrainzId = "recording-001",
                            ReleaseTrackMusicBrainzId = "release-track-001"
                        },
                        new()
                        {
                            AbsoluteTrackNumber = 2,
                            MediumNumber = 1,
                            Title = "An Eagle in Your Mind",
                            RecordingMusicBrainzId = "recording-002",
                            ReleaseTrackMusicBrainzId = "release-track-002"
                        }
                    }
                },
                downloadId: "download-01",
                outputPath: "/music/Boards of Canada/Music Has the Right to Children",
                diagnosticSummary: "Lookup returned one same-group candidate and one fallback candidate.",
                lastError: null,
                caseId: caseId,
                capturedAtUtc: Now.AddMinutes(-30),
                updatedAtUtc: Now.AddMinutes(-5),
                evaluationResult: new ReconciliationEvaluationResult
                {
                    Classification = ReconciliationClassification.MissingReleaseInCurrentGroup,
                    RefusalReason = ReconciliationRefusalReason.AmbiguousWinnerMargin,
                    UsedTitleArtistFallback = true,
                    WinnerAlbumMusicBrainzId = null,
                    WinnerReleaseMusicBrainzId = null,
                    EvaluatedAtUtc = Now.AddMinutes(-10),
                    LookupAttempts = new List<LidarrAlbumLookupAttempt>
                    {
                        new(CandidateLookupPath.RecordingIds, attempted: true, inputCount: 12, rawCandidateCount: 2, addedCandidateCount: 2, deduplicatedCandidateCount: 2, reason: null),
                        new(CandidateLookupPath.TitleArtistFallback, attempted: true, inputCount: 1, rawCandidateCount: 1, addedCandidateCount: 0, deduplicatedCandidateCount: 0, reason: "Winner margin remained narrow")
                    },
                    RankedCandidates = new List<RankedCandidateRelease>
                    {
                        new()
                        {
                            Rank = 1,
                            ArtistName = "Boards of Canada",
                            AlbumTitle = "Music Has the Right to Children",
                            ReleaseTitle = "Music Has the Right to Children",
                            ReleaseDisambiguation = "Warp Records CD",
                            ReleaseMusicBrainzId = "release-123",
                            AlbumMusicBrainzId = "release-group-123",
                            SameReleaseGroup = true,
                            HasStrongIdentitySignal = true,
                            TitleOnlyCandidate = false,
                            LookupPaths = new List<CandidateLookupPath> { CandidateLookupPath.RecordingIds },
                            ScoreBreakdown = new ReconciliationCandidateScoreBreakdown
                            {
                                TotalScore = 92,
                                StrongSignalScore = 20,
                                MediaFormatScore = 10,
                                LabelScore = 8
                            }
                        },
                        new()
                        {
                            Rank = 2,
                            ArtistName = "Boards of Canada",
                            AlbumTitle = "Music Has the Right to Children",
                            ReleaseTitle = "Music Has the Right to Children",
                            ReleaseDisambiguation = "Streaming",
                            ReleaseMusicBrainzId = "release-456",
                            AlbumMusicBrainzId = "release-group-123",
                            SameReleaseGroup = true,
                            HasStrongIdentitySignal = false,
                            TitleOnlyCandidate = true,
                            LookupPaths = new List<CandidateLookupPath> { CandidateLookupPath.TitleArtistFallback },
                            ScoreBreakdown = new ReconciliationCandidateScoreBreakdown
                            {
                                TotalScore = 63,
                                StrongSignalScore = 0,
                                MediaFormatScore = 4,
                                LabelScore = 0
                            }
                        }
                    }
                },
                notificationState: new ReconciliationNotificationState
                {
                    DispatchAttempts = new List<ReconciliationNotificationDispatchAttempt>
                    {
                        new()
                        {
                            AttemptedAtUtc = Now.AddMinutes(-4),
                            Transport = "discord",
                            Succeeded = true,
                            ActionUrlKind = "signed-content-path",
                            ActionTokenExpiresAtUtc = Now.AddHours(4)
                        }
                    }
                },
                operatorActionState: new ReconciliationOperatorActionState
                {
                    SnoozedUntilUtc = Now.AddHours(12),
                    LastAction = new ReconciliationOperatorActionAudit
                    {
                        Action = ReconciliationOperatorActionKinds.Snooze,
                        Result = ReconciliationOperatorActionOutcomes.Applied,
                        OccurredAtUtc = Now.AddMinutes(-3),
                        Transport = "discord",
                        ActionedBy = "operator",
                        ActionTokenHash = "hash-01"
                    },
                    ProcessedActionReceipts = new List<ReconciliationOperatorActionReceipt>
                    {
                        new()
                        {
                            ActionTokenHash = "hash-01",
                            Action = ReconciliationOperatorActionKinds.Snooze,
                            Outcome = ReconciliationOperatorActionOutcomes.Applied,
                            ProcessedAtUtc = Now.AddMinutes(-3),
                            ExpiresAtUtc = Now.AddHours(4),
                            Transport = "discord"
                        }
                    }
                });
        }
    }
}
