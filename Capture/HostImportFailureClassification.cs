using System;

namespace NzbDrone.Core.Plugins.ReleaseReconciler.Capture
{
    public sealed class HostImportFailureClassification
    {
        public const string CurrentRuleVersion = "host-import-failure-v1";

        public bool Actionable { get; set; }

        public string RuleVersion { get; set; } = CurrentRuleVersion;

        public string MatchedRule { get; set; } = string.Empty;

        public string? SuppressionReason { get; set; }

        public HostImportFailureClassification Normalize()
        {
            return new HostImportFailureClassification
            {
                Actionable = Actionable,
                RuleVersion = NormalizeRequiredValue(RuleVersion, nameof(RuleVersion)),
                MatchedRule = NormalizeRequiredValue(MatchedRule, nameof(MatchedRule)),
                SuppressionReason = NormalizeOptionalValue(SuppressionReason)
            };
        }

        public void ValidatePersistedShape()
        {
            if (string.IsNullOrWhiteSpace(RuleVersion))
            {
                throw new InvalidOperationException("Host import failure classification rule version is required.");
            }

            if (string.IsNullOrWhiteSpace(MatchedRule))
            {
                throw new InvalidOperationException("Host import failure classification matched rule is required.");
            }

            if (Actionable && !string.IsNullOrWhiteSpace(SuppressionReason))
            {
                throw new InvalidOperationException("Actionable host import failure classifications cannot persist suppression reasons.");
            }

            if (!Actionable && string.IsNullOrWhiteSpace(SuppressionReason))
            {
                throw new InvalidOperationException("Suppressed host import failure classifications must persist a suppression reason.");
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
