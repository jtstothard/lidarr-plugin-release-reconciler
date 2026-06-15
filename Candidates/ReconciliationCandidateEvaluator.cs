using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Core.Plugins.ReleaseReconciler.Cases;

namespace NzbDrone.Core.Plugins.ReleaseReconciler.Candidates
{
    public sealed class ReconciliationCandidateEvaluator
    {
        private readonly IReconciliationCaseStore _caseStore;
        private readonly ILidarrAlbumLookup _lookup;
        private readonly Logger _logger;
        private readonly DeterministicReleaseScorer _scorer;

        public ReconciliationCandidateEvaluator(
            IReconciliationCaseStore caseStore,
            ILidarrAlbumLookup lookup,
            DeterministicReleaseScorer scorer,
            Logger logger)
        {
            _caseStore = caseStore ?? throw new ArgumentNullException(nameof(caseStore));
            _lookup = lookup ?? throw new ArgumentNullException(nameof(lookup));
            _scorer = scorer ?? throw new ArgumentNullException(nameof(scorer));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public ReconciliationCaseSnapshot Evaluate(ReconciliationCaseSnapshot snapshot)
        {
            ArgumentNullException.ThrowIfNull(snapshot);
            snapshot.ValidatePersistedShape();

            var structuralEvidence = snapshot.StructuralEvidence
                ?? throw new InvalidOperationException("Reconciliation case snapshot structural evidence is required for evaluation.");
            var albumTitle = RequireIdentifier(snapshot.CapturedIdentifiers, "album");
            var artistName = RequireIdentifier(snapshot.CapturedIdentifiers, "artist");
            var currentAlbumMusicBrainzId = GetIdentifier(snapshot.CapturedIdentifiers, "albumMusicBrainzId");
            var lookup = _lookup.LookupCandidates(albumTitle, artistName, structuralEvidence, currentAlbumMusicBrainzId);
            var rankedCandidates = _scorer.ScoreCandidates(structuralEvidence, snapshot.CapturedIdentifiers, lookup.Candidates);
            var evaluatedAtUtc = DateTimeOffset.UtcNow;
            var evaluationResult = BuildEvaluationResult(rankedCandidates, lookup.Attempts, lookup.UsedTitleArtistFallback, evaluatedAtUtc);

            var updatedCase = new ReconciliationCase(
                sourceSeam: snapshot.SourceSeam,
                phase: "evaluated",
                capturedIdentifiers: snapshot.CapturedIdentifiers,
                structuralEvidence: structuralEvidence,
                downloadId: snapshot.DownloadId,
                outputPath: snapshot.OutputPath,
                diagnosticSummary: snapshot.DiagnosticSummary,
                lastError: snapshot.LastError,
                caseId: snapshot.CaseId,
                capturedAtUtc: snapshot.CapturedAtUtc,
                updatedAtUtc: evaluatedAtUtc,
                evaluationResult: evaluationResult,
                notificationState: snapshot.NotificationState,
                operatorActionState: snapshot.OperatorActionState,
                releaseSwitchApprovalState: snapshot.ReleaseSwitchApprovalState);

            var persisted = _caseStore.Save(updatedCase);
            LogDecision(snapshot.CaseId, evaluationResult);
            return persisted;
        }

        private ReconciliationEvaluationResult BuildEvaluationResult(
            IReadOnlyList<RankedCandidateRelease> rankedCandidates,
            IReadOnlyList<LidarrAlbumLookupAttempt> lookupAttempts,
            bool usedTitleArtistFallback,
            DateTimeOffset evaluatedAtUtc)
        {
            var result = new ReconciliationEvaluationResult
            {
                Classification = ReconciliationClassification.NoSafeMatch,
                UsedTitleArtistFallback = usedTitleArtistFallback,
                EvaluatedAtUtc = evaluatedAtUtc,
                LookupAttempts = (lookupAttempts ?? Array.Empty<LidarrAlbumLookupAttempt>()).ToList(),
                RankedCandidates = rankedCandidates.Select(static candidate => candidate.Normalize()).ToList()
            };

            if (result.RankedCandidates.Count == 0)
            {
                result.RefusalReason = ReconciliationRefusalReason.NoCandidates;
                return result.Normalize();
            }

            var winner = result.RankedCandidates[0];
            var runnerUp = result.RankedCandidates.Count > 1 ? result.RankedCandidates[1] : null;
            var sameGroupCandidates = result.RankedCandidates.Where(static candidate => candidate.SameReleaseGroup).ToList();

            if (!winner.HasStrongIdentitySignal)
            {
                if (sameGroupCandidates.Count > 0)
                {
                    result.Classification = ReconciliationClassification.MissingReleaseInCurrentGroup;
                    result.RefusalReason = ReconciliationRefusalReason.SameReleaseGroupWithoutStrongReleaseMatch;
                    return result.Normalize();
                }

                result.RefusalReason = ReconciliationRefusalReason.TitleOnlyMatchNotAllowed;
                return result.Normalize();
            }

            if (runnerUp != null && runnerUp.HasStrongIdentitySignal)
            {
                var scoreMargin = winner.TotalScore - runnerUp.TotalScore;

                if (scoreMargin < DeterministicReleaseScorer.UniqueWinnerMargin)
                {
                    result.RefusalReason = ReconciliationRefusalReason.AmbiguousWinnerMargin;
                    return result.Normalize();
                }
            }

            result.Classification = winner.SameReleaseGroup
                ? ReconciliationClassification.BetterExistingRelease
                : ReconciliationClassification.DifferentReleaseGroup;
            result.WinnerAlbumMusicBrainzId = winner.AlbumMusicBrainzId;
            result.WinnerReleaseMusicBrainzId = winner.ReleaseMusicBrainzId;
            return result.Normalize();
        }

        private void LogDecision(string caseId, ReconciliationEvaluationResult result)
        {
            var topCandidate = result.RankedCandidates.FirstOrDefault();

            if (result.UsedTitleArtistFallback)
            {
                var fallbackAttempt = result.LookupAttempts.FirstOrDefault(attempt => attempt.Path == CandidateLookupPath.TitleArtistFallback);
                _logger.Info(
                    "Reconciliation case {0} used title/artist fallback: attempted={1}, reason={2}",
                    caseId,
                    fallbackAttempt?.Attempted ?? false,
                    fallbackAttempt?.Reason ?? "none");
            }

            if (result.RefusalReason == ReconciliationRefusalReason.AmbiguousWinnerMargin && result.RankedCandidates.Count > 1)
            {
                var runnerUp = result.RankedCandidates[1];
                _logger.Warn(
                    "Reconciliation case {0} refused ambiguous winner: topRelease={1}, topScore={2}, runnerUpRelease={3}, runnerUpScore={4}, margin={5}",
                    caseId,
                    topCandidate?.ReleaseMusicBrainzId ?? "-",
                    topCandidate?.TotalScore ?? 0,
                    runnerUp.ReleaseMusicBrainzId,
                    runnerUp.TotalScore,
                    (topCandidate?.TotalScore ?? 0) - runnerUp.TotalScore);
            }
            else if (result.RefusalReason == ReconciliationRefusalReason.SameReleaseGroupWithoutStrongReleaseMatch)
            {
                _logger.Info(
                    "Reconciliation case {0} detected same release group without a strong release match: topRelease={1}, score={2}",
                    caseId,
                    topCandidate?.ReleaseMusicBrainzId ?? "-",
                    topCandidate?.TotalScore ?? 0);
            }
            else if (result.RefusalReason == ReconciliationRefusalReason.TitleOnlyMatchNotAllowed)
            {
                _logger.Warn(
                    "Reconciliation case {0} refused title-only candidate promotion: topRelease={1}, score={2}",
                    caseId,
                    topCandidate?.ReleaseMusicBrainzId ?? "-",
                    topCandidate?.TotalScore ?? 0);
            }
            else if (result.RefusalReason == ReconciliationRefusalReason.NoCandidates)
            {
                _logger.Warn("Reconciliation case {0} evaluation produced no candidates.", caseId);
            }

            _logger.Info(
                "Evaluated reconciliation case {0}: classification={1}, refusal={2}, rankedCandidates={3}, winnerRelease={4}, winnerAlbum={5}",
                caseId,
                result.Classification,
                result.RefusalReason?.ToString() ?? "none",
                result.RankedCandidates.Count,
                result.WinnerReleaseMusicBrainzId ?? "-",
                result.WinnerAlbumMusicBrainzId ?? "-");
        }

        private static string RequireIdentifier(IReadOnlyDictionary<string, string?> capturedIdentifiers, string key)
        {
            ArgumentNullException.ThrowIfNull(capturedIdentifiers);

            if (!capturedIdentifiers.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidOperationException($"Captured identifier '{key}' is required for deterministic candidate evaluation.");
            }

            return value.Trim();
        }

        private static string? GetIdentifier(IReadOnlyDictionary<string, string?> capturedIdentifiers, string key)
        {
            ArgumentNullException.ThrowIfNull(capturedIdentifiers);
            return capturedIdentifiers.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
                ? value.Trim()
                : null;
        }
    }
}
