using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Lidarr.Http.Frontend;
using Lidarr.Http.Frontend.Mappers;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Plugins.ReleaseReconciler.Actions;
using NzbDrone.Core.Plugins.ReleaseReconciler.Candidates;
using NzbDrone.Core.Plugins.ReleaseReconciler.Cases;
using NzbDrone.Core.Plugins.ReleaseReconciler.Evidence;
using NzbDrone.Core.Plugins.ReleaseReconciler.Notifications;
using Xunit;

namespace NzbDrone.Core.Plugins.ReleaseReconciler.Test.Notifications
{
    // Regression: since 4ed58aa the builder emitted {path}/index.html/{token} while the
    // mappers only served {path}/{token}/index.html, so every notification link 404'd.
    public sealed class SignedLinkRouteFixture : IDisposable
    {
        private static readonly DateTimeOffset Now = new(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);
        private const string CaseId = "case-signed-link-route";

        private readonly string _rootDirectory;
        private readonly Logger _logger;
        private readonly FileReconciliationCaseStore _store;
        private readonly OperatorActionTokenService _actionTokenService;
        private readonly ReconciliationEvidenceTokenService _evidenceTokenService;

        public SignedLinkRouteFixture()
        {
            _rootDirectory = Path.Combine(Path.GetTempPath(), $"release-reconciler-signed-link-{Guid.NewGuid():N}");
            _logger = LogManager.GetLogger(nameof(SignedLinkRouteFixture));
            _store = new FileReconciliationCaseStore(Path.Combine(_rootDirectory, "cases"), _logger);
            _store.Save(CreateCase());

            var provider = DataProtectionProvider.Create(
                new DirectoryInfo(Path.Combine(_rootDirectory, "keys")),
                configuration => configuration.SetApplicationName("signed-link-route-tests"));
            _actionTokenService = new OperatorActionTokenService(provider, _logger, () => Now);
            _evidenceTokenService = new ReconciliationEvidenceTokenService(provider, _logger, () => Now);
        }

        [Fact]
        public async Task notification_action_links_are_served_by_lidarr_routing()
        {
            var envelope = BuildEnvelope(CreateSettings());
            var snooze = Assert.Single(envelope.Actions, action => action.Action == ReconciliationOperatorActionKinds.Snooze);
            var path = new Uri(snooze.Url).AbsolutePath;

            Assert.StartsWith("https://lidarr.example/release-reconciler/action/", snooze.Url, StringComparison.Ordinal);
            Assert.EndsWith("/index.html", path, StringComparison.Ordinal);

            var page = Assert.IsType<OperatorActionResultPage>(await CreateController().Index(path.TrimStart('/')));

            Assert.Equal(200, page.StatusCode);
            Assert.Contains("Snooze applied", page.Content, StringComparison.Ordinal);
        }

        [Fact]
        public async Task notification_evidence_link_is_served_by_lidarr_routing()
        {
            var envelope = BuildEnvelope(CreateSettings());
            var evidence = Assert.Single(envelope.EvidenceLinks, link => link.Label == "Evidence Page");
            var path = new Uri(evidence.Url).AbsolutePath;

            Assert.StartsWith("https://lidarr.example/release-reconciler/evidence/", evidence.Url, StringComparison.Ordinal);
            Assert.EndsWith("/index.html", path, StringComparison.Ordinal);

            var page = Assert.IsType<ReconciliationEvidencePage>(await CreateController().Index(path.TrimStart('/')));

            Assert.Equal(200, page.StatusCode);
        }

        [Theory]
        [InlineData("/release-reconciler/action/index.html")]
        [InlineData("/content/release-reconciler/action")]
        [InlineData("release-reconciler/action/")]
        public async Task stored_action_paths_from_older_versions_still_produce_served_links(string configuredPath)
        {
            var settings = CreateSettings();
            settings.OperatorActionPath = configuredPath;

            var snooze = Assert.Single(BuildEnvelope(settings).Actions, action => action.Action == ReconciliationOperatorActionKinds.Snooze);
            var path = new Uri(snooze.Url).AbsolutePath;

            var page = Assert.IsType<OperatorActionResultPage>(await CreateController().Index(path.TrimStart('/')));
            Assert.Equal(200, page.StatusCode);
        }

