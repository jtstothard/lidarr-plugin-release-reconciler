using System;
using System.Collections.Generic;
using System.Linq;
using Moq;
using NLog;
using NzbDrone.Core.Plugins.ReleaseReconciler.Candidates;
using NzbDrone.Core.Plugins.ReleaseReconciler.Cases;
using Xunit;

namespace NzbDrone.Core.Plugins.ReleaseReconciler.Test.Candidates
{
    public sealed class ReconciliationCandidateEvaluatorFixture
    {
        private static readonly DateTimeOffset CapturedAtUtc = new(2026, 6, 14, 12, 0, 0, TimeSpan.Zero);
        private static readonly Logger Logger = LogManager.GetLogger(nameof(ReconciliationCandidateEvaluatorFixture));

        [Fact]
        public void should_persist_safe_same_group_winner_with_ranked_breakdown_and_lookup_attempts()
        {
            var (store, savedCase) = CreateStore();
            var lookup = new Mock<ILidarrAlbumLookup>(MockBehavior.Strict);
            lookup
                .Setup(service => service.LookupCandidates("Album Title", "Artist Name", It.IsAny<ReconciliationEvidence>(), "album-current"))
                .Returns(new LidarrAlbumLookupResult(
                    new[]
                    {
                        CreateAlbum(
                            albumMusicBrainzId: "album-current",
                            releases: new[]
                            {
                                CreateRelease(
                                    releaseMusicBrainzId: "release-preferred",
                                    releaseTrackIds: new[] { "release-track-1", "release-track-2" },
                                    recordingIds: new[] { "recording-1", "recording-2" },
                                    labels: new[] { "Label A" },
                                    lookupPaths: new[] { CandidateLookupPath.CurrentReleaseGroup })
                            },
                            lookupPaths: new[] { CandidateLookupPath.CurrentReleaseGroup })
                    },
                    new[]
                    {
                        new LidarrAlbumLookupAttempt(CandidateLookupPath.CurrentReleaseGroup, true, 1, 1, 1, 0, null),
                        new LidarrAlbumLookupAttempt(CandidateLookupPath.TitleArtistFallback, false, 0, 0, 0, 0, "Structural lookup already produced deterministic candidates.")
                    }));

            var subject = new ReconciliationCandidateEvaluator(store.Object, lookup.Object, new DeterministicReleaseScorer(), Logger);

            var persisted = subject.Evaluate(BuildSnapshot(albumMusicBrainzId: "album-current", releaseMusicBrainzId: "release-preferred"));

            Assert.NotNull(savedCase.Value);
            Assert.Equal("evaluated", savedCase.Value!.Phase);
            Assert.NotNull(savedCase.Value.EvaluationResult);
            Assert.Equal(ReconciliationClassification.BetterExistingRelease, persisted.EvaluationResult!.Classification);
            Assert.Null(persisted.EvaluationResult.RefusalReason);
            Assert.Equal("album-current", persisted.EvaluationResult.WinnerAlbumMusicBrainzId);
            Assert.Equal("release-preferred", persisted.EvaluationResult.WinnerReleaseMusicBrainzId);
            Assert.Equal(2, persisted.EvaluationResult.LookupAttempts.Count);
            Assert.Single(persisted.EvaluationResult.RankedCandidates);
            Assert.Equal(2, persisted.EvaluationResult.RankedCandidates[0].ScoreBreakdown.ReleaseTrackOverlapCount);
            Assert.Equal(2, persisted.EvaluationResult.RankedCandidates[0].ScoreBreakdown.RecordingOverlapCount);
        }

