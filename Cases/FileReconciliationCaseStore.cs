using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using NLog;

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

                var caseId = ReconciliationCaseSnapshot.ValidateCaseId(reconciliationCase.CaseId);
                var targetPath = GetFilePath(caseId);
                var existingSnapshot = ReadExistingSnapshotForUpdate(targetPath);
                var now = DateTimeOffset.UtcNow;
                var capturedAtUtc = reconciliationCase.CapturedAtUtc ?? existingSnapshot?.CapturedAtUtc ?? now;
                var updatedAtUtc = reconciliationCase.UpdatedAtUtc ?? now;
                var snapshot = ReconciliationCaseSnapshot.Create(reconciliationCase, capturedAtUtc, updatedAtUtc);
                var isUpdate = existingSnapshot != null || File.Exists(targetPath);
                var action = isUpdate ? "update" : "create";
                var tempPath = Path.Combine(StorageDirectory, $".{snapshot.CaseId}.{Guid.NewGuid():N}.tmp");
                var lastDispatchAttempt = snapshot.NotificationState?.DispatchAttempts.LastOrDefault();
                var lastOperatorAction = snapshot.OperatorActionState?.LastAction;

                _logger.Info(
                    "Persisting reconciliation case {0}: action={1}, seam={2}, phase={3}, identifierKeys={4}, downloadId={5}, outputPath={6}, trackCount={7}, recordingMbids={8}, releaseTrackMbids={9}, evaluationClassification={10}, evaluationRefusal={11}, rankedCandidates={12}, notificationAttempts={13}, lastNotificationTransport={14}, lastNotificationStatus={15}, lastActionUrlKind={16}, operatorReceipts={17}, operatorState={18}, snoozedUntil={19}, ignoredAt={20}, lastOperatorAction={21}, lastOperatorActionResult={22}, lastOperatorActionAt={23}",
                    snapshot.CaseId,
                    action,
                    snapshot.SourceSeam,
                    snapshot.Phase,
                    string.Join(",", snapshot.CapturedIdentifiers.Keys.OrderBy(static key => key, StringComparer.Ordinal)),
                    snapshot.DownloadId ?? "-",
                    snapshot.OutputPath ?? "-",
                    snapshot.StructuralEvidence?.TrackCount ?? 0,
                    snapshot.StructuralEvidence?.RecordingMusicBrainzIds.Count ?? 0,
                    snapshot.StructuralEvidence?.ReleaseTrackMusicBrainzIds.Count ?? 0,
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
                _logger.Error(ex, "Failed to persist reconciliation case {0}.", reconciliationCase.CaseId);
                throw;
            }
        }

        public ReconciliationCaseSnapshot? Get(string caseId)
        {
            var normalizedCaseId = ReconciliationCaseSnapshot.ValidateCaseId(caseId);
            var path = GetFilePath(normalizedCaseId);
            return File.Exists(path) ? ReadSnapshotFromFile(path) : null;
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
