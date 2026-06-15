using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using NzbDrone.Core.Plugins.ReleaseReconciler.Candidates;

namespace NzbDrone.Core.Plugins.ReleaseReconciler.Cases
{
    public sealed class ReconciliationCase
    {
        public ReconciliationCase(
            string sourceSeam,
            string phase,
            IReadOnlyDictionary<string, string?> capturedIdentifiers,
            ReconciliationEvidence structuralEvidence,
            string? downloadId = null,
            string? outputPath = null,
            string? diagnosticSummary = null,
            string? lastError = null,
            string? caseId = null,
            DateTimeOffset? capturedAtUtc = null,
            DateTimeOffset? updatedAtUtc = null,
            ReconciliationEvaluationResult? evaluationResult = null,
            ReconciliationNotificationState? notificationState = null,
            ReconciliationOperatorActionState? operatorActionState = null,
            ReconciliationReleaseSwitchApprovalState? releaseSwitchApprovalState = null)
        {
            SourceSeam = NormalizeRequiredValue(sourceSeam, nameof(sourceSeam));
            Phase = NormalizeRequiredValue(phase, nameof(phase));
            CapturedIdentifiers = NormalizeIdentifiers(capturedIdentifiers);
            StructuralEvidence = NormalizeStructuralEvidence(structuralEvidence);
            DownloadId = NormalizeOptionalValue(downloadId);
            OutputPath = NormalizeOptionalValue(outputPath);
            DiagnosticSummary = NormalizeOptionalValue(diagnosticSummary);
            LastError = NormalizeOptionalValue(lastError);
            CapturedAtUtc = capturedAtUtc;
            UpdatedAtUtc = updatedAtUtc;
            EvaluationResult = NormalizeEvaluationResult(evaluationResult);
            NotificationState = NormalizeNotificationState(notificationState);
            OperatorActionState = NormalizeOperatorActionState(operatorActionState);
            ReleaseSwitchApprovalState = NormalizeReleaseSwitchApprovalState(releaseSwitchApprovalState);
            CaseId = string.IsNullOrWhiteSpace(caseId)
                ? CreateStableId(SourceSeam, CapturedIdentifiers, DownloadId, OutputPath)
                : caseId.Trim();
        }

        public string CaseId { get; }

        public string SourceSeam { get; }

        public string Phase { get; }

        public IReadOnlyDictionary<string, string?> CapturedIdentifiers { get; }

        public ReconciliationEvidence StructuralEvidence { get; }

        public string? DownloadId { get; }

        public string? OutputPath { get; }

        public string? DiagnosticSummary { get; }

        public string? LastError { get; }

        public DateTimeOffset? CapturedAtUtc { get; }

        public DateTimeOffset? UpdatedAtUtc { get; }

        public ReconciliationEvaluationResult? EvaluationResult { get; }

        public ReconciliationNotificationState? NotificationState { get; }

        public ReconciliationOperatorActionState? OperatorActionState { get; }

        public ReconciliationReleaseSwitchApprovalState? ReleaseSwitchApprovalState { get; }

        public static string CreateStableId(
            string sourceSeam,
            IReadOnlyDictionary<string, string?> capturedIdentifiers,
            string? downloadId = null,
            string? outputPath = null)
        {
            var normalizedSourceSeam = NormalizeRequiredValue(sourceSeam, nameof(sourceSeam));
            var normalizedIdentifiers = NormalizeIdentifiers(capturedIdentifiers);
            var builder = new StringBuilder();

            builder.Append(normalizedSourceSeam);

            foreach (var identifier in normalizedIdentifiers.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
            {
                builder.Append('|');
                builder.Append(identifier.Key);
                builder.Append('=');
                builder.Append(identifier.Value);
            }

            if (!string.IsNullOrWhiteSpace(downloadId))
            {
                builder.Append("|downloadId=");
                builder.Append(downloadId.Trim());
            }

            if (!string.IsNullOrWhiteSpace(outputPath))
            {
                builder.Append("|outputPath=");
                builder.Append(outputPath.Trim());
            }

            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
            var hashText = Convert.ToHexString(hash).ToLowerInvariant();
            return $"rcase-{hashText[..24]}";
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

        private static ReconciliationEvidence NormalizeStructuralEvidence(ReconciliationEvidence structuralEvidence)
        {
            ArgumentNullException.ThrowIfNull(structuralEvidence);
            var normalized = structuralEvidence.Normalize();
            normalized.ValidatePersistedShape();
            return normalized;
        }

        private static ReconciliationEvaluationResult? NormalizeEvaluationResult(ReconciliationEvaluationResult? evaluationResult)
        {
            if (evaluationResult == null)
            {
                return null;
            }

            var normalized = evaluationResult.Normalize();
            normalized.ValidatePersistedShape();
            return normalized;
        }

        private static ReconciliationNotificationState? NormalizeNotificationState(ReconciliationNotificationState? notificationState)
        {
            if (notificationState == null)
            {
                return null;
            }

            var normalized = notificationState.Normalize();
            normalized.ValidatePersistedShape();
            return normalized;
        }

        private static ReconciliationOperatorActionState? NormalizeOperatorActionState(ReconciliationOperatorActionState? operatorActionState)
        {
            if (operatorActionState == null)
            {
                return null;
            }

            var normalized = operatorActionState.Normalize();
            normalized.ValidatePersistedShape();
            return normalized;
        }

        private static ReconciliationReleaseSwitchApprovalState? NormalizeReleaseSwitchApprovalState(ReconciliationReleaseSwitchApprovalState? releaseSwitchApprovalState)
        {
            if (releaseSwitchApprovalState == null)
            {
                return null;
            }

            var normalized = releaseSwitchApprovalState.Normalize();
            normalized.ValidatePersistedShape();
            return normalized;
        }

        private static IReadOnlyDictionary<string, string?> NormalizeIdentifiers(IReadOnlyDictionary<string, string?> capturedIdentifiers)
        {
            ArgumentNullException.ThrowIfNull(capturedIdentifiers);

            var normalized = new SortedDictionary<string, string?>(StringComparer.Ordinal);

            foreach (var identifier in capturedIdentifiers)
            {
                if (string.IsNullOrWhiteSpace(identifier.Key))
                {
                    throw new ArgumentException("Captured identifier keys cannot be blank.", nameof(capturedIdentifiers));
                }

                normalized[identifier.Key.Trim()] = NormalizeOptionalValue(identifier.Value);
            }

            if (normalized.Count == 0)
            {
                throw new ArgumentException("At least one captured identifier is required.", nameof(capturedIdentifiers));
            }

            return normalized.ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal);
        }
    }
}
