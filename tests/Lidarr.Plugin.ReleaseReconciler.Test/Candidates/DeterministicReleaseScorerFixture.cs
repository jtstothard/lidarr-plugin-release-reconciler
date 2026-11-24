using System;
using System.Collections.Generic;
using System.Linq;
using NzbDrone.Core.Plugins.ReleaseReconciler.Candidates;
using Xunit;

namespace NzbDrone.Core.Plugins.ReleaseReconciler.Test.Candidates
{
    public sealed class DeterministicReleaseScorerFixture
    {
        [Fact]
        public void should_rank_same_release_group_candidate_first_when_structural_signals_align()
        {
            var subject = new DeterministicReleaseScorer();
            var evidence = BuildEvidence(
                preferredReleaseMusicBrainzId: "release-preferred",
                preferredLabel: "Label A",
                preferredStatus: "Official",
                preferredReleaseDateUtc: new DateTimeOffset(2024, 05, 10, 0, 0, 0, TimeSpan.Zero),
                preferredMediaByDisc: new Dictionary<int, string> { [1] = "Digital Media" },
                tracks: new[]
                {
                    ("release-track-1", "recording-1", "Track One"),
                    ("release-track-2", "recording-2", "Track Two")
                });
            var identifiers = BuildIdentifiers(albumMusicBrainzId: "album-current", releaseMusicBrainzId: "release-preferred");
            var candidates = new[]
            {
                CreateAlbum(
                    albumMusicBrainzId: "album-current",
                    title: "Album Title",
                    releases: new[]
                    {
                        CreateRelease(
                            releaseMusicBrainzId: "release-preferred",
                            title: "Album Title",
                            status: "Official",
                            releaseDate: new DateTime(2024, 05, 10),
                            labels: new[] { "Label A" },
                            media: new[] { CreateMedium(1, "Digital Media") },
                            tracks: new[]
                            {
                                CreateTrack("release-track-1", "recording-1", "Track One", 1),
                                CreateTrack("release-track-2", "recording-2", "Track Two", 2)
                            })
                    },
                    lookupPaths: new[] { CandidateLookupPath.CurrentReleaseGroup }),
                CreateAlbum(
                    albumMusicBrainzId: "album-other",
                    title: "Album Title",
                    releases: new[]
                    {
                        CreateRelease(
                            releaseMusicBrainzId: "release-other",
                            title: "Album Title",
                            status: "Official",
                            releaseDate: new DateTime(2024, 05, 01),
                            labels: new[] { "Label B" },
                            media: new[] { CreateMedium(1, "Compact Disc") },
                            tracks: new[]
                            {
                                CreateTrack("release-track-x", "recording-1", "Track One", 1),
                                CreateTrack("release-track-y", "recording-x", "Track X", 2)
                            })
                    },
                    lookupPaths: new[] { CandidateLookupPath.RecordingIds })
            };

            var ranked = subject.ScoreCandidates(evidence, identifiers, candidates);

            var winner = ranked[0];
            Assert.Equal("release-preferred", winner.ReleaseMusicBrainzId);
            Assert.True(winner.SameReleaseGroup);
            Assert.True(winner.HasStrongIdentitySignal);
            Assert.Equal(2, winner.ScoreBreakdown.ReleaseTrackOverlapCount);
            Assert.Equal(2, winner.ScoreBreakdown.RecordingOverlapCount);
            Assert.Equal(80, winner.ScoreBreakdown.PreferredReleaseMatchScore);
            Assert.True(winner.TotalScore > ranked[1].TotalScore);
        }

