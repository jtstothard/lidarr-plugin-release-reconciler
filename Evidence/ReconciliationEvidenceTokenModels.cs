using System;

namespace NzbDrone.Core.Plugins.ReleaseReconciler.Evidence
{
    public static class ReconciliationEvidenceTokenOutcomes
    {
        public const string Valid = "valid";
        public const string Expired = "expired";
        public const string InvalidSignature = "invalid-signature";
        public const string InvalidPayload = "invalid-payload";
        public const string MissingCase = "missing-case";
    }

    public sealed class ReconciliationEvidenceTokenPayload
    {
        public int Version { get; set; } = 1;

        public string CaseId { get; set; } = string.Empty;

        public string Nonce { get; set; } = string.Empty;

        public DateTimeOffset ExpiresAtUtc { get; set; }

        public string? Transport { get; set; }
    }

    public sealed class ReconciliationIssuedEvidenceToken
    {
        public required string Token { get; init; }

        public required string CaseId { get; init; }

        public required string EvidenceTokenHash { get; init; }

        public required string TokenFingerprintPrefix { get; init; }

        public DateTimeOffset ExpiresAtUtc { get; init; }

        public string? Transport { get; init; }
    }

    public sealed class ReconciliationEvidenceTokenValidationResult
    {
        public bool Succeeded { get; init; }

        public string Result { get; init; } = string.Empty;

        public string? FailureSummary { get; init; }

        public string? CaseId { get; init; }

        public string? Nonce { get; init; }

        public string? Transport { get; init; }

        public string? EvidenceTokenHash { get; init; }

        public string? TokenFingerprintPrefix { get; init; }

        public DateTimeOffset? ExpiresAtUtc { get; init; }

        public ReconciliationEvidenceTokenPayload? Payload { get; init; }
    }
}
