using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using NLog;

namespace NzbDrone.Core.Plugins.ReleaseReconciler.Evidence
{
    public sealed class ReconciliationEvidenceTokenService
    {
        public const string ProtectorPurpose = "release-reconciler.evidence-page.v1";
        private const int CurrentVersion = 1;

        private static readonly JsonSerializerOptions SerializerOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        private readonly IDataProtector _protector;
        private readonly Logger _logger;
        private readonly Func<DateTimeOffset> _clock;

        public ReconciliationEvidenceTokenService(IDataProtectionProvider dataProtectionProvider, Logger logger, Func<DateTimeOffset>? clock = null)
            : this((dataProtectionProvider ?? throw new ArgumentNullException(nameof(dataProtectionProvider))).CreateProtector(ProtectorPurpose), logger, clock)
        {
        }

        public ReconciliationEvidenceTokenService(IDataProtector dataProtector, Logger logger, Func<DateTimeOffset>? clock = null)
        {
            _protector = dataProtector ?? throw new ArgumentNullException(nameof(dataProtector));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _clock = clock ?? (() => DateTimeOffset.UtcNow);
        }

        public ReconciliationIssuedEvidenceToken Issue(string caseId, DateTimeOffset expiresAtUtc, string? transport = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(caseId);

            var payload = new ReconciliationEvidenceTokenPayload
            {
                Version = CurrentVersion,
                CaseId = caseId.Trim(),
                Nonce = Guid.NewGuid().ToString("N"),
                ExpiresAtUtc = expiresAtUtc,
                Transport = string.IsNullOrWhiteSpace(transport) ? null : transport.Trim()
            };

            var token = ProtectPayload(payload);
            var evidenceTokenHash = ComputeTokenHash(token);
            var tokenFingerprintPrefix = GetTokenFingerprintPrefix(evidenceTokenHash);

            _logger.Debug(
                "Issued release reconciler evidence token caseId={0} expiresAtUtc={1:o} tokenFingerprintPrefix={2}.",
                payload.CaseId,
                payload.ExpiresAtUtc,
                tokenFingerprintPrefix);

            return new ReconciliationIssuedEvidenceToken
            {
                Token = token,
                CaseId = payload.CaseId,
                EvidenceTokenHash = evidenceTokenHash,
                TokenFingerprintPrefix = tokenFingerprintPrefix,
                ExpiresAtUtc = payload.ExpiresAtUtc,
                Transport = payload.Transport
            };
        }

        public ReconciliationEvidenceTokenValidationResult Validate(string token)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                return new ReconciliationEvidenceTokenValidationResult
                {
                    Succeeded = false,
                    Result = ReconciliationEvidenceTokenOutcomes.InvalidPayload,
                    FailureSummary = "Missing evidence token."
                };
            }

            var trimmedToken = token.Trim();
            var evidenceTokenHash = ComputeTokenHash(trimmedToken);
            var tokenFingerprintPrefix = GetTokenFingerprintPrefix(evidenceTokenHash);