        [Fact]
        public void should_refuse_ambiguous_near_ties_even_when_structural_signals_exist()
        {
            var (store, _) = CreateStore();
            var lookup = new Mock<ILidarrAlbumLookup>(MockBehavior.Strict);
            lookup
                .Setup(service => service.LookupCandidates("Album Title", "Artist Name", It.IsAny<ReconciliationEvidence>(), "album-current"))
                .Returns(new LidarrAlbumLookupResult(
                    new[]
                    {
                        CreateAlbum(
                            albumMusicBrainzId: "album-a",
                            releases: new[]
                            {
                                CreateRelease(
                                    releaseMusicBrainzId: "release-a",
                                    releaseTrackIds: new[] { "release-track-1", "release-track-2" },
                                    recordingIds: new[] { "recording-1", "recording-2" },
                                    labels: new[] { "Label A" },
                                    lookupPaths: new[] { CandidateLookupPath.RecordingIds })
                            },
                            lookupPaths: new[] { CandidateLookupPath.RecordingIds }),
                        CreateAlbum(
                            albumMusicBrainzId: "album-b",
                            releases: new[]
                            {
                                CreateRelease(
                                    releaseMusicBrainzId: "release-b",
                                    releaseTrackIds: new[] { "release-track-1", "release-track-2" },
                                    recordingIds: new[] { "recording-1", "recording-2" },
                                    labels: new[] { "Label A" },
                                    lookupPaths: new[] { CandidateLookupPath.RecordingIds })
                            },
                            lookupPaths: new[] { CandidateLookupPath.RecordingIds })
                    },
                    new[]
                    {
                        new LidarrAlbumLookupAttempt(CandidateLookupPath.RecordingIds, true, 2, 2, 2, 0, null)
                    }));

            var subject = new ReconciliationCandidateEvaluator(store.Object, lookup.Object, new DeterministicReleaseScorer(), Logger);

            var persisted = subject.Evaluate(BuildSnapshot(albumMusicBrainzId: "album-current", releaseMusicBrainzId: null));

            Assert.Equal(ReconciliationClassification.NoSafeMatch, persisted.EvaluationResult!.Classification);
            Assert.Equal(ReconciliationRefusalReason.AmbiguousWinnerMargin, persisted.EvaluationResult.RefusalReason);
            Assert.Equal(2, persisted.EvaluationResult.RankedCandidates.Count);
            Assert.Null(persisted.EvaluationResult.WinnerReleaseMusicBrainzId);
        }

        [Fact]
        public void should_classify_same_group_candidates_without_strong_release_signals_as_missing_release()
        {
            var (store, _) = CreateStore();
            var lookup = new Mock<ILidarrAlbumLookup>(MockBehavior.Strict);
            lookup
                .Setup(service => service.LookupCandidates("Album Title", "Artist Name", It.IsAny<ReconciliationEvidence>(), "album-current"))
                .Returns(new LidarrAlbumLookupResult(
                    new[]
                    {
                        CreateAlbum(
                            albumMusicBrainzId: "album-current",
                            releases: new[]
                            {
                                CreateRelease(
                                    releaseMusicBrainzId: "release-missing",
                                    releaseTrackIds: new[] { "release-track-x", "release-track-y" },
                                    recordingIds: new[] { "recording-x", "recording-y" },
                                    labels: new[] { "Label B" },
                                    lookupPaths: new[] { CandidateLookupPath.CurrentReleaseGroup })
                            },
                            lookupPaths: new[] { CandidateLookupPath.CurrentReleaseGroup })
                    },
                    new[]
                    {
                        new LidarrAlbumLookupAttempt(CandidateLookupPath.CurrentReleaseGroup, true, 1, 1, 1, 0, null)
                    }));

            var subject = new ReconciliationCandidateEvaluator(store.Object, lookup.Object, new DeterministicReleaseScorer(), Logger);

            var persisted = subject.Evaluate(BuildSnapshot(albumMusicBrainzId: "album-current", releaseMusicBrainzId: null));

            Assert.Equal(ReconciliationClassification.MissingReleaseInCurrentGroup, persisted.EvaluationResult!.Classification);
            Assert.Equal(ReconciliationRefusalReason.SameReleaseGroupWithoutStrongReleaseMatch, persisted.EvaluationResult.RefusalReason);
            Assert.Null(persisted.EvaluationResult.WinnerAlbumMusicBrainzId);
            Assert.Null(persisted.EvaluationResult.WinnerReleaseMusicBrainzId);
        }

