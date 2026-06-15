using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.DataProtection;
using NLog;
using NzbDrone.Core.Plugins.ReleaseReconciler.Actions;
using NzbDrone.Core.Plugins.ReleaseReconciler.Candidates;
using NzbDrone.Core.Plugins.ReleaseReconciler.Cases;
using NzbDrone.Core.Plugins.ReleaseReconciler.Evidence;
using NzbDrone.Core.Plugins.ReleaseReconciler.Switching;

namespace NzbDrone.Core.Plugins.ReleaseReconciler.Notifications
{
    public sealed class ReconciliationNotificationBuilder
    {
        private const string SignedContentPathActionUrlKind = "signed-content-path";
        private const string SignedReadOnlyPathEvidenceUrlKind = "signed-readonly-path";
        private readonly OperatorActionTokenService _tokenService;
        private readonly ReconciliationEvidenceTokenService _evidenceTokenService;
        private readonly Func<DateTimeOffset> _clock;

        public ReconciliationNotificationBuilder(Func<DateTimeOffset>? clock = null)
            : this(CreateFallbackTokenService(clock), CreateFallbackEvidenceTokenService(clock), clock)
        {
        }

        public ReconciliationNotificationBuilder(OperatorActionTokenService tokenService, Func<DateTimeOffset>? clock = null)
            : this(tokenService, CreateFallbackEvidenceTokenService(clock), clock)
        {
        }

        public ReconciliationNotificationBuilder(OperatorActionTokenService tokenService, ReconciliationEvidenceTokenService evidenceTokenService, Func<DateTimeOffset>? clock = null)
        {
            _tokenService = tokenService ?? throw new ArgumentNullException(nameof(tokenService));
            _evidenceTokenService = evidenceTokenService ?? throw new ArgumentNullException(nameof(evidenceTokenService));
            _clock = clock ?? (() => DateTimeOffset.UtcNow);
        }

        public ReconciliationNotificationEnvelope Build(ReconciliationCase reconciliationCase, ReconciliationNotificationSettings settings)
        {
            return Build(reconciliationCase, settings, new ReleaseSwitchSettings());
        }

