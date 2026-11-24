using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Lidarr.Http.Frontend;
using Moq;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NLog;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.Music;
using NzbDrone.Core.Plugins.ReleaseReconciler.Actions;
using NzbDrone.Core.Plugins.ReleaseReconciler.Candidates;
using NzbDrone.Core.Plugins.ReleaseReconciler.Cases;
using NzbDrone.Core.Plugins.ReleaseReconciler.Switching;
using Xunit;

namespace NzbDrone.Core.Plugins.ReleaseReconciler.Test.Actions
{
    public sealed class ReconciliationOperatorActionMapperFixture : IDisposable
    {
        private static readonly DateTimeOffset Now = new(2026, 6, 14, 21, 0, 0, TimeSpan.Zero);
        private readonly string _rootDirectory;
        private readonly string _keyDirectory;
        private readonly Logger _logger;

        public ReconciliationOperatorActionMapperFixture()
        {
            _rootDirectory = Path.Combine(Path.GetTempPath(), $"release-reconciler-mapper-{Guid.NewGuid():N}");
            _keyDirectory = Path.Combine(_rootDirectory, "keys");
            Directory.CreateDirectory(_keyDirectory);
            _logger = LogManager.GetLogger(nameof(ReconciliationOperatorActionMapperFixture));
        }

        [Fact]
        public async Task should_dispatch_signed_action_through_static_resource_controller_and_persist_snooze()
        {
            var store = CreateStore("snooze-store");
            store.Save(CreateCase("case-snooze"));

            var tokenService = CreateTokenService(() => Now);
            var issued = tokenService.Issue("case-snooze", ReconciliationOperatorActionKinds.Snooze, Now.AddHours(4));
            var controller = CreateController(store, tokenService, () => Now.AddMinutes(2));

            var result = await controller.Index($"release-reconciler/action/{issued.Token}/index.html");

            var page = Assert.IsType<OperatorActionResultPage>(result);
            Assert.Equal(200, page.StatusCode);
            Assert.Contains("Snooze applied", page.Content, StringComparison.Ordinal);

            var snapshot = Assert.IsType<ReconciliationCaseSnapshot>(store.Get("case-snooze"));
            Assert.Equal(issued.ExpiresAtUtc, snapshot.OperatorActionState!.SnoozedUntilUtc);
            Assert.Null(snapshot.OperatorActionState.IgnoredAtUtc);
            Assert.Equal(ReconciliationOperatorActionOutcomes.Applied, snapshot.OperatorActionState.LastAction!.Result);
            var receipt = Assert.Single(snapshot.OperatorActionState.ProcessedActionReceipts);
            Assert.Equal(ReconciliationOperatorActionOutcomes.Applied, receipt.Outcome);
            Assert.Equal(issued.ActionTokenHash, receipt.ActionTokenHash);
        }

        [Fact]
        public async Task should_refuse_invalid_signature_without_mutating_case_state()
        {
            var store = CreateStore("invalid-store");
            store.Save(CreateCase("case-invalid"));

            var tokenService = CreateTokenService(() => Now);
            var issued = tokenService.Issue("case-invalid", ReconciliationOperatorActionKinds.Ignore, Now.AddHours(2));
            var invalidToken = CorruptProtectedToken(issued.Token);
            var controller = CreateController(store, tokenService, () => Now);

            var result = await controller.Index($"release-reconciler/action/{invalidToken}/index.html");

            var page = Assert.IsType<OperatorActionResultPage>(result);
            Assert.Equal(400, page.StatusCode);
            var snapshot = Assert.IsType<ReconciliationCaseSnapshot>(store.Get("case-invalid"));
            Assert.Null(snapshot.OperatorActionState);
        }

        [Fact]
        public async Task should_refuse_expired_token_and_persist_refusal_receipt()
        {
            var store = CreateStore("expired-store");
            store.Save(CreateCase("case-expired"));

            var tokenService = CreateTokenService(() => Now);
            var issued = tokenService.Issue("case-expired", ReconciliationOperatorActionKinds.Snooze, Now.AddMinutes(-1));
            var controller = CreateController(store, tokenService, () => Now);

            var result = await controller.Index($"release-reconciler/action/{issued.Token}/index.html");

            var page = Assert.IsType<OperatorActionResultPage>(result);
            Assert.Equal(410, page.StatusCode);
            var snapshot = Assert.IsType<ReconciliationCaseSnapshot>(store.Get("case-expired"));
            Assert.Null(snapshot.OperatorActionState!.SnoozedUntilUtc);
            Assert.Equal(ReconciliationOperatorActionOutcomes.Expired, snapshot.OperatorActionState.LastAction!.Result);
            Assert.Equal(ReconciliationOperatorActionOutcomes.Expired, Assert.Single(snapshot.OperatorActionState.ProcessedActionReceipts).Outcome);
        }