        [Fact]
        public void should_refuse_title_only_fallback_matches_without_structural_support()
        {
            var (store, _) = CreateStore();
            var lookup = new Mock<ILidarrAlbumLookup>(MockBehavior.Strict);
            lookup
                .Setup(service => service.LookupCandidates("Album Title", "Artist Name", It.IsAny<ReconciliationEvidence>(), "album-current"))
                .Returns(new LidarrAlbumLookupResult(
                    new[]
                    {
                        CreateAlbum(
                            albumMusicBrainzId: "album-title-only",
                            releases: new[]
                            {
                                CreateRelease(
                                    releaseMusicBrainzId: "release-title-only",
                                    releaseTrackIds: new[] { "release-track-x", "release-track-y" },
                                    recordingIds: new[] { "recording-x", "recording-y" },
                                    labels: new[] { "Label X" },
                                    lookupPaths: new[] { CandidateLookupPath.TitleArtistFallback })
                            },
                            lookupPaths: new[] { CandidateLookupPath.TitleArtistFallback })
                    },
                    new[]
                    {
                        new LidarrAlbumLookupAttempt(CandidateLookupPath.RecordingIds, true, 2, 0, 0, 0, "Lookup completed without returning any candidate albums."),
                        new LidarrAlbumLookupAttempt(CandidateLookupPath.TitleArtistFallback, true, 1, 1, 1, 0, null)
                    }));

            var subject = new ReconciliationCandidateEvaluator(store.Object, lookup.Object, new DeterministicReleaseScorer(), Logger);

            var persisted = subject.Evaluate(BuildSnapshot(albumMusicBrainzId: "album-current", releaseMusicBrainzId: null));

            Assert.Equal(ReconciliationClassification.NoSafeMatch, persisted.EvaluationResult!.Classification);
            Assert.Equal(ReconciliationRefusalReason.TitleOnlyMatchNotAllowed, persisted.EvaluationResult.RefusalReason);
            Assert.True(persisted.EvaluationResult.UsedTitleArtistFallback);
            Assert.True(persisted.EvaluationResult.RankedCandidates[0].TitleOnlyCandidate);
        }

        [Fact]
        public void should_classify_empty_candidate_sets_as_no_safe_match()
        {
            var (store, _) = CreateStore();
            var lookup = new Mock<ILidarrAlbumLookup>(MockBehavior.Strict);
            lookup
                .Setup(service => service.LookupCandidates("Album Title", "Artist Name", It.IsAny<ReconciliationEvidence>(), "album-current"))
                .Returns(new LidarrAlbumLookupResult(
                    Array.Empty<CandidateAlbumSnapshot>(),
                    new[]
                    {
                        new LidarrAlbumLookupAttempt(CandidateLookupPath.RecordingIds, true, 2, 0, 0, 0, "Lookup completed without returning any candidate albums."),
                        new LidarrAlbumLookupAttempt(CandidateLookupPath.TitleArtistFallback, false, 0, 0, 0, 0, "Structural lookup returned nothing and fallback was suppressed for this scenario.")
                    }));

            var subject = new ReconciliationCandidateEvaluator(store.Object, lookup.Object, new DeterministicReleaseScorer(), Logger);

            var persisted = subject.Evaluate(BuildSnapshot(albumMusicBrainzId: "album-current", releaseMusicBrainzId: null));

            Assert.Equal(ReconciliationClassification.NoSafeMatch, persisted.EvaluationResult!.Classification);
            Assert.Equal(ReconciliationRefusalReason.NoCandidates, persisted.EvaluationResult.RefusalReason);
            Assert.Empty(persisted.EvaluationResult.RankedCandidates);
        }

        private static (Mock<IReconciliationCaseStore> Store, SavedCaseCapture SavedCase) CreateStore()
        {
            var savedCase = new SavedCaseCapture();
            var store = new Mock<IReconciliationCaseStore>(MockBehavior.Strict);
            store.SetupGet(service => service.StorageDirectory).Returns("/tmp/reconciliation-cases");
            store
                .Setup(service => service.Save(It.IsAny<ReconciliationCase>()))
                .Callback<ReconciliationCase>(reconciliationCase => savedCase.Value = reconciliationCase)
                .Returns<ReconciliationCase>(reconciliationCase => ReconciliationCaseSnapshot.Create(
                    reconciliationCase,
                    reconciliationCase.CapturedAtUtc ?? CapturedAtUtc,
                    reconciliationCase.UpdatedAtUtc ?? CapturedAtUtc.AddMinutes(5)));

            return (store, savedCase);
        }

