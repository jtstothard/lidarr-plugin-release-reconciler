using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Moq;
using NLog;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.Music;
using NzbDrone.Core.Plugins.ReleaseReconciler.Actions;
using NzbDrone.Core.Plugins.ReleaseReconciler.Candidates;
using NzbDrone.Core.Plugins.ReleaseReconciler.Cases;
using NzbDrone.Core.Plugins.ReleaseReconciler.Notifications;
using NzbDrone.Core.Plugins.ReleaseReconciler.Switching;
using Xunit;

namespace NzbDrone.Core.Plugins.ReleaseReconciler.Test.Switching
{
    public sealed class ReleaseSwitchWorkflowFixture : IDisposable
    {
        private static readonly DateTimeOffset Now = new(2026, 6, 15, 1, 0, 0, TimeSpan.Zero);
        private readonly string _rootDirectory;
        private readonly string _keyDirectory;
        private readonly Logger _logger;

        public ReleaseSwitchWorkflowFixture()
        {
            _rootDirectory = Path.Combine(Path.GetTempPath(), $"release-switch-workflow-{Guid.NewGuid():N}");
            _keyDirectory = Path.Combine(_rootDirectory, "keys");
            Directory.CreateDirectory(_keyDirectory);
            _logger = LogManager.GetLogger(nameof(ReleaseSwitchWorkflowFixture));
        }

        [Fact]
        public async Task should_replay_same_group_approval_workflow_and_keep_persisted_snapshot_token_safe()
        {
            using var context = CreateWorkflowContext(
                caseId: "case-same-group",
                classification: ReconciliationClassification.BetterExistingRelease,
                winnerAlbumId: "album-current",
                winnerReleaseId: "release-candidate",
                sameReleaseGroup: true,
                notificationEnableDifferentGroupApproval: false,
                executionEnableDifferentGroupApproval: false,
                tokenClock: () => Now,
                executionClock: () => Now);

            var approveAction = Assert.Single(
                context.NotificationBuilder
                    .Build(context.Case, CreateNotificationSettings(), new ReleaseSwitchSettings { EnableReleaseSwitchApproval = true })
                    .Actions
                    .Where(action => action.Action == ReconciliationOperatorActionKinds.ApproveSwitch));
            var approvePath = new Uri(approveAction.Url).AbsolutePath;

            var first = Assert.IsType<OperatorActionResultPage>(await context.Mapper.GetResponse(approvePath));
            var replay = Assert.IsType<OperatorActionResultPage>(await context.Mapper.GetResponse(approvePath));

            Assert.Equal(200, first.StatusCode);
            Assert.Equal(409, replay.StatusCode);

            var persisted = Assert.IsType<ReconciliationCaseSnapshot>(context.Store.Get("case-same-group"));
            Assert.Equal(ReconciliationOperatorActionOutcomes.Replay, persisted.OperatorActionState!.LastAction!.Result);
            Assert.Equal(ReconciliationOperatorActionOutcomes.Replay, persisted.ReleaseSwitchApprovalState!.Outcome);
            Assert.Equal("album-current", persisted.ReleaseSwitchApprovalState.AfterAlbumMusicBrainzId);
            Assert.Equal("release-candidate", persisted.ReleaseSwitchApprovalState.AfterReleaseMusicBrainzId);

            var persistedJson = ReadPersistedCaseJson(context.Store, "case-same-group");
            Assert.DoesNotContain(approveAction.Url, persistedJson, StringComparison.Ordinal);
            Assert.DoesNotContain(approvePath.Split('/').Last(), persistedJson, StringComparison.Ordinal);
            Assert.DoesNotContain("token=", persistedJson, StringComparison.OrdinalIgnoreCase);

            var expiredClock = Now;
            using var expiredContext = CreateWorkflowContext(
                caseId: "case-same-group-expired",
                classification: ReconciliationClassification.BetterExistingRelease,
                winnerAlbumId: "album-current",
                winnerReleaseId: "release-candidate",
                sameReleaseGroup: true,
                notificationEnableDifferentGroupApproval: false,
                executionEnableDifferentGroupApproval: false,
                tokenClock: () => expiredClock,
                executionClock: () => expiredClock);

            var expiredAction = Assert.Single(
                expiredContext.NotificationBuilder
                    .Build(expiredContext.Case, CreateNotificationSettings(), new ReleaseSwitchSettings { EnableReleaseSwitchApproval = true })
                    .Actions
                    .Where(action => action.Action == ReconciliationOperatorActionKinds.ApproveSwitch));

            expiredClock = Now.AddHours(5);
            var expired = Assert.IsType<OperatorActionResultPage>(await expiredContext.Mapper.GetResponse(new Uri(expiredAction.Url).AbsolutePath));
            Assert.Equal(410, expired.StatusCode);
            var expiredSnapshot = Assert.IsType<ReconciliationCaseSnapshot>(expiredContext.Store.Get("case-same-group-expired"));
            Assert.Equal(ReconciliationOperatorActionOutcomes.Expired, expiredSnapshot.OperatorActionState!.LastAction!.Result);
            Assert.Equal(ReconciliationOperatorActionOutcomes.Expired, expiredSnapshot.ReleaseSwitchApprovalState!.Outcome);
        }

