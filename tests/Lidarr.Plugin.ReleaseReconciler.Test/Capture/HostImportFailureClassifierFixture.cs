using System;
using System.Collections.Generic;
using NzbDrone.Core.Plugins.ReleaseReconciler.Candidates;
using NzbDrone.Core.Plugins.ReleaseReconciler.Capture;
using Xunit;

namespace NzbDrone.Core.Plugins.ReleaseReconciler.Test.Capture
{
    public sealed class HostImportFailureClassifierFixture
    {
        private readonly HostImportFailureClassifier _classifier = new();

        [Theory]
        [InlineData("failed", "Failed", "track number mismatch while importing Track Two", null, 2, "track-number-mismatch")]
        [InlineData("failed", "ImportError", "release metadata reported different track numbers for disc 1", null, 1, "track-number-mismatch")]
        [InlineData("failed", "Rejected", "disc order mismatch detected after retag validation", null, 2, "track-ordering-mismatch")]
        [InlineData("failed", "Error", null, "track ordering mismatch detected during host validation", 2, "track-ordering-mismatch")]
        public void classify_should_flag_supported_track_number_and_ordering_rules(
            string phase,
            string status,
            string? failureText,
            string? diagnosticSummary,
            int trackCount,
            string expectedRule)
        {
            var classification = _classifier.Classify(
                phase,
                status,
                failureText,
                diagnosticSummary,
                BuildStructuralEvidence(trackCount));

            Assert.True(classification.Actionable);
            Assert.Equal(HostImportFailureClassification.CurrentRuleVersion, classification.RuleVersion);
            Assert.Equal(expectedRule, classification.MatchedRule);
            Assert.Null(classification.SuppressionReason);
        }

        [Theory]
        [InlineData("failed", "Failed", "archive extraction failed after download completed")]
        [InlineData("failed", "Failed", "permission denied while moving imported files into the destination folder")]
        [InlineData("failed", "Failed", "missing file on disk after post-processing import")]
        [InlineData("failed", "Failed", "generic import error while refreshing the queue")]
        public void classify_should_suppress_non_actionable_import_failures(string phase, string status, string failureText)
        {
            var classification = _classifier.Classify(
                phase,
                status,
                failureText,
                diagnosticSummary: null,
                structuralEvidence: BuildStructuralEvidence(trackCount: 2));

            Assert.False(classification.Actionable);
            Assert.Equal(HostImportFailureClassification.CurrentRuleVersion, classification.RuleVersion);
            Assert.Equal("non-actionable-import-failure", classification.MatchedRule);
            Assert.Equal("import failure did not match track-number or ordering mismatch rules.", classification.SuppressionReason);
        }

        [Fact]
        public void classify_should_suppress_ordering_mismatch_when_persisted_track_corpus_is_too_small()
        {
            var classification = _classifier.Classify(
                phase: "failed",
                status: "Failed",
                failureText: "track ordering mismatch detected during host validation",
                diagnosticSummary: null,
                structuralEvidence: BuildStructuralEvidence(trackCount: 1));

            Assert.False(classification.Actionable);
            Assert.Equal(HostImportFailureClassification.CurrentRuleVersion, classification.RuleVersion);
            Assert.Equal("insufficient-structural-evidence", classification.MatchedRule);
            Assert.Equal("track-ordering mismatch was suppressed because fewer than two persisted tracks were captured.", classification.SuppressionReason);
        }

        [Fact]
        public void classify_should_suppress_when_failed_phase_has_no_failure_signals()
        {
            var classification = _classifier.Classify(
                phase: "failed",
                status: null,
                failureText: null,
                diagnosticSummary: null,
                structuralEvidence: BuildStructuralEvidence(trackCount: 2));

            Assert.False(classification.Actionable);
            Assert.Equal(HostImportFailureClassification.CurrentRuleVersion, classification.RuleVersion);
            Assert.Equal("missing-failure-context", classification.MatchedRule);
            Assert.Equal("import failure did not include failure text or diagnostic summary signals.", classification.SuppressionReason);
        }

        private static ReconciliationEvidence BuildStructuralEvidence(int trackCount)
        {
            var evidence = new ReconciliationEvidence
            {
                TrackCount = trackCount
            };

            for (var index = 1; index <= trackCount; index += 1)
            {
                evidence.Tracks.Add(new ReconciliationTrackEvidence
                {
                    AbsoluteTrackNumber = index,
                    MediumNumber = 1,
                    Title = $"Track {index}",
                    RecordingMusicBrainzId = $"recording-{index}",
                    ReleaseTrackMusicBrainzId = $"release-track-{index}"
                });
            }

            return evidence;
        }
    }
}