        public ReconciliationNotificationEnvelope Build(ReconciliationCase reconciliationCase, ReconciliationNotificationSettings settings, ReleaseSwitchSettings releaseSwitchSettings)
        {
            ArgumentNullException.ThrowIfNull(reconciliationCase);
            ArgumentNullException.ThrowIfNull(settings);
            ArgumentNullException.ThrowIfNull(releaseSwitchSettings);

            var normalizedSettings = settings.Normalize();
            var normalizedReleaseSwitchSettings = releaseSwitchSettings.Normalize();
            var evaluation = reconciliationCase.EvaluationResult;
            var capturedIdentifiers = reconciliationCase.CapturedIdentifiers;
            var generatedAtUtc = _clock();
            var actionExpiresAtUtc = generatedAtUtc.AddMinutes(normalizedSettings.ActionTokenLifetimeMinutes);
            var evidenceExpiresAtUtc = generatedAtUtc.AddMinutes(normalizedSettings.EvidenceTokenLifetimeMinutes);
            var displayArtist = SelectPreferredValue(capturedIdentifiers, "artistName", "artist", "albumArtist")
                ?? evaluation?.RankedCandidates.FirstOrDefault()?.ArtistName
                ?? "Unknown Artist";
            var displayAlbum = SelectPreferredValue(capturedIdentifiers, "albumTitle", "title", "album")
                ?? evaluation?.RankedCandidates.FirstOrDefault()?.AlbumTitle
                ?? "Unknown Release";
            var classification = GetClassificationLabel(evaluation?.Classification);
            var title = $"Release Reconciler: {displayArtist} - {displayAlbum}";
            var summary = $"{classification} for {displayArtist} - {displayAlbum}.";
            var refusalContext = GetRefusalContext(evaluation);

            var candidates = (evaluation?.RankedCandidates ?? new List<RankedCandidateRelease>())
                .OrderBy(static candidate => candidate.Rank)
                .Take(3)
                .Select(candidate => new ReconciliationNotificationCandidate
                {
                    Rank = candidate.Rank,
                    ArtistName = candidate.ArtistName,
                    ReleaseTitle = ComposeCandidateTitle(candidate),
                    ReleaseMusicBrainzId = candidate.ReleaseMusicBrainzId,
                    SameReleaseGroup = candidate.SameReleaseGroup,
                    HasStrongIdentitySignal = candidate.HasStrongIdentitySignal,
                    TitleOnlyCandidate = candidate.TitleOnlyCandidate,
                    LookupPaths = candidate.LookupPaths.Select(static path => path.ToString()).ToArray()
                })
                .ToArray();

            var evidenceLinkResult = BuildEvidenceLinks(
                normalizedSettings,
                reconciliationCase.CaseId,
                capturedIdentifiers,
                displayArtist,
                displayAlbum,
                evaluation,
                candidates,
                evidenceExpiresAtUtc);

            var fields = new List<ReconciliationNotificationField>
            {
                new() { Name = "Case Id", Value = reconciliationCase.CaseId },
                new() { Name = "Source Seam", Value = reconciliationCase.SourceSeam },
                new() { Name = "Phase", Value = reconciliationCase.Phase },
                new() { Name = "Track Count", Value = reconciliationCase.StructuralEvidence.TrackCount.ToString(System.Globalization.CultureInfo.InvariantCulture) },
                new() { Name = "Lookup Attempts", Value = (evaluation?.LookupAttempts.Count ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture) },
                new() { Name = "Action Token Expires", Value = actionExpiresAtUtc.ToString("O") },
                new() { Name = "Evidence Page", Value = evidenceLinkResult.EvidencePageStatus }
            };

            if (evidenceLinkResult.EvidenceTokenExpiresAtUtc.HasValue)
            {
                fields.Add(new ReconciliationNotificationField { Name = "Evidence Token Expires", Value = evidenceLinkResult.EvidenceTokenExpiresAtUtc.Value.ToString("O") });
            }

            if (!string.IsNullOrWhiteSpace(reconciliationCase.DownloadId))
            {
                fields.Add(new ReconciliationNotificationField { Name = "Download Id", Value = reconciliationCase.DownloadId! });
            }

            if (!string.IsNullOrWhiteSpace(reconciliationCase.OutputPath))
            {
                fields.Add(new ReconciliationNotificationField { Name = "Output Path", Value = reconciliationCase.OutputPath! });
            }

            if (evaluation?.WinnerReleaseMusicBrainzId != null)
            {
                fields.Add(new ReconciliationNotificationField { Name = "Winner Release MBID", Value = evaluation.WinnerReleaseMusicBrainzId });
            }

            if (evaluation?.WinnerAlbumMusicBrainzId != null)
            {
                fields.Add(new ReconciliationNotificationField { Name = "Winner Album MBID", Value = evaluation.WinnerAlbumMusicBrainzId });
            }

            if (evaluation?.UsedTitleArtistFallback == true)
            {
                fields.Add(new ReconciliationNotificationField { Name = "Fallback Match", Value = "Title/artist fallback used" });
            }

            var actions = BuildActions(normalizedSettings, normalizedReleaseSwitchSettings, reconciliationCase.CaseId, evaluation, actionExpiresAtUtc);

            return new ReconciliationNotificationEnvelope(
                caseId: reconciliationCase.CaseId,
                title: title,
                summary: summary,
                classification: classification,
                sourceSeam: reconciliationCase.SourceSeam,
                phase: reconciliationCase.Phase,
                generatedAtUtc: generatedAtUtc,
                actionUrlKind: SignedContentPathActionUrlKind,
                actionTokenExpiresAtUtc: actionExpiresAtUtc,
                evidenceUrlKind: evidenceLinkResult.EvidenceUrlKind,
                evidenceTokenExpiresAtUtc: evidenceLinkResult.EvidenceTokenExpiresAtUtc,
                fields: fields,
                evidenceLinks: evidenceLinkResult.Links,
                actions: actions,
                candidates: candidates,
                refusalContext: refusalContext,
                diagnosticSummary: reconciliationCase.DiagnosticSummary);
        }

