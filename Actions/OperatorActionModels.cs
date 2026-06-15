using System;

namespace NzbDrone.Core.Plugins.ReleaseReconciler.Actions
{
    public static class ReconciliationOperatorActionKinds
    {
        public const string Snooze = "snooze";
        public const string Ignore = "ignore";
        public const string ApproveSwitch = "approve-switch";

        public static bool IsSupported(string? action)
        {
            return string.Equals(action, Snooze, StringComparison.OrdinalIgnoreCase)
                || string.Equals(action, Ignore, StringComparison.OrdinalIgnoreCase)
                || string.Equals(action, ApproveSwitch, StringComparison.OrdinalIgnoreCase);
        }

        public static string Normalize(string action)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(action);

            if (string.Equals(action, Snooze, StringComparison.OrdinalIgnoreCase))
            {
                return Snooze;
            }

            if (string.Equals(action, Ignore, StringComparison.OrdinalIgnoreCase))
            {
                return Ignore;
            }

            if (string.Equals(action, ApproveSwitch, StringComparison.OrdinalIgnoreCase))
            {
                return ApproveSwitch;
            }

            throw new InvalidOperationException($"Unsupported operator action '{action}'.");
        }
    }

    public static class ReconciliationOperatorActionOutcomes
    {
        public const string Applied = "applied";
        public const string Replay = "replayed";
        public const string Expired = "expired";
        public const string InvalidSignature = "invalid-signature";
        public const string InvalidPayload = "invalid-payload";
        public const string MissingCase = "missing-case";
        public const string UnsupportedAction = "unsupported-action";
        public const string AlreadyApplied = "already-applied";
        public const string Refused = "refused";
    }

    public sealed class ReconciliationReleaseSwitchTarget
    {
        public string AlbumMusicBrainzId { get; set; } = string.Empty;

        public string ReleaseMusicBrainzId { get; set; } = string.Empty;

        public string? Classification { get; set; }

        public string? ScoringVersion { get; set; }
    }

    public sealed class ReconciliationOperatorActionTokenPayload
    {
        public int Version { get; set; } = 1;

        public string CaseId { get; set; } = string.Empty;

        public string Action { get; set; } = string.Empty;

        public string Nonce { get; set; } = string.Empty;

        public DateTimeOffset ExpiresAtUtc { get; set; }

        public string? Transport { get; set; }

        public ReconciliationReleaseSwitchTarget? ReleaseSwitchTarget { get; set; }
    }

    public sealed class ReconciliationIssuedOperatorActionToken
    {
        public required string Token { get; init; }

        public required string CaseId { get; init; }

        public required string Action { get; init; }

        public required string ActionTokenHash { get; init; }

        public required string TokenFingerprintPrefix { get; init; }

        public DateTimeOffset ExpiresAtUtc { get; init; }

        public string? Transport { get; init; }
    }

    public sealed class ReconciliationOperatorActionTokenValidationResult
    {
        public bool Succeeded { get; init; }

        public string Result { get; init; } = string.Empty;

        public string? FailureSummary { get; init; }

        public string? CaseId { get; init; }

        public string? Action { get; init; }

        public string? Nonce { get; init; }

        public string? Transport { get; init; }

        public string? ActionTokenHash { get; init; }

        public string? TokenFingerprintPrefix { get; init; }

        public DateTimeOffset? ExpiresAtUtc { get; init; }

        public ReconciliationOperatorActionTokenPayload? Payload { get; init; }
    }
}
