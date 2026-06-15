using System;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Lidarr.Http.Frontend.Mappers;
using Microsoft.AspNetCore.Mvc;
using NLog;
using NzbDrone.Core.Plugins.ReleaseReconciler.Cases;
using NzbDrone.Core.Plugins.ReleaseReconciler.Notifications;

namespace NzbDrone.Core.Plugins.ReleaseReconciler.Evidence
{
    public sealed class ReconciliationEvidenceMapper : IMapHttpRequestsToDisk
    {
        private static readonly Regex EvidencePathRegex = new(
            "^/release-reconciler/evidence/(?<token>[A-Za-z0-9_-]+)/index\\.html$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        private readonly IReconciliationCaseStore _caseStore;
        private readonly ReconciliationEvidenceTokenService _tokenService;
        private readonly ReconciliationNotificationSettings _settings;
        private readonly Logger _logger;

        public ReconciliationEvidenceMapper(
            IReconciliationCaseStore caseStore,
            ReconciliationEvidenceTokenService tokenService,
            ReconciliationNotificationSettings settings,
            Logger logger)
        {
            _caseStore = caseStore ?? throw new ArgumentNullException(nameof(caseStore));
            _tokenService = tokenService ?? throw new ArgumentNullException(nameof(tokenService));
            _settings = (settings ?? throw new ArgumentNullException(nameof(settings))).Normalize();
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public string Map(string resourceUrl)
        {
            return resourceUrl ?? string.Empty;
        }

        public bool CanHandle(string resourceUrl)
        {
            return !string.IsNullOrWhiteSpace(resourceUrl) && EvidencePathRegex.IsMatch(resourceUrl);
        }

        public Task<IActionResult> GetResponse(string resourceUrl)
        {
            var match = EvidencePathRegex.Match(resourceUrl ?? string.Empty);
            if (!match.Success)
            {
                return Task.FromResult<IActionResult>(ReconciliationEvidencePage.Refusal(
                    400,
                    "Evidence link is invalid",
                    "The evidence link format is invalid. Request a fresh notification and try again."));
            }

            var token = match.Groups["token"].Value;
            var validation = _tokenService.Validate(token);
            if (!validation.Succeeded)
            {
                _logger.Warn(
                    "Rejected release reconciler evidence page result={0} caseId={1} evidenceTokenHash={2} tokenFingerprintPrefix={3} expiresAtUtc={4}.",
                    validation.Result,
                    validation.CaseId ?? "unknown",
                    validation.EvidenceTokenHash ?? "none",
                    validation.TokenFingerprintPrefix ?? "none",
                    validation.ExpiresAtUtc ?? DateTimeOffset.MinValue);

                return Task.FromResult<IActionResult>(BuildRefusalPage(validation));
            }

            var snapshot = _caseStore.Get(validation.CaseId!);
            if (snapshot == null)
            {
                _logger.Warn(
                    "Rejected release reconciler evidence page result={0} caseId={1} evidenceTokenHash={2} tokenFingerprintPrefix={3} expiresAtUtc={4}.",
                    ReconciliationEvidenceTokenOutcomes.MissingCase,
                    validation.CaseId,
                    validation.EvidenceTokenHash ?? "none",
                    validation.TokenFingerprintPrefix ?? "none",
                    validation.ExpiresAtUtc ?? DateTimeOffset.MinValue);

                return Task.FromResult<IActionResult>(ReconciliationEvidencePage.Refusal(
                    404,
                    "Evidence case not found",
                    "The signed evidence link resolved, but the referenced reconciliation case is no longer available."));
            }

            _logger.Info(
                "Served release reconciler evidence page caseId={0} evidenceTokenHash={1} tokenFingerprintPrefix={2} expiresAtUtc={3}.",
                snapshot.CaseId,
                validation.EvidenceTokenHash ?? "none",
                validation.TokenFingerprintPrefix ?? "none",
                validation.ExpiresAtUtc ?? DateTimeOffset.MinValue);

            return Task.FromResult<IActionResult>(ReconciliationEvidencePage.Success(snapshot, _settings));
        }

        private static IActionResult BuildRefusalPage(ReconciliationEvidenceTokenValidationResult validation)
        {
            return validation.Result switch
            {
                ReconciliationEvidenceTokenOutcomes.Expired => ReconciliationEvidencePage.Refusal(
                    410,
                    "Evidence link expired",
                    EnsureGuidance(
                        validation.FailureSummary,
                        "This read-only evidence link has expired. Request a fresh notification to inspect the case again.",
                        "Request a fresh notification to inspect the case again.")),
                ReconciliationEvidenceTokenOutcomes.InvalidSignature or ReconciliationEvidenceTokenOutcomes.InvalidPayload => ReconciliationEvidencePage.Refusal(
                    400,
                    "Evidence link is invalid",
                    EnsureGuidance(
                        validation.FailureSummary,
                        "The evidence link is not valid. Request a fresh notification and try again.",
                        "Request a fresh notification and try again.")),
                _ => ReconciliationEvidencePage.Refusal(
                    400,
                    "Evidence link is invalid",
                    EnsureGuidance(
                        validation.FailureSummary,
                        "The evidence link is not valid. Request a fresh notification and try again.",
                        "Request a fresh notification and try again."))
            };
        }

        private static string EnsureGuidance(string? failureSummary, string fallback, string guidance)
        {
            if (string.IsNullOrWhiteSpace(failureSummary))
            {
                return fallback;
            }

            return failureSummary.Contains("Request a fresh notification", StringComparison.OrdinalIgnoreCase)
                ? failureSummary
                : $"{failureSummary.TrimEnd('.', ' ')}. {guidance}";
        }
    }
}
