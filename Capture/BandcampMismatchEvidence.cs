using System;
using System.Collections.Generic;

namespace NzbDrone.Core.Plugins.ReleaseReconciler.Capture
{
    /// <summary>
    /// Provider-shaped evidence envelope derived from BandcampRetagContext plus
    /// queue lifecycle fields. This keeps the reconciler independent from the
    /// Bandcamp runtime assembly while preserving the validated download-time seam.
    /// </summary>
    public sealed class BandcampMismatchEvidence
    {
        public string DownloadId { get; set; } = string.Empty;

        public string AlbumUrl { get; set; } = string.Empty;

        public string Title { get; set; } = string.Empty;

        public string? OutputPath { get; set; }

        public string QueuePhase { get; set; } = "queued";

        public string QueueStatus { get; set; } = "Queued";

        public string? ErrorMessage { get; set; }

        public DateTimeOffset? QueuedAtUtc { get; set; }

        public DateTimeOffset? CompletedAtUtc { get; set; }

        public DateTimeOffset? CapturedAtUtc { get; set; }

        public BandcampRetagEvidence RetagContext { get; set; } = new();
    }

    public sealed class BandcampRetagEvidence
    {
        public string ArtistName { get; set; } = string.Empty;

        public string? ArtistMusicBrainzId { get; set; }

        public string AlbumTitle { get; set; } = string.Empty;

        public string? AlbumMusicBrainzId { get; set; }

        public string? AlbumType { get; set; }

        public string? AlbumDisambiguation { get; set; }

        public DateTimeOffset? AlbumReleaseDateUtc { get; set; }

        public string[] Genres { get; set; } = Array.Empty<string>();

        public BandcampPreferredReleaseEvidence? PreferredRelease { get; set; }

        public List<BandcampTrackEvidence> Tracks { get; set; } = new();
    }

    public sealed class BandcampPreferredReleaseEvidence
    {
        public string? ReleaseMusicBrainzId { get; set; }

        public string? ReleaseArtistMusicBrainzId { get; set; }

        public string? ReleaseStatus { get; set; }

        public string? Label { get; set; }

        public DateTimeOffset? ReleaseDateUtc { get; set; }

        public int DiscCount { get; set; }

        public Dictionary<int, string> MediaByDisc { get; set; } = new();
    }

    public sealed class BandcampTrackEvidence
    {
        public int AbsoluteTrackNumber { get; set; }

        public int MediumNumber { get; set; }

        public string Title { get; set; } = string.Empty;

        public string? RecordingMusicBrainzId { get; set; }

        public string? ReleaseTrackMusicBrainzId { get; set; }
    }
}
