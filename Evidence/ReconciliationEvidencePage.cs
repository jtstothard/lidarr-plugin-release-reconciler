using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Core.Plugins.ReleaseReconciler.Candidates;
using NzbDrone.Core.Plugins.ReleaseReconciler.Cases;
using NzbDrone.Core.Plugins.ReleaseReconciler.Notifications;

namespace NzbDrone.Core.Plugins.ReleaseReconciler.Evidence
{
    public sealed class ReconciliationEvidencePage : ContentResult
    {
        private ReconciliationEvidencePage(int statusCode, string html)
        {
            StatusCode = statusCode;
            ContentType = "text/html; charset=utf-8";
            Content = html;
        }

        public static ReconciliationEvidencePage Success(ReconciliationCaseSnapshot snapshot, ReconciliationNotificationSettings settings)
        {
            ArgumentNullException.ThrowIfNull(snapshot);
            ArgumentNullException.ThrowIfNull(settings);

            return new ReconciliationEvidencePage(200, BuildSuccessHtml(snapshot, settings.Normalize()));
        }

        public static ReconciliationEvidencePage Refusal(int statusCode, string title, string message)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(title);
            ArgumentException.ThrowIfNullOrWhiteSpace(message);

            return new ReconciliationEvidencePage(statusCode, BuildRefusalHtml(statusCode, title, message));
        }

        public override Task ExecuteResultAsync(ActionContext context)
        {
            ArgumentNullException.ThrowIfNull(context);

            var headers = context.HttpContext.Response.Headers;
            headers["Cache-Control"] = "no-store, no-cache, max-age=0, must-revalidate";
            headers["Pragma"] = "no-cache";
            headers["Expires"] = "0";
            headers["X-Robots-Tag"] = "noindex, nofollow";
            headers["X-Content-Type-Options"] = "nosniff";

            return base.ExecuteResultAsync(context);
        }