        [Fact]
        public async Task should_refuse_replayed_token_without_duplicate_receipts()
        {
            var store = CreateStore("replay-store");
            store.Save(CreateCase("case-replay"));

            var tokenService = CreateTokenService(() => Now);
            var issued = tokenService.Issue("case-replay", ReconciliationOperatorActionKinds.Ignore, Now.AddHours(1));
            var controller = CreateController(store, tokenService, () => Now);

            var first = await controller.Index($"release-reconciler/action/{issued.Token}/index.html");
            var second = await controller.Index($"release-reconciler/action/{issued.Token}/index.html");

            Assert.Equal(200, Assert.IsType<OperatorActionResultPage>(first).StatusCode);
            Assert.Equal(409, Assert.IsType<OperatorActionResultPage>(second).StatusCode);
            var snapshot = Assert.IsType<ReconciliationCaseSnapshot>(store.Get("case-replay"));
            Assert.Equal(ReconciliationOperatorActionOutcomes.Replay, snapshot.OperatorActionState!.LastAction!.Result);
            Assert.Single(snapshot.OperatorActionState.ProcessedActionReceipts);
        }

        [Fact]
        public async Task should_refuse_unknown_case_unsupported_action_and_already_applied_action_honestly()
        {
            var missingStore = CreateStore("missing-store");
            var tokenService = CreateTokenService(() => Now);
            var missingToken = tokenService.Issue("case-missing", ReconciliationOperatorActionKinds.Ignore, Now.AddHours(1));
            var missingController = CreateController(missingStore, tokenService, () => Now);

            var missing = await missingController.Index($"release-reconciler/action/{missingToken.Token}/index.html");
            Assert.Equal(404, Assert.IsType<OperatorActionResultPage>(missing).StatusCode);

            var unsupportedStore = CreateStore("unsupported-store");
            unsupportedStore.Save(CreateCase("case-unsupported"));
            var unsupportedToken = tokenService.ProtectPayload(new ReconciliationOperatorActionTokenPayload
            {
                Version = 1,
                CaseId = "case-unsupported",
                Action = "explode",
                Nonce = "nonce-unsupported",
                ExpiresAtUtc = Now.AddHours(2)
            });
            var unsupportedController = CreateController(unsupportedStore, tokenService, () => Now);
            var unsupported = await unsupportedController.Index($"release-reconciler/action/{unsupportedToken}/index.html");

            Assert.Equal(400, Assert.IsType<OperatorActionResultPage>(unsupported).StatusCode);
            var unsupportedSnapshot = Assert.IsType<ReconciliationCaseSnapshot>(unsupportedStore.Get("case-unsupported"));
            Assert.Equal(ReconciliationOperatorActionOutcomes.UnsupportedAction, unsupportedSnapshot.OperatorActionState!.LastAction!.Result);

            var appliedStore = CreateStore("applied-store");
            appliedStore.Save(CreateCase(
                "case-applied",
                new ReconciliationOperatorActionState
                {
                    IgnoredAtUtc = Now.AddMinutes(-10)
                }));
            var alreadyAppliedToken = tokenService.Issue("case-applied", ReconciliationOperatorActionKinds.Ignore, Now.AddHours(3));
            var appliedController = CreateController(appliedStore, tokenService, () => Now);
            var alreadyApplied = await appliedController.Index($"release-reconciler/action/{alreadyAppliedToken.Token}/index.html");

            Assert.Equal(409, Assert.IsType<OperatorActionResultPage>(alreadyApplied).StatusCode);
            var appliedSnapshot = Assert.IsType<ReconciliationCaseSnapshot>(appliedStore.Get("case-applied"));
            Assert.Equal(ReconciliationOperatorActionOutcomes.AlreadyApplied, appliedSnapshot.OperatorActionState!.LastAction!.Result);
            Assert.Equal(ReconciliationOperatorActionOutcomes.AlreadyApplied, Assert.Single(appliedSnapshot.OperatorActionState.ProcessedActionReceipts).Outcome);
        }

