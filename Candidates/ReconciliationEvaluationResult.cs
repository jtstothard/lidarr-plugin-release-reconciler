using System;
using System.Collections.Generic;
using System.Linq;

namespace NzbDrone.Core.Plugins.ReleaseReconciler.Candidates
{
    public enum ReconciliationClassification
    {
        BetterExistingRelease = 0,
        MissingReleaseInCurrentGroup = 1,
        DifferentReleaseGroup = 2,
        NoSafeMatch = 3
    }

    public enum ReconciliationRefusalReason
    {
        NoCandidates = 1,
        AmbiguousWinnerMargin = 2,
        TitleOnlyMatchNotAllowed = 3,
        SameReleaseGroupWithoutStrongReleaseMatch = 4
    }

    public sealed class ReconciliationEvaluationResult
    {
        public const string CurrentScoringVersion = "deterministic-v1";

        public string ScoringVersion { get; set; } = CurrentScoringVersion;

        public ReconciliationClassification Classification { get; set; }

        public ReconciliationRefusalReason? RefusalReason { get; set; }

        public bool UsedTitleArtistFallback { get; set; }

        public string? WinnerAlbumMusicBrainzId { get; set; }

        public string? WinnerReleaseMusicBrainzId { get; set; }

        public DateTimeOffset EvaluatedAtUtc { get; set; }

        public List<LidarrAlbumLookupAttempt> LookupAttempts { get; set; } = new();

        public List<RankedCandidateRelease> RankedCandidates { get; set; } = new();

        public ReconciliationEvaluationResult Normalize()
        {
            var normalizedCandidates = RankedCandidates
                .Select(static candidate => candidate.Normalize())
                .OrderBy(static candidate => candidate.Rank)
                .ThenBy(static candidate => candidate.ReleaseMusicBrainzId, StringComparer.Ordinal)
                .ToList();

            for (var index = 0; index < normalizedCandidates.Count; index++)
            {
                normalizedCandidates[index].Rank = index + 1;
            }

            return new ReconciliationEvaluationResult
            {
                ScoringVersion = NormalizeRequiredValue(ScoringVersion, nameof(ScoringVersion)),
                Classification = Classification,
                RefusalReason = RefusalReason,
                UsedTitleArtistFallback = UsedTitleArtistFallback,
                WinnerAlbumMusicBrainzId = NormalizeOptionalValue(WinnerAlbumMusicBrainzId),
                WinnerReleaseMusicBrainzId = NormalizeOptionalValue(WinnerReleaseMusicBrainzId),
                EvaluatedAtUtc = EvaluatedAtUtc,
                LookupAttempts = (LookupAttempts ?? new List<LidarrAlbumLookupAttempt>()).ToList(),
                RankedCandidates = normalizedCandidates
            };
        }

        public void ValidatePersistedShape()
        {
            if (string.IsNullOrWhiteSpace(ScoringVersion))
            {
                throw new InvalidOperationException("Reconciliation evaluation scoring version is required.");
            }

            if (EvaluatedAtUtc == default)
            {
                throw new InvalidOperationException("Reconciliation evaluation timestamp is required.");
            }

            if (LookupAttempts == null)
            {
                throw new InvalidOperationException("Reconciliation evaluation lookup attempts are required.");
            }

            if (RankedCandidates == null)
            {
                throw new InvalidOperationException("Reconciliation evaluation ranked candidates are required.");
            }

            foreach (var candidate in RankedCandidates)
            {
                candidate.ValidatePersistedShape();
            }

            var winnerRequired = Classification is ReconciliationClassification.BetterExistingRelease or ReconciliationClassification.DifferentReleaseGroup;
            var hasWinner = !string.IsNullOrWhiteSpace(WinnerAlbumMusicBrainzId) && !string.IsNullOrWhiteSpace(WinnerReleaseMusicBrainzId);

            if (winnerRequired && !hasWinner)
            {
                throw new InvalidOperationException($"Reconciliation evaluation classification '{Classification}' requires a persisted winner.");
            }

            if (!winnerRequired && (WinnerAlbumMusicBrainzId != null || WinnerReleaseMusicBrainzId != null))
            {
                throw new InvalidOperationException($"Reconciliation evaluation classification '{Classification}' cannot persist a winner.");
            }

            if (hasWinner && RankedCandidates.All(candidate =>
                    !string.Equals(candidate.AlbumMusicBrainzId, WinnerAlbumMusicBrainzId, StringComparison.Ordinal) ||
                    !string.Equals(candidate.ReleaseMusicBrainzId, WinnerReleaseMusicBrainzId, StringComparison.Ordinal)))
            {
                throw new InvalidOperationException("Reconciliation evaluation winner must appear in ranked candidates.");
            }
        }