        private static string BuildSuccessHtml(ReconciliationCaseSnapshot snapshot, ReconciliationNotificationSettings settings)
        {
            var evaluation = snapshot.EvaluationResult;
            var topCandidate = evaluation?.RankedCandidates.OrderBy(static candidate => candidate.Rank).FirstOrDefault();
            var displayArtist = SelectPreferredValue(snapshot.CapturedIdentifiers, "artistName", "artist", "albumArtist")
                ?? topCandidate?.ArtistName
                ?? "Unknown Artist";
            var displayAlbum = SelectPreferredValue(snapshot.CapturedIdentifiers, "albumTitle", "title", "album")
                ?? topCandidate?.AlbumTitle
                ?? "Unknown Release";
            var classification = ReconciliationNotificationBuilder.GetClassificationLabel(evaluation?.Classification);
            var correctionLinks = ReconciliationNotificationBuilder.BuildSupplementalEvidenceLinks(
                settings,
                snapshot.CapturedIdentifiers,
                displayArtist,
                displayAlbum,
                evaluation,
                BuildNotificationCandidates(evaluation));

            var html = new StringBuilder();
            html.Append("<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
            html.Append("<title>").Append(Html($"Release Reconciler Evidence - {displayArtist} - {displayAlbum}")).Append("</title>");
            html.Append("<style>");
            html.Append("body{font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',sans-serif;margin:0;background:#0b1020;color:#e5e7eb;line-height:1.5;}main{max-width:1100px;margin:0 auto;padding:32px 24px 56px;}section{background:#111827;border:1px solid #243041;border-radius:12px;padding:20px;margin:18px 0;}h1,h2{margin:0 0 12px;}h1{font-size:30px;}h2{font-size:20px;}p{margin:8px 0 0;}table{width:100%;border-collapse:collapse;margin-top:12px;}th,td{text-align:left;padding:10px 12px;border-top:1px solid #243041;vertical-align:top;}th{font-size:12px;text-transform:uppercase;letter-spacing:.04em;color:#9ca3af;}ul{margin:10px 0 0 20px;padding:0;}li{margin:4px 0;}code{font-family:ui-monospace,SFMono-Regular,Menlo,monospace;font-size:13px;color:#c4b5fd;}a{color:#93c5fd;}small,.muted{color:#9ca3af;}.grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(220px,1fr));gap:12px;}.card{background:#0f172a;border:1px solid #243041;border-radius:10px;padding:14px;}.badge{display:inline-block;padding:6px 10px;border-radius:999px;background:#1d4ed8;color:#eff6ff;font-size:12px;font-weight:600;}.empty{color:#9ca3af;font-style:italic;}.danger{color:#fecaca;background:#3f1d1d;border:1px solid #7f1d1d;border-radius:10px;padding:12px;margin-top:12px;}.nowrap{white-space:nowrap;}.mono{font-family:ui-monospace,SFMono-Regular,Menlo,monospace;}.links li{word-break:break-all;}.footer{margin-top:18px;color:#9ca3af;font-size:12px;}");
            html.Append("</style></head><body><main>");
            html.Append("<header><span class=\"badge\">Read-only evidence</span><h1>")
                .Append(Html(displayArtist))
                .Append(" - ")
                .Append(Html(displayAlbum))
                .Append("</h1><p>")
                .Append(Html(classification))
                .Append("</p></header>");

            html.Append("<section><h2>Case summary</h2><div class=\"grid\">");
            AppendMetricCard(html, "Case Id", snapshot.CaseId);
            AppendMetricCard(html, "Source Seam", snapshot.SourceSeam);
            AppendMetricCard(html, "Phase", snapshot.Phase);
            AppendMetricCard(html, "Classification", classification);
            AppendMetricCard(html, "Captured", FormatTimestamp(snapshot.CapturedAtUtc));
            AppendMetricCard(html, "Track Count", snapshot.StructuralEvidence?.TrackCount.ToString(CultureInfo.InvariantCulture) ?? "0");
            AppendMetricCard(html, "Lookup Attempts", (evaluation?.LookupAttempts.Count ?? 0).ToString(CultureInfo.InvariantCulture));
            html.Append("</div>");

            if (!string.IsNullOrWhiteSpace(snapshot.DownloadId) || !string.IsNullOrWhiteSpace(snapshot.OutputPath))
            {
                html.Append("<p class=\"muted\">");
                if (!string.IsNullOrWhiteSpace(snapshot.DownloadId))
                {
                    html.Append("Download: <code>").Append(Html(snapshot.DownloadId!)).Append("</code>");
                }

                if (!string.IsNullOrWhiteSpace(snapshot.DownloadId) && !string.IsNullOrWhiteSpace(snapshot.OutputPath))
                {
                    html.Append(" · ");
                }

                if (!string.IsNullOrWhiteSpace(snapshot.OutputPath))
                {
                    html.Append("Output: <code>").Append(Html(snapshot.OutputPath!)).Append("</code>");
                }

                html.Append("</p>");
            }

            var refusalContext = GetRefusalContext(evaluation);
            if (!string.IsNullOrWhiteSpace(refusalContext))
            {
                html.Append("<div class=\"danger\"><strong>Refusal context:</strong> ")
                    .Append(Html(refusalContext))
                    .Append("</div>");
            }

            if (!string.IsNullOrWhiteSpace(snapshot.DiagnosticSummary))
            {
                html.Append("<p><strong>Diagnostic summary:</strong> ")
                    .Append(Html(snapshot.DiagnosticSummary!))
                    .Append("</p>");
            }

            if (!string.IsNullOrWhiteSpace(snapshot.LastError))
            {
                html.Append("<p><strong>Last error:</strong> ")
                    .Append(Html(snapshot.LastError!))
                    .Append("</p>");
            }

            html.Append("</section>");

            html.Append("<section><h2>Captured identifiers</h2>");
            if (snapshot.CapturedIdentifiers.Count == 0)
            {
                html.Append("<p class=\"empty\">No identifiers were captured.</p>");
            }
            else
            {
                html.Append("<table><thead><tr><th>Key</th><th>Value</th></tr></thead><tbody>");
                foreach (var pair in snapshot.CapturedIdentifiers.OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase))
                {
                    html.Append("<tr><td><code>").Append(Html(pair.Key)).Append("</code></td><td>")
                        .Append(Html(pair.Value ?? "(null)"))
                        .Append("</td></tr>");
                }
                html.Append("</tbody></table>");
            }
            html.Append("</section>");

            html.Append("<section><h2>Structural evidence</h2><div class=\"grid\">");
            AppendMetricCard(html, "Recording MBIDs", (snapshot.StructuralEvidence?.RecordingMusicBrainzIds.Count ?? 0).ToString(CultureInfo.InvariantCulture));
            AppendMetricCard(html, "Release track MBIDs", (snapshot.StructuralEvidence?.ReleaseTrackMusicBrainzIds.Count ?? 0).ToString(CultureInfo.InvariantCulture));
            AppendMetricCard(html, "Winner release MBID", evaluation?.WinnerReleaseMusicBrainzId ?? "—");
            AppendMetricCard(html, "Winner release group MBID", evaluation?.WinnerAlbumMusicBrainzId ?? "—");
            html.Append("</div></section>");

            html.Append("<section><h2>Lookup attempts</h2>");
            if (evaluation?.LookupAttempts.Count > 0)
            {
                html.Append("<table><thead><tr><th>Path</th><th>Attempted</th><th>Input</th><th>Raw</th><th>Added</th><th>Deduped</th><th>Reason</th></tr></thead><tbody>");
                foreach (var attempt in evaluation.LookupAttempts)
                {
                    html.Append("<tr><td>").Append(Html(attempt.Path.ToString())).Append("</td><td>")
                        .Append(attempt.Attempted ? "Yes" : "No")
                        .Append("</td><td>").Append(attempt.InputCount.ToString(CultureInfo.InvariantCulture))
                        .Append("</td><td>").Append(attempt.RawCandidateCount.ToString(CultureInfo.InvariantCulture))
                        .Append("</td><td>").Append(attempt.AddedCandidateCount.ToString(CultureInfo.InvariantCulture))
                        .Append("</td><td>").Append(attempt.DeduplicatedCandidateCount.ToString(CultureInfo.InvariantCulture))
                        .Append("</td><td>").Append(Html(NullToDash(attempt.Reason))).Append("</td></tr>");
                }
                html.Append("</tbody></table>");
            }
            else
            {
                html.Append("<p class=\"empty\">No lookup attempts were persisted for this case.</p>");
            }
            html.Append("</section>");

            html.Append("<section><h2>Ranked candidates</h2>");
            if (evaluation?.RankedCandidates.Count > 0)
            {
                html.Append("<table><thead><tr><th>Rank</th><th>Candidate</th><th>Artist</th><th>Release MBID</th><th>Release Group MBID</th><th>Score</th><th>Flags</th><th>Lookup Paths</th></tr></thead><tbody>");
                foreach (var candidate in evaluation.RankedCandidates.OrderBy(static candidate => candidate.Rank))
                {
                    var flags = new List<string>();
                    if (candidate.SameReleaseGroup)
                    {
                        flags.Add("Same release group");
                    }

                    if (candidate.HasStrongIdentitySignal)
                    {
                        flags.Add("Strong identity signal");
                    }

                    if (candidate.TitleOnlyCandidate)
                    {
                        flags.Add("Title-only");
                    }

                    html.Append("<tr><td class=\"nowrap\">#").Append(candidate.Rank.ToString(CultureInfo.InvariantCulture))
                        .Append("</td><td>").Append(Html(ReconciliationNotificationBuilder.ComposeCandidateTitle(candidate)))
                        .Append("</td><td>").Append(Html(NullToDash(candidate.ArtistName)))
                        .Append("</td><td class=\"mono\">").Append(Html(NullToDash(candidate.ReleaseMusicBrainzId)))
                        .Append("</td><td class=\"mono\">").Append(Html(NullToDash(candidate.AlbumMusicBrainzId)))
                        .Append("</td><td>").Append(candidate.ScoreBreakdown.TotalScore.ToString(CultureInfo.InvariantCulture))
                        .Append("</td><td>").Append(Html(flags.Count == 0 ? "—" : string.Join(", ", flags)))
                        .Append("</td><td>").Append(Html(candidate.LookupPaths.Count == 0 ? "—" : string.Join(", ", candidate.LookupPaths.Select(static path => path.ToString()))))
                        .Append("</td></tr>");
                }
                html.Append("</tbody></table>");
            }
            else
            {
                html.Append("<p class=\"empty\">No ranked candidates were persisted for this case.</p>");
            }
            html.Append("</section>");

            html.Append("<section><h2>Correction handoff links</h2>");
            if (correctionLinks.Count > 0)
            {
                html.Append("<ul class=\"links\">");
                foreach (var link in correctionLinks)
                {
                    html.Append("<li><a href=\"").Append(HtmlAttribute(link.Url)).Append("\">")
                        .Append(Html(link.Label))
                        .Append("</a><br><small>")
                        .Append(Html(link.Url))
                        .Append("</small></li>");
                }
                html.Append("</ul>");
            }
            else
            {
                html.Append("<p class=\"empty\">No external correction links were available for this case.</p>");
            }
            html.Append("</section>");

            html.Append("<section><h2>Notification audit</h2>");
            var dispatchAttempts = snapshot.NotificationState?.DispatchAttempts ?? new List<ReconciliationNotificationDispatchAttempt>();
            if (dispatchAttempts.Count > 0)
            {
                html.Append("<table><thead><tr><th>Attempted</th><th>Transport</th><th>Succeeded</th><th>Action URL Kind</th><th>Action Token Expires</th><th>Failure</th></tr></thead><tbody>");
                foreach (var attempt in dispatchAttempts.OrderByDescending(static attempt => attempt.AttemptedAtUtc))
                {
                    html.Append("<tr><td class=\"nowrap\">").Append(Html(FormatTimestamp(attempt.AttemptedAtUtc)))
                        .Append("</td><td>").Append(Html(attempt.Transport))
                        .Append("</td><td>").Append(attempt.Succeeded ? "Yes" : "No")
                        .Append("</td><td>").Append(Html(NullToDash(attempt.ActionUrlKind)))
                        .Append("</td><td class=\"nowrap\">").Append(Html(FormatTimestamp(attempt.ActionTokenExpiresAtUtc)))
                        .Append("</td><td>").Append(Html(CombineFailure(attempt.FailureKind, attempt.FailureSummary)))
                        .Append("</td></tr>");
                }
                html.Append("</tbody></table>");
            }
            else
            {
                html.Append("<p class=\"empty\">No notification dispatch attempts have been persisted yet.</p>");
            }
            html.Append("</section>");

            html.Append("<section><h2>Operator action audit</h2>");
            var actionState = snapshot.OperatorActionState;
            html.Append("<div class=\"grid\">");
            AppendMetricCard(html, "Snoozed until", FormatTimestamp(actionState?.SnoozedUntilUtc));
            AppendMetricCard(html, "Ignored at", FormatTimestamp(actionState?.IgnoredAtUtc));
            AppendMetricCard(html, "Last action", actionState?.LastAction?.Action ?? "—");
            AppendMetricCard(html, "Last action result", actionState?.LastAction?.Result ?? "—");
            html.Append("</div>");

            if (actionState?.LastAction != null)
            {
                html.Append("<p><strong>Last action details:</strong> ")
                    .Append(Html($"{actionState.LastAction.Action} -> {actionState.LastAction.Result} at {FormatTimestamp(actionState.LastAction.OccurredAtUtc)}"));
                if (!string.IsNullOrWhiteSpace(actionState.LastAction.Transport))
                {
                    html.Append(" · transport ").Append(Html(actionState.LastAction.Transport!));
                }

                if (!string.IsNullOrWhiteSpace(actionState.LastAction.ActionedBy))
                {
                    html.Append(" · by ").Append(Html(actionState.LastAction.ActionedBy!));
                }

                if (!string.IsNullOrWhiteSpace(actionState.LastAction.FailureSummary))
                {
                    html.Append(" · failure ").Append(Html(actionState.LastAction.FailureSummary!));
                }

                html.Append("</p>");
            }

            var receipts = actionState?.ProcessedActionReceipts ?? new List<ReconciliationOperatorActionReceipt>();
            if (receipts.Count > 0)
            {
                html.Append("<table><thead><tr><th>Processed</th><th>Action</th><th>Outcome</th><th>Transport</th><th>Expires</th><th>Failure</th></tr></thead><tbody>");
                foreach (var receipt in receipts.OrderByDescending(static receipt => receipt.ProcessedAtUtc))
                {
                    html.Append("<tr><td class=\"nowrap\">").Append(Html(FormatTimestamp(receipt.ProcessedAtUtc)))
                        .Append("</td><td>").Append(Html(receipt.Action))
                        .Append("</td><td>").Append(Html(receipt.Outcome))
                        .Append("</td><td>").Append(Html(NullToDash(receipt.Transport)))
                        .Append("</td><td class=\"nowrap\">").Append(Html(FormatTimestamp(receipt.ExpiresAtUtc)))
                        .Append("</td><td>").Append(Html(NullToDash(receipt.FailureSummary)))
                        .Append("</td></tr>");
                }
                html.Append("</tbody></table>");
            }
            else
            {
                html.Append("<p class=\"empty\">No operator-action receipts have been persisted yet.</p>");
            }
            html.Append("</section>");

            html.Append("<p class=\"footer\">This page is anonymous, signed, and read-only. Refreshing or viewing it does not mutate reconciliation case state.</p>");
            html.Append("</main></body></html>");
            return html.ToString();
        }