        [Fact]
        public async Task should_refuse_signed_different_group_approval_when_policy_changes_before_execution()
        {
            using var context = CreateWorkflowContext(
                caseId: "case-different-group-refused",
                classification: ReconciliationClassification.DifferentReleaseGroup,
                winnerAlbumId: "album-other",
                winnerReleaseId: "release-candidate",
                sameReleaseGroup: false,
                notificationEnableDifferentGroupApproval: true,
                executionEnableDifferentGroupApproval: false,
                initialTargetReleaseExists: false,
                tokenClock: () => Now,
                executionClock: () => Now);

            var approveAction = Assert.Single(
                context.NotificationBuilder
                    .Build(
                        context.Case,
                        CreateNotificationSettings(),
                        new ReleaseSwitchSettings
                        {
                            EnableReleaseSwitchApproval = true,
                            EnableDifferentGroupReleaseSwitchApproval = true
                        })
                    .Actions
                    .Where(action => action.Action == ReconciliationOperatorActionKinds.ApproveSwitch));

            var result = Assert.IsType<OperatorActionResultPage>(await context.Mapper.GetResponse(new Uri(approveAction.Url).AbsolutePath));

            Assert.NotEqual(200, result.StatusCode);
            Assert.Contains("policy", result.Content, StringComparison.OrdinalIgnoreCase);

            var persisted = Assert.IsType<ReconciliationCaseSnapshot>(context.Store.Get("case-different-group-refused"));
            Assert.Equal(ReconciliationOperatorActionOutcomes.Refused, persisted.ReleaseSwitchApprovalState!.Outcome);
            Assert.Equal("album-other", persisted.ReleaseSwitchApprovalState.RequestedAlbumMusicBrainzId);
            Assert.Equal("release-candidate", persisted.ReleaseSwitchApprovalState.RequestedReleaseMusicBrainzId);
            context.AlbumInfo.Verify(service => service.GetAlbumInfo(It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public async Task should_apply_guarded_different_group_approval_workflow_and_keep_persisted_snapshot_token_safe()
        {
            using var context = CreateWorkflowContext(
                caseId: "case-different-group-applied",
                classification: ReconciliationClassification.DifferentReleaseGroup,
                winnerAlbumId: "album-other",
                winnerReleaseId: "release-candidate",
                sameReleaseGroup: false,
                notificationEnableDifferentGroupApproval: true,
                executionEnableDifferentGroupApproval: true,
                initialTargetReleaseExists: false,
                hydratedTargetReleaseExists: true,
                tokenClock: () => Now,
                executionClock: () => Now);

            var approveAction = Assert.Single(
                context.NotificationBuilder
                    .Build(
                        context.Case,
                        CreateNotificationSettings(),
                        new ReleaseSwitchSettings
                        {
                            EnableReleaseSwitchApproval = true,
                            EnableDifferentGroupReleaseSwitchApproval = true
                        })
                    .Actions
                    .Where(action => action.Action == ReconciliationOperatorActionKinds.ApproveSwitch));

            var result = Assert.IsType<OperatorActionResultPage>(await context.Mapper.GetResponse(new Uri(approveAction.Url).AbsolutePath));
            Assert.Equal(200, result.StatusCode);
            Assert.Contains("Release switch applied", result.Content, StringComparison.Ordinal);

            var persisted = Assert.IsType<ReconciliationCaseSnapshot>(context.Store.Get("case-different-group-applied"));
            Assert.Equal(ReconciliationOperatorActionOutcomes.Applied, persisted.ReleaseSwitchApprovalState!.Outcome);
            Assert.Equal("album-current", persisted.ReleaseSwitchApprovalState.BeforeAlbumMusicBrainzId);
            Assert.Equal("release-preferred", persisted.ReleaseSwitchApprovalState.BeforeReleaseMusicBrainzId);
            Assert.Equal("album-other", persisted.ReleaseSwitchApprovalState.AfterAlbumMusicBrainzId);
            Assert.Equal("release-candidate", persisted.ReleaseSwitchApprovalState.AfterReleaseMusicBrainzId);

            var persistedJson = ReadPersistedCaseJson(context.Store, "case-different-group-applied");
            Assert.DoesNotContain(approveAction.Url, persistedJson, StringComparison.Ordinal);
            Assert.DoesNotContain(new Uri(approveAction.Url).AbsolutePath.Split('/').Last(), persistedJson, StringComparison.Ordinal);
            Assert.DoesNotContain("token=", persistedJson, StringComparison.OrdinalIgnoreCase);

            context.AlbumInfo.Verify(service => service.GetAlbumInfo("album-other"), Times.Once);
            context.AlbumService.Verify(service => service.AddAlbum(It.Is<Album>(album => album.ForeignAlbumId == "album-other"), false), Times.Once);
            context.RefreshAlbumService.Verify(service => service.RefreshAlbumInfo(
                It.Is<Album>(album => album.Id == 29 && album.ForeignAlbumId == "album-other"),
                It.Is<List<Album>>(albums => albums.Count == 1 && albums[0].ForeignAlbumId == "album-other"),
                false), Times.Once);
            context.ReleaseService.Verify(service => service.SetMonitored(It.Is<AlbumRelease>(release => release.AlbumId == 29 && release.ForeignReleaseId == "release-candidate" && release.Monitored)), Times.Once);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_rootDirectory))
                {
                    Directory.Delete(_rootDirectory, recursive: true);
                }
            }
            catch
            {
                // best effort temp cleanup
            }
        }

