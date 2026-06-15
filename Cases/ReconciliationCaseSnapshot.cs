using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using NzbDrone.Core.Plugins.ReleaseReconciler.Candidates;

namespace NzbDrone.Core.Plugins.ReleaseReconciler.Cases
{
    public sealed class ReconciliationCaseSnapshot
    {
        private static readonly Regex HeaderSecretPattern = new(
            "(?<label>authorization|cookie|cookies|set-cookie)\\s*[:=]\\s*(?<value>[^\\r\\n;]+)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex BearerSecretPattern = new(
            "bearer\\s+[A-Za-z0-9._~+/=-]+",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex TokenSecretPattern = new(
            "(?<label>token|api[-_ ]?key|signature|sig)\\s*[:=]\\s*(?<value>[^\\r\\n;]+)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public const int CurrentSchemaVersion = 2;

        public int SchemaVersion { get; init; } = CurrentSchemaVersion;

        public string CaseId { get; init; } = string.Empty;

        public string SourceSeam { get; init; } = string.Empty;

        public string Phase { get; init; } = string.Empty;

        public Dictionary<string, string?> CapturedIdentifiers { get; init; } = new(StringComparer.Ordinal);

        public ReconciliationEvidence? StructuralEvidence { get; init; }

        public ReconciliationEvaluationResult? EvaluationResult { get; init; }

        public ReconciliationNotificationState? NotificationState { get; init; }

        public ReconciliationOperatorActionState? OperatorActionState { get; init; }

        public ReconciliationReleaseSwitchApprovalState? ReleaseSwitchApprovalState { get; init; }

        public string? DownloadId { get; init; }

        public string? OutputPath { get; init; }

        public string? DiagnosticSummary { get; init; }

        public string? LastError { get; init; }

        public DateTimeOffset CapturedAtUtc { get; init; }

        public DateTimeOffset LastUpdatedAtUtc { get; init; }

        public static ReconciliationCaseSnapshot Create(
            ReconciliationCase reconciliationCase,
            DateTimeOffset capturedAtUtc,
            DateTimeOffset updatedAtUtc)
        {
            ArgumentNullException.ThrowIfNull(reconciliationCase);

            ValidateCaseId(reconciliationCase.CaseId);

            if (updatedAtUtc < capturedAtUtc)
            {
                updatedAtUtc = capturedAtUtc;
            }

            return new ReconciliationCaseSnapshot
            {
                SchemaVersion = CurrentSchemaVersion,
                CaseId = reconciliationCase.CaseId,
                SourceSeam = reconciliationCase.SourceSeam,
                Phase = reconciliationCase.Phase,
                CapturedIdentifiers = reconciliationCase.CapturedIdentifiers
                    .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
                    .ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal),
                StructuralEvidence = reconciliationCase.StructuralEvidence.Normalize(),
                EvaluationResult = reconciliationCase.EvaluationResult?.Normalize(),
                NotificationState = reconciliationCase.NotificationState?.Normalize(),
                OperatorActionState = reconciliationCase.OperatorActionState?.Normalize(),
                ReleaseSwitchApprovalState = reconciliationCase.ReleaseSwitchApprovalState?.Normalize(),
                DownloadId = NormalizeOptionalValue(reconciliationCase.DownloadId),
                OutputPath = NormalizeOptionalValue(reconciliationCase.OutputPath),
                DiagnosticSummary = RedactSensitiveText(reconciliationCase.DiagnosticSummary),
                LastError = RedactSensitiveText(reconciliationCase.LastError),
                CapturedAtUtc = capturedAtUtc,
                LastUpdatedAtUtc = updatedAtUtc
            };
        }

        public void ValidatePersistedShape()
        {
            if (SchemaVersion < CurrentSchemaVersion)
            {
                throw new InvalidOperationException($"Reconciliation case snapshot schema version '{SchemaVersion}' is missing structural evidence required for deterministic scoring. Re-capture the case with the current plugin build.");
            }

            if (SchemaVersion > CurrentSchemaVersion)
            {
                throw new InvalidOperationException($"Unsupported reconciliation case schema version '{SchemaVersion}'.");
            }

            ValidateCaseId(CaseId);

            if (string.IsNullOrWhiteSpace(SourceSeam))
            {
                throw new InvalidOperationException("Reconciliation case snapshot source seam is required.");
            }

            if (string.IsNullOrWhiteSpace(Phase))
            {
                throw new InvalidOperationException("Reconciliation case snapshot phase is required.");
            }

            if (CapturedIdentifiers.Count == 0)
            {
                throw new InvalidOperationException("Reconciliation case snapshot must include at least one captured identifier.");
            }

            if (StructuralEvidence == null)
            {
                throw new InvalidOperationException("Reconciliation case snapshot structural evidence is required.");
            }

            StructuralEvidence.ValidatePersistedShape();
            EvaluationResult?.ValidatePersistedShape();
            NotificationState?.ValidatePersistedShape();
            OperatorActionState?.ValidatePersistedShape();
            ReleaseSwitchApprovalState?.ValidatePersistedShape();

            if (LastUpdatedAtUtc < CapturedAtUtc)
            {
                throw new InvalidOperationException("Reconciliation case snapshot timestamps are out of order.");
            }
        }

        public static string ValidateCaseId(string caseId)
        {
            if (string.IsNullOrWhiteSpace(caseId))
            {
                throw new ArgumentException("Case id is required.", nameof(caseId));
            }

            var normalized = caseId.Trim();

            if (normalized.Any(static character => !(char.IsLetterOrDigit(character) || character is '-' or '_' or '.')))
            {
                throw new ArgumentException($"Case id '{normalized}' contains unsupported characters.", nameof(caseId));
            }

            return normalized;
        }

        internal static string? RedactSensitiveText(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var redacted = value.Trim();
            redacted = HeaderSecretPattern.Replace(redacted, static match =>
            {
                var label = match.Groups["label"].Value;
                return $"{label}=[REDACTED]";
            });
            redacted = TokenSecretPattern.Replace(redacted, static match =>
            {
                var label = match.Groups["label"].Value;
                return $"{label}=[REDACTED]";
            });
            redacted = BearerSecretPattern.Replace(redacted, "Bearer [REDACTED]");
            return redacted;
        }

        private static string? NormalizeOptionalValue(string? value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
    }

    public sealed class ReconciliationNotificationState
    {
        public List<ReconciliationNotificationDispatchAttempt> DispatchAttempts { get; set; } = new();

        public ReconciliationNotificationState Normalize()
        {
            return new ReconciliationNotificationState
            {
                DispatchAttempts = (DispatchAttempts ?? new List<ReconciliationNotificationDispatchAttempt>())
                    .Select(static attempt => attempt.Normalize())
                    .OrderBy(static attempt => attempt.AttemptedAtUtc)
                    .ThenBy(static attempt => attempt.Transport, StringComparer.Ordinal)
                    .ToList()
            };
        }

        public void ValidatePersistedShape()
        {
            if (DispatchAttempts == null)
            {
                throw new InvalidOperationException("Reconciliation notification dispatch attempts are required.");
            }

            foreach (var attempt in DispatchAttempts)
            {
                attempt.ValidatePersistedShape();
            }
        }
    }

    public sealed class ReconciliationNotificationDispatchAttempt
    {
        public DateTimeOffset AttemptedAtUtc { get; set; }

        public string Transport { get; set; } = string.Empty;

        public bool Succeeded { get; set; }

        public string? ActionUrlKind { get; set; }

        public DateTimeOffset? ActionTokenExpiresAtUtc { get; set; }

        public string? FailureKind { get; set; }

        public string? FailureSummary { get; set; }

        public ReconciliationNotificationDispatchAttempt Normalize()
        {
            return new ReconciliationNotificationDispatchAttempt
            {
                AttemptedAtUtc = AttemptedAtUtc,
                Transport = NormalizeRequiredValue(Transport, nameof(Transport)),
                Succeeded = Succeeded,
                ActionUrlKind = NormalizeOptionalValue(ActionUrlKind),
                ActionTokenExpiresAtUtc = ActionTokenExpiresAtUtc,
                FailureKind = NormalizeOptionalValue(FailureKind),
                FailureSummary = ReconciliationCaseSnapshot.RedactSensitiveText(FailureSummary)
            };
        }

        public void ValidatePersistedShape()
        {
            if (AttemptedAtUtc == default)
            {
                throw new InvalidOperationException("Reconciliation notification attempt timestamp is required.");
            }

            if (string.IsNullOrWhiteSpace(Transport))
            {
                throw new InvalidOperationException("Reconciliation notification attempt transport is required.");
            }

            if (ActionTokenExpiresAtUtc.HasValue && ActionTokenExpiresAtUtc.Value < AttemptedAtUtc)
            {
                throw new InvalidOperationException("Reconciliation notification action token expiry cannot precede the dispatch attempt.");
            }

            if (Succeeded && (!string.IsNullOrWhiteSpace(FailureKind) || !string.IsNullOrWhiteSpace(FailureSummary)))
            {
                throw new InvalidOperationException("Successful reconciliation notification attempts cannot persist failure details.");
            }

            if (!Succeeded && string.IsNullOrWhiteSpace(FailureKind) && string.IsNullOrWhiteSpace(FailureSummary))
            {
                throw new InvalidOperationException("Failed reconciliation notification attempts must persist a failure kind or summary.");
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

        private static string? NormalizeOptionalValue(string? value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
    }

    public sealed class ReconciliationReleaseSwitchApprovalState
    {
        public DateTimeOffset? AttemptedAtUtc { get; set; }

        public DateTimeOffset? AppliedAtUtc { get; set; }

        public DateTimeOffset? RefusedAtUtc { get; set; }

        public string? Outcome { get; set; }

        public string? RefusalSummary { get; set; }

        public string? BeforeAlbumMusicBrainzId { get; set; }

        public string? BeforeReleaseMusicBrainzId { get; set; }

        public string? RequestedAlbumMusicBrainzId { get; set; }

        public string? RequestedReleaseMusicBrainzId { get; set; }

        public string? AfterAlbumMusicBrainzId { get; set; }

        public string? AfterReleaseMusicBrainzId { get; set; }

        public string? Classification { get; set; }

        public string? RefusalReason { get; set; }

        public string? ScoringVersion { get; set; }

        public int? CandidateTotalScore { get; set; }

        public int? CandidateStrongSignalScore { get; set; }

        public bool? CandidateSameReleaseGroup { get; set; }

        public bool? CandidateHasStrongIdentitySignal { get; set; }

        public bool? CandidateTitleOnly { get; set; }

        public string? Transport { get; set; }

        public string? ActionTokenHash { get; set; }

        public ReconciliationReleaseSwitchApprovalState Normalize()
        {
            return new ReconciliationReleaseSwitchApprovalState
            {
                AttemptedAtUtc = AttemptedAtUtc,
                AppliedAtUtc = AppliedAtUtc,
                RefusedAtUtc = RefusedAtUtc,
                Outcome = NormalizeOptionalValue(Outcome),
                RefusalSummary = ReconciliationCaseSnapshot.RedactSensitiveText(RefusalSummary),
                BeforeAlbumMusicBrainzId = NormalizeOptionalValue(BeforeAlbumMusicBrainzId),
                BeforeReleaseMusicBrainzId = NormalizeOptionalValue(BeforeReleaseMusicBrainzId),
                RequestedAlbumMusicBrainzId = NormalizeOptionalValue(RequestedAlbumMusicBrainzId),
                RequestedReleaseMusicBrainzId = NormalizeOptionalValue(RequestedReleaseMusicBrainzId),
                AfterAlbumMusicBrainzId = NormalizeOptionalValue(AfterAlbumMusicBrainzId),
                AfterReleaseMusicBrainzId = NormalizeOptionalValue(AfterReleaseMusicBrainzId),
                Classification = NormalizeOptionalValue(Classification),
                RefusalReason = NormalizeOptionalValue(RefusalReason),
                ScoringVersion = NormalizeOptionalValue(ScoringVersion),
                CandidateTotalScore = CandidateTotalScore,
                CandidateStrongSignalScore = CandidateStrongSignalScore,
                CandidateSameReleaseGroup = CandidateSameReleaseGroup,
                CandidateHasStrongIdentitySignal = CandidateHasStrongIdentitySignal,
                CandidateTitleOnly = CandidateTitleOnly,
                Transport = NormalizeOptionalValue(Transport),
                ActionTokenHash = NormalizeOptionalValue(ActionTokenHash)
            };
        }

        public void ValidatePersistedShape()
        {
            if (AttemptedAtUtc.HasValue && AttemptedAtUtc.Value == default)
            {
                throw new InvalidOperationException("Reconciliation release-switch attempt timestamp cannot be default.");
            }

            if (AppliedAtUtc.HasValue && AppliedAtUtc.Value == default)
            {
                throw new InvalidOperationException("Reconciliation release-switch applied timestamp cannot be default.");
            }

            if (RefusedAtUtc.HasValue && RefusedAtUtc.Value == default)
            {
                throw new InvalidOperationException("Reconciliation release-switch refused timestamp cannot be default.");
            }

            if (CandidateTotalScore.HasValue && CandidateStrongSignalScore.HasValue && CandidateStrongSignalScore.Value > CandidateTotalScore.Value)
            {
                throw new InvalidOperationException("Reconciliation release-switch strong-signal score cannot exceed total score.");
            }
        }

        private static string? NormalizeOptionalValue(string? value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
    }

    public sealed class ReconciliationOperatorActionState
    {
        public DateTimeOffset? SnoozedUntilUtc { get; set; }

        public DateTimeOffset? IgnoredAtUtc { get; set; }

        public ReconciliationOperatorActionAudit? LastAction { get; set; }

        public List<ReconciliationOperatorActionReceipt> ProcessedActionReceipts { get; set; } = new();

        public ReconciliationOperatorActionState Normalize()
        {
            return new ReconciliationOperatorActionState
            {
                SnoozedUntilUtc = SnoozedUntilUtc,
                IgnoredAtUtc = IgnoredAtUtc,
                LastAction = LastAction?.Normalize(),
                ProcessedActionReceipts = (ProcessedActionReceipts ?? new List<ReconciliationOperatorActionReceipt>())
                    .Select(static receipt => receipt.Normalize())
                    .OrderBy(static receipt => receipt.ProcessedAtUtc)
                    .ThenBy(static receipt => receipt.ActionTokenHash, StringComparer.Ordinal)
                    .ToList()
            };
        }

        public void ValidatePersistedShape()
        {
            if (SnoozedUntilUtc.HasValue && IgnoredAtUtc.HasValue)
            {
                throw new InvalidOperationException("Reconciliation operator action state cannot be both snoozed and ignored.");
            }

            if (ProcessedActionReceipts == null)
            {
                throw new InvalidOperationException("Reconciliation operator action receipts are required.");
            }

            LastAction?.ValidatePersistedShape();

            foreach (var receipt in ProcessedActionReceipts)
            {
                receipt.ValidatePersistedShape();
            }

            if (LastAction != null && string.Equals(LastAction.Result, "applied", StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(LastAction.Action, "ignore", StringComparison.OrdinalIgnoreCase) && !IgnoredAtUtc.HasValue)
                {
                    throw new InvalidOperationException("Applied ignore actions must persist the ignored timestamp.");
                }

                if (string.Equals(LastAction.Action, "snooze", StringComparison.OrdinalIgnoreCase) && !SnoozedUntilUtc.HasValue)
                {
                    throw new InvalidOperationException("Applied snooze actions must persist the snooze-until timestamp.");
                }
            }
        }
    }

    public sealed class ReconciliationOperatorActionAudit
    {
        public string Action { get; set; } = string.Empty;

        public string Result { get; set; } = string.Empty;

        public DateTimeOffset OccurredAtUtc { get; set; }

        public string? Transport { get; set; }

        public string? ActionedBy { get; set; }

        public string? ActionTokenHash { get; set; }

        public string? FailureSummary { get; set; }

        public ReconciliationOperatorActionAudit Normalize()
        {
            return new ReconciliationOperatorActionAudit
            {
                Action = NormalizeRequiredValue(Action, nameof(Action)),
                Result = NormalizeRequiredValue(Result, nameof(Result)),
                OccurredAtUtc = OccurredAtUtc,
                Transport = NormalizeOptionalValue(Transport),
                ActionedBy = NormalizeOptionalValue(ActionedBy),
                ActionTokenHash = NormalizeOptionalValue(ActionTokenHash),
                FailureSummary = ReconciliationCaseSnapshot.RedactSensitiveText(FailureSummary)
            };
        }

        public void ValidatePersistedShape()
        {
            if (string.IsNullOrWhiteSpace(Action))
            {
                throw new InvalidOperationException("Reconciliation operator action kind is required.");
            }

            if (string.IsNullOrWhiteSpace(Result))
            {
                throw new InvalidOperationException("Reconciliation operator action result is required.");
            }

            if (OccurredAtUtc == default)
            {
                throw new InvalidOperationException("Reconciliation operator action timestamp is required.");
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

        private static string? NormalizeOptionalValue(string? value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
    }

    public sealed class ReconciliationOperatorActionReceipt
    {
        public string ActionTokenHash { get; set; } = string.Empty;

        public string Action { get; set; } = string.Empty;

        public string Outcome { get; set; } = string.Empty;

        public DateTimeOffset ProcessedAtUtc { get; set; }

        public DateTimeOffset? ExpiresAtUtc { get; set; }

        public string? Transport { get; set; }

        public string? FailureSummary { get; set; }

        public ReconciliationOperatorActionReceipt Normalize()
        {
            return new ReconciliationOperatorActionReceipt
            {
                ActionTokenHash = NormalizeRequiredValue(ActionTokenHash, nameof(ActionTokenHash)),
                Action = NormalizeRequiredValue(Action, nameof(Action)),
                Outcome = NormalizeRequiredValue(Outcome, nameof(Outcome)),
                ProcessedAtUtc = ProcessedAtUtc,
                ExpiresAtUtc = ExpiresAtUtc,
                Transport = NormalizeOptionalValue(Transport),
                FailureSummary = ReconciliationCaseSnapshot.RedactSensitiveText(FailureSummary)
            };
        }

        public void ValidatePersistedShape()
        {
            if (string.IsNullOrWhiteSpace(ActionTokenHash))
            {
                throw new InvalidOperationException("Reconciliation operator action token hash is required.");
            }

            if (string.IsNullOrWhiteSpace(Action))
            {
                throw new InvalidOperationException("Reconciliation operator action receipt kind is required.");
            }

            if (string.IsNullOrWhiteSpace(Outcome))
            {
                throw new InvalidOperationException("Reconciliation operator action receipt outcome is required.");
            }

            if (ProcessedAtUtc == default)
            {
                throw new InvalidOperationException("Reconciliation operator action receipt timestamp is required.");
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

        private static string? NormalizeOptionalValue(string? value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
    }
}