        private static string BuildRefusalHtml(int statusCode, string title, string message)
        {
            return $"<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\"><title>{Html(title)}</title><style>body{{font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',sans-serif;background:#0f172a;color:#e5e7eb;padding:32px;line-height:1.6;}}main{{max-width:760px;margin:0 auto;background:#111827;border:1px solid #243041;border-radius:12px;padding:24px;}}.status{{display:inline-block;padding:4px 10px;border-radius:999px;background:#7f1d1d;color:#fee2e2;font-size:12px;font-weight:700;letter-spacing:.04em;}}h1{{margin:16px 0 8px;font-size:28px;}}p{{margin:0;}}</style></head><body><main><span class=\"status\">HTTP {statusCode.ToString(CultureInfo.InvariantCulture)}</span><h1>{Html(title)}</h1><p>{Html(message)}</p></main></body></html>";
        }

        private static IReadOnlyList<ReconciliationNotificationCandidate> BuildNotificationCandidates(ReconciliationEvaluationResult? evaluation)
        {
            return (evaluation?.RankedCandidates ?? new List<RankedCandidateRelease>())
                .OrderBy(static candidate => candidate.Rank)
                .Select(candidate => new ReconciliationNotificationCandidate
                {
                    Rank = candidate.Rank,
                    ArtistName = candidate.ArtistName,
                    ReleaseTitle = ReconciliationNotificationBuilder.ComposeCandidateTitle(candidate),
                    ReleaseMusicBrainzId = candidate.ReleaseMusicBrainzId,
                    SameReleaseGroup = candidate.SameReleaseGroup,
                    HasStrongIdentitySignal = candidate.HasStrongIdentitySignal,
                    TitleOnlyCandidate = candidate.TitleOnlyCandidate,
                    LookupPaths = candidate.LookupPaths.Select(static path => path.ToString()).ToArray()
                })
                .ToArray();
        }