        [Fact]
        public async Task should_delegate_approve_switch_to_service_and_persist_switch_audit()
        {
            var store = CreateStore("approve-store");
            store.Save(CreateApprovalReadyCase("case-approve"));

            var tokenService = CreateTokenService(() => Now);
            var token = CreateApproveSwitchToken(tokenService, "case-approve", Now.AddHours(2));
            var controller = CreateController(store, tokenService, () => Now, CreateReleaseSwitchApprovalService(store));

            var result = await controller.Index($"release-reconciler/action/{token}/index.html");

            var page = Assert.IsType<OperatorActionResultPage>(result);
            Assert.Equal(200, page.StatusCode);
            Assert.Contains("Release switch applied", page.Content, StringComparison.Ordinal);

            var snapshot = Assert.IsType<ReconciliationCaseSnapshot>(store.Get("case-approve"));
            Assert.Equal(ReconciliationOperatorActionOutcomes.Applied, snapshot.OperatorActionState!.LastAction!.Result);
            Assert.Equal(ReconciliationOperatorActionOutcomes.Applied, Assert.Single(snapshot.OperatorActionState.ProcessedActionReceipts).Outcome);
            Assert.NotNull(snapshot.ReleaseSwitchApprovalState);
            Assert.Equal(ReconciliationOperatorActionOutcomes.Applied, snapshot.ReleaseSwitchApprovalState!.Outcome);
            Assert.Equal("release-candidate", snapshot.ReleaseSwitchApprovalState.AfterReleaseMusicBrainzId);
            Assert.Equal("album-current", snapshot.ReleaseSwitchApprovalState.AfterAlbumMusicBrainzId);
        }

        [Fact]
        public async Task should_refuse_expired_approve_switch_and_persist_switch_audit()
        {
            var store = CreateStore("approve-expired-store");
            store.Save(CreateApprovalReadyCase("case-approve-expired"));

            var tokenService = CreateTokenService(() => Now);
            var token = CreateApproveSwitchToken(tokenService, "case-approve-expired", Now.AddMinutes(-1));
            var controller = CreateController(store, tokenService, () => Now, CreateReleaseSwitchApprovalService(store));

            var result = await controller.Index($"release-reconciler/action/{token}/index.html");

            var page = Assert.IsType<OperatorActionResultPage>(result);
            Assert.Equal(410, page.StatusCode);
            var snapshot = Assert.IsType<ReconciliationCaseSnapshot>(store.Get("case-approve-expired"));
            Assert.Equal(ReconciliationOperatorActionOutcomes.Expired, snapshot.OperatorActionState!.LastAction!.Result);
            Assert.NotNull(snapshot.ReleaseSwitchApprovalState);
            Assert.Equal(ReconciliationOperatorActionOutcomes.Expired, snapshot.ReleaseSwitchApprovalState!.Outcome);
            Assert.Equal("release-candidate", snapshot.ReleaseSwitchApprovalState.RequestedReleaseMusicBrainzId);
        }

        [Fact]
        public async Task should_refuse_replayed_approve_switch_without_duplicate_receipts()
        {
            var store = CreateStore("approve-replay-store");
            store.Save(CreateApprovalReadyCase("case-approve-replay"));

            var tokenService = CreateTokenService(() => Now);
            var token = CreateApproveSwitchToken(tokenService, "case-approve-replay", Now.AddHours(2));
            var controller = CreateController(store, tokenService, () => Now, CreateReleaseSwitchApprovalService(store));

            var first = await controller.Index($"release-reconciler/action/{token}/index.html");
            var second = await controller.Index($"release-reconciler/action/{token}/index.html");

            Assert.Equal(200, Assert.IsType<OperatorActionResultPage>(first).StatusCode);
            Assert.Equal(409, Assert.IsType<OperatorActionResultPage>(second).StatusCode);
            var snapshot = Assert.IsType<ReconciliationCaseSnapshot>(store.Get("case-approve-replay"));
            Assert.Equal(ReconciliationOperatorActionOutcomes.Replay, snapshot.OperatorActionState!.LastAction!.Result);
            Assert.Single(snapshot.OperatorActionState.ProcessedActionReceipts);
            Assert.NotNull(snapshot.ReleaseSwitchApprovalState);
            Assert.Equal(ReconciliationOperatorActionOutcomes.Replay, snapshot.ReleaseSwitchApprovalState!.Outcome);
            Assert.Equal("release-candidate", snapshot.ReleaseSwitchApprovalState.AfterReleaseMusicBrainzId);
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

        private StaticResourceController CreateController(IReconciliationCaseStore store, OperatorActionTokenService tokenService, Func<DateTimeOffset> clock, ReleaseSwitchApprovalService? approvalService = null)
        {
            var mapper = new ReconciliationOperatorActionMapper(store, tokenService, _logger, clock, approvalService);
            var controller = new StaticResourceController(new[] { mapper }, _logger)
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext()
                }
            };