            try
            {
                var protectedBytes = DecodeBase64Url(trimmedToken);
                var jsonBytes = _protector.Unprotect(protectedBytes);
                var payload = JsonSerializer.Deserialize<ReconciliationEvidenceTokenPayload>(jsonBytes, SerializerOptions);
                if (payload == null)
                {
                    return Failure(ReconciliationEvidenceTokenOutcomes.InvalidPayload, "Evidence token payload was empty.", null, null, null, evidenceTokenHash, tokenFingerprintPrefix, null, null);
                }

                if (payload.Version != CurrentVersion)
                {
                    return Failure(ReconciliationEvidenceTokenOutcomes.InvalidPayload, $"Unsupported evidence token version '{payload.Version}'.", payload, evidenceTokenHash, tokenFingerprintPrefix);
                }

                if (string.IsNullOrWhiteSpace(payload.CaseId))
                {
                    return Failure(ReconciliationEvidenceTokenOutcomes.InvalidPayload, "Evidence token is missing case id.", payload, evidenceTokenHash, tokenFingerprintPrefix);
                }

                if (string.IsNullOrWhiteSpace(payload.Nonce))
                {
                    return Failure(ReconciliationEvidenceTokenOutcomes.InvalidPayload, "Evidence token is missing nonce.", payload, evidenceTokenHash, tokenFingerprintPrefix);
                }

                if (payload.ExpiresAtUtc <= _clock())
                {
                    _logger.Warn(
                        "Rejected expired release reconciler evidence token caseId={0} expiresAtUtc={1:o} tokenFingerprintPrefix={2}.",
                        payload.CaseId,
                        payload.ExpiresAtUtc,
                        tokenFingerprintPrefix);

                    return new ReconciliationEvidenceTokenValidationResult
                    {
                        Succeeded = false,
                        Result = ReconciliationEvidenceTokenOutcomes.Expired,
                        FailureSummary = "Evidence page link has expired.",
                        CaseId = payload.CaseId.Trim(),
                        Nonce = payload.Nonce.Trim(),
                        Transport = NormalizeOptionalValue(payload.Transport),
                        EvidenceTokenHash = evidenceTokenHash,
                        TokenFingerprintPrefix = tokenFingerprintPrefix,
                        ExpiresAtUtc = payload.ExpiresAtUtc,
                        Payload = payload
                    };
                }

                return new ReconciliationEvidenceTokenValidationResult
                {
                    Succeeded = true,
                    Result = ReconciliationEvidenceTokenOutcomes.Valid,
                    CaseId = payload.CaseId.Trim(),
                    Nonce = payload.Nonce.Trim(),
                    Transport = NormalizeOptionalValue(payload.Transport),
                    EvidenceTokenHash = evidenceTokenHash,
                    TokenFingerprintPrefix = tokenFingerprintPrefix,
                    ExpiresAtUtc = payload.ExpiresAtUtc,
                    Payload = payload
                };
            }
            catch (Exception ex) when (ex is FormatException or CryptographicException or JsonException)
            {
                _logger.Warn(
                    "Rejected invalid release reconciler evidence token result={0} tokenFingerprintPrefix={1} reason={2}.",
                    ex is CryptographicException ? ReconciliationEvidenceTokenOutcomes.InvalidSignature : ReconciliationEvidenceTokenOutcomes.InvalidPayload,
                    tokenFingerprintPrefix,
                    ex.GetType().Name);

                return new ReconciliationEvidenceTokenValidationResult
                {
                    Succeeded = false,
                    Result = ex is CryptographicException ? ReconciliationEvidenceTokenOutcomes.InvalidSignature : ReconciliationEvidenceTokenOutcomes.InvalidPayload,
                    FailureSummary = ex is CryptographicException
                        ? "Evidence token signature was invalid."
                        : "Evidence token could not be parsed.",
                    EvidenceTokenHash = evidenceTokenHash,
                    TokenFingerprintPrefix = tokenFingerprintPrefix
                };
            }
        }

        public string ProtectPayload(ReconciliationEvidenceTokenPayload payload)
        {
            ArgumentNullException.ThrowIfNull(payload);

            var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(payload, SerializerOptions);
            var protectedBytes = _protector.Protect(jsonBytes);
            return EncodeBase64Url(protectedBytes);
        }

        public static string ComputeTokenHash(string token)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(token);
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token.Trim()))).ToLowerInvariant();
        }

        public static string GetTokenFingerprintPrefix(string evidenceTokenHash)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(evidenceTokenHash);
            return evidenceTokenHash[..Math.Min(12, evidenceTokenHash.Length)];
        }

        private static string? NormalizeOptionalValue(string? value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        private static ReconciliationEvidenceTokenValidationResult Failure(string result, string failureSummary, ReconciliationEvidenceTokenPayload payload, string evidenceTokenHash, string tokenFingerprintPrefix)
        {
            return Failure(result, failureSummary, payload.CaseId, payload.Nonce, NormalizeOptionalValue(payload.Transport), evidenceTokenHash, tokenFingerprintPrefix, payload.ExpiresAtUtc, payload);
        }

        private static ReconciliationEvidenceTokenValidationResult Failure(string result, string failureSummary, string? caseId, string? nonce, string? transport, string? evidenceTokenHash, string? tokenFingerprintPrefix, DateTimeOffset? expiresAtUtc, ReconciliationEvidenceTokenPayload? payload)
        {
            return new ReconciliationEvidenceTokenValidationResult
            {
                Succeeded = false,
                Result = result,
                FailureSummary = failureSummary,
                CaseId = NormalizeOptionalValue(caseId),
                Nonce = NormalizeOptionalValue(nonce),
                Transport = NormalizeOptionalValue(transport),
                EvidenceTokenHash = evidenceTokenHash,
                TokenFingerprintPrefix = tokenFingerprintPrefix,
                ExpiresAtUtc = expiresAtUtc,
                Payload = payload
            };
        }

        private static string EncodeBase64Url(byte[] value)
        {
            return Convert.ToBase64String(value)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
        }

        private static byte[] DecodeBase64Url(string value)
        {
            var normalized = value.Replace('-', '+').Replace('_', '/');
            switch (normalized.Length % 4)
            {
                case 2:
                    normalized += "==";
                    break;
                case 3:
                    normalized += "=";
                    break;
            }

            return Convert.FromBase64String(normalized);
        }
    }
}
