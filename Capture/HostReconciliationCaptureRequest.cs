using System;
using System.Collections.Generic;

namespace NzbDrone.Core.Plugins.ReleaseReconciler.Capture
{
    public sealed class HostReconciliationCaptureRequest
    {
        public string SourceSeam { get; set; } = string.Empty;

        public string DownloadId { get; set; } = string.Empty;

        public string Phase { get; set; } = "queued";

        public string? Status { get; set; }

        public string? OutputPath { get; set; }

        public string? DiagnosticSummary { get; set; }

        public string? LastError { get; set; }

        public DateTimeOffset? QueuedAtUtc { get; set; }

        public DateTimeOffset? CompletedAtUtc { get; set; }

        public DateTimeOffset? CapturedAtUtc { get; set; }

        public DateTimeOffset? UpdatedAtUtc { get; set; }

        public Dictionary<string, string?> CapturedIdentifiers { get; set; } = new(StringComparer.Ordinal);

        public Dictionary<string, string?> SourceMetadata { get; set; } = new(StringComparer.Ordinal);

        public HostReconciliationStructuralEvidence? StructuralEvidence { get; set; }
    }

    public sealed class HostReconciliationStructuralEvidence
    {
        public int TrackCount { get; set; }

        public HostPreferredReleaseEvidence? PreferredRelease { get; set; }

        public List<HostTrackEvidence> Tracks { get; set; } = new();
    }

    public sealed class HostPreferredReleaseEvidence
    {
        public string? ReleaseMusicBrainzId { get; set; }

        public string? ReleaseArtistMusicBrainzId { get; set; }

        public string? ReleaseStatus { get; set; }

        public string? Label { get; set; }

        public DateTimeOffset? ReleaseDateUtc { get; set; }

        public int DiscCount { get; set; }

        public Dictionary<int, string> MediaByDisc { get; set; } = new();
    }

    public sealed class HostTrackEvidence
    {
        public int AbsoluteTrackNumber { get; set; }

        public int MediumNumber { get; set; }

        public string Title { get; set; } = string.Empty;

        public string? RecordingMusicBrainzId { get; set; }

        public string? ReleaseTrackMusicBrainzId { get; set; }
    }
}
