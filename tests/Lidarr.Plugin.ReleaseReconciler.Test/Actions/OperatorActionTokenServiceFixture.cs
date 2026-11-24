using System;
using System.IO;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using NLog;
using NzbDrone.Core.Plugins.ReleaseReconciler.Actions;
using Xunit;

namespace NzbDrone.Core.Plugins.ReleaseReconciler.Test.Actions
{
    public sealed class OperatorActionTokenServiceFixture : IDisposable
    {
        private static readonly DateTimeOffset Now = new(2026, 6, 14, 20, 0, 0, TimeSpan.Zero);
        private readonly string _rootDirectory;
        private readonly string _keyDirectory;
        private readonly Logger _logger;

        public OperatorActionTokenServiceFixture()
        {
            _rootDirectory = Path.Combine(Path.GetTempPath(), $"release-reconciler-token-{Guid.NewGuid():N}");
            _keyDirectory = Path.Combine(_rootDirectory, "keys");
            Directory.CreateDirectory(_keyDirectory);
            _logger = LogManager.GetLogger(nameof(OperatorActionTokenServiceFixture));
        }

        [Fact]
        public void should_round_trip_signed_token_across_data_protection_provider_restart()
        {
            var firstService = CreateService(() => Now);
            var issued = firstService.Issue("case-roundtrip", ReconciliationOperatorActionKinds.Snooze, Now.AddHours(4));

            var secondService = CreateService(() => Now.AddMinutes(1));
            var validation = secondService.Validate(issued.Token);

            Assert.True(validation.Succeeded);
            Assert.Equal(ReconciliationOperatorActionOutcomes.Applied, validation.Result);
            Assert.Equal("case-roundtrip", validation.CaseId);
            Assert.Equal(ReconciliationOperatorActionKinds.Snooze, validation.Action);
            Assert.Equal(Now.AddHours(4), validation.ExpiresAtUtc);
            Assert.Equal(issued.ActionTokenHash, validation.ActionTokenHash);
            Assert.Equal(issued.TokenFingerprintPrefix, validation.TokenFingerprintPrefix);
        }

        [Fact]
        public void should_reject_tampered_token_without_leaking_secret_material()
        {
            var service = CreateService(() => Now);
            var issued = service.Issue("case-tampered", ReconciliationOperatorActionKinds.Ignore, Now.AddHours(2));
            var tamperIndex = issued.Token.Length / 2;
            var replacement = issued.Token[tamperIndex] == 'A' ? 'B' : 'A';
            var tampered = issued.Token[..tamperIndex] + replacement + issued.Token[(tamperIndex + 1)..];

            var validation = service.Validate(tampered);

            Assert.False(validation.Succeeded);
            Assert.Equal(ReconciliationOperatorActionOutcomes.InvalidSignature, validation.Result);
            Assert.Null(validation.CaseId);
            Assert.NotNull(validation.ActionTokenHash);
            Assert.DoesNotContain(issued.Token, validation.FailureSummary ?? string.Empty, StringComparison.Ordinal);
        }

        [Fact]
        public void should_round_trip_approve_switch_token_with_signed_release_target()
        {
            var service = CreateService(() => Now);
            var issued = service.Issue(
                "case-approve-switch",
                ReconciliationOperatorActionKinds.ApproveSwitch,
                Now.AddHours(2),
                releaseSwitchTarget: new ReconciliationReleaseSwitchTarget
                {
                    AlbumMusicBrainzId = "album-mbid-1",
                    ReleaseMusicBrainzId = "release-mbid-1",
                    Classification = "BetterExistingRelease",
                    ScoringVersion = "deterministic-v1"
                });

            var validation = service.Validate(issued.Token);

            Assert.True(validation.Succeeded);
            Assert.Equal(ReconciliationOperatorActionOutcomes.Applied, validation.Result);
            Assert.Equal("case-approve-switch", validation.CaseId);
            Assert.Equal(ReconciliationOperatorActionKinds.ApproveSwitch, validation.Action);
            Assert.NotNull(validation.Payload?.ReleaseSwitchTarget);
            Assert.Equal("album-mbid-1", validation.Payload!.ReleaseSwitchTarget!.AlbumMusicBrainzId);
            Assert.Equal("release-mbid-1", validation.Payload.ReleaseSwitchTarget.ReleaseMusicBrainzId);
            Assert.Equal("BetterExistingRelease", validation.Payload.ReleaseSwitchTarget.Classification);
            Assert.Equal("deterministic-v1", validation.Payload.ReleaseSwitchTarget.ScoringVersion);
        }

        [Fact]
        public void should_report_expired_unsupported_and_missing_approve_target_tokens_with_case_context()
        {
            var service = CreateService(() => Now);
            var expired = service.Issue("case-expired", ReconciliationOperatorActionKinds.Snooze, Now.AddMinutes(-5));
            var unsupported = service.ProtectPayload(new ReconciliationOperatorActionTokenPayload
            {
                Version = 1,
                CaseId = "case-unsupported",
                Action = "explode",
                Nonce = "nonce-unsupported",
                ExpiresAtUtc = Now.AddHours(3)
            });
            var missingApproveTarget = service.ProtectPayload(new ReconciliationOperatorActionTokenPayload
            {
                Version = 1,
                CaseId = "case-approve-invalid",
                Action = ReconciliationOperatorActionKinds.ApproveSwitch,
                Nonce = "nonce-approve-invalid",
                ExpiresAtUtc = Now.AddHours(3),
                ReleaseSwitchTarget = new ReconciliationReleaseSwitchTarget
                {
                    AlbumMusicBrainzId = "album-mbid-1",
                    ReleaseMusicBrainzId = "  "
                }
            });

            var expiredValidation = service.Validate(expired.Token);
            var unsupportedValidation = service.Validate(unsupported);
            var missingApproveTargetValidation = service.Validate(missingApproveTarget);

            Assert.False(expiredValidation.Succeeded);
            Assert.Equal(ReconciliationOperatorActionOutcomes.Expired, expiredValidation.Result);
            Assert.Equal("case-expired", expiredValidation.CaseId);
            Assert.Equal(ReconciliationOperatorActionKinds.Snooze, expiredValidation.Action);

            Assert.False(unsupportedValidation.Succeeded);
            Assert.Equal(ReconciliationOperatorActionOutcomes.UnsupportedAction, unsupportedValidation.Result);
            Assert.Equal("case-unsupported", unsupportedValidation.CaseId);
            Assert.Equal("explode", unsupportedValidation.Action);

            Assert.False(missingApproveTargetValidation.Succeeded);
            Assert.Equal(ReconciliationOperatorActionOutcomes.InvalidPayload, missingApproveTargetValidation.Result);
            Assert.Equal("case-approve-invalid", missingApproveTargetValidation.CaseId);
            Assert.Equal(ReconciliationOperatorActionKinds.ApproveSwitch, missingApproveTargetValidation.Action);
            Assert.Contains("winner album and release identifiers", missingApproveTargetValidation.FailureSummary ?? string.Empty, StringComparison.OrdinalIgnoreCase);
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

        private OperatorActionTokenService CreateService(Func<DateTimeOffset> clock)
        {
            var provider = DataProtectionProvider.Create(
                new DirectoryInfo(_keyDirectory),
                configuration => configuration.SetApplicationName("release-reconciler-tests"));

            return new OperatorActionTokenService(provider, _logger, clock);
        }
    }
}