        private WorkflowContext CreateWorkflowContext(
            string caseId,
            ReconciliationClassification classification,
            string winnerAlbumId,
            string winnerReleaseId,
            bool sameReleaseGroup,
            bool notificationEnableDifferentGroupApproval,
            bool executionEnableDifferentGroupApproval,
            bool initialTargetReleaseExists = true,
            bool hydratedTargetReleaseExists = false,
            Func<DateTimeOffset>? tokenClock = null,
            Func<DateTimeOffset>? executionClock = null)
        {
            tokenClock ??= () => Now;
            executionClock ??= tokenClock;

            var store = new FileReconciliationCaseStore(Path.Combine(_rootDirectory, caseId, "cases"), _logger);
            var settingsStore = new ReleaseSwitchSettingsStore(Path.Combine(_rootDirectory, caseId, "release-switch.json"), _logger);
            settingsStore.Save(new ReleaseSwitchSettings
            {
                EnableReleaseSwitchApproval = true,
                EnableDifferentGroupReleaseSwitchApproval = executionEnableDifferentGroupApproval
            });

            var releaseService = new Mock<IReleaseService>(MockBehavior.Strict);
            var albumService = new Mock<IAlbumService>(MockBehavior.Strict);
            var albumInfo = new Mock<IProvideAlbumInfo>(MockBehavior.Strict);
            var refreshAlbumService = new Mock<IRefreshAlbumService>(MockBehavior.Strict);

            var currentAlbum = new Album
            {
                Id = 17,
                ForeignAlbumId = "album-current",
                Title = "Test Album",
                Monitored = true,
                ArtistMetadataId = 42
            };
            var currentRelease = new AlbumRelease
            {
                Id = 200,
                AlbumId = 17,
                ForeignReleaseId = "release-preferred",
                Title = "Current Release",
                Monitored = true
            };
            var sameGroupTargetRelease = new AlbumRelease
            {
                Id = 201,
                AlbumId = 17,
                ForeignReleaseId = winnerReleaseId,
                Title = "Candidate Release",
                Monitored = false
            };
            var remoteAlbum = new Album
            {
                Id = 0,
                ForeignAlbumId = winnerAlbumId,
                Title = sameReleaseGroup ? "Test Album" : "Other Group Album",
                Monitored = true,
                ArtistMetadataId = 42
            };
            var addedAlbum = new Album
            {
                Id = 29,
                ForeignAlbumId = winnerAlbumId,
                Title = "Other Group Album",
                Monitored = true,
                ArtistMetadataId = 42
            };
            var hydratedTargetRelease = new AlbumRelease
            {
                Id = 401,
                AlbumId = 29,
                ForeignReleaseId = winnerReleaseId,
                Title = sameReleaseGroup ? "Candidate Release" : "Other Group Release",
                Monitored = false
            };

            albumService.Setup(service => service.GetAlbum(17)).Returns(currentAlbum);
            releaseService.Setup(service => service.GetReleasesByAlbum(17)).Returns(new List<AlbumRelease>
            {
                currentRelease,
                sameGroupTargetRelease
            });

            if (sameReleaseGroup)
            {
                releaseService.Setup(service => service.GetReleaseByForeignReleaseId(winnerReleaseId, false))
                    .Returns(initialTargetReleaseExists ? sameGroupTargetRelease : null!);
                releaseService.Setup(service => service.SetMonitored(It.IsAny<AlbumRelease>()))
                    .Returns<AlbumRelease>(release => new List<AlbumRelease>
                    {
                        new AlbumRelease
                        {
                            Id = currentRelease.Id,
                            AlbumId = currentRelease.AlbumId,
                            ForeignReleaseId = currentRelease.ForeignReleaseId,
                            Title = currentRelease.Title,
                            Monitored = string.Equals(currentRelease.ForeignReleaseId, release.ForeignReleaseId, StringComparison.Ordinal)
                        },
                        new AlbumRelease
                        {
                            Id = sameGroupTargetRelease.Id,
                            AlbumId = sameGroupTargetRelease.AlbumId,
                            ForeignReleaseId = sameGroupTargetRelease.ForeignReleaseId,
                            Title = sameGroupTargetRelease.Title,
                            Monitored = true
                        }
                    });
            }
            else
            {
                var targetLookupCount = 0;
                releaseService.Setup(service => service.GetReleaseByForeignReleaseId(winnerReleaseId, false))
                    .Returns(() =>
                    {
                        targetLookupCount++;
                        if (!hydratedTargetReleaseExists)
                        {
                            return null!;
                        }

                        return targetLookupCount == 1 ? null! : hydratedTargetRelease;
                    });
                releaseService.Setup(service => service.GetReleaseByForeignReleaseId("release-preferred", false)).Returns(currentRelease);
                releaseService.Setup(service => service.SetMonitored(It.IsAny<AlbumRelease>()))
                    .Returns<AlbumRelease>(_ => new List<AlbumRelease>
                    {
                        new AlbumRelease
                        {
                            Id = hydratedTargetRelease.Id,
                            AlbumId = hydratedTargetRelease.AlbumId,
                            ForeignReleaseId = hydratedTargetRelease.ForeignReleaseId,
                            Title = hydratedTargetRelease.Title,
                            Monitored = true
                        }
                    });
                albumInfo.Setup(service => service.GetAlbumInfo(winnerAlbumId))
                    .Returns(Tuple.Create("artist-1", remoteAlbum, new List<ArtistMetadata>()));
                albumService.Setup(service => service.GetAlbumsByArtistMetadataId(remoteAlbum.ArtistMetadataId))
                    .Returns(new List<Album>());
                if (executionEnableDifferentGroupApproval)
                {
                    albumService.Setup(service => service.AddAlbum(It.IsAny<Album>(), false)).Returns(addedAlbum);
                    refreshAlbumService.Setup(service => service.RefreshAlbumInfo(addedAlbum, It.IsAny<List<Album>>(), false)).Returns(true);
                }
            }

            var caseEntity = CreateApprovalReadyCase(caseId, classification, winnerAlbumId, winnerReleaseId, sameReleaseGroup);
            store.Save(caseEntity);

            var lookup = new Mock<ILidarrAlbumLookup>(MockBehavior.Strict);
            lookup.Setup(service => service.LookupCandidates(
                    "Test Album",
                    "Test Artist",
                    It.IsAny<ReconciliationEvidence>(),
                    "album-current"))
                .Returns(new LidarrAlbumLookupResult(
                    new[]
                    {
                        new CandidateAlbumSnapshot(
                            winnerAlbumId,
                            "Test Album",
                            "artist-1",
                            "Test Artist",
                            null,
                            new DateTime(2021, 1, 1),
                            "Album",
                            new[]
                            {
                                new CandidateReleaseSnapshot(
                                    winnerReleaseId,
                                    sameReleaseGroup ? "Candidate Release" : "Other Group Release",
                                    "Official",
                                    null,
                                    new DateTime(2021, 1, 1),
                                    1,
                                    180,
                                    Array.Empty<string>(),
                                    Array.Empty<string>(),
                                    new[] { new CandidateMediumSnapshot(1, "Disc 1", "Digital") },
                                    new[]
                                    {
                                        new CandidateTrackSnapshot(
                                            "track-1",
                                            "recording-1",
                                            "Track One",
                                            "1",
                                            1,
                                            1,
                                            180,
                                            false)
                                    })
                            },
                            new[] { sameReleaseGroup ? CandidateLookupPath.CurrentReleaseGroup : CandidateLookupPath.RecordingIds })
                    },
                    new[]
                    {
                        new LidarrAlbumLookupAttempt(
                            sameReleaseGroup ? CandidateLookupPath.CurrentReleaseGroup : CandidateLookupPath.RecordingIds,
                            true,
                            1,
                            1,
                            1,
                            1,
                            null)
                    }));

            var evaluator = new ReconciliationCandidateEvaluator(store, lookup.Object, new DeterministicReleaseScorer(), _logger);
            var approvalService = new ReleaseSwitchApprovalService(
                store,
                evaluator,
                settingsStore,
                albumService.Object,
                releaseService.Object,
                albumInfo.Object,
                refreshAlbumService.Object,
                _logger,
                executionClock);
            var tokenService = CreateTokenService(tokenClock);
            var notificationBuilder = new ReconciliationNotificationBuilder(tokenService, tokenClock);
            var mapper = new ReconciliationOperatorActionMapper(store, tokenService, _logger, executionClock, approvalService);

            return new WorkflowContext(store, caseEntity, albumService, releaseService, albumInfo, refreshAlbumService, notificationBuilder, mapper);
        }

