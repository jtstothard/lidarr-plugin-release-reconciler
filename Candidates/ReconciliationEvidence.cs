using System;
using System.Collections.Generic;
using System.Linq;

namespace NzbDrone.Core.Plugins.ReleaseReconciler.Candidates
{
    public sealed class ReconciliationEvidence
    {
        public int TrackCount { get; set; }

        public PreferredReleaseReconciliationEvidence? PreferredRelease { get; set; }

        public List<ReconciliationTrackEvidence> Tracks { get; set; } = new();

        public IReadOnlyList<string> RecordingMusicBrainzIds => Tracks
            .Select(static track => track.RecordingMusicBrainzId)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value!)
            .ToArray();

        public IReadOnlyList<string> ReleaseTrackMusicBrainzIds => Tracks
            .Select(static track => track.ReleaseTrackMusicBrainzId)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value!)
            .ToArray();

        public string? PreferredReleaseMusicBrainzId => PreferredRelease?.ReleaseMusicBrainzId;

        public ReconciliationEvidence Normalize()
        {
            var normalizedTracks = (Tracks ?? new List<ReconciliationTrackEvidence>())
                .Select(static track => track.Normalize())
                .OrderBy(static track => track.AbsoluteTrackNumber)
                .ThenBy(static track => track.MediumNumber)
                .ThenBy(static track => track.Title, StringComparer.Ordinal)
                .ToList();

            return new ReconciliationEvidence
            {
                TrackCount = TrackCount > 0 ? TrackCount : normalizedTracks.Count,
                PreferredRelease = PreferredRelease?.Normalize(),
                Tracks = normalizedTracks
            };
        }

        public void ValidatePersistedShape()
        {
            if (TrackCount < 0)
            {
                throw new InvalidOperationException("Reconciliation evidence track count cannot be negative.");
            }

            if (TrackCount != Tracks.Count)
            {
                throw new InvalidOperationException($"Reconciliation evidence track count '{TrackCount}' did not match persisted track entries '{Tracks.Count}'.");
            }

            PreferredRelease?.ValidatePersistedShape();

            foreach (var track in Tracks)
            {
                track.ValidatePersistedShape();
            }
        }
    }

    public sealed class PreferredReleaseReconciliationEvidence
    {
        public string? ReleaseMusicBrainzId { get; set; }

        public string? ReleaseArtistMusicBrainzId { get; set; }

        public string? ReleaseStatus { get; set; }

        public string? Label { get; set; }

        public DateTimeOffset? ReleaseDateUtc { get; set; }

        public int DiscCount { get; set; }

        public Dictionary<int, string> MediaByDisc { get; set; } = new();

        public PreferredReleaseReconciliationEvidence Normalize()
        {
            return new PreferredReleaseReconciliationEvidence
            {
                ReleaseMusicBrainzId = ReconciliationEvidenceValueNormalizer.NormalizeOptionalValue(ReleaseMusicBrainzId),
                ReleaseArtistMusicBrainzId = ReconciliationEvidenceValueNormalizer.NormalizeOptionalValue(ReleaseArtistMusicBrainzId),
                ReleaseStatus = ReconciliationEvidenceValueNormalizer.NormalizeOptionalValue(ReleaseStatus),
                Label = ReconciliationEvidenceValueNormalizer.NormalizeOptionalValue(Label),
                ReleaseDateUtc = ReleaseDateUtc,
                DiscCount = DiscCount,
                MediaByDisc = (MediaByDisc ?? new Dictionary<int, string>())
                    .OrderBy(static pair => pair.Key)
                    .ToDictionary(
                        static pair => pair.Key,
                        static pair => ReconciliationEvidenceValueNormalizer.NormalizeOptionalValue(pair.Value) ?? string.Empty)
            };
        }

        public void ValidatePersistedShape()
        {
            if (DiscCount < 0)
            {
                throw new InvalidOperationException("Preferred release evidence disc count cannot be negative.");
            }

            if (MediaByDisc.Keys.Any(static discNumber => discNumber <= 0))
            {
                throw new InvalidOperationException("Preferred release evidence disc numbers must be greater than zero.");
            }
        }
    }

    public sealed class ReconciliationTrackEvidence
    {
        public int AbsoluteTrackNumber { get; set; }

        public int MediumNumber { get; set; }

        public string Title { get; set; } = string.Empty;

        public string? RecordingMusicBrainzId { get; set; }

        public string? ReleaseTrackMusicBrainzId { get; set; }

        public ReconciliationTrackEvidence Normalize()
        {
            return new ReconciliationTrackEvidence
            {
                AbsoluteTrackNumber = AbsoluteTrackNumber,
                MediumNumber = MediumNumber,
                Title = ReconciliationEvidenceValueNormalizer.NormalizeOptionalValue(Title) ?? string.Empty,
                RecordingMusicBrainzId = ReconciliationEvidenceValueNormalizer.NormalizeOptionalValue(RecordingMusicBrainzId),
                ReleaseTrackMusicBrainzId = ReconciliationEvidenceValueNormalizer.NormalizeOptionalValue(ReleaseTrackMusicBrainzId)
            };
        }

        public void ValidatePersistedShape()
        {
            if (AbsoluteTrackNumber < 0)
            {
                throw new InvalidOperationException("Track evidence absolute track number cannot be negative.");
            }

            if (MediumNumber < 0)
            {
                throw new InvalidOperationException("Track evidence medium number cannot be negative.");
            }
        }
    }

    internal static class ReconciliationEvidenceValueNormalizer
    {
        public static string? NormalizeOptionalValue(string? value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
    }
}