        private EvidenceLinkBuildResult BuildEvidenceLinks(
            ReconciliationNotificationSettings settings,
            string caseId,
            IReadOnlyDictionary<string, string?> capturedIdentifiers,
            string displayArtist,
            string displayAlbum,
            ReconciliationEvaluationResult? evaluation,
            IReadOnlyList<ReconciliationNotificationCandidate> candidates,
            DateTimeOffset evidenceExpiresAtUtc)
        {
            var links = new List<ReconciliationNotificationLink>();
            string? evidenceUrlKind = null;
            DateTimeOffset? evidenceTokenExpiresAtUtc = null;

            if (TryCreateEvidencePageLink(settings, caseId, evidenceExpiresAtUtc, out var evidencePageLink))
            {
                links.Add(evidencePageLink!);
                evidenceUrlKind = SignedReadOnlyPathEvidenceUrlKind;
                evidenceTokenExpiresAtUtc = evidenceExpiresAtUtc;
            }

            links.AddRange(BuildSupplementalEvidenceLinks(settings, capturedIdentifiers, displayArtist, displayAlbum, evaluation, candidates));

            return new EvidenceLinkBuildResult
            {
                EvidencePageStatus = GetEvidencePageStatus(settings, evidenceUrlKind != null),
                EvidenceUrlKind = evidenceUrlKind,
                EvidenceTokenExpiresAtUtc = evidenceTokenExpiresAtUtc,
                Links = links
                    .GroupBy(static link => $"{link.Label}\u001f{link.Url}", StringComparer.Ordinal)
                    .Select(static group => group.First())
                    .ToArray()
            };
        }

        internal static IReadOnlyList<ReconciliationNotificationLink> BuildSupplementalEvidenceLinks(
            ReconciliationNotificationSettings settings,
            IReadOnlyDictionary<string, string?> capturedIdentifiers,
            string displayArtist,
            string displayAlbum,
            ReconciliationEvaluationResult? evaluation,
            IReadOnlyList<ReconciliationNotificationCandidate> candidates)
        {
            var links = new List<ReconciliationNotificationLink>();

            if (TryGetIdentifier(capturedIdentifiers, "albumUrl", out var albumUrl))
            {
                links.Add(new ReconciliationNotificationLink { Label = "Bandcamp Source", Url = albumUrl! });
            }

            var topCandidate = evaluation?.RankedCandidates.FirstOrDefault();
            var releaseId = evaluation?.WinnerReleaseMusicBrainzId ?? topCandidate?.ReleaseMusicBrainzId ?? candidates.FirstOrDefault()?.ReleaseMusicBrainzId;
            if (!string.IsNullOrWhiteSpace(releaseId))
            {
                links.Add(new ReconciliationNotificationLink { Label = "MusicBrainz Release", Url = $"https://musicbrainz.org/release/{Uri.EscapeDataString(releaseId!)}" });
            }

            var albumId = evaluation?.WinnerAlbumMusicBrainzId ?? topCandidate?.AlbumMusicBrainzId;
            if (!string.IsNullOrWhiteSpace(albumId))
            {
                links.Add(new ReconciliationNotificationLink { Label = "MusicBrainz Release Group", Url = $"https://musicbrainz.org/release-group/{Uri.EscapeDataString(albumId!)}" });
            }

            if (!string.IsNullOrWhiteSpace(settings.HarmonySearchBaseUrl))
            {
                var harmonyUrl = BuildHarmonySearchUrl(settings.HarmonySearchBaseUrl!, $"{displayArtist} {displayAlbum}".Trim());
                links.Add(new ReconciliationNotificationLink { Label = "Harmony Search", Url = harmonyUrl });
            }

            return links
                .GroupBy(static link => $"{link.Label}\u001f{link.Url}", StringComparer.Ordinal)
                .Select(static group => group.First())
                .ToArray();
        }

        private IReadOnlyList<ReconciliationNotificationAction> BuildActions(ReconciliationNotificationSettings settings, ReleaseSwitchSettings releaseSwitchSettings, string caseId, ReconciliationEvaluationResult? evaluation, DateTimeOffset actionExpiresAtUtc)
        {
            var baseUri = settings.GetPublicBaseUri();
            var actions = new List<ReconciliationNotificationAction>();

            if (TryCreateApprovalTarget(releaseSwitchSettings, evaluation, out var approvalTarget))
            {
                actions.Add(CreateAction(baseUri, settings.OperatorActionPath!, "Approve switch", caseId, ReconciliationOperatorActionKinds.ApproveSwitch, actionExpiresAtUtc, approvalTarget));
            }

            actions.Add(CreateAction(baseUri, settings.OperatorActionPath!, "Snooze 24h", caseId, ReconciliationOperatorActionKinds.Snooze, actionExpiresAtUtc));
            actions.Add(CreateAction(baseUri, settings.OperatorActionPath!, "Ignore case", caseId, ReconciliationOperatorActionKinds.Ignore, actionExpiresAtUtc));
            return actions;
        }

