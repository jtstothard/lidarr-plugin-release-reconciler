using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using NLog;

namespace NzbDrone.Core.Plugins.ReleaseReconciler.Actions
{
    public sealed class OperatorActionTokenService
    {
        public const string ProtectorPurpose = "release-reconciler.operator-action.v1";
        private const int CurrentVersion = 1;

        private static readonly JsonSerializerOptions SerializerOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        private readonly IDataProtector _protector;
        private readonly Logger _logger;
        private readonly Func<DateTimeOffset> _clock;

        public OperatorActionTokenService(IDataProtectionProvider dataProtectionProvider, Logger logger, Func<DateTimeOffset>? clock = null)
            : this((dataProtectionProvider ?? throw new ArgumentNullException(nameof(dataProtectionProvider))).CreateProtector(ProtectorPurpose), logger, clock)
        {
        }

        public OperatorActionTokenService(IDataProtector dataProtector, Logger logger, Func<DateTimeOffset>? clock = null)
        {
            _protector = dataProtector ?? throw new ArgumentNullException(nameof(dataProtector));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _clock = clock ?? (() => DateTimeOffset.UtcNow);
        }

        public ReconciliationIssuedOperatorActionToken Issue(string caseId, string action, DateTimeOffset expiresAtUtc, string? transport = null, ReconciliationReleaseSwitchTarget? releaseSwitchTarget = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(caseId);

            var normalizedAction = ReconciliationOperatorActionKinds.Normalize(action);
            var payload = new ReconciliationOperatorActionTokenPayload
            {
                Version = CurrentVersion,
                CaseId = caseId.Trim(),
                Action = normalizedAction,
                Nonce = Guid.NewGuid().ToString("N"),
                ExpiresAtUtc = expiresAtUtc,
                Transport = string.IsNullOrWhiteSpace(transport) ? null : transport.Trim(),
                ReleaseSwitchTarget = NormalizeReleaseSwitchTargetForIssue(normalizedAction, releaseSwitchTarget)
            };

            var token = ProtectPayload(payload);
            var actionTokenHash = ComputeTokenHash(token);
            var tokenFingerprintPrefix = GetTokenFingerprintPrefix(actionTokenHash);

            _logger.Debug(
                "Issued release reconciler operator action token caseId={0} action={1} expiresAtUtc={2:o} tokenFingerprintPrefix={3}.",
                payload.CaseId,
                payload.Action,
                payload.ExpiresAtUtc,
                tokenFingerprintPrefix);

            return new ReconciliationIssuedOperatorActionToken
            {
                Token = token,
                CaseId = payload.CaseId,
                Action = payload.Action,
                ActionTokenHash = actionTokenHash,
                TokenFingerprintPrefix = tokenFingerprintPrefix,
                ExpiresAtUtc = payload.ExpiresAtUtc,
                Transport = payload.Transport
            };
        }

        public ReconciliationOperatorActionTokenValidationResult Validate(string token)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                return new ReconciliationOperatorActionTokenValidationResult
                {
                    Succeeded = false,
                    Result = ReconciliationOperatorActionOutcomes.InvalidPayload,
                    FailureSummary = "Missing operator action token."
                };
            }

            var trimmedToken = token.Trim();
            var actionTokenHash = ComputeTokenHash(trimmedToken);
            var tokenFingerprintPrefix = GetTokenFingerprintPrefix(actionTokenHash);

