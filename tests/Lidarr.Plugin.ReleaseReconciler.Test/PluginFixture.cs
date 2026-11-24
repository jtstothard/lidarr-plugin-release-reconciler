using Xunit;

namespace NzbDrone.Core.Plugins
{
    public class PluginFixture
    {
        [Fact]
        public void plugin_type_should_derive_from_host_plugin_base()
        {
            var plugin = new ReleaseReconcilerPlugin();

            Assert.IsAssignableFrom<Plugin>(plugin);
        }

        [Fact]
        public void plugin_metadata_should_match_expected_values()
        {
            var plugin = new ReleaseReconcilerPlugin();

            Assert.Equal("Release Reconciler", plugin.Name);
            Assert.Equal("jtstothard", plugin.Owner);
            Assert.Equal("https://github.com/jtstothard/lidarr-plugin-release-reconciler", plugin.GithubUrl);
        }

        [Fact]
        public void plugin_assembly_shape_should_match_expected_scaffold()
        {
            var assembly = typeof(ReleaseReconcilerPlugin).Assembly;

            Assert.Equal("Lidarr.Plugin.ReleaseReconciler", assembly.GetName().Name);
            Assert.Equal("NzbDrone.Core.Plugins.ReleaseReconcilerPlugin", typeof(ReleaseReconcilerPlugin).FullName);
        }
    }
}