        private ReconciliationNotificationAction CreateAction(Uri baseUri, string actionPath, string label, string caseId, string action, DateTimeOffset actionExpiresAtUtc, ReconciliationReleaseSwitchTarget? releaseSwitchTarget = null)
        {
            var issuedToken = _tokenService.Issue(caseId, action, actionExpiresAtUtc, releaseSwitchTarget: releaseSwitchTarget);
            return new ReconciliationNotificationAction
            {
                Action = action,
                Label = label,
                Url = BuildSignedPathUrl(baseUri, actionPath, issuedToken.Token)
            };
        }

        private static bool TryCreateApprovalTarget(ReleaseSwitchSettings releaseSwitchSettings, ReconciliationEvaluationResult? evaluation, out ReconciliationReleaseSwitchTarget? approvalTarget)
        {
            approvalTarget = null;
            if (!releaseSwitchSettings.EnableReleaseSwitchApproval || evaluation == null)
            {
                return false;
            }

            if (evaluation.UsedTitleArtistFallback || evaluation.RefusalReason != null)
            {
                return false;
            }

            if (evaluation.Classification is not ReconciliationClassification.BetterExistingRelease and not ReconciliationClassification.DifferentReleaseGroup)
            {
                return false;
            }

            if (evaluation.Classification == ReconciliationClassification.DifferentReleaseGroup
                && !releaseSwitchSettings.EnableDifferentGroupReleaseSwitchApproval)
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(evaluation.WinnerAlbumMusicBrainzId)
                || string.IsNullOrWhiteSpace(evaluation.WinnerReleaseMusicBrainzId))
            {
                return false;
            }

            var winner = evaluation.RankedCandidates.FirstOrDefault(candidate =>
                string.Equals(candidate.AlbumMusicBrainzId, evaluation.WinnerAlbumMusicBrainzId, StringComparison.Ordinal)
                && string.Equals(candidate.ReleaseMusicBrainzId, evaluation.WinnerReleaseMusicBrainzId, StringComparison.Ordinal));
            if (winner == null || winner.Rank != 1 || winner.TitleOnlyCandidate || !winner.HasStrongIdentitySignal)
            {
                return false;
            }

            approvalTarget = new ReconciliationReleaseSwitchTarget
            {
                AlbumMusicBrainzId = evaluation.WinnerAlbumMusicBrainzId!,
                ReleaseMusicBrainzId = evaluation.WinnerReleaseMusicBrainzId!,
                Classification = evaluation.Classification.ToString(),
                ScoringVersion = evaluation.ScoringVersion
            };

            return true;
        }

        private bool TryCreateEvidencePageLink(ReconciliationNotificationSettings settings, string caseId, DateTimeOffset evidenceExpiresAtUtc, out ReconciliationNotificationLink? evidencePageLink)
        {
            evidencePageLink = null;
            if (!TryGetConfiguredPublicBaseUri(settings, out var baseUri))
            {
                return false;
            }

            var issuedToken = _evidenceTokenService.Issue(caseId, evidenceExpiresAtUtc);
            evidencePageLink = new ReconciliationNotificationLink
            {
                Label = "Evidence Page",
                Url = BuildSignedPathUrl(baseUri!, settings.EvidencePagePath!, issuedToken.Token)
            };

            return true;
        }

        private static bool TryGetConfiguredPublicBaseUri(ReconciliationNotificationSettings settings, out Uri? baseUri)
        {
            baseUri = null;
            if (string.IsNullOrWhiteSpace(settings.PublicBaseUrl))
            {
                return false;
            }

            if (!Uri.TryCreate(settings.PublicBaseUrl, UriKind.Absolute, out var parsedBaseUri))
            {
                return false;
            }

            baseUri = parsedBaseUri;
            return true;
        }