            return controller;
        }

        private FileReconciliationCaseStore CreateStore(string name)
        {
            return new FileReconciliationCaseStore(Path.Combine(_rootDirectory, name), _logger);
        }

        private OperatorActionTokenService CreateTokenService(Func<DateTimeOffset> clock, string applicationName = "release-reconciler-tests")
        {
            var provider = DataProtectionProvider.Create(
                new DirectoryInfo(_keyDirectory),
                configuration => configuration.SetApplicationName(applicationName));

            return new OperatorActionTokenService(provider, _logger, clock);
        }

        private static ReconciliationCase CreateCase(string caseId, ReconciliationOperatorActionState? operatorActionState = null)
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
                caseId: caseId,
                capturedAtUtc: Now.AddMinutes(-30),
                updatedAtUtc: Now.AddMinutes(-30),
                operatorActionState: operatorActionState);
        }

        private static ReconciliationCase CreateApprovalReadyCase(string caseId)
        {
            return new ReconciliationCase(
                sourceSeam: "capture",
                phase: "captured",
                capturedIdentifiers: new Dictionary<string, string?>
                {
                    ["artist"] = "Test Artist",
                    ["artistName"] = "Test Artist",
                    ["album"] = "Test Album",
                    ["albumTitle"] = "Test Album",
                    ["albumMusicBrainzId"] = "album-current"
                },
                structuralEvidence: new ReconciliationEvidence
                {
                    TrackCount = 1,
                    PreferredRelease = new PreferredReleaseReconciliationEvidence
                    {
                        ReleaseMusicBrainzId = "release-preferred"
                    },
                    Tracks =
                    {
                        new ReconciliationTrackEvidence
                        {
                            AbsoluteTrackNumber = 1,
                            MediumNumber = 1,
                            Title = "Track One",
                            RecordingMusicBrainzId = "recording-1",
                            ReleaseTrackMusicBrainzId = "release-track-1"
                        }
                    }
                },
                caseId: caseId,
                capturedAtUtc: Now.AddMinutes(-30),
                updatedAtUtc: Now.AddMinutes(-30));
        }

        private ReleaseSwitchApprovalService CreateReleaseSwitchApprovalService(FileReconciliationCaseStore store)
        {
            var settingsStore = new ReleaseSwitchSettingsStore(Path.Combine(_rootDirectory, $"switch-{Guid.NewGuid():N}.json"), _logger);
            settingsStore.Save(new ReleaseSwitchSettings { EnableReleaseSwitchApproval = true });

            var releaseService = new Mock<IReleaseService>(MockBehavior.Strict);
            var albumService = new Mock<IAlbumService>(MockBehavior.Strict);
            var albumInfo = new Mock<IProvideAlbumInfo>(MockBehavior.Strict);
            var refreshAlbumService = new Mock<IRefreshAlbumService>(MockBehavior.Strict);
            var targetRelease = new AlbumRelease
            {
                Id = 201,
                AlbumId = 17,
                ForeignReleaseId = "release-candidate",
                Title = "Candidate Release",
                Monitored = false
            };
            var currentRelease = new AlbumRelease
            {
                Id = 200,
                AlbumId = 17,
                ForeignReleaseId = "release-preferred",
                Title = "Current Release",
                Monitored = true
            };
            var album = new Album
            {
                Id = 17,
                ForeignAlbumId = "album-current",
                Title = "Test Album",
                Monitored = true
            };

            albumService.Setup(service => service.GetAlbum(17)).Returns(album);
            releaseService.Setup(service => service.GetReleaseByForeignReleaseId("release-candidate", false)).Returns(targetRelease);
            releaseService.Setup(service => service.GetReleasesByAlbum(17)).Returns(new List<AlbumRelease> { currentRelease, targetRelease });
            releaseService.Setup(service => service.SetMonitored(It.IsAny<AlbumRelease>())).Returns<AlbumRelease>(release => new List<AlbumRelease>
            {
                new AlbumRelease
                {
                    Id = currentRelease.Id,
                    AlbumId = currentRelease.AlbumId,
                    ForeignReleaseId = currentRelease.ForeignReleaseId,
                    Title = currentRelease.Title,
                    Monitored = string.Equals(currentRelease.ForeignReleaseId, release.ForeignReleaseId, StringComparison.Ordinal)
                },
                new AlbumRelease
                {
                    Id = targetRelease.Id,
                    AlbumId = targetRelease.AlbumId,
                    ForeignReleaseId = targetRelease.ForeignReleaseId,
                    Title = targetRelease.Title,
                    Monitored = true
                }
            });

            var lookup = new Mock<ILidarrAlbumLookup>(MockBehavior.Strict);
            lookup.Setup(service => service.LookupCandidates("Test Album", "Test Artist", It.IsAny<ReconciliationEvidence>(), "album-current"))
                .Returns(new LidarrAlbumLookupResult(
                    new[]
                    {
                        new CandidateAlbumSnapshot(
                            "album-current",
                            "Test Album",
                            "artist-1",
                            "Test Artist",
                            null,
                            new DateTime(2021, 1, 1),
                            "Album",
                            new[]
                            {
                                new CandidateReleaseSnapshot(
                                    "release-candidate",
                                    "Candidate Release",
                                    "Official",
                                    null,
                                    new DateTime(2021, 1, 1),
                                    1,
                                    180,
                                    Array.Empty<string>(),
                                    Array.Empty<string>(),
                                    new[] { new CandidateMediumSnapshot(1, "Disc 1", "Digital") },
                                    new[]
                                    {
                                        new CandidateTrackSnapshot(
                                            "track-1",
                                            "recording-1",
                                            "Track One",
                                            "1",
                                            1,
                                            1,
                                            180,
                                            false)
                                    })
                            },
                            new[] { CandidateLookupPath.CurrentReleaseGroup })
                    },
                    new[]
                    {
                        new LidarrAlbumLookupAttempt(CandidateLookupPath.CurrentReleaseGroup, true, 1, 1, 1, 1, null)
                    }));

            var evaluator = new ReconciliationCandidateEvaluator(store, lookup.Object, new DeterministicReleaseScorer(), _logger);
            return new ReleaseSwitchApprovalService(
                store,
                evaluator,
                settingsStore,
                albumService.Object,
                releaseService.Object,
                albumInfo.Object,
                refreshAlbumService.Object,
                _logger,
                () => Now);
        }

        private static string CreateApproveSwitchToken(OperatorActionTokenService tokenService, string caseId, DateTimeOffset expiresAtUtc)
        {
            return tokenService.ProtectPayload(new ReconciliationOperatorActionTokenPayload
            {
                Version = 1,
                CaseId = caseId,
                Action = ReconciliationOperatorActionKinds.ApproveSwitch,
                Nonce = $"nonce-{caseId}",
                ExpiresAtUtc = expiresAtUtc,
                Transport = "discord-webhook",
                ReleaseSwitchTarget = new ReconciliationReleaseSwitchTarget
                {
                    AlbumMusicBrainzId = "album-current",
                    ReleaseMusicBrainzId = "release-candidate",
                    Classification = ReconciliationClassification.BetterExistingRelease.ToString(),
                    ScoringVersion = ReconciliationEvaluationResult.CurrentScoringVersion
                }
            });
        }

        private static string CorruptProtectedToken(string token)
        {
            var bytes = DecodeBase64Url(token);
            var index = bytes.Length / 2;
            bytes[index] ^= 0x5a;
            return EncodeBase64Url(bytes);
        }

        private static string EncodeBase64Url(byte[] value)
        {
            return Convert.ToBase64String(value)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
        }

        private static byte[] DecodeBase64Url(string value)
        {
            var normalized = value.Replace('-', '+').Replace('_', '/');
            switch (normalized.Length % 4)
            {
                case 2:
                    normalized += "==";
                    break;
                case 3:
                    normalized += "=";
                    break;
            }

            return Convert.FromBase64String(normalized);
        }
    }
}
