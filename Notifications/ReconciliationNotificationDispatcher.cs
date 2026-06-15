using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Core.Plugins.ReleaseReconciler.Cases;

namespace NzbDrone.Core.Plugins.ReleaseReconciler.Notifications
{
    public sealed class ReconciliationNotificationDispatcher
    {
        private static readonly Regex TokenPattern = new(@"(?<label>token|key|secret|authorization|cookie)(?<sep>[:=])(?<value>[^\r\n&\s]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex QueryTokenPattern = new(@"([?&](?:token|key|secret)=[^&\s]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex BearerPattern = new(@"bearer\s+[A-Za-z0-9._~+/=-]+", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private readonly ReconciliationNotificationBuilder _builder;
        private readonly IReconciliationCaseStore _caseStore;
        private readonly IReadOnlyList<INotificationTransport> _transports;
        private readonly Logger _logger;

        public ReconciliationNotificationDispatcher(
            ReconciliationNotificationBuilder builder,
            IReconciliationCaseStore caseStore,
            IEnumerable<INotificationTransport> transports,
            Logger logger)
        {
            _builder = builder ?? throw new ArgumentNullException(nameof(builder));
            _caseStore = caseStore ?? throw new ArgumentNullException(nameof(caseStore));
            _transports = (transports ?? throw new ArgumentNullException(nameof(transports))).ToArray();
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public ReconciliationNotificationDispatchResult Dispatch(ReconciliationCase reconciliationCase, ReconciliationNotificationSettings settings)
        {
            ArgumentNullException.ThrowIfNull(reconciliationCase);
            ArgumentNullException.ThrowIfNull(settings);

            var envelope = _builder.Build(reconciliationCase, settings);
            var enabledTransports = _transports.Where(transport => transport.IsEnabled(settings)).ToArray();
            var results = new List<ReconciliationNotificationDispatchOutcome>();
            ReconciliationCase currentCase = reconciliationCase;
            ReconciliationCaseSnapshot? lastSnapshot = null;

            if (enabledTransports.Length == 0)
            {
                _logger.Info("No release reconciler notification transports were enabled for caseId={0}.", reconciliationCase.CaseId);
                return new ReconciliationNotificationDispatchResult(envelope, results, null);
            }

            foreach (var transport in enabledTransports)
            {
                if (!transport.TryValidate(settings, out var validationFailure))
                {
                    var summary = NormalizeFailureSummary(validationFailure ?? "Transport settings are invalid.");
                    lastSnapshot = PersistAttempt(ref currentCase, envelope, transport.TransportName, succeeded: false, failureKind: "invalid-settings", failureSummary: summary);
                    results.Add(new ReconciliationNotificationDispatchOutcome(transport.TransportName, false, "invalid-settings", summary));
                    _logger.Warn("Refused release reconciler notification caseId={0} transport={1} reason={2}.", envelope.CaseId, transport.TransportName, summary);
                    continue;
                }

                try
                {
                    transport.Send(envelope, settings);
                    lastSnapshot = PersistAttempt(ref currentCase, envelope, transport.TransportName, succeeded: true, failureKind: null, failureSummary: null);
                    results.Add(new ReconciliationNotificationDispatchOutcome(transport.TransportName, true, null, null));
                }
                catch (HttpException ex)
                {
                    var summary = NormalizeFailureSummary($"HTTP {(int)ex.Response.StatusCode} {ex.Response.StatusCode}");
                    lastSnapshot = PersistAttempt(ref currentCase, envelope, transport.TransportName, succeeded: false, failureKind: "http-error", failureSummary: summary);
                    results.Add(new ReconciliationNotificationDispatchOutcome(transport.TransportName, false, "http-error", summary));
                    _logger.Warn(ex, "Release reconciler notification dispatch failed caseId={0} transport={1} statusCode={2}.", envelope.CaseId, transport.TransportName, ex.Response.StatusCode);
                }
                catch (Exception ex)
                {
                    var summary = NormalizeFailureSummary($"{ex.GetType().Name}: {ex.Message}");
                    lastSnapshot = PersistAttempt(ref currentCase, envelope, transport.TransportName, succeeded: false, failureKind: "dispatch-error", failureSummary: summary);
                    results.Add(new ReconciliationNotificationDispatchOutcome(transport.TransportName, false, "dispatch-error", summary));
                    _logger.Warn(ex, "Release reconciler notification dispatch failed caseId={0} transport={1}.", envelope.CaseId, transport.TransportName);
                }
            }

            return new ReconciliationNotificationDispatchResult(envelope, results, lastSnapshot);
        }

        private ReconciliationCaseSnapshot PersistAttempt(
            ref ReconciliationCase currentCase,
            ReconciliationNotificationEnvelope envelope,
            string transport,
            bool succeeded,
            string? failureKind,
            string? failureSummary)
        {
            var notificationState = currentCase.NotificationState?.Normalize() ?? new ReconciliationNotificationState();
            notificationState.DispatchAttempts.Add(new ReconciliationNotificationDispatchAttempt
            {
                AttemptedAtUtc = envelope.GeneratedAtUtc,
                Transport = transport,
                Succeeded = succeeded,
                ActionUrlKind = envelope.ActionUrlKind,
                ActionTokenExpiresAtUtc = envelope.ActionTokenExpiresAtUtc,
                FailureKind = failureKind,
                FailureSummary = failureSummary
            });

            currentCase = new ReconciliationCase(
                sourceSeam: currentCase.SourceSeam,
                phase: currentCase.Phase,
                capturedIdentifiers: currentCase.CapturedIdentifiers,
                structuralEvidence: currentCase.StructuralEvidence,
                downloadId: currentCase.DownloadId,
                outputPath: currentCase.OutputPath,
                diagnosticSummary: currentCase.DiagnosticSummary,
                lastError: currentCase.LastError,
                caseId: currentCase.CaseId,
                capturedAtUtc: currentCase.CapturedAtUtc,
                updatedAtUtc: envelope.GeneratedAtUtc,
                evaluationResult: currentCase.EvaluationResult,
                notificationState: notificationState,
                operatorActionState: currentCase.OperatorActionState);

            return _caseStore.Save(currentCase);
        }

        private static string NormalizeFailureSummary(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "Unknown dispatch failure.";
            }

            var redacted = QueryTokenPattern.Replace(value.Trim(), match =>
            {
                var key = match.Value.Split('=')[0];
                return key + "=[REDACTED]";
            });
            redacted = TokenPattern.Replace(redacted, static match => match.Groups["label"].Value + match.Groups["sep"].Value + "[REDACTED]");
            redacted = BearerPattern.Replace(redacted, "Bearer [REDACTED]");
            return redacted;
        }
    }

    public sealed class ReconciliationNotificationDispatchResult
    {
        public ReconciliationNotificationDispatchResult(
            ReconciliationNotificationEnvelope envelope,
            IReadOnlyList<ReconciliationNotificationDispatchOutcome> outcomes,
            ReconciliationCaseSnapshot? lastSnapshot)
        {
            Envelope = envelope ?? throw new ArgumentNullException(nameof(envelope));
            Outcomes = outcomes ?? throw new ArgumentNullException(nameof(outcomes));
            LastSnapshot = lastSnapshot;
        }

        public ReconciliationNotificationEnvelope Envelope { get; }

        public IReadOnlyList<ReconciliationNotificationDispatchOutcome> Outcomes { get; }

        public ReconciliationCaseSnapshot? LastSnapshot { get; }
    }

    public sealed class ReconciliationNotificationDispatchOutcome
    {
        public ReconciliationNotificationDispatchOutcome(string transport, bool succeeded, string? failureKind, string? failureSummary)
        {
            Transport = transport ?? throw new ArgumentNullException(nameof(transport));
            Succeeded = succeeded;
            FailureKind = failureKind;
            FailureSummary = failureSummary;
        }

        public string Transport { get; }

        public bool Succeeded { get; }

        public string? FailureKind { get; }

        public string? FailureSummary { get; }
    }
}
