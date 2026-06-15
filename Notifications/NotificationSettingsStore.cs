using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using NLog;
using NzbDrone.Common.EnvironmentInfo;

namespace NzbDrone.Core.Plugins.ReleaseReconciler.Notifications
{
    public sealed class NotificationSettingsStore
    {
        private static readonly JsonSerializerOptions SerializerOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = true
        };

        private readonly Logger _logger;

        public NotificationSettingsStore(IAppFolderInfo appFolderInfo, Logger logger)
            : this(Path.Combine(appFolderInfo?.AppDataFolder ?? throw new ArgumentNullException(nameof(appFolderInfo)), "release-reconciler", "notification-settings.json"), logger)
        {
        }

        public NotificationSettingsStore(string settingsPath, Logger logger)
        {
            if (string.IsNullOrWhiteSpace(settingsPath))
            {
                throw new ArgumentException("Settings path is required.", nameof(settingsPath));
            }

            SettingsPath = Path.GetFullPath(settingsPath.Trim());
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public string SettingsPath { get; }

        public ReconciliationNotificationSettings Load()
        {
            if (!File.Exists(SettingsPath))
            {
                return new ReconciliationNotificationSettings().Normalize();
            }

            try
            {
                var json = File.ReadAllText(SettingsPath);
                var settings = JsonSerializer.Deserialize<ReconciliationNotificationSettings>(json, SerializerOptions);
                return (settings ?? new ReconciliationNotificationSettings()).Normalize();
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                _logger.Warn(ex, "Falling back to default release reconciler notification settings from path={0}.", SettingsPath);
                return new ReconciliationNotificationSettings().Normalize();
            }
        }

        public void Save(ReconciliationNotificationSettings settings)
        {
            ArgumentNullException.ThrowIfNull(settings);

            var normalized = settings.Normalize();
            var directory = Path.GetDirectoryName(SettingsPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(normalized, SerializerOptions);
            File.WriteAllText(SettingsPath, json);
        }
    }
}
