extern alias release_reconciler_nlog;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Logger = release_reconciler_nlog::NLog.Logger;
using NzbDrone.Core.Plugins.ReleaseReconciler.Cases;
using NzbDrone.Core.Plugins.ReleaseReconciler.Notifications;

namespace NzbDrone.Core.Plugins.ReleaseReconciler.Capture
{
    public sealed class BandcampReconciliationCaseCapture
    {
        public const string BandcampDownloadSourceSeam = "bandcamp-download-client";

        private readonly HostReconciliationCaseCapture _hostCapture;
        private readonly Logger _logger;

        public BandcampReconciliationCaseCapture(
            IReconciliationCaseStore caseStore,
            Logger logger,
            IHostMusicBrainzContextResolver? hostMusicBrainzContextResolver = null,
            NotificationSettingsStore? notificationSettingsStore = null,
            ReconciliationNotificationDispatcher? notificationDispatcher = null)
            : this(new HostReconciliationCaseCapture(caseStore, logger, hostMusicBrainzContextResolver, notificationSettingsStore, notificationDispatcher), logger)
        {
        }

        internal BandcampReconciliationCaseCapture(HostReconciliationCaseCapture hostCapture, Logger logger)
        {
            _hostCapture = hostCapture ?? throw new ArgumentNullException(nameof(hostCapture));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public ReconciliationCaseSnapshot Capture(BandcampMismatchEvidence evidence)
        {
            ArgumentNullException.ThrowIfNull(evidence);
            ArgumentNullException.ThrowIfNull(evidence.RetagContext);

            var request = BuildRequest(evidence);

            _logger.Info(
                "Adapting Bandcamp reconciliation evidence into host capture request: seam={0}, phase={1}, status={2}, downloadId={3}, albumMbId={4}, preferredReleaseMbId={5}, trackCount={6}",
                request.SourceSeam,
                request.Phase,
                NormalizeOrFallback(request.Status, "none"),
                NormalizeOrFallback(request.DownloadId, "-"),
                NormalizeOrFallback(evidence.RetagContext.AlbumMusicBrainzId, "none"),
                NormalizeOrFallback(evidence.RetagContext.PreferredRelease?.ReleaseMusicBrainzId, "none"),
                request.StructuralEvidence?.TrackCount ?? 0);

            return _hostCapture.Capture(request);
        }

        private static HostReconciliationCaptureRequest BuildRequest(BandcampMismatchEvidence evidence)
        {
            var phase = DeterminePhase(evidence);

            return new HostReconciliationCaptureRequest
            {
                SourceSeam = BandcampDownloadSourceSeam,
                DownloadId = evidence.DownloadId,
                Phase = phase,
                Status = evidence.QueueStatus,
                OutputPath = evidence.OutputPath,
                DiagnosticSummary = BuildDiagnosticSummary(evidence, phase),
                LastError = DetermineLastError(phase, evidence.ErrorMessage),
                QueuedAtUtc = evidence.QueuedAtUtc,
                CompletedAtUtc = evidence.CompletedAtUtc,
                CapturedAtUtc = DetermineCapturedAtUtc(evidence),
                CapturedIdentifiers = BuildCapturedIdentifiers(evidence),
                SourceMetadata = BuildSourceMetadata(evidence),
                StructuralEvidence = BuildStructuralEvidence(evidence)
            };
        }

        private static Dictionary<string, string?> BuildCapturedIdentifiers(BandcampMismatchEvidence evidence)
        {
            var identifiers = new Dictionary<string, string?>(StringComparer.Ordinal);
            AddIdentifier(identifiers, "artistName", evidence.RetagContext.ArtistName);
            AddIdentifier(identifiers, "artistMusicBrainzId", evidence.RetagContext.ArtistMusicBrainzId);
            AddIdentifier(identifiers, "albumTitle", evidence.RetagContext.AlbumTitle);
            AddIdentifier(identifiers, "albumMusicBrainzId", evidence.RetagContext.AlbumMusicBrainzId);
            AddIdentifier(identifiers, "preferredReleaseMusicBrainzId", evidence.RetagContext.PreferredRelease?.ReleaseMusicBrainzId);
            AddIdentifier(identifiers, "preferredReleaseArtistMusicBrainzId", evidence.RetagContext.PreferredRelease?.ReleaseArtistMusicBrainzId);
            return identifiers;
        }

        private static Dictionary<string, string?> BuildSourceMetadata(BandcampMismatchEvidence evidence)
        {
            var metadata = new Dictionary<string, string?>(StringComparer.Ordinal);
            AddIdentifier(metadata, "title", evidence.Title);
            AddIdentifier(metadata, "provider", "harmony");
            AddIdentifier(metadata, "providerUrl", evidence.AlbumUrl);
            AddIdentifier(metadata, "lookupArtist", evidence.RetagContext.ArtistName);
            AddIdentifier(metadata, "lookupAlbum", evidence.RetagContext.AlbumTitle);
            AddIdentifier(metadata, "queuePhase", evidence.QueuePhase);
            AddIdentifier(metadata, "releaseStatus", evidence.RetagContext.PreferredRelease?.ReleaseStatus);
            AddIdentifier(metadata, "releaseSpecificity", HasReleaseSpecificity(evidence.RetagContext) ? "preferred-release" : "guidance-needed");
            return metadata;
        }

        private static HostReconciliationStructuralEvidence BuildStructuralEvidence(BandcampMismatchEvidence evidence)
        {
            return new HostReconciliationStructuralEvidence
            {
                TrackCount = evidence.RetagContext.Tracks.Count,
                PreferredRelease = evidence.RetagContext.PreferredRelease == null
                    ? null
                    : new HostPreferredReleaseEvidence
                    {
                        ReleaseMusicBrainzId = evidence.RetagContext.PreferredRelease.ReleaseMusicBrainzId,
                        ReleaseArtistMusicBrainzId = evidence.RetagContext.PreferredRelease.ReleaseArtistMusicBrainzId,
                        ReleaseStatus = evidence.RetagContext.PreferredRelease.ReleaseStatus,
                        Label = evidence.RetagContext.PreferredRelease.Label,
                        ReleaseDateUtc = evidence.RetagContext.PreferredRelease.ReleaseDateUtc,
                        DiscCount = evidence.RetagContext.PreferredRelease.DiscCount,
                        MediaByDisc = evidence.RetagContext.PreferredRelease.MediaByDisc
                            .Where(static pair => !string.IsNullOrWhiteSpace(pair.Value))
                            .ToDictionary(static pair => pair.Key, static pair => pair.Value.Trim())
                    },
                Tracks = evidence.RetagContext.Tracks
                    .Select(static track => new HostTrackEvidence
                    {
                        AbsoluteTrackNumber = track.AbsoluteTrackNumber,
                        MediumNumber = track.MediumNumber,
                        Title = track.Title,
                        RecordingMusicBrainzId = track.RecordingMusicBrainzId,
                        ReleaseTrackMusicBrainzId = track.ReleaseTrackMusicBrainzId
                    })
                    .ToList()
            };
        }

        private static string DeterminePhase(BandcampMismatchEvidence evidence)
        {
            if (!HasReleaseSpecificity(evidence.RetagContext))
            {
                return "guidance-needed";
            }

            var normalizedQueuePhase = NormalizeOptional(evidence.QueuePhase);
            if (normalizedQueuePhase != null)
            {
                return normalizedQueuePhase;
            }

            var normalizedQueueStatus = NormalizeOptional(evidence.QueueStatus);
            if (normalizedQueueStatus != null)
            {
                return normalizedQueueStatus.Replace(' ', '-').ToLowerInvariant();
            }

            return "captured";
        }

        private static string? DetermineLastError(string phase, string? errorMessage)
        {
            var normalized = NormalizeOptional(errorMessage);
            if (normalized == null)
            {
                return null;
            }

            return string.Equals(phase, "evaluated", StringComparison.Ordinal)
                ? null
                : normalized;
        }

        private static DateTimeOffset DetermineCapturedAtUtc(BandcampMismatchEvidence evidence)
        {
            return evidence.CapturedAtUtc
                ?? evidence.CompletedAtUtc
                ?? evidence.QueuedAtUtc
                ?? DateTimeOffset.UtcNow;
        }

        private static string BuildDiagnosticSummary(BandcampMismatchEvidence evidence, string phase)
        {
            var parts = new List<string>
            {
                $"queueStatus={NormalizeOrFallback(evidence.QueueStatus, "Unknown")}",
                $"queuePhase={NormalizeOrFallback(evidence.QueuePhase, phase)}",
                $"releaseSpecificity={(HasReleaseSpecificity(evidence.RetagContext) ? "preferred-release" : "guidance-needed")}",
                $"trackCount={evidence.RetagContext.Tracks.Count.ToString(CultureInfo.InvariantCulture)}",
                $"recordingMbids={evidence.RetagContext.Tracks.Count(static track => !string.IsNullOrWhiteSpace(track.RecordingMusicBrainzId)).ToString(CultureInfo.InvariantCulture)}",
                $"releaseTrackMbids={evidence.RetagContext.Tracks.Count(static track => !string.IsNullOrWhiteSpace(track.ReleaseTrackMusicBrainzId)).ToString(CultureInfo.InvariantCulture)}"
            };

            AddDiagnosticPart(parts, "title", evidence.Title);
            AddDiagnosticPart(parts, "artist", evidence.RetagContext.ArtistName);
            AddDiagnosticPart(parts, "album", evidence.RetagContext.AlbumTitle);
            AddDiagnosticPart(parts, "albumUrl", evidence.AlbumUrl);
            AddDiagnosticPart(parts, "releaseStatus", evidence.RetagContext.PreferredRelease?.ReleaseStatus);

            return string.Join("; ", parts);
        }

        private static bool HasReleaseSpecificity(BandcampRetagEvidence retagContext)
        {
            return retagContext != null
                && !string.IsNullOrWhiteSpace(retagContext.AlbumMusicBrainzId)
                && !string.IsNullOrWhiteSpace(retagContext.PreferredRelease?.ReleaseMusicBrainzId);
        }

        private static void AddIdentifier(IDictionary<string, string?> identifiers, string key, string? value)
        {
            var normalized = NormalizeOptional(value);
            if (normalized != null)
            {
                identifiers[key] = normalized;
            }
        }

        private static void AddDiagnosticPart(ICollection<string> parts, string key, string? value)
        {
            var normalized = NormalizeOptional(value);
            if (normalized != null)
            {
                parts.Add($"{key}={normalized}");
            }
        }

        private static string NormalizeOrFallback(string? value, string fallback)
        {
            return NormalizeOptional(value) ?? fallback;
        }

        private static string? NormalizeOptional(string? value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
    }
}
