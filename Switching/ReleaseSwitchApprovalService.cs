using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.Music;
using NzbDrone.Core.Plugins.ReleaseReconciler.Actions;
using NzbDrone.Core.Plugins.ReleaseReconciler.Candidates;
using NzbDrone.Core.Plugins.ReleaseReconciler.Cases;

namespace NzbDrone.Core.Plugins.ReleaseReconciler.Switching
{
    public sealed class ReleaseSwitchApprovalService
    {
        private readonly IAlbumService _albumService;
        private readonly IProvideAlbumInfo _albumInfo;
        private readonly IReconciliationCaseStore _caseStore;
        private readonly Func<DateTimeOffset> _clock;
        private readonly ReconciliationCandidateEvaluator _evaluator;
        private readonly Logger _logger;
        private readonly IRefreshAlbumService _refreshAlbumService;
        private readonly IReleaseService _releaseService;
        private readonly ReleaseSwitchSettingsStore _settingsStore;

        public ReleaseSwitchApprovalService(
            IReconciliationCaseStore caseStore,
            ReconciliationCandidateEvaluator evaluator,
            ReleaseSwitchSettingsStore settingsStore,
            IAlbumService albumService,
            IReleaseService releaseService,
            IProvideAlbumInfo albumInfo,
            IRefreshAlbumService refreshAlbumService,
            Logger logger,
            Func<DateTimeOffset>? clock = null)
        {
            _caseStore = caseStore ?? throw new ArgumentNullException(nameof(caseStore));
            _evaluator = evaluator ?? throw new ArgumentNullException(nameof(evaluator));
            _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
            _albumService = albumService ?? throw new ArgumentNullException(nameof(albumService));
            _releaseService = releaseService ?? throw new ArgumentNullException(nameof(releaseService));
            _albumInfo = albumInfo ?? throw new ArgumentNullException(nameof(albumInfo));
            _refreshAlbumService = refreshAlbumService ?? throw new ArgumentNullException(nameof(refreshAlbumService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _clock = clock ?? (() => DateTimeOffset.UtcNow);
        }

        public ReleaseSwitchApprovalResult Approve(
            ReconciliationCaseSnapshot snapshot,
            ReconciliationOperatorActionTokenValidationResult validation)
        {
            ArgumentNullException.ThrowIfNull(snapshot);
            ArgumentNullException.ThrowIfNull(validation);
            snapshot.ValidatePersistedShape();

            var reevaluatedSnapshot = _evaluator.Evaluate(snapshot);
            var processedAtUtc = _clock();
            var audit = BuildAuditSeed(reevaluatedSnapshot, validation, processedAtUtc);
            var target = validation.Payload?.ReleaseSwitchTarget;
            var settings = _settingsStore.Load();

            if (!settings.EnableReleaseSwitchApproval)
            {
                return Refuse(reevaluatedSnapshot, audit, processedAtUtc, "Release-switch approvals are disabled.");
            }

            if (target == null || string.IsNullOrWhiteSpace(target.AlbumMusicBrainzId) || string.IsNullOrWhiteSpace(target.ReleaseMusicBrainzId))
            {
                return Persist(reevaluatedSnapshot, audit, ReconciliationOperatorActionOutcomes.InvalidPayload, "The signed approval link is missing its target release.", processedAtUtc);
            }

            var evaluation = reevaluatedSnapshot.EvaluationResult;
            if (evaluation == null)
            {
                return Refuse(reevaluatedSnapshot, audit, processedAtUtc, "A fresh reconciliation evaluation could not be loaded.");
            }

            EnrichAuditFromEvaluation(audit, evaluation, target);

            if (evaluation.RefusalReason.HasValue)
            {
                return Refuse(reevaluatedSnapshot, audit, processedAtUtc, $"This approval is no longer eligible: {evaluation.RefusalReason.Value}.");
            }

            if (evaluation.UsedTitleArtistFallback)
            {
                return Refuse(reevaluatedSnapshot, audit, processedAtUtc, "Title-fallback winners cannot be approved for release switching.");
            }

            if (evaluation.Classification is not ReconciliationClassification.BetterExistingRelease and not ReconciliationClassification.DifferentReleaseGroup)
            {
                return Refuse(reevaluatedSnapshot, audit, processedAtUtc, $"Only BetterExistingRelease or DifferentReleaseGroup winners can be approved. Current classification: {evaluation.Classification}.");
            }

            if (string.IsNullOrWhiteSpace(evaluation.WinnerAlbumMusicBrainzId) || string.IsNullOrWhiteSpace(evaluation.WinnerReleaseMusicBrainzId))
            {
                return Refuse(reevaluatedSnapshot, audit, processedAtUtc, "The reevaluated winner is missing required MusicBrainz identifiers.");
            }

            if (!string.Equals(evaluation.WinnerAlbumMusicBrainzId, target.AlbumMusicBrainzId, StringComparison.Ordinal) ||
                !string.Equals(evaluation.WinnerReleaseMusicBrainzId, target.ReleaseMusicBrainzId, StringComparison.Ordinal))
            {
                return Refuse(reevaluatedSnapshot, audit, processedAtUtc, "The approved winner no longer matches the latest reconciliation result.");
            }

            var expectedClassification = evaluation.Classification.ToString();
            if (!string.IsNullOrWhiteSpace(target.Classification) &&
                !string.Equals(target.Classification, expectedClassification, StringComparison.OrdinalIgnoreCase))
            {
                return Persist(reevaluatedSnapshot, audit, ReconciliationOperatorActionOutcomes.InvalidPayload, $"The signed approval link does not represent the current {expectedClassification} winner.", processedAtUtc);
            }

            var winner = evaluation.RankedCandidates.FirstOrDefault(candidate =>
                string.Equals(candidate.AlbumMusicBrainzId, target.AlbumMusicBrainzId, StringComparison.Ordinal) &&
                string.Equals(candidate.ReleaseMusicBrainzId, target.ReleaseMusicBrainzId, StringComparison.Ordinal));

            if (winner == null)
            {
                return Refuse(reevaluatedSnapshot, audit, processedAtUtc, "The reevaluated winner is no longer present in ranked candidates.");
            }

            if (evaluation.Classification == ReconciliationClassification.BetterExistingRelease && !winner.SameReleaseGroup)
            {
                return Refuse(reevaluatedSnapshot, audit, processedAtUtc, "The reevaluated winner no longer belongs to the current release group.");
            }

            if (evaluation.Classification == ReconciliationClassification.DifferentReleaseGroup && winner.SameReleaseGroup)
            {
                return Refuse(reevaluatedSnapshot, audit, processedAtUtc, "The reevaluated winner no longer requires a different-group switch.");
            }

            if (!winner.HasStrongIdentitySignal || winner.TitleOnlyCandidate)
            {
                return Refuse(reevaluatedSnapshot, audit, processedAtUtc, "Only high-confidence winners may be approved for release switching.");
            }

            return evaluation.Classification switch
            {
                ReconciliationClassification.BetterExistingRelease => ApplySameGroupSwitch(reevaluatedSnapshot, audit, target, winner, processedAtUtc),
                ReconciliationClassification.DifferentReleaseGroup => settings.EnableDifferentGroupReleaseSwitchApproval
                    ? ApplyDifferentGroupSwitch(reevaluatedSnapshot, audit, target, winner, processedAtUtc)
                    : Refuse(reevaluatedSnapshot, audit, processedAtUtc, "Different-group release switches are disabled by policy."),
                _ => Refuse(reevaluatedSnapshot, audit, processedAtUtc, $"Unsupported release-switch classification: {evaluation.Classification}.")
            };
        }

        private ReleaseSwitchApprovalResult ApplySameGroupSwitch(
            ReconciliationCaseSnapshot snapshot,
            ReconciliationReleaseSwitchApprovalState audit,
            ReconciliationReleaseSwitchTarget target,
            RankedCandidateRelease winner,
            DateTimeOffset processedAtUtc)
        {
            var targetRelease = _releaseService.GetReleaseByForeignReleaseId(target.ReleaseMusicBrainzId);
            if (targetRelease == null)
            {
                return Refuse(snapshot, audit, processedAtUtc, "The target release could not be found in Lidarr.");
            }

            var targetAlbum = _albumService.GetAlbum(targetRelease.AlbumId);
            audit.AfterAlbumMusicBrainzId = NormalizeOptional(targetAlbum?.ForeignAlbumId) ?? audit.AfterAlbumMusicBrainzId;

            var currentAlbumReleases = _releaseService.GetReleasesByAlbum(targetRelease.AlbumId) ?? new List<AlbumRelease>();
            var monitoredReleases = currentAlbumReleases.Where(static release => release.Monitored).ToList();

            if (monitoredReleases.Count == 0)
            {
                return Refuse(snapshot, audit, processedAtUtc, "No currently monitored release exists for the winning album.");
            }

            if (monitoredReleases.Count > 1)
            {
                return Refuse(snapshot, audit, processedAtUtc, "Multiple monitored releases exist for the winning album, so the switch target cannot be chosen safely.");
            }

            var currentMonitored = monitoredReleases[0];
            audit.BeforeReleaseMusicBrainzId ??= NormalizeOptional(currentMonitored.ForeignReleaseId);
            audit.BeforeAlbumMusicBrainzId ??= NormalizeOptional(targetAlbum?.ForeignAlbumId);

            if (string.Equals(currentMonitored.ForeignReleaseId, target.ReleaseMusicBrainzId, StringComparison.Ordinal))
            {
                audit.AfterReleaseMusicBrainzId = NormalizeOptional(currentMonitored.ForeignReleaseId);
                audit.AfterAlbumMusicBrainzId ??= NormalizeOptional(targetAlbum?.ForeignAlbumId);
                return Persist(snapshot, audit, ReconciliationOperatorActionOutcomes.AlreadyApplied, "This release switch was already applied.", processedAtUtc);
            }

            if (!string.IsNullOrWhiteSpace(audit.BeforeReleaseMusicBrainzId) &&
                !string.Equals(currentMonitored.ForeignReleaseId, audit.BeforeReleaseMusicBrainzId, StringComparison.Ordinal))
            {
                return Refuse(snapshot, audit, processedAtUtc, "The monitored release changed before this approval could be applied.");
            }

            targetRelease.Monitored = true;
            var updatedReleases = _releaseService.SetMonitored(targetRelease) ?? new List<AlbumRelease>();
            var appliedRelease = updatedReleases.FirstOrDefault(release => string.Equals(release.ForeignReleaseId, target.ReleaseMusicBrainzId, StringComparison.Ordinal))
                               ?? targetRelease;

            audit.AppliedAtUtc = processedAtUtc;
            audit.RefusedAtUtc = null;
            audit.Outcome = ReconciliationOperatorActionOutcomes.Applied;
            audit.RefusalSummary = null;
            audit.AfterReleaseMusicBrainzId = NormalizeOptional(appliedRelease.ForeignReleaseId) ?? target.ReleaseMusicBrainzId;
            audit.AfterAlbumMusicBrainzId = NormalizeOptional(targetAlbum?.ForeignAlbumId) ?? target.AlbumMusicBrainzId;

            var persisted = PersistSnapshot(snapshot, audit, processedAtUtc);
            _logger.Info(
                "Approved same-group release switch caseId={0} beforeRelease={1} afterRelease={2} targetAlbum={3}.",
                persisted.CaseId,
                audit.BeforeReleaseMusicBrainzId ?? "none",
                audit.AfterReleaseMusicBrainzId ?? "none",
                audit.AfterAlbumMusicBrainzId ?? "none");

            return new ReleaseSwitchApprovalResult
            {
                Snapshot = persisted,
                Result = ReconciliationOperatorActionOutcomes.Applied,
                Message = $"Approved same-group release switch to {winner.ReleaseTitle} ({target.ReleaseMusicBrainzId}).",
                ProcessedAtUtc = processedAtUtc
            };
        }

        private ReleaseSwitchApprovalResult ApplyDifferentGroupSwitch(
            ReconciliationCaseSnapshot snapshot,
            ReconciliationReleaseSwitchApprovalState audit,
            ReconciliationReleaseSwitchTarget target,
            RankedCandidateRelease winner,
            DateTimeOffset processedAtUtc)
        {
            var currentReleaseMusicBrainzId = audit.BeforeReleaseMusicBrainzId;
            if (string.IsNullOrWhiteSpace(currentReleaseMusicBrainzId))
            {
                return Refuse(snapshot, audit, processedAtUtc, "The current monitored release could not be identified for the guarded different-group switch.");
            }

            var currentRelease = _releaseService.GetReleaseByForeignReleaseId(currentReleaseMusicBrainzId);
            if (currentRelease == null)
            {
                return Refuse(snapshot, audit, processedAtUtc, "The current monitored release could not be found in Lidarr.");
            }

            var currentAlbum = _albumService.GetAlbum(currentRelease.AlbumId);
            audit.BeforeAlbumMusicBrainzId ??= NormalizeOptional(currentAlbum?.ForeignAlbumId);

            var currentAlbumReleases = _releaseService.GetReleasesByAlbum(currentRelease.AlbumId) ?? new List<AlbumRelease>();
            var monitoredReleases = currentAlbumReleases.Where(static release => release.Monitored).ToList();

            if (monitoredReleases.Count == 0)
            {
                return Refuse(snapshot, audit, processedAtUtc, "No currently monitored release exists for the current library album.");
            }

            if (monitoredReleases.Count > 1)
            {
                return Refuse(snapshot, audit, processedAtUtc, "Multiple monitored releases exist for the current library album, so the guarded switch cannot be applied safely.");
            }

            var currentMonitored = monitoredReleases[0];
            audit.BeforeReleaseMusicBrainzId ??= NormalizeOptional(currentMonitored.ForeignReleaseId);
            if (!string.IsNullOrWhiteSpace(audit.BeforeReleaseMusicBrainzId) &&
                !string.Equals(currentMonitored.ForeignReleaseId, audit.BeforeReleaseMusicBrainzId, StringComparison.Ordinal))
            {
                return Refuse(snapshot, audit, processedAtUtc, "The monitored release changed before this approval could be applied.");
            }

            var existingTargetRelease = _releaseService.GetReleaseByForeignReleaseId(target.ReleaseMusicBrainzId);
            if (existingTargetRelease != null)
            {
                var existingTargetAlbum = _albumService.GetAlbum(existingTargetRelease.AlbumId);
                audit.AfterAlbumMusicBrainzId = NormalizeOptional(existingTargetAlbum?.ForeignAlbumId) ?? audit.AfterAlbumMusicBrainzId;
                audit.AfterReleaseMusicBrainzId = NormalizeOptional(existingTargetRelease.ForeignReleaseId) ?? audit.AfterReleaseMusicBrainzId;

                return Refuse(snapshot, audit, processedAtUtc, "The target album already exists in Lidarr.");
            }

            Album remoteAlbum;
            try
            {
                remoteAlbum = _albumInfo.GetAlbumInfo(target.AlbumMusicBrainzId)?.Item2
                    ?? throw new InvalidOperationException("Remote album lookup returned no album payload.");
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Rejected different-group release switch caseId={0} targetAlbum={1} reason=hydrate-failure.", snapshot.CaseId, target.AlbumMusicBrainzId);
                return Refuse(snapshot, audit, processedAtUtc, "The target album could not be hydrated from Lidarr metadata.");
            }

            if (!string.Equals(NormalizeOptional(remoteAlbum.ForeignAlbumId), target.AlbumMusicBrainzId, StringComparison.Ordinal))
            {
                return Refuse(snapshot, audit, processedAtUtc, "The hydrated target album did not match the approved winner.");
            }

            var existingTargetAlbumByArtist = (_albumService.GetAlbumsByArtistMetadataId(remoteAlbum.ArtistMetadataId) ?? new List<Album>())
                .FirstOrDefault(album =>
                    album.Id != currentRelease.AlbumId &&
                    string.Equals(NormalizeOptional(album.ForeignAlbumId), target.AlbumMusicBrainzId, StringComparison.Ordinal));

            if (existingTargetAlbumByArtist != null)
            {
                audit.AfterAlbumMusicBrainzId = NormalizeOptional(existingTargetAlbumByArtist.ForeignAlbumId) ?? audit.AfterAlbumMusicBrainzId;
                return Refuse(snapshot, audit, processedAtUtc, "The target album already exists in Lidarr.");
            }

            remoteAlbum.Monitored = currentAlbum?.Monitored ?? true;
            Album? addedAlbum = null;

            try
            {
                addedAlbum = _albumService.AddAlbum(remoteAlbum, false);
                if (addedAlbum == null)
                {
                    return Refuse(snapshot, audit, processedAtUtc, "The target album could not be created in Lidarr.");
                }

                var refreshSucceeded = _refreshAlbumService.RefreshAlbumInfo(addedAlbum, new List<Album> { remoteAlbum }, false);
                if (!refreshSucceeded)
                {
                    DeleteAddedAlbum(addedAlbum, snapshot.CaseId);
                    return Refuse(snapshot, audit, processedAtUtc, "The target album refresh failed before the switch could be applied.");
                }

                var hydratedTargetRelease = _releaseService.GetReleaseByForeignReleaseId(target.ReleaseMusicBrainzId);
                if (hydratedTargetRelease == null || hydratedTargetRelease.AlbumId != addedAlbum.Id)
                {
                    DeleteAddedAlbum(addedAlbum, snapshot.CaseId);
                    return Refuse(snapshot, audit, processedAtUtc, "The hydrated target album did not expose the approved release.");
                }

                hydratedTargetRelease.Monitored = true;
                var updatedReleases = _releaseService.SetMonitored(hydratedTargetRelease) ?? new List<AlbumRelease>();
                var appliedRelease = updatedReleases.FirstOrDefault(release => string.Equals(release.ForeignReleaseId, target.ReleaseMusicBrainzId, StringComparison.Ordinal))
                                   ?? hydratedTargetRelease;

                audit.AppliedAtUtc = processedAtUtc;
                audit.RefusedAtUtc = null;
                audit.Outcome = ReconciliationOperatorActionOutcomes.Applied;
                audit.RefusalSummary = null;
                audit.AfterReleaseMusicBrainzId = NormalizeOptional(appliedRelease.ForeignReleaseId) ?? target.ReleaseMusicBrainzId;
                audit.AfterAlbumMusicBrainzId = NormalizeOptional(addedAlbum.ForeignAlbumId) ?? target.AlbumMusicBrainzId;

                var persisted = PersistSnapshot(snapshot, audit, processedAtUtc);
                _logger.Info(
                    "Approved different-group release switch caseId={0} beforeAlbum={1} afterAlbum={2} beforeRelease={3} afterRelease={4}.",
                    persisted.CaseId,
                    audit.BeforeAlbumMusicBrainzId ?? "none",
                    audit.AfterAlbumMusicBrainzId ?? "none",
                    audit.BeforeReleaseMusicBrainzId ?? "none",
                    audit.AfterReleaseMusicBrainzId ?? "none");

                return new ReleaseSwitchApprovalResult
                {
                    Snapshot = persisted,
                    Result = ReconciliationOperatorActionOutcomes.Applied,
                    Message = $"Approved different-group release switch to {winner.ReleaseTitle} ({target.ReleaseMusicBrainzId}).",
                    ProcessedAtUtc = processedAtUtc
                };
            }
            catch (Exception ex)
            {
                if (addedAlbum != null)
                {
                    DeleteAddedAlbum(addedAlbum, snapshot.CaseId);
                }

                _logger.Warn(ex, "Rejected different-group release switch caseId={0} targetAlbum={1} targetRelease={2} reason=guarded-mutation-failure.", snapshot.CaseId, target.AlbumMusicBrainzId, target.ReleaseMusicBrainzId);
                return Refuse(snapshot, audit, processedAtUtc, "The target album could not be applied safely after hydration.");
            }
        }

        private void DeleteAddedAlbum(Album addedAlbum, string caseId)
        {
            try
            {
                _albumService.DeleteAlbum(addedAlbum.Id, false);
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Failed to roll back hydrated album caseId={0} albumId={1} foreignAlbumId={2}.", caseId, addedAlbum.Id, NormalizeOptional(addedAlbum.ForeignAlbumId) ?? "none");
            }
        }

        private ReleaseSwitchApprovalResult Refuse(
            ReconciliationCaseSnapshot snapshot,
            ReconciliationReleaseSwitchApprovalState audit,
            DateTimeOffset processedAtUtc,
            string message)
        {
            return Persist(snapshot, audit, ReconciliationOperatorActionOutcomes.Refused, message, processedAtUtc);
        }

        private ReleaseSwitchApprovalResult Persist(
            ReconciliationCaseSnapshot snapshot,
            ReconciliationReleaseSwitchApprovalState audit,
            string result,
            string message,
            DateTimeOffset processedAtUtc)
        {
            audit.Outcome = result;
            audit.AttemptedAtUtc = processedAtUtc;

            if (!string.Equals(result, ReconciliationOperatorActionOutcomes.Applied, StringComparison.Ordinal))
            {
                audit.RefusedAtUtc = processedAtUtc;
                audit.RefusalSummary = message;
            }

            var persisted = PersistSnapshot(snapshot, audit, processedAtUtc);
            return new ReleaseSwitchApprovalResult
            {
                Snapshot = persisted,
                Result = result,
                Message = message,
                ProcessedAtUtc = processedAtUtc
            };
        }

        private ReconciliationCaseSnapshot PersistSnapshot(
            ReconciliationCaseSnapshot snapshot,
            ReconciliationReleaseSwitchApprovalState audit,
            DateTimeOffset updatedAtUtc)
        {
            var updatedCase = new ReconciliationCase(
                sourceSeam: snapshot.SourceSeam,
                phase: snapshot.Phase,
                capturedIdentifiers: snapshot.CapturedIdentifiers,
                structuralEvidence: snapshot.StructuralEvidence ?? new ReconciliationEvidence(),
                downloadId: snapshot.DownloadId,
                outputPath: snapshot.OutputPath,
                diagnosticSummary: snapshot.DiagnosticSummary,
                lastError: snapshot.LastError,
                caseId: snapshot.CaseId,
                capturedAtUtc: snapshot.CapturedAtUtc,
                updatedAtUtc: updatedAtUtc,
                evaluationResult: snapshot.EvaluationResult,
                notificationState: snapshot.NotificationState,
                operatorActionState: snapshot.OperatorActionState,
                releaseSwitchApprovalState: audit);

            return _caseStore.Save(updatedCase);
        }

        private ReconciliationReleaseSwitchApprovalState BuildAuditSeed(
            ReconciliationCaseSnapshot snapshot,
            ReconciliationOperatorActionTokenValidationResult validation,
            DateTimeOffset attemptedAtUtc)
        {
            var audit = snapshot.ReleaseSwitchApprovalState?.Normalize() ?? new ReconciliationReleaseSwitchApprovalState();
            audit.AttemptedAtUtc = attemptedAtUtc;
            audit.Transport = NormalizeOptional(validation.Transport) ?? audit.Transport;
            audit.ActionTokenHash = NormalizeOptional(validation.ActionTokenHash) ?? audit.ActionTokenHash;
            audit.RequestedAlbumMusicBrainzId = NormalizeOptional(validation.Payload?.ReleaseSwitchTarget?.AlbumMusicBrainzId) ?? audit.RequestedAlbumMusicBrainzId;
            audit.RequestedReleaseMusicBrainzId = NormalizeOptional(validation.Payload?.ReleaseSwitchTarget?.ReleaseMusicBrainzId) ?? audit.RequestedReleaseMusicBrainzId;
            audit.Classification = NormalizeOptional(validation.Payload?.ReleaseSwitchTarget?.Classification) ?? audit.Classification;
            audit.ScoringVersion = NormalizeOptional(validation.Payload?.ReleaseSwitchTarget?.ScoringVersion) ?? audit.ScoringVersion;
            audit.BeforeAlbumMusicBrainzId = GetIdentifier(snapshot.CapturedIdentifiers, "albumMusicBrainzId") ?? audit.BeforeAlbumMusicBrainzId;
            audit.BeforeReleaseMusicBrainzId = NormalizeOptional(snapshot.StructuralEvidence?.PreferredReleaseMusicBrainzId) ?? audit.BeforeReleaseMusicBrainzId;
            return audit;
        }

        private static void EnrichAuditFromEvaluation(
            ReconciliationReleaseSwitchApprovalState audit,
            ReconciliationEvaluationResult evaluation,
            ReconciliationReleaseSwitchTarget target)
        {
            audit.Classification = evaluation.Classification.ToString();
            audit.RefusalReason = evaluation.RefusalReason?.ToString();
            audit.ScoringVersion = NormalizeOptional(evaluation.ScoringVersion) ?? audit.ScoringVersion;

            var winner = evaluation.RankedCandidates.FirstOrDefault(candidate =>
                string.Equals(candidate.AlbumMusicBrainzId, target.AlbumMusicBrainzId, StringComparison.Ordinal) &&
                string.Equals(candidate.ReleaseMusicBrainzId, target.ReleaseMusicBrainzId, StringComparison.Ordinal));

            if (winner == null)
            {
                return;
            }

            audit.CandidateTotalScore = winner.TotalScore;
            audit.CandidateStrongSignalScore = winner.StrongSignalScore;
            audit.CandidateSameReleaseGroup = winner.SameReleaseGroup;
            audit.CandidateHasStrongIdentitySignal = winner.HasStrongIdentitySignal;
            audit.CandidateTitleOnly = winner.TitleOnlyCandidate;
        }

        private static string? GetIdentifier(IReadOnlyDictionary<string, string?> capturedIdentifiers, string key)
        {
            if (capturedIdentifiers.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }

            return null;
        }

        private static string? NormalizeOptional(string? value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
    }
}
