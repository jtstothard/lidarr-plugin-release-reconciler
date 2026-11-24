extern alias release_reconciler_nlog;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Logger = release_reconciler_nlog::NLog.Logger;
using NzbDrone.Common.Http;
using NzbDrone.Core.Plugins.ReleaseReconciler.Candidates;
using NzbDrone.Core.Plugins.ReleaseReconciler.Cases;
using NzbDrone.Core.Plugins.ReleaseReconciler.Notifications;

namespace NzbDrone.Core.Plugins.ReleaseReconciler.Capture
{
    public sealed class HostReconciliationCaseCapture
    {
        private const string AlbumMusicBrainzIdKey = "albumMusicBrainzId";
        private const string PreferredReleaseMusicBrainzIdKey = "preferredReleaseMusicBrainzId";
        private const string ExistingCapturedAlbumMusicBrainzIdSource = "captured-album-musicbrainz-id";
        private const string NonActionableResolutionSource = "mismatch-not-actionable";
        private const string ResolverNotConfiguredResolutionSource = "resolver-not-configured";
        private const string ResolverErrorResolutionSource = "resolver-error";

        private static readonly Regex HeaderSecretPattern = new(
            "(?<label>authorization|cookie|cookies|set-cookie)\\s*[:=]\\s*(?<value>[^\\r\\n;]+)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex BearerSecretPattern = new(
            "bearer\\s+[A-Za-z0-9._~+/=-]+",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex TokenSecretPattern = new(
            "(?<label>token|api[_-]?key|apikey|password|passwd|secret|session(?:id)?)\\s*[:=]\\s*(?<value>[^\\r\\n;]+)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly HostImportFailureClassifier ImportFailureClassifier = new();
        private static readonly HostHarmonySourceSupportClassifier HarmonySourceSupportClassifier = new();

        private readonly IReconciliationCaseStore _caseStore;
        private readonly IHostMusicBrainzContextResolver _hostMusicBrainzContextResolver;
        private readonly NotificationSettingsStore? _notificationSettingsStore;
        private readonly ReconciliationNotificationDispatcher? _notificationDispatcher;
        private readonly Logger _logger;

        public HostReconciliationCaseCapture(
            IReconciliationCaseStore caseStore,
            Logger logger,
            IHostMusicBrainzContextResolver? hostMusicBrainzContextResolver = null,
            NotificationSettingsStore? notificationSettingsStore = null,
            ReconciliationNotificationDispatcher? notificationDispatcher = null)
        {
            _caseStore = caseStore ?? throw new ArgumentNullException(nameof(caseStore));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _hostMusicBrainzContextResolver = hostMusicBrainzContextResolver ?? NoOpHostMusicBrainzContextResolver.Instance;
            _notificationSettingsStore = notificationSettingsStore;
            _notificationDispatcher = notificationDispatcher;
        }

        public ReconciliationCaseSnapshot Capture(HostReconciliationCaptureRequest request)
        {
            ArgumentNullException.ThrowIfNull(request);

            var normalizedSourceSeam = NormalizeRequired(request.SourceSeam, nameof(request.SourceSeam));
            var normalizedDownloadId = NormalizeRequired(request.DownloadId, nameof(request.DownloadId));
            var normalizedPhase = NormalizeRequired(request.Phase, nameof(request.Phase));
            var normalizedStatus = NormalizeOptional(request.Status);
            var capturedIdentifiers = BuildCapturedIdentifiers(request.CapturedIdentifiers);

            if (capturedIdentifiers.Count == 0)
            {
                throw new ArgumentException("Host reconciliation capture requires at least one stable captured identifier.", nameof(request));
            }

            var structuralEvidence = BuildStructuralEvidence(request.StructuralEvidence);
            var capturedAtUtc = DetermineCapturedAtUtc(request);
            var updatedAtUtc = DetermineUpdatedAtUtc(request, capturedAtUtc);
            var diagnosticSummary = NormalizeOptional(request.DiagnosticSummary)
                ?? BuildDiagnosticSummary(request, normalizedSourceSeam, normalizedPhase, normalizedStatus, structuralEvidence);
            var normalizedLastError = NormalizeOptional(request.LastError);
            var importFailureClassification = ImportFailureClassifier.Classify(
                normalizedPhase,
                normalizedStatus,
                normalizedLastError,
                diagnosticSummary,
                structuralEvidence);
            var musicBrainzCaptureOutcome = EnrichCapturedIdentifiers(
                capturedIdentifiers,
                structuralEvidence,
                importFailureClassification,
                normalizedSourceSeam,
                normalizedPhase,
                normalizedDownloadId);
            var harmonySourceSupport = importFailureClassification.Actionable
                ? HarmonySourceSupportClassifier.Classify(importFailureClassification, request.SourceMetadata)
                : HostHarmonySourceSupportClassifier.SuppressNonActionable(importFailureClassification);
            var existingSnapshot = _caseStore.FindByDownloadId(normalizedSourceSeam, normalizedDownloadId);
            var lookup = existingSnapshot == null ? "miss" : "hit";

            _logger.Info(
                "Capturing host reconciliation evidence: seam={0}, phase={1}, status={2}, downloadId={3}, lookup={4}, identifiers={5}, trackCount={6}, failure={7}, mismatchActionable={8}, mismatchRuleVersion={9}, mismatchRule={10}, mismatchSuppression={11}, mbContextResolved={12}, mbResolutionSource={13}, mbReleaseIdPresent={14}, mbAlbumIdPresent={15}, sourceSupport={16}, sourceSupportRuleVersion={17}, sourceSupportRule={18}, sourceSupportProvider={19}, sourceSupportSuppression={20}, sourceSupportLookup={21}",
                normalizedSourceSeam,
                normalizedPhase,
                normalizedStatus ?? "none",
                normalizedDownloadId,
                lookup,
                capturedIdentifiers.Count,
                structuralEvidence.TrackCount,
                normalizedLastError == null ? "none" : "redacted",
                importFailureClassification.Actionable,
                importFailureClassification.RuleVersion,
                importFailureClassification.MatchedRule,
                importFailureClassification.SuppressionReason ?? "none",
                musicBrainzCaptureOutcome.MbContextResolved,
                musicBrainzCaptureOutcome.ResolutionSource,
                musicBrainzCaptureOutcome.MbReleaseIdPresent,
                musicBrainzCaptureOutcome.MbAlbumIdPresent,
                harmonySourceSupport.Supported,
                harmonySourceSupport.RuleVersion,
                harmonySourceSupport.MatchedRule,
                harmonySourceSupport.MatchedProvider ?? "none",
                harmonySourceSupport.SuppressionReason ?? "none",
                FormatHarmonyLookupSummary(harmonySourceSupport));

            var reconciliationCase = new ReconciliationCase(
                normalizedSourceSeam,
                normalizedPhase,
                capturedIdentifiers,
                structuralEvidence,
                downloadId: normalizedDownloadId,
                outputPath: NormalizeOptional(request.OutputPath),
                diagnosticSummary: diagnosticSummary,
                lastError: normalizedLastError,
                importFailureClassification: importFailureClassification,
                harmonySourceSupport: harmonySourceSupport,
                caseId: existingSnapshot?.CaseId,
                capturedAtUtc: existingSnapshot?.CapturedAtUtc ?? capturedAtUtc,
                updatedAtUtc: updatedAtUtc,
                evaluationResult: existingSnapshot?.EvaluationResult,
                notificationState: existingSnapshot?.NotificationState,
                operatorActionState: existingSnapshot?.OperatorActionState,
                releaseSwitchApprovalState: existingSnapshot?.ReleaseSwitchApprovalState);

            try
            {
                var persisted = _caseStore.Save(reconciliationCase);

                _logger.Info(
                    "Persisted host reconciliation case: seam={0}, phase={1}, status={2}, downloadId={3}, lookup={4}, caseId={5}, finalFailure={6}, mismatchActionable={7}, mismatchRuleVersion={8}, mismatchRule={9}, mismatchSuppression={10}, mbContextResolved={11}, mbResolutionSource={12}, mbReleaseIdPresent={13}, mbAlbumIdPresent={14}, sourceSupport={15}, sourceSupportRuleVersion={16}, sourceSupportRule={17}, sourceSupportProvider={18}, sourceSupportSuppression={19}, sourceSupportLookup={20}, notificationAttempts={21}",
                    normalizedSourceSeam,
                    persisted.Phase,
                    normalizedStatus ?? "none",
                    normalizedDownloadId,
                    lookup,
                    persisted.CaseId,
                    persisted.LastError == null ? "none" : "redacted",
                    persisted.ImportFailureClassification?.Actionable ?? false,
                    persisted.ImportFailureClassification?.RuleVersion ?? "none",
                    persisted.ImportFailureClassification?.MatchedRule ?? "none",
                    persisted.ImportFailureClassification?.SuppressionReason ?? "none",
                    musicBrainzCaptureOutcome.MbContextResolved,
                    musicBrainzCaptureOutcome.ResolutionSource,
                    musicBrainzCaptureOutcome.MbReleaseIdPresent,
                    musicBrainzCaptureOutcome.MbAlbumIdPresent,
                    persisted.HarmonySourceSupport?.Supported ?? false,
                    persisted.HarmonySourceSupport?.RuleVersion ?? "none",
                    persisted.HarmonySourceSupport?.MatchedRule ?? "none",
                    persisted.HarmonySourceSupport?.MatchedProvider ?? "none",
                    persisted.HarmonySourceSupport?.SuppressionReason ?? "none",
                    FormatHarmonyLookupSummary(persisted.HarmonySourceSupport),
                    persisted.NotificationState?.DispatchAttempts.Count ?? 0);

                return TryDispatchNotification(persisted, normalizedStatus);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                _logger.Error(
                    ex,
                    "Failed to capture host reconciliation evidence: seam={0}, phase={1}, status={2}, downloadId={3}, diagnostic={4}",
                    NormalizeOrFallback(request.SourceSeam, "-"),
                    NormalizeOrFallback(request.Phase, "-"),
                    NormalizeOrFallback(request.Status, "-"),
                    NormalizeOrFallback(request.DownloadId, "-"),
                    RedactSensitiveText(string.Join("; ", new[] { request.DiagnosticSummary, request.LastError }.Where(static value => !string.IsNullOrWhiteSpace(value)))));
                throw;
            }
        }

        private ReconciliationCaseSnapshot TryDispatchNotification(ReconciliationCaseSnapshot persisted, string? status)
        {
            if (_notificationSettingsStore == null || _notificationDispatcher == null)
            {
                return persisted;
            }

            if (persisted.ImportFailureClassification?.Actionable != true)
            {
                _logger.Info(
                    "Skipping host reconciliation notification dispatch: caseId={0}, status={1}, reason=non-actionable.",
                    persisted.CaseId,
                    NormalizeOrFallback(status, "none"));
                return persisted;
            }

            if (persisted.HarmonySourceSupport?.Supported != true)
            {
                _logger.Info(
                    "Skipping host reconciliation notification dispatch: caseId={0}, status={1}, reason=source-unsupported, matchedRule={2}, suppression={3}, lookup={4}.",
                    persisted.CaseId,
                    NormalizeOrFallback(status, "none"),
                    persisted.HarmonySourceSupport?.MatchedRule ?? "none",
                    persisted.HarmonySourceSupport?.SuppressionReason ?? "none",
                    FormatHarmonyLookupSummary(persisted.HarmonySourceSupport));
                return persisted;
            }

            var priorAttempts = persisted.NotificationState?.DispatchAttempts ?? new List<ReconciliationNotificationDispatchAttempt>();
            if (priorAttempts.Any(static attempt => attempt.Succeeded))
            {
                _logger.Info(
                    "Skipping host reconciliation notification dispatch: caseId={0}, status={1}, reason=prior-success, attempts={2}.",
                    persisted.CaseId,
                    NormalizeOrFallback(status, "none"),
                    priorAttempts.Count);
                return persisted;
            }

            var settings = _notificationSettingsStore.Load().Normalize();
            var enabledTransports = DescribeEnabledTransports(settings);
            if (enabledTransports == "none")
            {
                _logger.Info(
                    "Skipping host reconciliation notification dispatch: caseId={0}, status={1}, reason=no-enabled-transports, settingsPath={2}.",
                    persisted.CaseId,
                    NormalizeOrFallback(status, "none"),
                    _notificationSettingsStore.SettingsPath);
                return persisted;
            }

            var validationFailures = settings.Validate();
            if (validationFailures.Count > 0)
            {
                var failureSummary = string.Join("; ", validationFailures);
                var failedSnapshot = PersistNotificationFailure(persisted, ResolvePreDispatchFailureTransport(settings), "invalid-settings", failureSummary);
                _logger.Warn(
                    "Skipping host reconciliation notification dispatch due to invalid settings: caseId={0}, status={1}, transport={2}, settingsPath={3}, failures={4}.",
                    persisted.CaseId,
                    NormalizeOrFallback(status, "none"),
                    ResolvePreDispatchFailureTransport(settings),
                    _notificationSettingsStore.SettingsPath,
                    failureSummary);
                return failedSnapshot;
            }

            try
            {
                var result = _notificationDispatcher.Dispatch(RehydrateCase(persisted), settings);
                if (result.LastSnapshot != null)
                {
                    _logger.Info(
                        "Completed host reconciliation notification dispatch: caseId={0}, status={1}, enabledTransports={2}, outcomes={3}, successes={4}, failures={5}.",
                        persisted.CaseId,
                        NormalizeOrFallback(status, "none"),
                        enabledTransports,
                        result.Outcomes.Count,
                        result.Outcomes.Count(static outcome => outcome.Succeeded),
                        result.Outcomes.Count(static outcome => !outcome.Succeeded));

                    return result.LastSnapshot;
                }

                _logger.Info(
                    "Host reconciliation notification dispatch produced no persisted attempts: caseId={0}, status={1}, enabledTransports={2}.",
                    persisted.CaseId,
                    NormalizeOrFallback(status, "none"),
                    enabledTransports);
                return persisted;
            }
            catch (Exception ex)
            {
                var failureKind = ex is ArgumentException or InvalidOperationException ? "invalid-settings"
                    : ex is HttpException ? "http-error"
                    : "dispatch-error";
                var failedSnapshot = PersistNotificationFailure(persisted, ResolvePreDispatchFailureTransport(settings), failureKind, ex.Message);

                _logger.Warn(
                    ex,
                    "Host reconciliation notification dispatch failed before transport execution: caseId={0}, status={1}, transport={2}, failureKind={3}, settingsPath={4}.",
                    persisted.CaseId,
                    NormalizeOrFallback(status, "none"),
                    ResolvePreDispatchFailureTransport(settings),
                    failureKind,
                    _notificationSettingsStore.SettingsPath);

                return failedSnapshot;
            }
        }

        private ReconciliationCaseSnapshot PersistNotificationFailure(
            ReconciliationCaseSnapshot current,
            string transport,
            string failureKind,
            string? failureSummary)
        {
            var notificationState = current.NotificationState?.Normalize() ?? new ReconciliationNotificationState();
            notificationState.DispatchAttempts.Add(new ReconciliationNotificationDispatchAttempt
            {
                AttemptedAtUtc = current.LastUpdatedAtUtc >= current.CapturedAtUtc ? current.LastUpdatedAtUtc : current.CapturedAtUtc,
                Transport = transport,
                Succeeded = false,
                FailureKind = failureKind,
                FailureSummary = RedactSensitiveText(failureSummary)
            });

            return _caseStore.Save(RehydrateCase(current, notificationState));
        }

        private static ReconciliationCase RehydrateCase(
            ReconciliationCaseSnapshot snapshot,
            ReconciliationNotificationState? notificationState = null)
        {
            return new ReconciliationCase(
                sourceSeam: snapshot.SourceSeam,
                phase: snapshot.Phase,
                capturedIdentifiers: snapshot.CapturedIdentifiers,
                structuralEvidence: snapshot.StructuralEvidence!,
                downloadId: snapshot.DownloadId,
                outputPath: snapshot.OutputPath,
                diagnosticSummary: snapshot.DiagnosticSummary,
                lastError: snapshot.LastError,
                importFailureClassification: snapshot.ImportFailureClassification,
                harmonySourceSupport: snapshot.HarmonySourceSupport,
                caseId: snapshot.CaseId,
                capturedAtUtc: snapshot.CapturedAtUtc,
                updatedAtUtc: snapshot.LastUpdatedAtUtc,
                evaluationResult: snapshot.EvaluationResult,
                notificationState: notificationState ?? snapshot.NotificationState,
                operatorActionState: snapshot.OperatorActionState,
                releaseSwitchApprovalState: snapshot.ReleaseSwitchApprovalState);
        }

        private static string DescribeEnabledTransports(ReconciliationNotificationSettings settings)
        {
            var enabled = new List<string>();
            if (settings.Discord.Enabled)
            {
                enabled.Add("discord");
            }

            if (settings.Telegram.Enabled)
            {
                enabled.Add("telegram");
            }

            return enabled.Count == 0 ? "none" : string.Join(",", enabled);
        }

        private static string ResolvePreDispatchFailureTransport(ReconciliationNotificationSettings settings)
        {
            return settings.Discord.Enabled && !settings.Telegram.Enabled
                ? "discord"
                : !settings.Discord.Enabled && settings.Telegram.Enabled
                    ? "telegram"
                    : "dispatcher";
        }

        private HostMusicBrainzCaptureOutcome EnrichCapturedIdentifiers(
            Dictionary<string, string?> capturedIdentifiers,
            ReconciliationEvidence structuralEvidence,
            HostImportFailureClassification importFailureClassification,
            string sourceSeam,
            string phase,
            string downloadId)
        {
            var preferredReleaseMusicBrainzId = EnsurePreferredReleaseMusicBrainzId(capturedIdentifiers, structuralEvidence);
            var existingAlbumMusicBrainzId = GetCapturedIdentifier(capturedIdentifiers, AlbumMusicBrainzIdKey);

            if (existingAlbumMusicBrainzId != null)
            {
                return HostMusicBrainzCaptureOutcome.FromIdentifiers(
                    ExistingCapturedAlbumMusicBrainzIdSource,
                    preferredReleaseMusicBrainzId,
                    existingAlbumMusicBrainzId);
            }

            if (!importFailureClassification.Actionable)
            {
                return HostMusicBrainzCaptureOutcome.FromIdentifiers(
                    NonActionableResolutionSource,
                    preferredReleaseMusicBrainzId,
                    null);
            }

            if (preferredReleaseMusicBrainzId == null)
            {
                return HostMusicBrainzCaptureOutcome.FromIdentifiers(
                    HostMusicBrainzContextResolution.MissingPreferredReleaseId,
                    null,
                    null);
            }

            try
            {
                var resolution = _hostMusicBrainzContextResolver.Resolve(preferredReleaseMusicBrainzId);
                var resolvedAlbumMusicBrainzId = NormalizeOptional(resolution.AlbumMusicBrainzId);
                if (resolvedAlbumMusicBrainzId != null)
                {
                    capturedIdentifiers[AlbumMusicBrainzIdKey] = resolvedAlbumMusicBrainzId;
                }

                return HostMusicBrainzCaptureOutcome.FromResolution(
                    resolution,
                    GetCapturedIdentifier(capturedIdentifiers, PreferredReleaseMusicBrainzIdKey),
                    GetCapturedIdentifier(capturedIdentifiers, AlbumMusicBrainzIdKey));
            }
            catch (Exception ex)
            {
                _logger.Warn(
                    ex,
                    "Host MusicBrainz context resolution failed during host capture: seam={0}, phase={1}, downloadId={2}, preferredReleaseMusicBrainzId={3}",
                    sourceSeam,
                    phase,
                    downloadId,
                    preferredReleaseMusicBrainzId);

                return HostMusicBrainzCaptureOutcome.FromIdentifiers(
                    ResolverErrorResolutionSource,
                    preferredReleaseMusicBrainzId,
                    null);
            }
        }

        private static string? EnsurePreferredReleaseMusicBrainzId(
            IDictionary<string, string?> capturedIdentifiers,
            ReconciliationEvidence structuralEvidence)
        {
            var existingPreferredReleaseMusicBrainzId = GetCapturedIdentifier(capturedIdentifiers, PreferredReleaseMusicBrainzIdKey);
            if (existingPreferredReleaseMusicBrainzId != null)
            {
                return existingPreferredReleaseMusicBrainzId;
            }

            var structuralPreferredReleaseMusicBrainzId = NormalizeOptional(structuralEvidence.PreferredRelease?.ReleaseMusicBrainzId);
            if (structuralPreferredReleaseMusicBrainzId != null)
            {
                capturedIdentifiers[PreferredReleaseMusicBrainzIdKey] = structuralPreferredReleaseMusicBrainzId;
            }

            return structuralPreferredReleaseMusicBrainzId;
        }

        private static string? GetCapturedIdentifier(IDictionary<string, string?> capturedIdentifiers, string key)
        {
            return capturedIdentifiers.TryGetValue(key, out var value)
                ? NormalizeOptional(value)
                : null;
        }

        private static Dictionary<string, string?> BuildCapturedIdentifiers(IReadOnlyDictionary<string, string?>? rawIdentifiers)
        {
            var identifiers = new Dictionary<string, string?>(StringComparer.Ordinal);

            if (rawIdentifiers == null)
            {
                return identifiers;
            }

            foreach (var pair in rawIdentifiers)
            {
                var key = NormalizeRequired(pair.Key, nameof(rawIdentifiers));
                var value = NormalizeOptional(pair.Value);
                if (value != null)
                {
                    identifiers[key] = value;
                }
            }

            return identifiers;
        }

        private static ReconciliationEvidence BuildStructuralEvidence(HostReconciliationStructuralEvidence? structuralEvidence)
        {
            if (structuralEvidence == null)
            {
                return new ReconciliationEvidence().Normalize();
            }

            return new ReconciliationEvidence
            {
                TrackCount = structuralEvidence.TrackCount > 0 ? structuralEvidence.TrackCount : structuralEvidence.Tracks.Count,
                PreferredRelease = structuralEvidence.PreferredRelease == null
                    ? null
                    : new PreferredReleaseReconciliationEvidence
                    {
                        ReleaseMusicBrainzId = structuralEvidence.PreferredRelease.ReleaseMusicBrainzId,
                        ReleaseArtistMusicBrainzId = structuralEvidence.PreferredRelease.ReleaseArtistMusicBrainzId,
                        ReleaseStatus = structuralEvidence.PreferredRelease.ReleaseStatus,
                        Label = structuralEvidence.PreferredRelease.Label,
                        ReleaseDateUtc = structuralEvidence.PreferredRelease.ReleaseDateUtc,
                        DiscCount = structuralEvidence.PreferredRelease.DiscCount,
                        MediaByDisc = structuralEvidence.PreferredRelease.MediaByDisc
                            .Where(static pair => !string.IsNullOrWhiteSpace(pair.Value))
                            .ToDictionary(static pair => pair.Key, static pair => pair.Value.Trim())
                    },
                Tracks = structuralEvidence.Tracks
                    .Select(static track => new ReconciliationTrackEvidence
                    {
                        AbsoluteTrackNumber = track.AbsoluteTrackNumber,
                        MediumNumber = track.MediumNumber,
                        Title = track.Title,
                        RecordingMusicBrainzId = track.RecordingMusicBrainzId,
                        ReleaseTrackMusicBrainzId = track.ReleaseTrackMusicBrainzId
                    })
                    .ToList()
            }.Normalize();
        }

        private static DateTimeOffset DetermineCapturedAtUtc(HostReconciliationCaptureRequest request)
        {
            return request.CapturedAtUtc
                ?? request.QueuedAtUtc
                ?? request.CompletedAtUtc
                ?? DateTimeOffset.UtcNow;
        }

        private static DateTimeOffset DetermineUpdatedAtUtc(HostReconciliationCaptureRequest request, DateTimeOffset capturedAtUtc)
        {
            var updatedAtUtc = request.UpdatedAtUtc
                ?? request.CompletedAtUtc
                ?? request.CapturedAtUtc
                ?? request.QueuedAtUtc
                ?? capturedAtUtc;

            return updatedAtUtc < capturedAtUtc ? capturedAtUtc : updatedAtUtc;
        }

        private static string BuildDiagnosticSummary(
            HostReconciliationCaptureRequest request,
            string sourceSeam,
            string phase,
            string? status,
            ReconciliationEvidence structuralEvidence)
        {
            var parts = new List<string>
            {
                $"status={NormalizeOrFallback(status, "Unknown")}",
                $"phase={phase}",
                $"sourceSeam={sourceSeam}",
                $"trackCount={structuralEvidence.TrackCount.ToString(CultureInfo.InvariantCulture)}",
                $"recordingMbids={structuralEvidence.RecordingMusicBrainzIds.Count.ToString(CultureInfo.InvariantCulture)}",
                $"releaseTrackMbids={structuralEvidence.ReleaseTrackMusicBrainzIds.Count.ToString(CultureInfo.InvariantCulture)}"
            };

            AddDiagnosticPart(parts, "outputPath", request.OutputPath);

            foreach (var pair in request.SourceMetadata.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
            {
                AddDiagnosticPart(parts, pair.Key, pair.Value);
            }

            if (!string.IsNullOrWhiteSpace(request.LastError))
            {
                parts.Add("failure=redacted");
            }

            return string.Join("; ", parts);
        }

        private static void AddDiagnosticPart(ICollection<string> parts, string key, string? value)
        {
            var normalized = NormalizeOptional(value);
            if (normalized != null)
            {
                parts.Add($"{key}={normalized}");
            }
        }

        private static string FormatHarmonyLookupSummary(HostHarmonySourceSupport? harmonySourceSupport)
        {
            if (harmonySourceSupport == null)
            {
                return "none";
            }

            var parts = new List<string>();
            AddSummaryPart(parts, "providerUrl", harmonySourceSupport.ProviderUrl);
            AddSummaryPart(parts, "artist", harmonySourceSupport.LookupArtist);
            AddSummaryPart(parts, "album", harmonySourceSupport.LookupAlbum);
            AddSummaryPart(parts, "query", harmonySourceSupport.LookupQuery);
            return parts.Count == 0 ? "none" : string.Join(",", parts);
        }

        private static void AddSummaryPart(ICollection<string> parts, string key, string? value)
        {
            var normalized = NormalizeOptional(value);
            if (normalized != null)
            {
                parts.Add($"{key}={normalized}");
            }
        }

        private static string RedactSensitiveText(string? value)
        {
            var normalized = NormalizeOptional(value);
            if (normalized == null)
            {
                return string.Empty;
            }

            var redacted = HeaderSecretPattern.Replace(normalized, static match => $"{match.Groups["label"].Value}=[REDACTED]");
            redacted = TokenSecretPattern.Replace(redacted, static match => $"{match.Groups["label"].Value}=[REDACTED]");
            redacted = BearerSecretPattern.Replace(redacted, "Bearer [REDACTED]");
            return redacted;
        }

        private static string NormalizeRequired(string? value, string paramName)
        {
            var normalized = NormalizeOptional(value);
            return normalized ?? throw new ArgumentException($"{paramName} is required.", paramName);
        }

        private static string NormalizeOrFallback(string? value, string fallback)
        {
            return NormalizeOptional(value) ?? fallback;
        }

        private static string? NormalizeOptional(string? value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        private sealed class HostMusicBrainzCaptureOutcome
        {
            private HostMusicBrainzCaptureOutcome(
                string resolutionSource,
                bool mbContextResolved,
                bool mbReleaseIdPresent,
                bool mbAlbumIdPresent)
            {
                ResolutionSource = resolutionSource;
                MbContextResolved = mbContextResolved;
                MbReleaseIdPresent = mbReleaseIdPresent;
                MbAlbumIdPresent = mbAlbumIdPresent;
            }

            public string ResolutionSource { get; }

            public bool MbContextResolved { get; }

            public bool MbReleaseIdPresent { get; }

            public bool MbAlbumIdPresent { get; }

            public static HostMusicBrainzCaptureOutcome FromIdentifiers(
                string resolutionSource,
                string? preferredReleaseMusicBrainzId,
                string? albumMusicBrainzId)
            {
                var normalizedResolutionSource = NormalizeOrFallback(resolutionSource, HostMusicBrainzContextResolution.MissingPreferredReleaseId);
                var mbReleaseIdPresent = NormalizeOptional(preferredReleaseMusicBrainzId) != null;
                var mbAlbumIdPresent = NormalizeOptional(albumMusicBrainzId) != null;
                return new HostMusicBrainzCaptureOutcome(
                    normalizedResolutionSource,
                    mbReleaseIdPresent && mbAlbumIdPresent,
                    mbReleaseIdPresent,
                    mbAlbumIdPresent);
            }

            public static HostMusicBrainzCaptureOutcome FromResolution(
                HostMusicBrainzContextResolution resolution,
                string? preferredReleaseMusicBrainzId,
                string? albumMusicBrainzId)
            {
                return FromIdentifiers(
                    resolution.ResolutionSource,
                    preferredReleaseMusicBrainzId ?? resolution.PreferredReleaseMusicBrainzId ?? resolution.ReleaseMusicBrainzId,
                    albumMusicBrainzId ?? resolution.AlbumMusicBrainzId);
            }
        }

        private sealed class NoOpHostMusicBrainzContextResolver : IHostMusicBrainzContextResolver
        {
            public static readonly IHostMusicBrainzContextResolver Instance = new NoOpHostMusicBrainzContextResolver();

            public HostMusicBrainzContextResolution Resolve(string? preferredReleaseMusicBrainzId)
            {
                var normalizedPreferredReleaseMusicBrainzId = NormalizeOptional(preferredReleaseMusicBrainzId);
                return normalizedPreferredReleaseMusicBrainzId == null
                    ? HostMusicBrainzContextResolution.Unresolved(null, null, HostMusicBrainzContextResolution.MissingPreferredReleaseId)
                    : HostMusicBrainzContextResolution.Unresolved(
                        normalizedPreferredReleaseMusicBrainzId,
                        normalizedPreferredReleaseMusicBrainzId,
                        ResolverNotConfiguredResolutionSource);
            }
        }
    }
}
