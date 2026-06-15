using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Lidarr.Http.Frontend.Mappers;
using Microsoft.AspNetCore.Mvc;
using NLog;
using NzbDrone.Core.Plugins.ReleaseReconciler.Candidates;
using NzbDrone.Core.Plugins.ReleaseReconciler.Cases;
using NzbDrone.Core.Plugins.ReleaseReconciler.Switching;

namespace NzbDrone.Core.Plugins.ReleaseReconciler.Actions
{
    public sealed class ReconciliationOperatorActionMapper : IMapHttpRequestsToDisk
    {
        private static readonly Regex ActionPathRegex = new(
            "^/release-reconciler/action/(?<token>[A-Za-z0-9_-]+)/index\\.html$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        private readonly IReconciliationCaseStore _caseStore;
        private readonly OperatorActionTokenService _tokenService;
        private readonly ReleaseSwitchApprovalService? _releaseSwitchApprovalService;
        private readonly Logger _logger;
        private readonly Func<DateTimeOffset> _clock;

        public ReconciliationOperatorActionMapper(
            IReconciliationCaseStore caseStore,
            OperatorActionTokenService tokenService,
            Logger logger,
            Func<DateTimeOffset>? clock = null,
            ReleaseSwitchApprovalService? releaseSwitchApprovalService = null)
        {
            _caseStore = caseStore ?? throw new ArgumentNullException(nameof(caseStore));
            _tokenService = tokenService ?? throw new ArgumentNullException(nameof(tokenService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _clock = clock ?? (() => DateTimeOffset.UtcNow);
            _releaseSwitchApprovalService = releaseSwitchApprovalService;
        }

        public string Map(string resourceUrl)
        {
            return resourceUrl;
        }

        public bool CanHandle(string resourceUrl)
        {
            return !string.IsNullOrWhiteSpace(resourceUrl) && ActionPathRegex.IsMatch(resourceUrl);
        }

        public Task<IActionResult> GetResponse(string resourceUrl)
        {
            var match = ActionPathRegex.Match(resourceUrl ?? string.Empty);
            if (!match.Success)
            {
                return Task.FromResult<IActionResult>(OperatorActionResultPage.Refusal(404, "Unknown action link", "The release reconciler action link could not be matched."));
            }

            var token = match.Groups["token"].Value;
            var validation = _tokenService.Validate(token);
            if (validation.CaseId == null)
            {
                _logger.Warn(
                    "Rejected release reconciler operator action path action={0} result={1} tokenFingerprintPrefix={2} reason={3}.",
                    validation.Action ?? "unknown",
                    validation.Result,
                    validation.TokenFingerprintPrefix ?? "none",
                    validation.FailureSummary ?? "No case id could be extracted.");

                return Task.FromResult<IActionResult>(BuildRefusalPage(validation.Result, validation.FailureSummary ?? "The action link could not be verified."));
            }

            var snapshot = _caseStore.Get(validation.CaseId);
            if (snapshot == null)
            {
                _logger.Warn(
                    "Rejected release reconciler operator action caseId={0} action={1} result={2} tokenFingerprintPrefix={3} reason=case-missing.",
                    validation.CaseId,
                    validation.Action ?? "unknown",
                    ReconciliationOperatorActionOutcomes.MissingCase,
                    validation.TokenFingerprintPrefix ?? "none");

                return Task.FromResult<IActionResult>(BuildRefusalPage(ReconciliationOperatorActionOutcomes.MissingCase, "This reconciliation case is no longer available."));
            }

            var state = snapshot.OperatorActionState?.Normalize() ?? new ReconciliationOperatorActionState();
            var existingReceipt = state.ProcessedActionReceipts.FirstOrDefault(receipt => string.Equals(receipt.ActionTokenHash, validation.ActionTokenHash, StringComparison.Ordinal));
            if (existingReceipt != null)
            {
                var replayedCase = SaveState(
                    snapshot,
                    RecordAudit(state, validation, ReconciliationOperatorActionOutcomes.Replay, "This operator action link was already used.", includeReceipt: false),
                    RecordReleaseSwitchAudit(snapshot.ReleaseSwitchApprovalState, validation, ReconciliationOperatorActionOutcomes.Replay, "This operator action link was already used."));
                _logger.Info(
                    "Rejected replayed release reconciler operator action caseId={0} action={1} result={2} tokenFingerprintPrefix={3}.",
                    replayedCase.CaseId,
                    validation.Action ?? existingReceipt.Action,
                    ReconciliationOperatorActionOutcomes.Replay,
                    validation.TokenFingerprintPrefix ?? "none");

                return Task.FromResult<IActionResult>(BuildRefusalPage(ReconciliationOperatorActionOutcomes.Replay, "This operator action link was already used."));
            }

            if (!validation.Succeeded)
            {
                var refusedCase = SaveState(
                    snapshot,
                    RecordAudit(state, validation, validation.Result, validation.FailureSummary ?? "The action link was refused."),
                    RecordReleaseSwitchAudit(snapshot.ReleaseSwitchApprovalState, validation, validation.Result, validation.FailureSummary ?? "The action link was refused."));
                _logger.Warn(
                    "Rejected release reconciler operator action caseId={0} action={1} result={2} tokenFingerprintPrefix={3} reason={4}.",
                    refusedCase.CaseId,
                    validation.Action ?? "unknown",
                    validation.Result,
                    validation.TokenFingerprintPrefix ?? "none",
                    validation.FailureSummary ?? "none");

                return Task.FromResult<IActionResult>(BuildRefusalPage(validation.Result, validation.FailureSummary ?? "The action link was refused."));
            }

            var updatedState = state.Normalize();
            var appliedAtUtc = _clock();
            var result = ApplyAction(snapshot, updatedState, validation, appliedAtUtc);
            var savedCase = SaveState(result.Snapshot, result.State, result.ReleaseSwitchApprovalState);

            _logger.Info(
                "Processed release reconciler operator action caseId={0} action={1} result={2} tokenFingerprintPrefix={3} expiresAtUtc={4:o}.",
                savedCase.CaseId,
                validation.Action,
                result.Result,
                validation.TokenFingerprintPrefix ?? "none",
                validation.ExpiresAtUtc ?? DateTimeOffset.MinValue);

            if (string.Equals(result.Result, ReconciliationOperatorActionOutcomes.Applied, StringComparison.Ordinal))
            {
                return Task.FromResult<IActionResult>(OperatorActionResultPage.Success(
                    title: $"{ToTitle(validation.Action!)} applied",
                    message: result.SuccessMessage));
            }

            return Task.FromResult<IActionResult>(BuildRefusalPage(result.Result, result.SuccessMessage));
        }

        private AppliedActionResult ApplyAction(ReconciliationCaseSnapshot snapshot, ReconciliationOperatorActionState state, ReconciliationOperatorActionTokenValidationResult validation, DateTimeOffset appliedAtUtc)
        {
            var normalizedAction = ReconciliationOperatorActionKinds.Normalize(validation.Action!);
            switch (normalizedAction)
            {
                case ReconciliationOperatorActionKinds.Snooze:
                    if (state.IgnoredAtUtc.HasValue)
                    {
                        return new AppliedActionResult(
                            snapshot,
                            RecordAudit(state, validation, ReconciliationOperatorActionOutcomes.AlreadyApplied, "This case was already ignored."),
                            ReconciliationOperatorActionOutcomes.AlreadyApplied,
                            "This case was already ignored.");
                    }

                    if (state.SnoozedUntilUtc.HasValue && state.SnoozedUntilUtc.Value >= validation.ExpiresAtUtc)
                    {
                        return new AppliedActionResult(
                            snapshot,
                            RecordAudit(state, validation, ReconciliationOperatorActionOutcomes.AlreadyApplied, "This case is already snoozed through an equal or later time."),
                            ReconciliationOperatorActionOutcomes.AlreadyApplied,
                            "This case is already snoozed through an equal or later time.");
                    }

                    state.SnoozedUntilUtc = validation.ExpiresAtUtc;
                    return new AppliedActionResult(
                        snapshot,
                        RecordAudit(state, validation, ReconciliationOperatorActionOutcomes.Applied, $"Notifications snoozed until {validation.ExpiresAtUtc:O}.", processedAtUtc: appliedAtUtc),
                        ReconciliationOperatorActionOutcomes.Applied,
                        $"Notifications snoozed until {validation.ExpiresAtUtc:O}.");

                case ReconciliationOperatorActionKinds.Ignore:
                    if (state.IgnoredAtUtc.HasValue)
                    {
                        return new AppliedActionResult(
                            snapshot,
                            RecordAudit(state, validation, ReconciliationOperatorActionOutcomes.AlreadyApplied, "This case was already ignored."),
                            ReconciliationOperatorActionOutcomes.AlreadyApplied,
                            "This case was already ignored.");
                    }

                    state.IgnoredAtUtc = appliedAtUtc;
                    state.SnoozedUntilUtc = null;
                    return new AppliedActionResult(
                        snapshot,
                        RecordAudit(state, validation, ReconciliationOperatorActionOutcomes.Applied, "This case has been ignored.", processedAtUtc: appliedAtUtc),
                        ReconciliationOperatorActionOutcomes.Applied,
                        "This case has been ignored.");

                case ReconciliationOperatorActionKinds.ApproveSwitch:
                    if (_releaseSwitchApprovalService == null)
                    {
                        var summary = "Release-switch approvals are not configured.";
                        return new AppliedActionResult(
                            snapshot,
                            RecordAudit(state, validation, ReconciliationOperatorActionOutcomes.Refused, summary),
                            ReconciliationOperatorActionOutcomes.Refused,
                            summary,
                            RecordReleaseSwitchAudit(snapshot.ReleaseSwitchApprovalState, validation, ReconciliationOperatorActionOutcomes.Refused, summary));
                    }

                    var approval = _releaseSwitchApprovalService.Approve(snapshot, validation);
                    return new AppliedActionResult(
                        approval.Snapshot,
                        RecordAudit(state, validation, approval.Result, approval.Message, processedAtUtc: approval.ProcessedAtUtc),
                        approval.Result,
                        approval.Message,
                        approval.Snapshot.ReleaseSwitchApprovalState);

                default:
                    return new AppliedActionResult(
                        snapshot,
                        RecordAudit(state, validation, ReconciliationOperatorActionOutcomes.UnsupportedAction, $"Unsupported operator action '{validation.Action}'."),
                        ReconciliationOperatorActionOutcomes.UnsupportedAction,
                        $"Unsupported operator action '{validation.Action}'.");
            }
        }

        private ReconciliationOperatorActionState RecordAudit(
            ReconciliationOperatorActionState state,
            ReconciliationOperatorActionTokenValidationResult validation,
            string result,
            string failureSummary,
            bool includeReceipt = true,
            DateTimeOffset? processedAtUtc = null)
        {
            var auditTimestamp = _clock();
            state.LastAction = new ReconciliationOperatorActionAudit
            {
                Action = validation.Action ?? "unknown",
                Result = result,
                OccurredAtUtc = auditTimestamp,
                Transport = validation.Transport,
                ActionedBy = "signed-link",
                ActionTokenHash = validation.ActionTokenHash,
                FailureSummary = string.Equals(result, ReconciliationOperatorActionOutcomes.Applied, StringComparison.Ordinal) ? null : failureSummary
            };

            if (includeReceipt && validation.ActionTokenHash != null)
            {
                state.ProcessedActionReceipts.Add(new ReconciliationOperatorActionReceipt
                {
                    ActionTokenHash = validation.ActionTokenHash,
                    Action = validation.Action ?? "unknown",
                    Outcome = result,
                    ProcessedAtUtc = processedAtUtc ?? auditTimestamp,
                    ExpiresAtUtc = validation.ExpiresAtUtc,
                    Transport = validation.Transport,
                    FailureSummary = string.Equals(result, ReconciliationOperatorActionOutcomes.Applied, StringComparison.Ordinal) ? null : failureSummary
                });
            }

            return state;
        }

        private ReconciliationCase SaveState(
            ReconciliationCaseSnapshot snapshot,
            ReconciliationOperatorActionState state,
            ReconciliationReleaseSwitchApprovalState? releaseSwitchApprovalState = null)
        {
            var updatedCase = RehydrateCase(snapshot, state.Normalize(), releaseSwitchApprovalState?.Normalize() ?? snapshot.ReleaseSwitchApprovalState?.Normalize());
            _caseStore.Save(updatedCase);
            return updatedCase;
        }

        private ReconciliationCase RehydrateCase(
            ReconciliationCaseSnapshot snapshot,
            ReconciliationOperatorActionState operatorActionState,
            ReconciliationReleaseSwitchApprovalState? releaseSwitchApprovalState)
        {
            return new ReconciliationCase(
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
                updatedAtUtc: _clock(),
                evaluationResult: snapshot.EvaluationResult,
                notificationState: snapshot.NotificationState,
                operatorActionState: operatorActionState,
                releaseSwitchApprovalState: releaseSwitchApprovalState);
        }

        private static IActionResult BuildRefusalPage(string result, string failureSummary)
        {
            return result switch
            {
                ReconciliationOperatorActionOutcomes.MissingCase => OperatorActionResultPage.Refusal(404, "Case unavailable", failureSummary),
                ReconciliationOperatorActionOutcomes.Expired => OperatorActionResultPage.Refusal(410, "Link expired", failureSummary),
                ReconciliationOperatorActionOutcomes.Replay => OperatorActionResultPage.Refusal(409, "Link already used", failureSummary),
                ReconciliationOperatorActionOutcomes.AlreadyApplied => OperatorActionResultPage.Refusal(409, "Action already applied", failureSummary),
                ReconciliationOperatorActionOutcomes.Refused => OperatorActionResultPage.Refusal(409, "Action refused", failureSummary),
                ReconciliationOperatorActionOutcomes.InvalidSignature or ReconciliationOperatorActionOutcomes.InvalidPayload or ReconciliationOperatorActionOutcomes.UnsupportedAction => OperatorActionResultPage.Refusal(400, "Invalid action link", failureSummary),
                _ => OperatorActionResultPage.Refusal(400, "Action refused", failureSummary)
            };
        }

        private ReconciliationReleaseSwitchApprovalState? RecordReleaseSwitchAudit(
            ReconciliationReleaseSwitchApprovalState? existingState,
            ReconciliationOperatorActionTokenValidationResult validation,
            string result,
            string failureSummary)
        {
            if (!string.Equals(validation.Action, ReconciliationOperatorActionKinds.ApproveSwitch, StringComparison.OrdinalIgnoreCase))
            {
                return existingState?.Normalize();
            }

            var audit = existingState?.Normalize() ?? new ReconciliationReleaseSwitchApprovalState();
            audit.AttemptedAtUtc = _clock();
            audit.Outcome = result;
            audit.Transport = validation.Transport;
            audit.ActionTokenHash = validation.ActionTokenHash;
            audit.RefusalSummary = string.Equals(result, ReconciliationOperatorActionOutcomes.Applied, StringComparison.Ordinal) ? null : failureSummary;

            var target = validation.Payload?.ReleaseSwitchTarget;
            if (target != null)
            {
                audit.RequestedAlbumMusicBrainzId = target.AlbumMusicBrainzId;
                audit.RequestedReleaseMusicBrainzId = target.ReleaseMusicBrainzId;
                audit.Classification = target.Classification;
                audit.ScoringVersion = target.ScoringVersion;
            }

            if (!string.Equals(result, ReconciliationOperatorActionOutcomes.Applied, StringComparison.Ordinal))
            {
                audit.RefusedAtUtc = audit.AttemptedAtUtc;
            }

            return audit.Normalize();
        }

        private static string ToTitle(string action)
        {
            return action switch
            {
                ReconciliationOperatorActionKinds.Snooze => "Snooze",
                ReconciliationOperatorActionKinds.Ignore => "Ignore",
                ReconciliationOperatorActionKinds.ApproveSwitch => "Release switch",
                _ => action
            };
        }

        private sealed record AppliedActionResult(
            ReconciliationCaseSnapshot Snapshot,
            ReconciliationOperatorActionState State,
            string Result,
            string SuccessMessage,
            ReconciliationReleaseSwitchApprovalState? ReleaseSwitchApprovalState = null);
    }
}
