using System;
using System.Linq;
using System.Text.RegularExpressions;
using NzbDrone.Core.Plugins.ReleaseReconciler.Candidates;

namespace NzbDrone.Core.Plugins.ReleaseReconciler.Capture
{
    public sealed class HostImportFailureClassifier
    {
        private static readonly Regex TrackNumberMismatchPattern = new(
            @"\btrack(?:\s|-)?number(?:s)?\b.*\b(mismatch|mismatched|incorrect|invalid|wrong|different|out\s+of\s+order|do(?:es)?n'?t\s+match)\b|\b(mismatch|mismatched|incorrect|invalid|wrong|different)\b.*\btrack(?:\s|-)?number(?:s)?\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex TrackOrderingMismatchPattern = new(
            @"\b(track|disc|medium)\b.*\b(order|ordering|sequence)\b.*\b(mismatch|mismatched|incorrect|invalid|wrong|unexpected|out\s+of\s+order)\b|\bout\s+of\s+order\b.*\b(track|disc|medium)\b|\btrack\s+ordering\s+mismatch\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public HostImportFailureClassification Classify(
            string phase,
            string? status,
            string? failureText,
            string? diagnosticSummary,
            ReconciliationEvidence structuralEvidence)
        {
            var normalizedPhase = NormalizeRequiredValue(phase, nameof(phase));
            var normalizedStatus = NormalizeOptionalValue(status);
            var normalizedFailureText = NormalizeOptionalValue(failureText);
            var normalizedDiagnosticSummary = NormalizeOptionalValue(diagnosticSummary);
            var normalizedStructuralEvidence = NormalizeStructuralEvidence(structuralEvidence);
            var signalText = BuildSignalText(normalizedStatus, normalizedFailureText, normalizedDiagnosticSummary);

            if (!IsFailedPhaseOrStatus(normalizedPhase, normalizedStatus))
            {
                return Suppress(
                    matchedRule: "phase-not-failed",
                    suppressionReason: $"phase/status did not indicate an import failure (phase={normalizedPhase}, status={normalizedStatus ?? "none"}).");
            }

            if (signalText == null)
            {
                return Suppress(
                    matchedRule: "missing-failure-context",
                    suppressionReason: "import failure did not include failure text or diagnostic summary signals.");
            }

            if (TrackNumberMismatchPattern.IsMatch(signalText))
            {
                if (!HasTrackEvidence(normalizedStructuralEvidence, minimumTrackCount: 1))
                {
                    return Suppress(
                        matchedRule: "insufficient-structural-evidence",
                        suppressionReason: "track-number mismatch was suppressed because no persisted track evidence was captured.");
                }

                return Actionable("track-number-mismatch");
            }

            if (TrackOrderingMismatchPattern.IsMatch(signalText))
            {
                if (!HasTrackEvidence(normalizedStructuralEvidence, minimumTrackCount: 2))
                {
                    return Suppress(
                        matchedRule: "insufficient-structural-evidence",
                        suppressionReason: "track-ordering mismatch was suppressed because fewer than two persisted tracks were captured.");
                }

                return Actionable("track-ordering-mismatch");
            }

            return Suppress(
                matchedRule: "non-actionable-import-failure",
                suppressionReason: "import failure did not match track-number or ordering mismatch rules.");
        }

        private static HostImportFailureClassification Actionable(string matchedRule)
        {
            return new HostImportFailureClassification
            {
                Actionable = true,
                RuleVersion = HostImportFailureClassification.CurrentRuleVersion,
                MatchedRule = matchedRule
            }.Normalize();
        }

        private static HostImportFailureClassification Suppress(string matchedRule, string suppressionReason)
        {
            return new HostImportFailureClassification
            {
                Actionable = false,
                RuleVersion = HostImportFailureClassification.CurrentRuleVersion,
                MatchedRule = matchedRule,
                SuppressionReason = suppressionReason
            }.Normalize();
        }

        private static string? BuildSignalText(params string?[] values)
        {
            var parts = values
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Select(static value => value!.Trim())
                .ToArray();

            return parts.Length == 0 ? null : string.Join(" | ", parts);
        }

        private static bool HasTrackEvidence(ReconciliationEvidence structuralEvidence, int minimumTrackCount)
        {
            var persistedTrackCount = Math.Max(structuralEvidence.TrackCount, structuralEvidence.Tracks?.Count ?? 0);
            return persistedTrackCount >= minimumTrackCount;
        }

        private static bool IsFailedPhaseOrStatus(string phase, string? status)
        {
            return ContainsFailureSignal(phase) || ContainsFailureSignal(status);
        }

        private static bool ContainsFailureSignal(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            var normalized = value.Trim();
            return normalized.Contains("fail", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("error", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("reject", StringComparison.OrdinalIgnoreCase);
        }

        private static ReconciliationEvidence NormalizeStructuralEvidence(ReconciliationEvidence structuralEvidence)
        {
            ArgumentNullException.ThrowIfNull(structuralEvidence);
            var normalized = structuralEvidence.Normalize();
            normalized.ValidatePersistedShape();
            return normalized;
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