        private OperatorActionTokenService CreateTokenService(Func<DateTimeOffset> clock)
        {
            var provider = DataProtectionProvider.Create(
                new DirectoryInfo(_keyDirectory),
                configuration => configuration.SetApplicationName("release-switch-workflow-tests"));

            return new OperatorActionTokenService(provider, _logger, clock);
        }

        private static ReconciliationCase CreateApprovalReadyCase(
            string caseId,
            ReconciliationClassification classification,
            string winnerAlbumId,
            string winnerReleaseId,
            bool sameReleaseGroup)
        {
            return new ReconciliationCase(
                sourceSeam: "capture",
                phase: "captured",
                capturedIdentifiers: new Dictionary<string, string?>
                {
                    ["artist"] = "Test Artist",
                    ["artistName"] = "Test Artist",
                    ["album"] = "Test Album",
                    ["albumTitle"] = "Test Album",
                    ["albumMusicBrainzId"] = "album-current"
                },
                structuralEvidence: new ReconciliationEvidence
                {
                    TrackCount = 1,
                    PreferredRelease = new PreferredReleaseReconciliationEvidence
                    {
                        ReleaseMusicBrainzId = "release-preferred"
                    },
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
                },
                caseId: caseId,
                capturedAtUtc: Now.AddMinutes(-30),
                updatedAtUtc: Now.AddMinutes(-30),
                evaluationResult: new ReconciliationEvaluationResult
                {
                    Classification = classification,
                    WinnerAlbumMusicBrainzId = winnerAlbumId,
                    WinnerReleaseMusicBrainzId = winnerReleaseId,
                    ScoringVersion = ReconciliationEvaluationResult.CurrentScoringVersion,
                    EvaluatedAtUtc = Now.AddMinutes(-20),
                    RankedCandidates = new List<RankedCandidateRelease>
                    {
                        new()
                        {
                            Rank = 1,
                            AlbumMusicBrainzId = winnerAlbumId,
                            AlbumTitle = "Test Album",
                            ArtistMusicBrainzId = "artist-1",
                            ArtistName = "Test Artist",
                            ReleaseMusicBrainzId = winnerReleaseId,
                            ReleaseTitle = sameReleaseGroup ? "Candidate Release" : "Other Group Release",
                            SameReleaseGroup = sameReleaseGroup,
                            HasStrongIdentitySignal = true,
                            TitleOnlyCandidate = false,
                            LookupPaths = new List<CandidateLookupPath>
                            {
                                sameReleaseGroup ? CandidateLookupPath.CurrentReleaseGroup : CandidateLookupPath.RecordingIds
                            }
                        }
                    }
                });
        }