        [Fact]
        public void should_rank_different_release_group_candidate_first_when_it_has_stronger_release_evidence()
        {
            var subject = new DeterministicReleaseScorer();
            var evidence = BuildEvidence(
                preferredReleaseMusicBrainzId: "release-target",
                preferredLabel: "Label Z",
                preferredStatus: "Official",
                preferredReleaseDateUtc: new DateTimeOffset(2023, 11, 12, 0, 0, 0, TimeSpan.Zero),
                preferredMediaByDisc: new Dictionary<int, string> { [1] = "Digital Media" },
                tracks: new[]
                {
                    ("release-track-1", "recording-1", "Track One"),
                    ("release-track-2", "recording-2", "Track Two")
                });
            var identifiers = BuildIdentifiers(albumMusicBrainzId: "album-current", releaseMusicBrainzId: "release-target");
            var candidates = new[]
            {
                CreateAlbum(
                    albumMusicBrainzId: "album-current",
                    title: "Album Title",
                    releases: new[]
                    {
                        CreateRelease(
                            releaseMusicBrainzId: "release-weak",
                            title: "Album Title",
                            status: "Official",
                            releaseDate: new DateTime(2023, 11, 11),
                            labels: new[] { "Label Z" },
                            media: new[] { CreateMedium(1, "Digital Media") },
                            tracks: new[]
                            {
                                CreateTrack("release-track-x", "recording-x", "Track One", 1),
                                CreateTrack("release-track-y", "recording-y", "Track Two", 2)
                            })
                    },
                    lookupPaths: new[] { CandidateLookupPath.CurrentReleaseGroup }),
                CreateAlbum(
                    albumMusicBrainzId: "album-other",
                    title: "Album Title",
                    releases: new[]
                    {
                        CreateRelease(
                            releaseMusicBrainzId: "release-target",
                            title: "Album Title",
                            status: "Official",
                            releaseDate: new DateTime(2023, 11, 12),
                            labels: new[] { "Label Z" },
                            media: new[] { CreateMedium(1, "Digital Media") },
                            tracks: new[]
                            {
                                CreateTrack("release-track-1", "recording-1", "Track One", 1),
                                CreateTrack("release-track-2", "recording-2", "Track Two", 2)
                            })
                    },
                    lookupPaths: new[] { CandidateLookupPath.RecordingIds })
            };

            var ranked = subject.ScoreCandidates(evidence, identifiers, candidates);

            var winner = ranked[0];
            Assert.Equal("release-target", winner.ReleaseMusicBrainzId);
            Assert.False(winner.SameReleaseGroup);
            Assert.True(winner.HasStrongIdentitySignal);
            Assert.True(winner.TotalScore > ranked[1].TotalScore);
            Assert.Equal(0, ranked[1].ScoreBreakdown.ReleaseTrackOverlapCount);
            Assert.Equal(0, ranked[1].ScoreBreakdown.RecordingOverlapCount);
        }

        private static IReadOnlyDictionary<string, string?> BuildIdentifiers(string albumMusicBrainzId, string releaseMusicBrainzId)
        {
            return new Dictionary<string, string?>
            {
                ["artist"] = "Artist Name",
                ["album"] = "Album Title",
                ["albumMusicBrainzId"] = albumMusicBrainzId,
                ["releaseMusicBrainzId"] = releaseMusicBrainzId
            };
        }

        private static ReconciliationEvidence BuildEvidence(
            string preferredReleaseMusicBrainzId,
            string preferredLabel,
            string preferredStatus,
            DateTimeOffset preferredReleaseDateUtc,
            IReadOnlyDictionary<int, string> preferredMediaByDisc,
            IEnumerable<(string ReleaseTrackId, string RecordingId, string Title)> tracks)
        {
            var trackList = tracks.ToList();
            var evidence = new ReconciliationEvidence
            {
                TrackCount = trackList.Count,
                PreferredRelease = new PreferredReleaseReconciliationEvidence
                {
                    ReleaseMusicBrainzId = preferredReleaseMusicBrainzId,
                    ReleaseStatus = preferredStatus,
                    Label = preferredLabel,
                    ReleaseDateUtc = preferredReleaseDateUtc,
                    DiscCount = preferredMediaByDisc.Count,
                    MediaByDisc = preferredMediaByDisc.ToDictionary(static pair => pair.Key, static pair => pair.Value)
                }
            };

            foreach (var track in trackList.Select((value, index) => new { value, index }))
            {
                evidence.Tracks.Add(new ReconciliationTrackEvidence
                {
                    AbsoluteTrackNumber = track.index + 1,
                    MediumNumber = 1,
                    Title = track.value.Title,
                    ReleaseTrackMusicBrainzId = track.value.ReleaseTrackId,
                    RecordingMusicBrainzId = track.value.RecordingId
                });
            }

            return evidence;
        }

        private static CandidateAlbumSnapshot CreateAlbum(
            string albumMusicBrainzId,
            string title,
            IReadOnlyList<CandidateReleaseSnapshot> releases,
            IReadOnlyList<CandidateLookupPath> lookupPaths)
        {
            return new CandidateAlbumSnapshot(
                albumMusicBrainzId,
                title,
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
            string title,
            string status,
            DateTime releaseDate,
            IReadOnlyList<string> labels,
            IReadOnlyList<CandidateMediumSnapshot> media,
            IReadOnlyList<CandidateTrackSnapshot> tracks)
        {
            return new CandidateReleaseSnapshot(
                releaseMusicBrainzId,
                title,
                status,
                null,
                releaseDate,
                tracks.Count,
                0,
                labels,
                Array.Empty<string>(),
                media,
                tracks);
        }

        private static CandidateMediumSnapshot CreateMedium(int number, string format)
        {
            return new CandidateMediumSnapshot(number, $"Disc {number}", format);
        }

        private static CandidateTrackSnapshot CreateTrack(string trackMusicBrainzId, string recordingMusicBrainzId, string title, int absoluteTrackNumber)
        {
            return new CandidateTrackSnapshot(trackMusicBrainzId, recordingMusicBrainzId, title, absoluteTrackNumber.ToString(), absoluteTrackNumber, 1, 0, false);
        }
    }
}
