extern alias release_reconciler_nlog;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Logger = release_reconciler_nlog::NLog.Logger;

using NzbDrone.Common.EnvironmentInfo;

namespace NzbDrone.Core.Plugins.ReleaseReconciler.Cases
{
    public sealed class FileReconciliationCaseStore : IReconciliationCaseStore
    {
        private static readonly JsonSerializerOptions SerializerOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = true
        };

        private readonly Logger _logger;

        public FileReconciliationCaseStore(IAppFolderInfo appFolderInfo, Logger logger)
            : this(Path.Combine(appFolderInfo?.AppDataFolder ?? throw new ArgumentNullException(nameof(appFolderInfo)), "release-reconciler", "cases"), logger)
        {
        }

        public FileReconciliationCaseStore(string storageDirectory, Logger logger)
        {
            if (string.IsNullOrWhiteSpace(storageDirectory))
            {
                throw new ArgumentException("Storage directory is required.", nameof(storageDirectory));
            }

            StorageDirectory = Path.GetFullPath(storageDirectory.Trim());
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public string StorageDirectory { get; }

        public ReconciliationCaseSnapshot Save(ReconciliationCase reconciliationCase)
        {
            ArgumentNullException.ThrowIfNull(reconciliationCase);

            try
            {
                Directory.CreateDirectory(StorageDirectory);

                var existingSnapshot = FindByDownloadId(reconciliationCase.SourceSeam, reconciliationCase.DownloadId);
                var identitySource = existingSnapshot == null ? "caseId" : "downloadId";
                var caseId = existingSnapshot?.CaseId ?? ReconciliationCaseSnapshot.ValidateCaseId(reconciliationCase.CaseId);
                var targetPath = GetFilePath(caseId);
                var targetPathExists = File.Exists(targetPath);

                existingSnapshot ??= ReadExistingSnapshotForUpdate(targetPath);

                if (existingSnapshot == null && !targetPathExists)
                {
                    identitySource = "new";
                }
                else if (existingSnapshot != null && !string.Equals(reconciliationCase.CaseId, existingSnapshot.CaseId, StringComparison.Ordinal))
                {
                    _logger.Info(
                        "Reconciliation case downloadId update matched existing snapshot: seam={0}, phase={1}, downloadId={2}, incomingCaseId={3}, persistedCaseId={4}, persistedCapturedAtUtc={5}",
                        reconciliationCase.SourceSeam,
                        reconciliationCase.Phase,
                        reconciliationCase.DownloadId ?? "-",
                        reconciliationCase.CaseId,
                        existingSnapshot.CaseId,
                        existingSnapshot.CapturedAtUtc.ToString("O"));
                }

                var now = DateTimeOffset.UtcNow;
                var capturedAtUtc = existingSnapshot?.CapturedAtUtc ?? reconciliationCase.CapturedAtUtc ?? now;
                var updatedAtUtc = reconciliationCase.UpdatedAtUtc ?? now;
                var persistedCase = CreatePersistedCase(reconciliationCase, caseId, capturedAtUtc, updatedAtUtc);
                var snapshot = ReconciliationCaseSnapshot.Create(persistedCase, capturedAtUtc, updatedAtUtc);
                var action = existingSnapshot != null || targetPathExists ? "update" : "create";
                var tempPath = Path.Combine(StorageDirectory, $".{snapshot.CaseId}.{Guid.NewGuid():N}.tmp");
                var lastDispatchAttempt = snapshot.NotificationState?.DispatchAttempts.LastOrDefault();
                var lastOperatorAction = snapshot.OperatorActionState?.LastAction;

                _logger.Info(
                    "Persisting reconciliation case {0}: action={1}, identitySource={2}, seam={3}, phase={4}, identifierKeys={5}, downloadId={6}, outputPath={7}, trackCount={8}, recordingMbids={9}, releaseTrackMbids={10}, mismatchActionable={11}, mismatchRuleVersion={12}, mismatchRule={13}, mismatchSuppression={14}, sourceSupport={15}, sourceSupportRuleVersion={16}, sourceSupportRule={17}, sourceSupportProvider={18}, sourceSupportSuppression={19}, sourceSupportProviderUrl={20}, sourceSupportArtist={21}, sourceSupportAlbum={22}, sourceSupportQuery={23}, evaluationClassification={24}, evaluationRefusal={25}, rankedCandidates={26}, notificationAttempts={27}, lastNotificationTransport={28}, lastNotificationStatus={29}, lastActionUrlKind={30}, operatorReceipts={31}, operatorState={32}, snoozedUntil={33}, ignoredAt={34}, lastOperatorAction={35}, lastOperatorActionResult={36}, lastOperatorActionAt={37}",
                    snapshot.CaseId,
                    action,
                    identitySource,
                    snapshot.SourceSeam,
                    snapshot.Phase,
                    string.Join(",", snapshot.CapturedIdentifiers.Keys.OrderBy(static key => key, StringComparer.Ordinal)),
                    snapshot.DownloadId ?? "-",
                    snapshot.OutputPath ?? "-",
                    snapshot.StructuralEvidence?.TrackCount ?? 0,
                    snapshot.StructuralEvidence?.RecordingMusicBrainzIds.Count ?? 0,
                    snapshot.StructuralEvidence?.ReleaseTrackMusicBrainzIds.Count ?? 0,
                    snapshot.ImportFailureClassification?.Actionable ?? false,
                    snapshot.ImportFailureClassification?.RuleVersion ?? "none",
                    snapshot.ImportFailureClassification?.MatchedRule ?? "none",
                    snapshot.ImportFailureClassification?.SuppressionReason ?? "none",
                    snapshot.HarmonySourceSupport?.Supported ?? false,
                    snapshot.HarmonySourceSupport?.RuleVersion ?? "none",
                    snapshot.HarmonySourceSupport?.MatchedRule ?? "none",
                    snapshot.HarmonySourceSupport?.MatchedProvider ?? "none",
                    snapshot.HarmonySourceSupport?.SuppressionReason ?? "none",
                    snapshot.HarmonySourceSupport?.ProviderUrl ?? "none",
                    snapshot.HarmonySourceSupport?.LookupArtist ?? "none",
                    snapshot.HarmonySourceSupport?.LookupAlbum ?? "none",
                    snapshot.HarmonySourceSupport?.LookupQuery ?? "none",
                    snapshot.EvaluationResult?.Classification.ToString() ?? "none",
                    snapshot.EvaluationResult?.RefusalReason?.ToString() ?? "none",
                    snapshot.EvaluationResult?.RankedCandidates.Count ?? 0,
                    snapshot.NotificationState?.DispatchAttempts.Count ?? 0,
                    lastDispatchAttempt?.Transport ?? "none",
                    lastDispatchAttempt == null ? "none" : lastDispatchAttempt.Succeeded ? "success" : "failed",
                    lastDispatchAttempt?.ActionUrlKind ?? "none",
                    snapshot.OperatorActionState?.ProcessedActionReceipts.Count ?? 0,
                    DescribeOperatorState(snapshot.OperatorActionState),
                    snapshot.OperatorActionState?.SnoozedUntilUtc?.ToString("O") ?? "none",
                    snapshot.OperatorActionState?.IgnoredAtUtc?.ToString("O") ?? "none",
                    lastOperatorAction?.Action ?? "none",
                    lastOperatorAction?.Result ?? "none",
                    lastOperatorAction?.OccurredAtUtc.ToString("O") ?? "none");

                try
                {
                    using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                    {
                        JsonSerializer.Serialize(stream, snapshot, SerializerOptions);
                        stream.Flush(true);
                    }

                    File.Move(tempPath, targetPath, true);
                }
                finally
                {
                    TryDeleteTempFile(tempPath);
                }

                return snapshot;
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
            {
                _logger.Error(ex, "Failed to persist reconciliation case {0}: seam={1}, phase={2}, downloadId={3}.", reconciliationCase.CaseId, reconciliationCase.SourceSeam, reconciliationCase.Phase, reconciliationCase.DownloadId ?? "-");
                throw;
            }
        }

        public ReconciliationCaseSnapshot? Get(string caseId)
        {
            var normalizedCaseId = ReconciliationCaseSnapshot.ValidateCaseId(caseId);
            var path = GetFilePath(normalizedCaseId);
            return File.Exists(path) ? ReadSnapshotFromFile(path) : null;
        }

        public ReconciliationCaseSnapshot? FindByDownloadId(string sourceSeam, string? downloadId)
        {
            var normalizedSourceSeam = NormalizeRequiredValue(sourceSeam, nameof(sourceSeam));
            var normalizedDownloadId = NormalizeOptionalValue(downloadId);

            if (normalizedDownloadId == null)
            {
                _logger.Info(
                    "Reconciliation case downloadId lookup skipped: seam={0}, reason=no-download-id.",
                    normalizedSourceSeam);
                return null;
            }

            if (!Directory.Exists(StorageDirectory))
            {
                _logger.Info(
                    "Reconciliation case downloadId lookup miss: seam={0}, downloadId={1}, reason=storage-missing.",
                    normalizedSourceSeam,
                    normalizedDownloadId);
                return null;
            }

            var matches = new List<ReconciliationCaseSnapshot>();

            foreach (var path in Directory.EnumerateFiles(StorageDirectory, "*.json", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    var snapshot = ReadSnapshotFromFile(path);

                    if (string.Equals(snapshot.SourceSeam, normalizedSourceSeam, StringComparison.Ordinal) &&
                        string.Equals(snapshot.DownloadId, normalizedDownloadId, StringComparison.Ordinal))
                    {
                        matches.Add(snapshot);
                    }
                }
                catch (InvalidDataException ex)
                {
                    _logger.Warn(ex, "Skipping malformed reconciliation case snapshot during downloadId lookup at {0}.", path);
                }
            }

            if (matches.Count == 0)
            {
                _logger.Info(
                    "Reconciliation case downloadId lookup miss: seam={0}, downloadId={1}, reason=no-match.",
                    normalizedSourceSeam,
                    normalizedDownloadId);
                return null;
            }

            if (matches.Count > 1)
            {
                var duplicateCaseIds = string.Join(",", matches.Select(static snapshot => snapshot.CaseId).OrderBy(static caseId => caseId, StringComparer.Ordinal));

                _logger.Error(
                    "Reconciliation case downloadId lookup duplicate: seam={0}, downloadId={1}, caseIds={2}.",
                    normalizedSourceSeam,
                    normalizedDownloadId,
                    duplicateCaseIds);

                throw new InvalidOperationException($"Multiple reconciliation cases matched source seam '{normalizedSourceSeam}' and downloadId '{normalizedDownloadId}': {duplicateCaseIds}.");
            }

            var match = matches[0];
            _logger.Info(
                "Reconciliation case downloadId lookup hit: seam={0}, downloadId={1}, caseId={2}, phase={3}.",
                normalizedSourceSeam,
                normalizedDownloadId,
                match.CaseId,
                match.Phase);
            return match;
        }

        public IReadOnlyList<ReconciliationCaseSnapshot> List()
        {
            if (!Directory.Exists(StorageDirectory))
            {
                return Array.Empty<ReconciliationCaseSnapshot>();
            }

            var snapshots = new List<ReconciliationCaseSnapshot>();

            foreach (var path in Directory.EnumerateFiles(StorageDirectory, "*.json", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    snapshots.Add(ReadSnapshotFromFile(path));
                }
                catch (InvalidDataException ex)
                {
                    _logger.Warn(ex, "Skipping malformed reconciliation case snapshot at {0}.", path);
                }
            }

            return snapshots
                .OrderByDescending(static snapshot => snapshot.LastUpdatedAtUtc)
                .ThenBy(static snapshot => snapshot.CaseId, StringComparer.Ordinal)
                .ToArray();
        }

        private string GetFilePath(string caseId)
        {
            return Path.Combine(StorageDirectory, $"{caseId}.json");
        }

        private ReconciliationCaseSnapshot? ReadExistingSnapshotForUpdate(string path)
        {
            if (!File.Exists(path))
            {
                return null;
            }

            try
            {
                return ReadSnapshotFromFile(path);
            }
            catch (InvalidDataException ex)
            {
                _logger.Warn(ex, "Overwriting malformed reconciliation case snapshot at {0}.", path);
                return null;
            }
        }

        private static string DescribeOperatorState(ReconciliationOperatorActionState? operatorActionState)
        {
            if (operatorActionState == null)
            {
                return "none";
            }

            if (operatorActionState.IgnoredAtUtc.HasValue)
            {
                return "ignored";
            }

            if (operatorActionState.SnoozedUntilUtc.HasValue)
            {
                return "snoozed";
            }

            return "active";
        }

        private static ReconciliationCase CreatePersistedCase(
            ReconciliationCase reconciliationCase,
            string caseId,
            DateTimeOffset capturedAtUtc,
            DateTimeOffset updatedAtUtc)
        {
            return new ReconciliationCase(
                sourceSeam: reconciliationCase.SourceSeam,
                phase: reconciliationCase.Phase,
                capturedIdentifiers: reconciliationCase.CapturedIdentifiers,
                structuralEvidence: reconciliationCase.StructuralEvidence,
                downloadId: reconciliationCase.DownloadId,
                outputPath: reconciliationCase.OutputPath,
                diagnosticSummary: reconciliationCase.DiagnosticSummary,
                lastError: reconciliationCase.LastError,
                importFailureClassification: reconciliationCase.ImportFailureClassification,
                harmonySourceSupport: reconciliationCase.HarmonySourceSupport,
                caseId: caseId,
                capturedAtUtc: capturedAtUtc,
                updatedAtUtc: updatedAtUtc,
                evaluationResult: reconciliationCase.EvaluationResult,
                notificationState: reconciliationCase.NotificationState,
                operatorActionState: reconciliationCase.OperatorActionState,
                releaseSwitchApprovalState: reconciliationCase.ReleaseSwitchApprovalState);
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

        private static void TryDeleteTempFile(string tempPath)
        {
            if (!File.Exists(tempPath))
            {
                return;
            }

            try
            {
                File.Delete(tempPath);
            }
            catch
            {
                // Best-effort cleanup only.
            }
        }

        private ReconciliationCaseSnapshot ReadSnapshotFromFile(string path)
        {
            try
            {
                using var stream = File.OpenRead(path);
                var snapshot = JsonSerializer.Deserialize<ReconciliationCaseSnapshot>(stream, SerializerOptions);

                if (snapshot == null)
                {
                    throw new InvalidDataException($"Reconciliation case snapshot '{path}' was empty.");
                }

                snapshot.ValidatePersistedShape();
                return snapshot;
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException($"Reconciliation case snapshot '{path}' contained malformed JSON.", ex);
            }
            catch (InvalidOperationException ex)
            {
                throw new InvalidDataException($"Reconciliation case snapshot '{path}' failed validation: {ex.Message}", ex);
            }
        }
    }
}
