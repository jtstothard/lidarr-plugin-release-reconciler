using System;
using System.IO;
using Microsoft.AspNetCore.DataProtection;
using NLog;
using NzbDrone.Core.Plugins.ReleaseReconciler.Evidence;
using Xunit;

namespace NzbDrone.Core.Plugins.ReleaseReconciler.Test.Evidence
{
    public sealed class ReconciliationEvidenceTokenServiceFixture : IDisposable
    {
        private static readonly DateTimeOffset Now = new(2026, 6, 14, 20, 0, 0, TimeSpan.Zero);
        private readonly string _rootDirectory;
        private readonly string _keyDirectory;
        private readonly Logger _logger;

        public ReconciliationEvidenceTokenServiceFixture()
        {
            _rootDirectory = Path.Combine(Path.GetTempPath(), $"release-reconciler-evidence-token-{Guid.NewGuid():N}");
            _keyDirectory = Path.Combine(_rootDirectory, "keys");
            Directory.CreateDirectory(_keyDirectory);
            _logger = LogManager.GetLogger(nameof(ReconciliationEvidenceTokenServiceFixture));
        }

        [Fact]
        public void should_round_trip_signed_evidence_token_across_data_protection_provider_restart()
        {
            var firstService = CreateService(() => Now);
            var issued = firstService.Issue("case-roundtrip", Now.AddHours(4));

            var secondService = CreateService(() => Now.AddMinutes(1));
            var validation = secondService.Validate(issued.Token);

            Assert.True(validation.Succeeded);
            Assert.Equal(ReconciliationEvidenceTokenOutcomes.Valid, validation.Result);
            Assert.Equal("case-roundtrip", validation.CaseId);
            Assert.Equal(Now.AddHours(4), validation.ExpiresAtUtc);
            Assert.Equal(issued.EvidenceTokenHash, validation.EvidenceTokenHash);
            Assert.Equal(issued.TokenFingerprintPrefix, validation.TokenFingerprintPrefix);
        }

        [Fact]
        public void should_reject_tampered_evidence_token_without_leaking_secret_material()
        {
            var service = CreateService(() => Now);
            var issued = service.Issue("case-tampered", Now.AddHours(2));
            var tamperIndex = issued.Token.Length / 2;
            var replacement = issued.Token[tamperIndex] == 'A' ? 'B' : 'A';
            var tampered = issued.Token[..tamperIndex] + replacement + issued.Token[(tamperIndex + 1)..];

            var validation = service.Validate(tampered);

            Assert.False(validation.Succeeded);
            Assert.Equal(ReconciliationEvidenceTokenOutcomes.InvalidSignature, validation.Result);
            Assert.Null(validation.CaseId);
            Assert.NotNull(validation.EvidenceTokenHash);
            Assert.DoesNotContain(issued.Token, validation.FailureSummary ?? string.Empty, StringComparison.Ordinal);
        }

        [Fact]
        public void should_report_expired_evidence_tokens_with_case_context()
        {
            var service = CreateService(() => Now);
            var issued = service.Issue("case-expired", Now.AddMinutes(-5));

            var validation = service.Validate(issued.Token);

            Assert.False(validation.Succeeded);
            Assert.Equal(ReconciliationEvidenceTokenOutcomes.Expired, validation.Result);
            Assert.Equal("case-expired", validation.CaseId);
            Assert.Equal(Now.AddMinutes(-5), validation.ExpiresAtUtc);
            Assert.NotNull(validation.EvidenceTokenHash);
            Assert.NotNull(validation.TokenFingerprintPrefix);
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

        private ReconciliationEvidenceTokenService CreateService(Func<DateTimeOffset> clock)
        {
            var provider = DataProtectionProvider.Create(
                new DirectoryInfo(_keyDirectory),
                configuration => configuration.SetApplicationName("release-reconciler-tests"));

            return new ReconciliationEvidenceTokenService(provider, _logger, clock);
        }
    }
}
