using System;
using System.IO;
using NLog;
using NzbDrone.Core.Plugins.ReleaseReconciler.Switching;
using Xunit;

namespace NzbDrone.Core.Plugins.ReleaseReconciler.Test.Switching
{
    public sealed class ReleaseSwitchSettingsStoreFixture : IDisposable
    {
        private readonly string _rootDirectory;
        private readonly Logger _logger;

        public ReleaseSwitchSettingsStoreFixture()
        {
            _rootDirectory = Path.Combine(Path.GetTempPath(), $"release-reconciler-switch-settings-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_rootDirectory);
            _logger = LogManager.GetLogger($"ReleaseSwitchSettingsStoreFixture.{Guid.NewGuid():N}");
        }

        [Fact]
        public void should_return_safe_defaults_when_settings_file_missing()
        {
            var store = CreateStore();

            var settings = store.Load();

            Assert.False(settings.EnableReleaseSwitchApproval);
        }

        [Fact]
        public void should_round_trip_release_switch_settings()
        {
            var store = CreateStore();
            var settings = new ReleaseSwitchSettings
            {
                EnableReleaseSwitchApproval = true
            };

            store.Save(settings);
            var reloaded = store.Load();

            Assert.True(reloaded.EnableReleaseSwitchApproval);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_rootDirectory))
                {
                    Directory.Delete(_rootDirectory, recursive: true);
                }
            }
            catch
            {
                // best effort temp cleanup
            }
        }

        private ReleaseSwitchSettingsStore CreateStore()
        {
            return new ReleaseSwitchSettingsStore(Path.Combine(_rootDirectory, "release-switch-settings.json"), _logger);
        }
    }
}