        private static void AppendMetricCard(StringBuilder html, string label, string value)
        {
            html.Append("<div class=\"card\"><div class=\"muted\">")
                .Append(Html(label))
                .Append("</div><div>")
                .Append(Html(NullToDash(value)))
                .Append("</div></div>");
        }

        private static string? SelectPreferredValue(IReadOnlyDictionary<string, string?> identifiers, params string[] keys)
        {
            foreach (var key in keys)
            {
                if (identifiers.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
                {
                    return value.Trim();
                }
            }

            return null;
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

        private static string Html(string value)
        {
            return WebUtility.HtmlEncode(value);
        }

        private static string HtmlAttribute(string value)
        {
            return WebUtility.HtmlEncode(value);
        }

        private static string FormatTimestamp(DateTimeOffset? value)
        {
            return value?.ToString("O") ?? "—";
        }

        private static string NullToDash(string? value)
        {
            return string.IsNullOrWhiteSpace(value) ? "—" : value!;
        }

        private static string CombineFailure(string? kind, string? summary)
        {
            if (string.IsNullOrWhiteSpace(kind) && string.IsNullOrWhiteSpace(summary))
            {
                return "—";
            }

            if (string.IsNullOrWhiteSpace(kind))
            {
                return summary!;
            }

            if (string.IsNullOrWhiteSpace(summary))
            {
                return kind;
            }

            return $"{kind}: {summary}";
        }
    }
}
