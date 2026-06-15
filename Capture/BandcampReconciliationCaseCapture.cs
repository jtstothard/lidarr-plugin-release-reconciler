using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using NLog;
using NzbDrone.Core.Plugins.ReleaseReconciler.Candidates;
using NzbDrone.Core.Plugins.ReleaseReconciler.Cases;

namespace NzbDrone.Core.Plugins.ReleaseReconciler.Capture
{
    public sealed class BandcampReconciliationCaseCapture
    {
        public const string BandcampDownloadSourceSeam = "bandcamp-download-client";

        private readonly IReconciliationCaseStore _caseStore;
        private readonly Logger _logger;

        public BandcampReconciliationCaseCapture(IReconciliationCaseStore caseStore, Logger logger)
        {
            _caseStore = caseStore ?? throw new ArgumentNullException(nameof(caseStore));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public ReconciliationCaseSnapshot Capture(BandcampMismatchEvidence evidence)
        {
            ArgumentNullException.ThrowIfNull(evidence);
            ArgumentNullException.ThrowIfNull(evidence.RetagContext);

            var normalizedPhase = DeterminePhase(evidence);
            var capturedIdentifiers = BuildCapturedIdentifiers(evidence);
            var structuralEvidence = BuildStructuralEvidence(evidence);
            var capturedAtUtc = DetermineCapturedAtUtc(evidence);
            var diagnosticSummary = BuildDiagnosticSummary(evidence, normalizedPhase);

            _logger.Info(
                "Capturing Bandcamp reconciliation evidence: seam={0}, phase={1}, status={2}, downloadId={3}, albumMbId={4}, preferredReleaseMbId={5}, trackCount={6}",
                BandcampDownloadSourceSeam,
                normalizedPhase,
                NormalizeOrFallback(evidence.QueueStatus, "Unknown"),
                NormalizeOrFallback(evidence.DownloadId, "-"),
                NormalizeOrFallback(evidence.RetagContext.AlbumMusicBrainzId, "-"),
                NormalizeOrFallback(evidence.RetagContext.PreferredRelease?.ReleaseMusicBrainzId, "-"),
                structuralEvidence.TrackCount);

            if (string.Equals(normalizedPhase, "guidance-needed", StringComparison.Ordinal))
            {
                _logger.Warn(
                    "Bandcamp reconciliation evidence lacks release specificity: downloadId={0}, title={1}, albumUrl={2}, recordingMbids={3}, releaseTrackMbids={4}",
                    NormalizeOrFallback(evidence.DownloadId, "-"),
                    NormalizeOrFallback(evidence.Title, "-"),
                    NormalizeOrFallback(evidence.AlbumUrl, "-"),
                    structuralEvidence.RecordingMusicBrainzIds.Count.ToString(CultureInfo.InvariantCulture),
                    structuralEvidence.ReleaseTrackMusicBrainzIds.Count.ToString(CultureInfo.InvariantCulture));
            }

            var reconciliationCase = new ReconciliationCase(
                sourceSeam: BandcampDownloadSourceSeam,
                phase: normalizedPhase,
                capturedIdentifiers: capturedIdentifiers,
                structuralEvidence: structuralEvidence,
                downloadId: evidence.DownloadId,
                outputPath: evidence.OutputPath,
                diagnosticSummary: diagnosticSummary,
                lastError: DetermineLastError(normalizedPhase, evidence.ErrorMessage),
                capturedAtUtc: capturedAtUtc);

            try
            {
                return _caseStore.Save(reconciliationCase);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                _logger.Error(ex, "Failed to capture Bandcamp reconciliation evidence for downloadId={0}.", NormalizeOrFallback(evidence.DownloadId, "-"));
                throw;
            }
        }

        private static IReadOnlyDictionary<string, string?> BuildCapturedIdentifiers(BandcampMismatchEvidence evidence)
        {
            var identifiers = new Dictionary<string, string?>(StringComparer.Ordinal);
            AddIdentifier(identifiers, "artistName", evidence.RetagContext.ArtistName);
            AddIdentifier(identifiers, "artistMusicBrainzId", evidence.RetagContext.ArtistMusicBrainzId);
            AddIdentifier(identifiers, "albumTitle", evidence.RetagContext.AlbumTitle);
            AddIdentifier(identifiers, "albumMusicBrainzId", evidence.RetagContext.AlbumMusicBrainzId);
            AddIdentifier(identifiers, "albumType", evidence.RetagContext.AlbumType);
            AddIdentifier(identifiers, "albumDisambiguation", evidence.RetagContext.AlbumDisambiguation);
            AddIdentifier(identifiers, "albumReleaseDateUtc", evidence.RetagContext.AlbumReleaseDateUtc?.ToString("O", CultureInfo.InvariantCulture));
            AddIdentifier(identifiers, "preferredReleaseMusicBrainzId", evidence.RetagContext.PreferredRelease?.ReleaseMusicBrainzId);
            AddIdentifier(identifiers, "releaseArtistMusicBrainzId", evidence.RetagContext.PreferredRelease?.ReleaseArtistMusicBrainzId);
            AddIdentifier(identifiers, "albumUrl", evidence.AlbumUrl);
            AddIdentifier(identifiers, "downloadTitle", evidence.Title);

            if (identifiers.Count == 0)
            {
                throw new ArgumentException("Bandcamp mismatch evidence did not include any stable identifiers.", nameof(evidence));
            }

            return identifiers;
        }

        private static ReconciliationEvidence BuildStructuralEvidence(BandcampMismatchEvidence evidence)
        {
            return new ReconciliationEvidence
            {
                TrackCount = evidence.RetagContext.Tracks.Count,
                PreferredRelease = evidence.RetagContext.PreferredRelease == null
                    ? null
                    : new PreferredReleaseReconciliationEvidence
                    {
                        ReleaseMusicBrainzId = evidence.RetagContext.PreferredRelease.ReleaseMusicBrainzId,
                        ReleaseArtistMusicBrainzId = evidence.RetagContext.PreferredRelease.ReleaseArtistMusicBrainzId,
                        ReleaseStatus = evidence.RetagContext.PreferredRelease.ReleaseStatus,
                        Label = evidence.RetagContext.PreferredRelease.Label,
                        ReleaseDateUtc = evidence.RetagContext.PreferredRelease.ReleaseDateUtc,
                        DiscCount = evidence.RetagContext.PreferredRelease.DiscCount,
                        MediaByDisc = new Dictionary<int, string>(evidence.RetagContext.PreferredRelease.MediaByDisc)
                    },
                Tracks = evidence.RetagContext.Tracks
                    .Select(static track => new ReconciliationTrackEvidence
                    {
                        AbsoluteTrackNumber = track.AbsoluteTrackNumber,
                        MediumNumber = track.MediumNumber,
                        Title = track.Title,
                        RecordingMusicBrainzId = track.RecordingMusicBrainzId,
                        ReleaseTrackMusicBrainzId = track.ReleaseTrackMusicBrainzId
                    })
                    .ToList()
            }.Normalize();
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

            return string.IsNullOrWhiteSpace(evidence.ErrorMessage) ? "captured" : "failed";
        }

        private static string? DetermineLastError(string phase, string? errorMessage)
        {
            return string.Equals(phase, "failed", StringComparison.Ordinal) ? errorMessage : null;
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
            AddDiagnosticPart(parts, "label", evidence.RetagContext.PreferredRelease?.Label);
            AddDiagnosticPart(parts, "discCount", evidence.RetagContext.PreferredRelease?.DiscCount.ToString(CultureInfo.InvariantCulture));
            AddDiagnosticPart(parts, "genres", evidence.RetagContext.Genres.Length > 0 ? string.Join(",", evidence.RetagContext.Genres) : null);
            AddDiagnosticPart(parts, "albumDisambiguation", evidence.RetagContext.AlbumDisambiguation);
            AddDiagnosticPart(parts, "completedAtUtc", evidence.CompletedAtUtc?.ToString("O", CultureInfo.InvariantCulture));

            return string.Join("; ", parts);
        }

        private static bool HasReleaseSpecificity(BandcampRetagEvidence retagContext)
        {
            ArgumentNullException.ThrowIfNull(retagContext);
            return !string.IsNullOrWhiteSpace(retagContext.AlbumMusicBrainzId)
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

        private static string? NormalizeOptional(string? value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        private static string NormalizeOrFallback(string? value, string fallback)
        {
            return NormalizeOptional(value) ?? fallback;
        }
    }
}
