using System;
using System.Collections.Generic;
using System.IO;
using Moq;
using NLog;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.Music;
using NzbDrone.Core.Plugins.ReleaseReconciler.Actions;
using NzbDrone.Core.Plugins.ReleaseReconciler.Candidates;
using NzbDrone.Core.Plugins.ReleaseReconciler.Cases;
using NzbDrone.Core.Plugins.ReleaseReconciler.Switching;
using Xunit;

namespace NzbDrone.Core.Plugins.ReleaseReconciler.Test.Switching
{
    public sealed class ReleaseSwitchApprovalServiceFixture : IDisposable
    {
        private static readonly DateTimeOffset CapturedAtUtc = new(2026, 6, 14, 12, 0, 0, TimeSpan.Zero);
        private static readonly DateTimeOffset Now = new(2026, 6, 14, 22, 0, 0, TimeSpan.Zero);
        private readonly string _rootDirectory;
        private readonly Logger _logger;

        public ReleaseSwitchApprovalServiceFixture()
        {
            _rootDirectory = Path.Combine(Path.GetTempPath(), $"release-switch-approval-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_rootDirectory);
            _logger = LogManager.GetLogger(nameof(ReleaseSwitchApprovalServiceFixture));
        }

        [Fact]
        public void should_apply_same_group_approval_and_persist_before_after_audit_details()
        {
            using var context = CreateContext();
            var service = context.CreateService();

            var result = service.Approve(
                context.InitialSnapshot,
                context.CreateValidation("album-current", "release-candidate", ReconciliationClassification.BetterExistingRelease));

            Assert.Equal(ReconciliationOperatorActionOutcomes.Applied, result.Result);
            Assert.Contains("Approved same-group release switch", result.Message, StringComparison.Ordinal);

            var persisted = context.Store.Get(context.InitialSnapshot.CaseId)!;
            var audit = persisted.ReleaseSwitchApprovalState;
            Assert.NotNull(audit);
            Assert.Equal(ReconciliationOperatorActionOutcomes.Applied, audit.Outcome);
            Assert.Equal(Now, audit.AttemptedAtUtc);
            Assert.Equal(Now, audit.AppliedAtUtc);
            Assert.Null(audit.RefusedAtUtc);
            Assert.Equal("album-current", audit.BeforeAlbumMusicBrainzId);
            Assert.Equal("release-preferred", audit.BeforeReleaseMusicBrainzId);
            Assert.Equal("album-current", audit.AfterAlbumMusicBrainzId);
            Assert.Equal("release-candidate", audit.AfterReleaseMusicBrainzId);
            Assert.Equal("BetterExistingRelease", audit.Classification);
            Assert.Equal(ReconciliationEvaluationResult.CurrentScoringVersion, audit.ScoringVersion);
            Assert.True(audit.CandidateSameReleaseGroup);
            Assert.True(audit.CandidateHasStrongIdentitySignal);
            Assert.False(audit.CandidateTitleOnly);
            Assert.Equal("discord-webhook", audit.Transport);
            Assert.Equal("token-hash-1", audit.ActionTokenHash);
            Assert.NotNull(persisted.EvaluationResult);

            context.ReleaseService.Verify(service => service.SetMonitored(It.Is<AlbumRelease>(release =>
                release.AlbumId == 17 &&
                release.Monitored &&
                release.ForeignReleaseId == "release-candidate")), Times.Once);
        }

        [Fact]
        public void should_refuse_when_reevaluated_winner_no_longer_matches_signed_target()
        {
            using var context = CreateContext();
            var service = context.CreateService();

            var result = service.Approve(
                context.InitialSnapshot,
                context.CreateValidation("album-current", "release-stale", ReconciliationClassification.BetterExistingRelease));

            Assert.Equal(ReconciliationOperatorActionOutcomes.Refused, result.Result);
            Assert.Contains("no longer matches", result.Message, StringComparison.Ordinal);

            var audit = context.Store.Get(context.InitialSnapshot.CaseId)!.ReleaseSwitchApprovalState;
            Assert.NotNull(audit);
            Assert.Equal(ReconciliationOperatorActionOutcomes.Refused, audit.Outcome);
            Assert.Equal("release-stale", audit.RequestedReleaseMusicBrainzId);
            Assert.Equal(Now, audit.RefusedAtUtc);
            Assert.Contains("no longer matches", audit.RefusalSummary, StringComparison.Ordinal);
            context.ReleaseService.Verify(service => service.SetMonitored(It.IsAny<AlbumRelease>()), Times.Never);
        }

        [Fact]
        public void should_refuse_when_target_release_is_missing()
        {
            using var context = CreateContext(initialTargetReleaseExists: false);
            var service = context.CreateService();

            var result = service.Approve(
                context.InitialSnapshot,
                context.CreateValidation("album-current", "release-candidate", ReconciliationClassification.BetterExistingRelease));

            Assert.Equal(ReconciliationOperatorActionOutcomes.Refused, result.Result);
            Assert.Contains("could not be found", result.Message, StringComparison.Ordinal);
            Assert.Equal(ReconciliationOperatorActionOutcomes.Refused, context.Store.Get(context.InitialSnapshot.CaseId)!.ReleaseSwitchApprovalState!.Outcome);
            context.ReleaseService.Verify(service => service.SetMonitored(It.IsAny<AlbumRelease>()), Times.Never);
        }

        [Fact]
        public void should_refuse_when_feature_flag_is_disabled()
        {
            using var context = CreateContext(enableApproval: false);
            var service = context.CreateService();

            var result = service.Approve(
                context.InitialSnapshot,
                context.CreateValidation("album-current", "release-candidate", ReconciliationClassification.BetterExistingRelease));

            Assert.Equal(ReconciliationOperatorActionOutcomes.Refused, result.Result);
            Assert.Contains("disabled", result.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(ReconciliationOperatorActionOutcomes.Refused, context.Store.Get(context.InitialSnapshot.CaseId)!.ReleaseSwitchApprovalState!.Outcome);
            context.ReleaseService.Verify(service => service.SetMonitored(It.IsAny<AlbumRelease>()), Times.Never);
        }

        [Fact]
        public void should_return_already_applied_when_target_release_is_already_monitored()
        {
            using var context = CreateContext(currentMonitoredReleaseId: "release-candidate");
            var service = context.CreateService();

            var result = service.Approve(
                context.InitialSnapshot,
                context.CreateValidation("album-current", "release-candidate", ReconciliationClassification.BetterExistingRelease));

            Assert.Equal(ReconciliationOperatorActionOutcomes.AlreadyApplied, result.Result);
            Assert.Contains("already applied", result.Message, StringComparison.OrdinalIgnoreCase);

            var audit = context.Store.Get(context.InitialSnapshot.CaseId)!.ReleaseSwitchApprovalState;
            Assert.NotNull(audit);
            Assert.Equal(ReconciliationOperatorActionOutcomes.AlreadyApplied, audit.Outcome);
            Assert.Equal("release-candidate", audit.AfterReleaseMusicBrainzId);
            Assert.Null(audit.AppliedAtUtc);
            context.ReleaseService.Verify(service => service.SetMonitored(It.IsAny<AlbumRelease>()), Times.Never);
        }

        [Fact]
        public void should_refuse_different_group_when_policy_is_disabled_even_with_valid_token()
        {
            using var context = CreateContext(
                winnerAlbumId: "album-other",
                sameReleaseGroup: false,
                lookupPath: CandidateLookupPath.RecordingIds,
                enableDifferentGroupApproval: false,
                initialTargetReleaseExists: false);
            var service = context.CreateService();

            var result = service.Approve(
                context.InitialSnapshot,
                context.CreateValidation("album-other", "release-candidate", ReconciliationClassification.DifferentReleaseGroup));

            Assert.Equal(ReconciliationOperatorActionOutcomes.Refused, result.Result);
            Assert.Contains("disabled by policy", result.Message, StringComparison.OrdinalIgnoreCase);

            var audit = context.Store.Get(context.InitialSnapshot.CaseId)!.ReleaseSwitchApprovalState;
            Assert.NotNull(audit);
            Assert.Equal("album-other", audit.RequestedAlbumMusicBrainzId);
            Assert.Equal(ReconciliationOperatorActionOutcomes.Refused, audit.Outcome);
            context.AlbumInfo.Verify(service => service.GetAlbumInfo(It.IsAny<string>()), Times.Never);
            context.ReleaseService.Verify(service => service.SetMonitored(It.IsAny<AlbumRelease>()), Times.Never);
        }

        [Fact]
        public void should_apply_guarded_different_group_switch_and_persist_before_after_album_and_release_ids()
        {
            using var context = CreateContext(
                winnerAlbumId: "album-other",
                sameReleaseGroup: false,
                lookupPath: CandidateLookupPath.RecordingIds,
                enableDifferentGroupApproval: true,
                initialTargetReleaseExists: false,
                hydratedTargetReleaseExists: true);
            var service = context.CreateService();

            var result = service.Approve(
                context.InitialSnapshot,
                context.CreateValidation("album-other", "release-candidate", ReconciliationClassification.DifferentReleaseGroup));

            Assert.Equal(ReconciliationOperatorActionOutcomes.Applied, result.Result);
            Assert.Contains("Approved different-group release switch", result.Message, StringComparison.Ordinal);

            var audit = context.Store.Get(context.InitialSnapshot.CaseId)!.ReleaseSwitchApprovalState;
            Assert.NotNull(audit);
            Assert.Equal(ReconciliationOperatorActionOutcomes.Applied, audit.Outcome);
            Assert.Equal("album-current", audit.BeforeAlbumMusicBrainzId);
            Assert.Equal("release-preferred", audit.BeforeReleaseMusicBrainzId);
            Assert.Equal("album-other", audit.AfterAlbumMusicBrainzId);
            Assert.Equal("release-candidate", audit.AfterReleaseMusicBrainzId);
            Assert.Equal(Now, audit.AppliedAtUtc);
            Assert.Null(audit.RefusedAtUtc);

            context.AlbumInfo.Verify(service => service.GetAlbumInfo("album-other"), Times.Once);
            context.AlbumService.Verify(service => service.AddAlbum(It.Is<Album>(album => album.ForeignAlbumId == "album-other"), false), Times.Once);
            context.RefreshAlbumService.Verify(service => service.RefreshAlbumInfo(
                It.Is<Album>(album => album.Id == 29 && album.ForeignAlbumId == "album-other"),
                It.Is<List<Album>>(albums => albums.Count == 1 && albums[0].ForeignAlbumId == "album-other"),
                false), Times.Once);
            context.ReleaseService.Verify(service => service.SetMonitored(It.Is<AlbumRelease>(release => release.AlbumId == 29 && release.ForeignReleaseId == "release-candidate" && release.Monitored)), Times.Once);
            context.AlbumService.Verify(service => service.DeleteAlbum(It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>()), Times.Never);
        }

        [Fact]
        public void should_refuse_different_group_when_target_album_already_exists_before_mutation()
        {
            using var context = CreateContext(
                winnerAlbumId: "album-other",
                sameReleaseGroup: false,
                lookupPath: CandidateLookupPath.RecordingIds,
                enableDifferentGroupApproval: true,
                initialTargetReleaseExists: true,
                duplicateTargetAlbumExists: true);
            var service = context.CreateService();

            var result = service.Approve(
                context.InitialSnapshot,
                context.CreateValidation("album-other", "release-candidate", ReconciliationClassification.DifferentReleaseGroup));

            Assert.Equal(ReconciliationOperatorActionOutcomes.Refused, result.Result);
            Assert.Contains("already exists in Lidarr", result.Message, StringComparison.Ordinal);

            var audit = context.Store.Get(context.InitialSnapshot.CaseId)!.ReleaseSwitchApprovalState;
            Assert.NotNull(audit);
            Assert.Equal("album-other", audit.AfterAlbumMusicBrainzId);
            Assert.Equal(ReconciliationOperatorActionOutcomes.Refused, audit.Outcome);
            context.AlbumInfo.Verify(service => service.GetAlbumInfo(It.IsAny<string>()), Times.Never);
            context.AlbumService.Verify(service => service.AddAlbum(It.IsAny<Album>(), It.IsAny<bool>()), Times.Never);
            context.ReleaseService.Verify(service => service.SetMonitored(It.IsAny<AlbumRelease>()), Times.Never);
        }

        [Fact]
        public void should_refuse_different_group_when_hydrated_album_mismatches_target()
        {
            using var context = CreateContext(
                winnerAlbumId: "album-other",
                sameReleaseGroup: false,
                lookupPath: CandidateLookupPath.RecordingIds,
                enableDifferentGroupApproval: true,
                initialTargetReleaseExists: false,
                hydratedAlbumMismatch: true);
            var service = context.CreateService();

            var result = service.Approve(
                context.InitialSnapshot,
                context.CreateValidation("album-other", "release-candidate", ReconciliationClassification.DifferentReleaseGroup));

            Assert.Equal(ReconciliationOperatorActionOutcomes.Refused, result.Result);
            Assert.Contains("did not match the approved winner", result.Message, StringComparison.Ordinal);
            context.AlbumInfo.Verify(service => service.GetAlbumInfo("album-other"), Times.Once);
            context.AlbumService.Verify(service => service.AddAlbum(It.IsAny<Album>(), It.IsAny<bool>()), Times.Never);
            context.ReleaseService.Verify(service => service.SetMonitored(It.IsAny<AlbumRelease>()), Times.Never);
        }

        [Fact]
        public void should_refuse_different_group_when_refresh_fails_and_rollback_added_album()
        {
            using var context = CreateContext(
                winnerAlbumId: "album-other",
                sameReleaseGroup: false,
                lookupPath: CandidateLookupPath.RecordingIds,
                enableDifferentGroupApproval: true,
                initialTargetReleaseExists: false,
                differentGroupRefreshSucceeds: false);
            var service = context.CreateService();

            var result = service.Approve(
                context.InitialSnapshot,
                context.CreateValidation("album-other", "release-candidate", ReconciliationClassification.DifferentReleaseGroup));

            Assert.Equal(ReconciliationOperatorActionOutcomes.Refused, result.Result);
            Assert.Contains("refresh failed", result.Message, StringComparison.OrdinalIgnoreCase);
            context.AlbumService.Verify(service => service.AddAlbum(It.IsAny<Album>(), false), Times.Once);
            context.AlbumService.Verify(service => service.DeleteAlbum(29, false, false), Times.Once);
            context.ReleaseService.Verify(service => service.SetMonitored(It.IsAny<AlbumRelease>()), Times.Never);
        }

        public void Dispose()
        {
            if (Directory.Exists(_rootDirectory))
            {
                Directory.Delete(_rootDirectory, recursive: true);
            }
        }

        private TestContext CreateContext(
            bool enableApproval = true,
            bool enableDifferentGroupApproval = false,
            string winnerAlbumId = "album-current",
            string winnerReleaseId = "release-candidate",
            bool sameReleaseGroup = true,
            CandidateLookupPath lookupPath = CandidateLookupPath.CurrentReleaseGroup,
            string currentMonitoredReleaseId = "release-preferred",
            bool initialTargetReleaseExists = true,
            bool hydratedTargetReleaseExists = false,
            bool duplicateTargetAlbumExists = false,
            bool hydratedAlbumMismatch = false,
            bool differentGroupRefreshSucceeds = true)
        {
            var caseStore = new FileReconciliationCaseStore(Path.Combine(_rootDirectory, Guid.NewGuid().ToString("N"), "cases"), _logger);
            var settingsStore = new ReleaseSwitchSettingsStore(Path.Combine(_rootDirectory, Guid.NewGuid().ToString("N"), "release-switch.json"), _logger);
            settingsStore.Save(new ReleaseSwitchSettings
            {
                EnableReleaseSwitchApproval = enableApproval,
                EnableDifferentGroupReleaseSwitchApproval = enableDifferentGroupApproval
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
                ForeignReleaseId = currentMonitoredReleaseId,
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
            var duplicateTargetAlbum = new Album
            {
                Id = 31,
                ForeignAlbumId = winnerAlbumId,
                Title = "Other Group Album",
                Monitored = true,
                ArtistMetadataId = 42
            };
            var duplicateTargetRelease = new AlbumRelease
            {
                Id = 301,
                AlbumId = 31,
                ForeignReleaseId = winnerReleaseId,
                Title = "Other Group Release",
                Monitored = false
            };
            var remoteAlbum = new Album
            {
                Id = 0,
                ForeignAlbumId = hydratedAlbumMismatch ? $"{winnerAlbumId}-mismatch" : winnerAlbumId,
                Title = "Other Group Album",
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
            if (duplicateTargetAlbumExists)
            {
                albumService.Setup(service => service.GetAlbum(31)).Returns(duplicateTargetAlbum);
            }

            releaseService.Setup(service => service.GetReleasesByAlbum(17))
                .Returns(new List<AlbumRelease>
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
                        if (duplicateTargetAlbumExists && initialTargetReleaseExists)
                        {
                            return duplicateTargetRelease;
                        }

                        if (!hydratedTargetReleaseExists)
                        {
                            return null!;
                        }

                        return targetLookupCount == 1 ? null! : hydratedTargetRelease;
                    });
                releaseService.Setup(service => service.GetReleaseByForeignReleaseId(currentMonitoredReleaseId, false))
                    .Returns(currentRelease);
                releaseService.Setup(service => service.SetMonitored(It.IsAny<AlbumRelease>()))
                    .Returns<AlbumRelease>(release => new List<AlbumRelease>
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
                    .Returns(duplicateTargetAlbumExists ? new List<Album> { duplicateTargetAlbum } : new List<Album>());
                if (!duplicateTargetAlbumExists && !hydratedAlbumMismatch)
                {
                    albumService.Setup(service => service.AddAlbum(It.IsAny<Album>(), false)).Returns(addedAlbum);
                    refreshAlbumService.Setup(service => service.RefreshAlbumInfo(addedAlbum, It.IsAny<List<Album>>(), false))
                        .Returns(differentGroupRefreshSucceeds);
                    if (!differentGroupRefreshSucceeds || hydratedTargetReleaseExists)
                    {
                        albumService.Setup(service => service.DeleteAlbum(29, false, false));
                    }
                }
            }

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
                            new[] { lookupPath })
                    },
                    new[]
                    {
                        new LidarrAlbumLookupAttempt(lookupPath, true, 1, 1, 1, 1, null)
                    }));

            var initialSnapshot = caseStore.Save(new ReconciliationCase(
                sourceSeam: "fixture",
                phase: "captured",
                capturedIdentifiers: new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["album"] = "Test Album",
                    ["albumTitle"] = "Test Album",
                    ["artist"] = "Test Artist",
                    ["artistName"] = "Test Artist",
                    ["albumMusicBrainzId"] = "album-current"
                },
                structuralEvidence: new ReconciliationEvidence
                {
                    TrackCount = 1,
                    PreferredRelease = new PreferredReleaseReconciliationEvidence
                    {
                        ReleaseMusicBrainzId = "release-preferred"
                    },
                    Tracks = new List<ReconciliationTrackEvidence>
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
                caseId: "case-1",
                capturedAtUtc: CapturedAtUtc,
                updatedAtUtc: CapturedAtUtc));

            var evaluator = new ReconciliationCandidateEvaluator(caseStore, lookup.Object, new DeterministicReleaseScorer(), _logger);
            return new TestContext(
                caseStore,
                settingsStore,
                evaluator,
                albumService,
                releaseService,
                albumInfo,
                refreshAlbumService,
                initialSnapshot,
                _logger);
        }

