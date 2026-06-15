using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace NzbDrone.Core.Plugins.ReleaseReconciler.Candidates
{
    public sealed class DeterministicReleaseScorer
    {
        public const int UniqueWinnerMargin = 12;

        private const int PreferredReleaseMatchWeight = 80;
        private const int SameReleaseGroupWeight = 30;
        private const int ReleaseTrackOverlapWeight = 24;
        private const int RecordingOverlapWeight = 14;

        public IReadOnlyList<RankedCandidateRelease> ScoreCandidates(
            ReconciliationEvidence structuralEvidence,
            IReadOnlyDictionary<string, string?> capturedIdentifiers,
            IReadOnlyList<CandidateAlbumSnapshot> candidates)
        {
            ArgumentNullException.ThrowIfNull(structuralEvidence);
            ArgumentNullException.ThrowIfNull(capturedIdentifiers);
            ArgumentNullException.ThrowIfNull(candidates);

            var preferredReleaseMusicBrainzId = Normalize(capturedIdentifiers.TryGetValue("releaseMusicBrainzId", out var releaseMusicBrainzId)
                ? releaseMusicBrainzId
                : structuralEvidence.PreferredRelease?.ReleaseMusicBrainzId);
            var currentAlbumMusicBrainzId = Normalize(capturedIdentifiers.TryGetValue("albumMusicBrainzId", out var albumMusicBrainzId)
                ? albumMusicBrainzId
                : null);
            var albumTitle = Normalize(capturedIdentifiers.TryGetValue("album", out var album)
                ? album
                : null);
            var releaseDisambiguation = Normalize(capturedIdentifiers.TryGetValue("releaseDisambiguation", out var capturedDisambiguation)
                ? capturedDisambiguation
                : null);

            var releaseTrackIds = structuralEvidence.ReleaseTrackMusicBrainzIds
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Select(static value => value.Trim())
                .ToHashSet(StringComparer.Ordinal);
            var recordingIds = structuralEvidence.RecordingMusicBrainzIds
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Select(static value => value.Trim())
                .ToHashSet(StringComparer.Ordinal);

            var scored = new List<RankedCandidateRelease>();

            foreach (var albumCandidate in candidates)
            {
                foreach (var releaseCandidate in albumCandidate.Releases)
                {
                    var breakdown = BuildBreakdown(
                        albumCandidate,
                        releaseCandidate,
                        structuralEvidence,
                        releaseTrackIds,
                        recordingIds,
                        preferredReleaseMusicBrainzId,
                        currentAlbumMusicBrainzId,
                        albumTitle,
                        releaseDisambiguation);

                    var hasStrongIdentitySignal = breakdown.PreferredReleaseMatchScore > 0 ||
                                                  breakdown.ReleaseTrackOverlapCount > 0 ||
                                                  breakdown.RecordingOverlapCount > 0;
                    var titleOnlyCandidate = !hasStrongIdentitySignal &&
                                             albumCandidate.LookupPaths.Count > 0 &&
                                             albumCandidate.LookupPaths.All(static path => path == CandidateLookupPath.TitleArtistFallback);

                    scored.Add(new RankedCandidateRelease
                    {
                        AlbumMusicBrainzId = albumCandidate.AlbumMusicBrainzId,
                        AlbumTitle = albumCandidate.Title,
                        ArtistMusicBrainzId = albumCandidate.ArtistMusicBrainzId,
                        ArtistName = albumCandidate.ArtistName,
                        ReleaseMusicBrainzId = releaseCandidate.ReleaseMusicBrainzId,
                        ReleaseTitle = releaseCandidate.Title,
                        ReleaseDisambiguation = releaseCandidate.Disambiguation,
                        SameReleaseGroup = breakdown.SameReleaseGroupScore > 0,
                        TotalScore = breakdown.TotalScore,
                        StrongSignalScore = breakdown.StrongSignalScore,
                        HasStrongIdentitySignal = hasStrongIdentitySignal,
                        TitleOnlyCandidate = titleOnlyCandidate,
                        LookupPaths = albumCandidate.LookupPaths.Distinct().OrderBy(static path => path).ToList(),
                        ScoreBreakdown = breakdown
                    });
                }
            }

            var ordered = scored
                .OrderByDescending(static candidate => candidate.TotalScore)
                .ThenByDescending(static candidate => candidate.StrongSignalScore)
                .ThenByDescending(static candidate => candidate.ScoreBreakdown.ReleaseTrackOverlapCount)
                .ThenByDescending(static candidate => candidate.ScoreBreakdown.RecordingOverlapCount)
                .ThenBy(static candidate => candidate.ScoreBreakdown.TrackCountDelta)
                .ThenByDescending(static candidate => candidate.SameReleaseGroup)
                .ThenByDescending(static candidate => candidate.ScoreBreakdown.ReleaseDateScore)
                .ThenBy(static candidate => candidate.AlbumMusicBrainzId, StringComparer.Ordinal)
                .ThenBy(static candidate => candidate.ReleaseMusicBrainzId, StringComparer.Ordinal)
                .ToList();

            for (var index = 0; index < ordered.Count; index++)
            {
                ordered[index].Rank = index + 1;
            }

            return ordered;
        }

        private static ReconciliationCandidateScoreBreakdown BuildBreakdown(
            CandidateAlbumSnapshot albumCandidate,
            CandidateReleaseSnapshot releaseCandidate,
            ReconciliationEvidence structuralEvidence,
            HashSet<string> releaseTrackIds,
            HashSet<string> recordingIds,
            string? preferredReleaseMusicBrainzId,
            string? currentAlbumMusicBrainzId,
            string? capturedAlbumTitle,
            string? capturedReleaseDisambiguation)
        {
            var preferredRelease = structuralEvidence.PreferredRelease;
            var releaseTrackOverlapCount = releaseCandidate.Tracks.Count(track =>
                !string.IsNullOrWhiteSpace(track.TrackMusicBrainzId) && releaseTrackIds.Contains(track.TrackMusicBrainzId.Trim()));
            var recordingOverlapCount = releaseCandidate.Tracks.Count(track =>
                !string.IsNullOrWhiteSpace(track.RecordingMusicBrainzId) && recordingIds.Contains(track.RecordingMusicBrainzId.Trim()));
            var trackCountDelta = structuralEvidence.TrackCount <= 0
                ? Math.Abs(releaseCandidate.TrackCount)
                : Math.Abs(releaseCandidate.TrackCount - structuralEvidence.TrackCount);

            var breakdown = new ReconciliationCandidateScoreBreakdown
            {
                PreferredReleaseMatchScore = StringEquals(releaseCandidate.ReleaseMusicBrainzId, preferredReleaseMusicBrainzId)
                    ? PreferredReleaseMatchWeight
                    : 0,
                SameReleaseGroupScore = StringEquals(albumCandidate.AlbumMusicBrainzId, currentAlbumMusicBrainzId)
                    ? SameReleaseGroupWeight
                    : 0,
                ReleaseTrackOverlapCount = releaseTrackOverlapCount,
                ReleaseTrackOverlapScore = releaseTrackOverlapCount * ReleaseTrackOverlapWeight,
                RecordingOverlapCount = recordingOverlapCount,
                RecordingOverlapScore = recordingOverlapCount * RecordingOverlapWeight,
                TrackCountDelta = trackCountDelta,
                TrackCountFitScore = ScoreTrackCountFit(structuralEvidence.TrackCount, releaseCandidate.TrackCount),
                AlbumTitleScore = ScoreExactMatch(albumCandidate.Title, capturedAlbumTitle, 4),
                ReleaseTitleScore = ScoreExactMatch(releaseCandidate.Title, capturedAlbumTitle, 6),
                DisambiguationScore = ScoreDisambiguation(releaseCandidate.Disambiguation, capturedReleaseDisambiguation),
                StatusScore = ScoreExactMatch(releaseCandidate.Status, preferredRelease?.ReleaseStatus, 3),
                ReleaseDateScore = ScoreReleaseDate(releaseCandidate.ReleaseDate, preferredRelease?.ReleaseDateUtc),
                MediaFormatScore = ScoreMedia(releaseCandidate, preferredRelease),
                LabelScore = ScoreLabel(releaseCandidate, preferredRelease?.Label)
            };

            return breakdown.Normalize();
        }

        private static int ScoreTrackCountFit(int expectedTrackCount, int candidateTrackCount)
        {
            if (expectedTrackCount <= 0 || candidateTrackCount <= 0)
            {
                return 0;
            }

            var delta = Math.Abs(candidateTrackCount - expectedTrackCount);
            return delta switch
            {
                0 => 18,
                1 => 10,
                2 => 4,
                _ => 0
            };
        }

        private static int ScoreDisambiguation(string? candidateDisambiguation, string? expectedDisambiguation)
        {
            return ScoreExactMatch(candidateDisambiguation, expectedDisambiguation, 3);
        }

        private static int ScoreReleaseDate(DateTime? candidateDate, DateTimeOffset? preferredReleaseDate)
        {
            if (!candidateDate.HasValue || !preferredReleaseDate.HasValue)
            {
                return 0;
            }

            var candidate = candidateDate.Value.Date;
            var expected = preferredReleaseDate.Value.UtcDateTime.Date;

            if (candidate == expected)
            {
                return 3;
            }

            return candidate.Year == expected.Year ? 1 : 0;
        }

        private static int ScoreMedia(CandidateReleaseSnapshot releaseCandidate, PreferredReleaseReconciliationEvidence? preferredRelease)
        {
            if (preferredRelease == null || preferredRelease.MediaByDisc.Count == 0 || releaseCandidate.Media.Count == 0)
            {
                return 0;
            }

            var matches = 0;

            foreach (var medium in releaseCandidate.Media)
            {
                if (!preferredRelease.MediaByDisc.TryGetValue(medium.Number, out var expectedFormat))
                {
                    continue;
                }

                if (StringEquals(medium.Format, expectedFormat))
                {
                    matches++;
                }
            }

            return matches > 0 ? 3 : 0;
        }

        private static int ScoreLabel(CandidateReleaseSnapshot releaseCandidate, string? preferredLabel)
        {
            var normalizedPreferredLabel = Normalize(preferredLabel);

            if (normalizedPreferredLabel == null)
            {
                return 0;
            }

            return releaseCandidate.Labels.Any(label => StringEquals(label, normalizedPreferredLabel)) ? 3 : 0;
        }

        private static int ScoreExactMatch(string? candidateValue, string? expectedValue, int score)
        {
            return StringEquals(candidateValue, expectedValue) ? score : 0;
        }

        private static bool StringEquals(string? left, string? right)
        {
            return string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);
        }

        private static string? Normalize(string? value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? null
                : value.Trim().Normalize(NormalizationForm.FormKC).ToLower(CultureInfo.InvariantCulture);
        }
    }
}