        private static ReconciliationCaseSnapshot BuildSnapshot(string albumMusicBrainzId, string? releaseMusicBrainzId)
        {
            var identifiers = new Dictionary<string, string?>
            {
                ["artist"] = "Artist Name",
                ["album"] = "Album Title",
                ["albumMusicBrainzId"] = albumMusicBrainzId,
                ["releaseMusicBrainzId"] = releaseMusicBrainzId
            };

            var reconciliationCase = new ReconciliationCase(
                sourceSeam: "download-client",
                phase: "captured",
                capturedIdentifiers: identifiers,
                structuralEvidence: BuildEvidence(releaseMusicBrainzId));

            return ReconciliationCaseSnapshot.Create(reconciliationCase, CapturedAtUtc, CapturedAtUtc);
        }

        private static ReconciliationEvidence BuildEvidence(string? preferredReleaseMusicBrainzId)
        {
            return new ReconciliationEvidence
            {
                TrackCount = 2,
                PreferredRelease = new PreferredReleaseReconciliationEvidence
                {
                    ReleaseMusicBrainzId = preferredReleaseMusicBrainzId,
                    ReleaseStatus = "Official",
                    Label = "Label A",
                    ReleaseDateUtc = new DateTimeOffset(2024, 05, 10, 0, 0, 0, TimeSpan.Zero),
                    DiscCount = 1,
                    MediaByDisc = new Dictionary<int, string> { [1] = "Digital Media" }
                },
                Tracks = new List<ReconciliationTrackEvidence>
                {
                    new()
                    {
                        AbsoluteTrackNumber = 1,
                        MediumNumber = 1,
                        Title = "Track One",
                        ReleaseTrackMusicBrainzId = "release-track-1",
                        RecordingMusicBrainzId = "recording-1"
                    },
                    new()
                    {
                        AbsoluteTrackNumber = 2,
                        MediumNumber = 1,
                        Title = "Track Two",
                        ReleaseTrackMusicBrainzId = "release-track-2",
                        RecordingMusicBrainzId = "recording-2"
                    }
                }
            };
        }

        private static CandidateAlbumSnapshot CreateAlbum(
            string albumMusicBrainzId,
            IReadOnlyList<CandidateReleaseSnapshot> releases,
            IReadOnlyList<CandidateLookupPath> lookupPaths)
        {
            return new CandidateAlbumSnapshot(
                albumMusicBrainzId,
                "Album Title",
                "artist-1",
                "Artist Name",
                null,
                new DateTime(2024, 05, 10),
                "Album",
                releases,
                lookupPaths);
        }

        private static CandidateReleaseSnapshot CreateRelease(
            string releaseMusicBrainzId,
            IReadOnlyList<string> releaseTrackIds,
            IReadOnlyList<string> recordingIds,
            IReadOnlyList<string> labels,
            IReadOnlyList<CandidateLookupPath> lookupPaths)
        {
            return new CandidateReleaseSnapshot(
                releaseMusicBrainzId,
                "Album Title",
                "Official",
                lookupPaths.Contains(CandidateLookupPath.TitleArtistFallback) ? "search-hit" : null,
                new DateTime(2024, 05, 10),
                2,
                0,
                labels,
                Array.Empty<string>(),
                new[] { new CandidateMediumSnapshot(1, "Disc 1", "Digital Media") },
                new[]
                {
                    new CandidateTrackSnapshot(releaseTrackIds[0], recordingIds[0], "Track One", "1", 1, 1, 0, false),
                    new CandidateTrackSnapshot(releaseTrackIds[1], recordingIds[1], "Track Two", "2", 2, 1, 0, false)
                });
        }

        private sealed class SavedCaseCapture
        {
            public ReconciliationCase? Value { get; set; }
        }
    }
}
