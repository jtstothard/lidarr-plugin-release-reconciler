using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using NLog;
using NzbDrone.Common.EnvironmentInfo;

namespace NzbDrone.Core.Plugins.ReleaseReconciler.Switching
{
    public sealed class ReleaseSwitchSettingsStore
    {
        private static readonly JsonSerializerOptions SerializerOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = true
        };

        private readonly Logger _logger;

        public ReleaseSwitchSettingsStore(IAppFolderInfo appFolderInfo, Logger logger)
            : this(Path.Combine(appFolderInfo?.AppDataFolder ?? throw new ArgumentNullException(nameof(appFolderInfo)), "release-reconciler", "release-switch-settings.json"), logger)
        {
        }

        public ReleaseSwitchSettingsStore(string settingsPath, Logger logger)
        {
            if (string.IsNullOrWhiteSpace(settingsPath))
            {
                throw new ArgumentException("Settings path is required.", nameof(settingsPath));
            }

            SettingsPath = Path.GetFullPath(settingsPath.Trim());
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public string SettingsPath { get; }

        public ReleaseSwitchSettings Load()
        {
            if (!File.Exists(SettingsPath))
            {
                return new ReleaseSwitchSettings().Normalize();
            }

            try
            {
                var json = File.ReadAllText(SettingsPath);
                var settings = JsonSerializer.Deserialize<ReleaseSwitchSettings>(json, SerializerOptions);
                return (settings ?? new ReleaseSwitchSettings()).Normalize();
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                _logger.Warn(ex, "Falling back to default release reconciler switch settings from path={0}.", SettingsPath);
                return new ReleaseSwitchSettings().Normalize();
            }
        }

        public void Save(ReleaseSwitchSettings settings)
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