        [Fact]
        public async Task links_already_sent_in_the_0_1_2_shape_keep_working()
        {
            var actionToken = _actionTokenService.Issue(CaseId, ReconciliationOperatorActionKinds.Ignore, Now.AddHours(4)).Token;
            var evidenceToken = _evidenceTokenService.Issue(CaseId, Now.AddHours(4)).Token;
            var controller = CreateController();

            var action = Assert.IsType<OperatorActionResultPage>(await controller.Index($"release-reconciler/action/index.html/{actionToken}"));
            var evidence = Assert.IsType<ReconciliationEvidencePage>(await controller.Index($"release-reconciler/evidence/index.html/{evidenceToken}"));

            Assert.Equal(200, action.StatusCode);
            Assert.Contains("Ignore applied", action.Content, StringComparison.Ordinal);
            Assert.Equal(200, evidence.StatusCode);
        }

        [Theory]
        [InlineData("release-reconciler/action/not-a-real-token/index.html")]
        [InlineData("release-reconciler/action/index.html/not-a-real-token")]
        public async Task bogus_tokens_are_refused_by_the_plugin_not_lost_to_another_mapper(string path)
        {
            var page = Assert.IsType<OperatorActionResultPage>(await CreateController().Index(path));

            Assert.Equal(400, page.StatusCode);
        }

        [Theory]
        [InlineData("release-reconciler/action/abc/def/index.html")]
        [InlineData("release-reconciler/action/index.html")]
        [InlineData("release-reconciler/action/abc")]
        public async Task other_paths_under_the_prefix_are_not_claimed(string path)
        {
            Assert.IsType<NotFoundResult>(await CreateController().Index(path));
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

        private ReconciliationNotificationEnvelope BuildEnvelope(ReconciliationNotificationSettings settings)
        {
            var builder = new ReconciliationNotificationBuilder(_actionTokenService, _evidenceTokenService, () => Now);
            return builder.Build(CreateCase(), settings);
        }

        // Lidarr's controller picks the one mapper whose CanHandle matches (SingleOrDefault),
        // so register Lidarr's own catch-all mappers next to the plugin's to prove no collision.
        private StaticResourceController CreateController()
        {
            var appFolderInfo = new Mock<IAppFolderInfo>();
            appFolderInfo.SetupGet(info => info.StartUpFolder).Returns(_rootDirectory);
            var configFileProvider = new Mock<IConfigFileProvider>();
            configFileProvider.SetupGet(config => config.UiFolder).Returns("UI");
            configFileProvider.SetupGet(config => config.UrlBase).Returns(string.Empty);
            var diskProvider = new Mock<IDiskProvider>();

            var mappers = new IMapHttpRequestsToDisk[]
            {
                new IndexHtmlMapper(appFolderInfo.Object, diskProvider.Object, configFileProvider.Object, new Lazy<ICacheBreakerProvider>(() => Mock.Of<ICacheBreakerProvider>()), _logger),
                new StaticResourceMapper(appFolderInfo.Object, diskProvider.Object, configFileProvider.Object, _logger),
                new ReconciliationOperatorActionMapper(_store, _actionTokenService, _logger, () => Now.AddMinutes(1)),
                new ReconciliationEvidenceMapper(_store, _evidenceTokenService, CreateSettings(), _logger)
            };

            return new StaticResourceController(mappers, _logger)
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
            };
        }

        private static ReconciliationNotificationSettings CreateSettings()
        {
            return new ReconciliationNotificationSettings
            {
                PublicBaseUrl = "https://lidarr.example"
            };
        }

        private static ReconciliationCase CreateCase()
        {
            return new ReconciliationCase(
                sourceSeam: "capture",
                phase: "evaluated",
                capturedIdentifiers: new Dictionary<string, string?>
                {
                    ["artistName"] = "Test Artist",
                    ["albumTitle"] = "Test Album"
                },
                structuralEvidence: new ReconciliationEvidence(),
                caseId: CaseId,
                capturedAtUtc: Now.AddMinutes(-30),
                updatedAtUtc: Now.AddMinutes(-30));
        }
    }
}