        private sealed class TestContext : IDisposable
        {
            private readonly Logger _logger;

            public TestContext(
                FileReconciliationCaseStore store,
                ReleaseSwitchSettingsStore settingsStore,
                ReconciliationCandidateEvaluator evaluator,
                Mock<IAlbumService> albumService,
                Mock<IReleaseService> releaseService,
                Mock<IProvideAlbumInfo> albumInfo,
                Mock<IRefreshAlbumService> refreshAlbumService,
                ReconciliationCaseSnapshot initialSnapshot,
                Logger logger)
            {
                Store = store;
                SettingsStore = settingsStore;
                Evaluator = evaluator;
                AlbumService = albumService;
                ReleaseService = releaseService;
                AlbumInfo = albumInfo;
                RefreshAlbumService = refreshAlbumService;
                InitialSnapshot = initialSnapshot;
                _logger = logger;
            }

            public FileReconciliationCaseStore Store { get; }
            public ReleaseSwitchSettingsStore SettingsStore { get; }
            public ReconciliationCandidateEvaluator Evaluator { get; }
            public Mock<IAlbumService> AlbumService { get; }
            public Mock<IReleaseService> ReleaseService { get; }
            public Mock<IProvideAlbumInfo> AlbumInfo { get; }
            public Mock<IRefreshAlbumService> RefreshAlbumService { get; }
            public ReconciliationCaseSnapshot InitialSnapshot { get; }