        private static string ReadPersistedCaseJson(FileReconciliationCaseStore store, string caseId)
        {
            return File.ReadAllText(Path.Combine(store.StorageDirectory, $"{caseId}.json"));
        }

        private static ReconciliationNotificationSettings CreateNotificationSettings()
        {
            return new ReconciliationNotificationSettings
            {
                PublicBaseUrl = "https://lidarr.example",
                HarmonySearchBaseUrl = "https://api.lidarr.audio/api/v0.4/search",
                Discord = new ReconciliationNotificationSettings.DiscordNotificationSettings
                {
                    Enabled = true,
                    WebHookUrl = "https://discord.example/webhook"
                },
                Telegram = new ReconciliationNotificationSettings.TelegramNotificationSettings
                {
                    Enabled = true,
                    BotToken = "bot-token",
                    ChatId = "12345"
                }
            };
        }

        private sealed record WorkflowContext(
            FileReconciliationCaseStore Store,
            ReconciliationCase Case,
            Mock<IAlbumService> AlbumService,
            Mock<IReleaseService> ReleaseService,
            Mock<IProvideAlbumInfo> AlbumInfo,
            Mock<IRefreshAlbumService> RefreshAlbumService,
            ReconciliationNotificationBuilder NotificationBuilder,
            ReconciliationOperatorActionMapper Mapper) : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }
}