        private static string GetEvidencePageStatus(ReconciliationNotificationSettings settings, bool evidenceLinkGenerated)
        {
            if (evidenceLinkGenerated)
            {
                return "Signed link generated";
            }

            return string.IsNullOrWhiteSpace(settings.PublicBaseUrl)
                ? "Omitted (PublicBaseUrl not configured)"
                : "Omitted (PublicBaseUrl invalid)";
        }

        private static string BuildSignedPathUrl(Uri baseUri, string path, string token)
        {
            var relative = $"{path.TrimEnd('/')}/{Uri.EscapeDataString(token)}";
            return new Uri(baseUri, relative).ToString();
        }

        internal static string BuildHarmonySearchUrl(string baseUrl, string query)
        {
            var builder = new UriBuilder(baseUrl);
            var current = builder.Query;
            if (!string.IsNullOrWhiteSpace(current))
            {
                current = current.TrimStart('?') + "&";
            }

            builder.Query = (current ?? string.Empty) + "term=" + Uri.EscapeDataString(query);
            return builder.Uri.ToString();
        }

        internal static string ComposeCandidateTitle(RankedCandidateRelease candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate.ReleaseDisambiguation))
            {
                return candidate.ReleaseTitle;
            }

            return $"{candidate.ReleaseTitle} ({candidate.ReleaseDisambiguation})";
        }

        internal static string GetClassificationLabel(ReconciliationClassification? classification)
        {
            return classification switch
            {
                ReconciliationClassification.BetterExistingRelease => "Better existing release already present",
                ReconciliationClassification.MissingReleaseInCurrentGroup => "Missing release in current group",
                ReconciliationClassification.DifferentReleaseGroup => "Different release group candidate",
                ReconciliationClassification.NoSafeMatch => "No safe match",
                _ => "Evaluation pending"
            };
        }

        private static string? GetRefusalContext(ReconciliationEvaluationResult? evaluation)
        {
            if (evaluation?.RefusalReason == null)
            {
                return null;
            }

            var reason = evaluation.RefusalReason.Value switch
            {
                ReconciliationRefusalReason.NoCandidates => "No reconciliation candidates were returned by the Lidarr lookup.",
                ReconciliationRefusalReason.AmbiguousWinnerMargin => "Top candidates stayed too close together to permit a safe operator action.",
                ReconciliationRefusalReason.TitleOnlyMatchNotAllowed => "Only title-level evidence was available, so the reconciler refused to suggest a direct match.",
                ReconciliationRefusalReason.SameReleaseGroupWithoutStrongReleaseMatch => "Candidates stayed inside the same release group but never crossed the strong release-identity threshold.",
                _ => "The reconciler refused to promote a candidate without stronger evidence."
            };

            if (evaluation.UsedTitleArtistFallback)
            {
                reason += " Title/artist fallback was used during lookup.";
            }

            return reason;
        }

        private static string? SelectPreferredValue(IReadOnlyDictionary<string, string?> identifiers, params string[] keys)
        {
            foreach (var key in keys)
            {
                if (TryGetIdentifier(identifiers, key, out var value))
                {
                    return value;
                }
            }

            return null;
        }

        private static bool TryGetIdentifier(IReadOnlyDictionary<string, string?> identifiers, string key, out string? value)
        {
            value = null;
            if (!identifiers.TryGetValue(key, out var candidate) || string.IsNullOrWhiteSpace(candidate))
            {
                return false;
            }

            value = candidate.Trim();
            return true;
        }

        private static OperatorActionTokenService CreateFallbackTokenService(Func<DateTimeOffset>? clock)
        {
            return new OperatorActionTokenService(
                new EphemeralDataProtectionProvider(),
                LogManager.GetLogger(nameof(ReconciliationNotificationBuilder)),
                clock);
        }

        private static ReconciliationEvidenceTokenService CreateFallbackEvidenceTokenService(Func<DateTimeOffset>? clock)
        {
            return new ReconciliationEvidenceTokenService(
                new EphemeralDataProtectionProvider(),
                LogManager.GetLogger(nameof(ReconciliationEvidenceTokenService)),
                clock);
        }

        private sealed class EvidenceLinkBuildResult
        {
            public required string EvidencePageStatus { get; init; }

            public string? EvidenceUrlKind { get; init; }

            public DateTimeOffset? EvidenceTokenExpiresAtUtc { get; init; }

            public required IReadOnlyList<ReconciliationNotificationLink> Links { get; init; }
        }
    }
}