            try
            {
                var protectedBytes = DecodeBase64Url(trimmedToken);
                var jsonBytes = _protector.Unprotect(protectedBytes);
                var payload = JsonSerializer.Deserialize<ReconciliationOperatorActionTokenPayload>(jsonBytes, SerializerOptions);
                if (payload == null)
                {
                    return Failure(ReconciliationOperatorActionOutcomes.InvalidPayload, "Operator action token payload was empty.", null, null, null, null, actionTokenHash, tokenFingerprintPrefix, null, null);
                }

                if (payload.Version != CurrentVersion)
                {
                    return Failure(ReconciliationOperatorActionOutcomes.InvalidPayload, $"Unsupported operator action token version '{payload.Version}'.", payload, actionTokenHash, tokenFingerprintPrefix);
                }

                if (string.IsNullOrWhiteSpace(payload.CaseId))
                {
                    return Failure(ReconciliationOperatorActionOutcomes.InvalidPayload, "Operator action token is missing case id.", payload, actionTokenHash, tokenFingerprintPrefix);
                }

                if (string.IsNullOrWhiteSpace(payload.Action))
                {
                    return Failure(ReconciliationOperatorActionOutcomes.InvalidPayload, "Operator action token is missing action kind.", payload, actionTokenHash, tokenFingerprintPrefix);
                }

                if (string.IsNullOrWhiteSpace(payload.Nonce))
                {
                    return Failure(ReconciliationOperatorActionOutcomes.InvalidPayload, "Operator action token is missing nonce.", payload, actionTokenHash, tokenFingerprintPrefix);
                }

                var normalizedAction = payload.Action.Trim();
                if (!ReconciliationOperatorActionKinds.IsSupported(normalizedAction))
                {
                    return Failure(ReconciliationOperatorActionOutcomes.UnsupportedAction, $"Unsupported operator action '{normalizedAction}'.", payload, actionTokenHash, tokenFingerprintPrefix);
                }

                normalizedAction = ReconciliationOperatorActionKinds.Normalize(normalizedAction);
                payload.ReleaseSwitchTarget = NormalizeReleaseSwitchTarget(payload.ReleaseSwitchTarget);
                if (string.Equals(normalizedAction, ReconciliationOperatorActionKinds.ApproveSwitch, StringComparison.Ordinal)
                    && payload.ReleaseSwitchTarget == null)
                {
                    return Failure(
                        ReconciliationOperatorActionOutcomes.InvalidPayload,
                        "Approve-switch token is missing signed winner album and release identifiers.",
                        payload.CaseId,
                        normalizedAction,
                        payload.Nonce,
                        payload.Transport,
                        actionTokenHash,
                        tokenFingerprintPrefix,
                        payload.ExpiresAtUtc,
                        payload);
                }

                if (payload.ExpiresAtUtc <= _clock())
                {
                    _logger.Warn(
                        "Rejected expired release reconciler operator action token caseId={0} action={1} expiresAtUtc={2:o} tokenFingerprintPrefix={3}.",
                        payload.CaseId,
                        normalizedAction,
                        payload.ExpiresAtUtc,
                        tokenFingerprintPrefix);

                    return new ReconciliationOperatorActionTokenValidationResult
                    {
                        Succeeded = false,
                        Result = ReconciliationOperatorActionOutcomes.Expired,
                        FailureSummary = "Operator action link has expired.",
                        CaseId = payload.CaseId.Trim(),
                        Action = normalizedAction,
                        Nonce = payload.Nonce.Trim(),
                        Transport = NormalizeOptionalValue(payload.Transport),
                        ActionTokenHash = actionTokenHash,
                        TokenFingerprintPrefix = tokenFingerprintPrefix,
                        ExpiresAtUtc = payload.ExpiresAtUtc,
                        Payload = payload
                    };
                }

                return new ReconciliationOperatorActionTokenValidationResult
                {
                    Succeeded = true,
                    Result = ReconciliationOperatorActionOutcomes.Applied,
                    CaseId = payload.CaseId.Trim(),
                    Action = normalizedAction,
                    Nonce = payload.Nonce.Trim(),
                    Transport = NormalizeOptionalValue(payload.Transport),
                    ActionTokenHash = actionTokenHash,
                    TokenFingerprintPrefix = tokenFingerprintPrefix,
                    ExpiresAtUtc = payload.ExpiresAtUtc,
                    Payload = payload
                };
            }
            catch (Exception ex) when (ex is FormatException or CryptographicException or JsonException)
            {
                _logger.Warn(
                    "Rejected invalid release reconciler operator action token result={0} tokenFingerprintPrefix={1} reason={2}.",
                    ex is CryptographicException ? ReconciliationOperatorActionOutcomes.InvalidSignature : ReconciliationOperatorActionOutcomes.InvalidPayload,
                    tokenFingerprintPrefix,
                    ex.GetType().Name);

                return new ReconciliationOperatorActionTokenValidationResult
                {
                    Succeeded = false,
                    Result = ex is CryptographicException ? ReconciliationOperatorActionOutcomes.InvalidSignature : ReconciliationOperatorActionOutcomes.InvalidPayload,
                    FailureSummary = ex is CryptographicException
                        ? "Operator action token signature was invalid."
                        : "Operator action token could not be parsed.",
                    ActionTokenHash = actionTokenHash,
                    TokenFingerprintPrefix = tokenFingerprintPrefix
                };
            }
        }

        public string ProtectPayload(ReconciliationOperatorActionTokenPayload payload)
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

        public static string GetTokenFingerprintPrefix(string actionTokenHash)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(actionTokenHash);
            return actionTokenHash[..Math.Min(12, actionTokenHash.Length)];
        }

        private static ReconciliationReleaseSwitchTarget? NormalizeReleaseSwitchTargetForIssue(string normalizedAction, ReconciliationReleaseSwitchTarget? releaseSwitchTarget)
        {
            if (!string.Equals(normalizedAction, ReconciliationOperatorActionKinds.ApproveSwitch, StringComparison.Ordinal))
            {
                return null;
            }

            return NormalizeReleaseSwitchTarget(releaseSwitchTarget)
                ?? throw new InvalidOperationException("Approve-switch operator action requires signed winner album and release identifiers.");
        }

        private static ReconciliationReleaseSwitchTarget? NormalizeReleaseSwitchTarget(ReconciliationReleaseSwitchTarget? releaseSwitchTarget)
        {
            if (releaseSwitchTarget == null)
            {
                return null;
            }

            var albumMusicBrainzId = NormalizeOptionalValue(releaseSwitchTarget.AlbumMusicBrainzId);
            var releaseMusicBrainzId = NormalizeOptionalValue(releaseSwitchTarget.ReleaseMusicBrainzId);
            if (albumMusicBrainzId == null || releaseMusicBrainzId == null)
            {
                return null;
            }

            return new ReconciliationReleaseSwitchTarget
            {
                AlbumMusicBrainzId = albumMusicBrainzId,
                ReleaseMusicBrainzId = releaseMusicBrainzId,
                Classification = NormalizeOptionalValue(releaseSwitchTarget.Classification),
                ScoringVersion = NormalizeOptionalValue(releaseSwitchTarget.ScoringVersion)
            };
        }

        private static string? NormalizeOptionalValue(string? value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        private static ReconciliationOperatorActionTokenValidationResult Failure(string result, string failureSummary, ReconciliationOperatorActionTokenPayload payload, string actionTokenHash, string tokenFingerprintPrefix)
        {
            return Failure(result, failureSummary, payload.CaseId, payload.Action, payload.Nonce, NormalizeOptionalValue(payload.Transport), actionTokenHash, tokenFingerprintPrefix, payload.ExpiresAtUtc, payload);
        }

        private static ReconciliationOperatorActionTokenValidationResult Failure(string result, string failureSummary, string? caseId, string? action, string? nonce, string? transport, string? actionTokenHash, string? tokenFingerprintPrefix, DateTimeOffset? expiresAtUtc, ReconciliationOperatorActionTokenPayload? payload)
        {
            return new ReconciliationOperatorActionTokenValidationResult
            {
                Succeeded = false,
                Result = result,
                FailureSummary = failureSummary,
                CaseId = NormalizeOptionalValue(caseId),
                Action = NormalizeOptionalValue(action),
                Nonce = NormalizeOptionalValue(nonce),
                Transport = NormalizeOptionalValue(transport),
                ActionTokenHash = actionTokenHash,
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