        private static string NormalizeRequiredValue(string value, string paramName)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException("Value is required.", paramName);
            }

            return value.Trim();
        }

        internal static string? NormalizeOptionalValue(string? value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
    }

    public sealed class RankedCandidateRelease
    {
        public int Rank { get; set; }

        public string AlbumMusicBrainzId { get; set; } = string.Empty;

        public string AlbumTitle { get; set; } = string.Empty;

        public string? ArtistMusicBrainzId { get; set; }

        public string ArtistName { get; set; } = string.Empty;

        public string ReleaseMusicBrainzId { get; set; } = string.Empty;

        public string ReleaseTitle { get; set; } = string.Empty;

        public string? ReleaseDisambiguation { get; set; }

        public bool SameReleaseGroup { get; set; }

        public int TotalScore { get; set; }

        public int StrongSignalScore { get; set; }

        public bool HasStrongIdentitySignal { get; set; }

        public bool TitleOnlyCandidate { get; set; }

        public List<CandidateLookupPath> LookupPaths { get; set; } = new();

        public ReconciliationCandidateScoreBreakdown ScoreBreakdown { get; set; } = new();

        public RankedCandidateRelease Normalize()
        {
            var normalizedBreakdown = (ScoreBreakdown ?? new ReconciliationCandidateScoreBreakdown()).Normalize();

            return new RankedCandidateRelease
            {
                Rank = Rank,
                AlbumMusicBrainzId = NormalizeRequiredValue(AlbumMusicBrainzId, nameof(AlbumMusicBrainzId)),
                AlbumTitle = NormalizeRequiredValue(AlbumTitle, nameof(AlbumTitle)),
                ArtistMusicBrainzId = ReconciliationEvaluationResult.NormalizeOptionalValue(ArtistMusicBrainzId),
                ArtistName = NormalizeRequiredValue(ArtistName, nameof(ArtistName)),
                ReleaseMusicBrainzId = NormalizeRequiredValue(ReleaseMusicBrainzId, nameof(ReleaseMusicBrainzId)),
                ReleaseTitle = NormalizeRequiredValue(ReleaseTitle, nameof(ReleaseTitle)),
                ReleaseDisambiguation = ReconciliationEvaluationResult.NormalizeOptionalValue(ReleaseDisambiguation),
                SameReleaseGroup = SameReleaseGroup,
                TotalScore = normalizedBreakdown.TotalScore,
                StrongSignalScore = normalizedBreakdown.StrongSignalScore,
                HasStrongIdentitySignal = HasStrongIdentitySignal,
                TitleOnlyCandidate = TitleOnlyCandidate,
                LookupPaths = (LookupPaths ?? new List<CandidateLookupPath>())
                    .Distinct()
                    .OrderBy(static path => path)
                    .ToList(),
                ScoreBreakdown = normalizedBreakdown
            };
        }

        public void ValidatePersistedShape()
        {
            if (Rank <= 0)
            {
                throw new InvalidOperationException("Ranked reconciliation candidate rank must be greater than zero.");
            }

            if (string.IsNullOrWhiteSpace(AlbumMusicBrainzId))
            {
                throw new InvalidOperationException("Ranked reconciliation candidate album MusicBrainz ID is required.");
            }

            if (string.IsNullOrWhiteSpace(ReleaseMusicBrainzId))
            {
                throw new InvalidOperationException("Ranked reconciliation candidate release MusicBrainz ID is required.");
            }

            if (ScoreBreakdown == null)
            {
                throw new InvalidOperationException("Ranked reconciliation candidate score breakdown is required.");
            }

            ScoreBreakdown.ValidatePersistedShape();

            if (TotalScore != ScoreBreakdown.TotalScore)
            {
                throw new InvalidOperationException("Ranked reconciliation candidate total score must match the persisted score breakdown total.");
            }

            if (StrongSignalScore != ScoreBreakdown.StrongSignalScore)
            {
                throw new InvalidOperationException("Ranked reconciliation candidate strong-signal score must match the persisted score breakdown strong-signal score.");
            }
        }

        private static string NormalizeRequiredValue(string value, string paramName)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException("Value is required.", paramName);
            }

            return value.Trim();
        }
    }

    public sealed class ReconciliationCandidateScoreBreakdown
    {
        public int PreferredReleaseMatchScore { get; set; }

        public int SameReleaseGroupScore { get; set; }

        public int ReleaseTrackOverlapCount { get; set; }

        public int ReleaseTrackOverlapScore { get; set; }

        public int RecordingOverlapCount { get; set; }

        public int RecordingOverlapScore { get; set; }

        public int TrackCountDelta { get; set; }

        public int TrackCountFitScore { get; set; }

        public int AlbumTitleScore { get; set; }

        public int ReleaseTitleScore { get; set; }

        public int DisambiguationScore { get; set; }

        public int StatusScore { get; set; }

        public int ReleaseDateScore { get; set; }

        public int MediaFormatScore { get; set; }

        public int LabelScore { get; set; }

        public int StrongSignalScore { get; set; }

        public int TotalScore { get; set; }

        public ReconciliationCandidateScoreBreakdown Normalize()
        {
            var normalized = new ReconciliationCandidateScoreBreakdown
            {
                PreferredReleaseMatchScore = PreferredReleaseMatchScore,
                SameReleaseGroupScore = SameReleaseGroupScore,
                ReleaseTrackOverlapCount = ReleaseTrackOverlapCount,
                ReleaseTrackOverlapScore = ReleaseTrackOverlapScore,
                RecordingOverlapCount = RecordingOverlapCount,
                RecordingOverlapScore = RecordingOverlapScore,
                TrackCountDelta = TrackCountDelta,
                TrackCountFitScore = TrackCountFitScore,
                AlbumTitleScore = AlbumTitleScore,
                ReleaseTitleScore = ReleaseTitleScore,
                DisambiguationScore = DisambiguationScore,
                StatusScore = StatusScore,
                ReleaseDateScore = ReleaseDateScore,
                MediaFormatScore = MediaFormatScore,
                LabelScore = LabelScore
            };

            normalized.StrongSignalScore = normalized.PreferredReleaseMatchScore +
                                           normalized.SameReleaseGroupScore +
                                           normalized.ReleaseTrackOverlapScore +
                                           normalized.RecordingOverlapScore;

            normalized.TotalScore = normalized.StrongSignalScore +
                                    normalized.TrackCountFitScore +
                                    normalized.AlbumTitleScore +
                                    normalized.ReleaseTitleScore +
                                    normalized.DisambiguationScore +
                                    normalized.StatusScore +
                                    normalized.ReleaseDateScore +
                                    normalized.MediaFormatScore +
                                    normalized.LabelScore;

            return normalized;
        }

        public void ValidatePersistedShape()
        {
            if (ReleaseTrackOverlapCount < 0)
            {
                throw new InvalidOperationException("Release-track overlap count cannot be negative.");
            }

            if (RecordingOverlapCount < 0)
            {
                throw new InvalidOperationException("Recording overlap count cannot be negative.");
            }

            if (TrackCountDelta < 0)
            {
                throw new InvalidOperationException("Track-count delta cannot be negative.");
            }

            var normalized = Normalize();

            if (StrongSignalScore != normalized.StrongSignalScore)
            {
                throw new InvalidOperationException("Strong-signal score must equal the sum of strong structural components.");
            }

            if (TotalScore != normalized.TotalScore)
            {
                throw new InvalidOperationException("Total score must equal the sum of the persisted score breakdown components.");
            }
        }
    }
}