            public ReleaseSwitchApprovalService CreateService()
            {
                return new ReleaseSwitchApprovalService(
                    Store,
                    Evaluator,
                    SettingsStore,
                    AlbumService.Object,
                    ReleaseService.Object,
                    AlbumInfo.Object,
                    RefreshAlbumService.Object,
                    _logger,
                    () => Now);
            }

            public ReconciliationOperatorActionTokenValidationResult CreateValidation(
                string albumMusicBrainzId,
                string releaseMusicBrainzId,
                ReconciliationClassification classification)
            {
                return new ReconciliationOperatorActionTokenValidationResult
                {
                    Succeeded = true,
                    Result = ReconciliationOperatorActionOutcomes.Applied,
                    CaseId = InitialSnapshot.CaseId,
                    Action = ReconciliationOperatorActionKinds.ApproveSwitch,
                    Nonce = "nonce-1",
                    Transport = "discord-webhook",
                    ActionTokenHash = "token-hash-1",
                    TokenFingerprintPrefix = "fingerprint-1",
                    ExpiresAtUtc = Now.AddHours(1),
                    Payload = new ReconciliationOperatorActionTokenPayload
                    {
                        CaseId = InitialSnapshot.CaseId,
                        Action = ReconciliationOperatorActionKinds.ApproveSwitch,
                        Nonce = "nonce-1",
                        ExpiresAtUtc = Now.AddHours(1),
                        Transport = "discord-webhook",
                        ReleaseSwitchTarget = new ReconciliationReleaseSwitchTarget
                        {
                            AlbumMusicBrainzId = albumMusicBrainzId,
                            ReleaseMusicBrainzId = releaseMusicBrainzId,
                            Classification = classification.ToString(),
                            ScoringVersion = ReconciliationEvaluationResult.CurrentScoringVersion
                        }
                    }
                };
            }

            public void Dispose()
            {
            }
        }
    }
}
