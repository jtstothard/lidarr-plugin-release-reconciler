using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace NzbDrone.Core.Plugins.ReleaseReconciler.Notifications
{
    public sealed class ReconciliationNotificationEnvelope
    {
        public ReconciliationNotificationEnvelope(
            string caseId,
            string title,
            string summary,
            string classification,
            string sourceSeam,
            string phase,
            DateTimeOffset generatedAtUtc,
            string actionUrlKind,
            DateTimeOffset? actionTokenExpiresAtUtc,
            string? evidenceUrlKind = null,
            DateTimeOffset? evidenceTokenExpiresAtUtc = null,
            IEnumerable<ReconciliationNotificationField>? fields = null,
            IEnumerable<ReconciliationNotificationLink>? evidenceLinks = null,
            IEnumerable<ReconciliationNotificationAction>? actions = null,
            IEnumerable<ReconciliationNotificationCandidate>? candidates = null,
            string? refusalContext = null,
            string? diagnosticSummary = null)
        {
            CaseId = NormalizeRequired(caseId, nameof(caseId));
            Title = NormalizeRequired(title, nameof(title));
            Summary = NormalizeRequired(summary, nameof(summary));
            Classification = NormalizeRequired(classification, nameof(classification));
            SourceSeam = NormalizeRequired(sourceSeam, nameof(sourceSeam));
            Phase = NormalizeRequired(phase, nameof(phase));
            GeneratedAtUtc = generatedAtUtc == default ? DateTimeOffset.UtcNow : generatedAtUtc;
            ActionUrlKind = NormalizeRequired(actionUrlKind, nameof(actionUrlKind));
            ActionTokenExpiresAtUtc = actionTokenExpiresAtUtc;
            EvidenceUrlKind = NormalizeOptional(evidenceUrlKind);
            EvidenceTokenExpiresAtUtc = evidenceTokenExpiresAtUtc;
            RefusalContext = NormalizeOptional(refusalContext);
            DiagnosticSummary = NormalizeOptional(diagnosticSummary);
            Fields = NormalizeList(fields, static field => field.Normalize()).ToArray();
            EvidenceLinks = NormalizeList(evidenceLinks, static link => link.Normalize()).ToArray();
            Actions = NormalizeList(actions, static action => action.Normalize()).ToArray();
            Candidates = NormalizeList(candidates, static candidate => candidate.Normalize()).ToArray();
        }

        public string CaseId { get; }

        public string Title { get; }

        public string Summary { get; }

        public string Classification { get; }

        public string SourceSeam { get; }

        public string Phase { get; }

        public DateTimeOffset GeneratedAtUtc { get; }

        public string ActionUrlKind { get; }

        public DateTimeOffset? ActionTokenExpiresAtUtc { get; }

        public string? EvidenceUrlKind { get; }

        public DateTimeOffset? EvidenceTokenExpiresAtUtc { get; }

        public string? RefusalContext { get; }

        public string? DiagnosticSummary { get; }

        public IReadOnlyList<ReconciliationNotificationField> Fields { get; }

        public IReadOnlyList<ReconciliationNotificationLink> EvidenceLinks { get; }

        public IReadOnlyList<ReconciliationNotificationAction> Actions { get; }

        public IReadOnlyList<ReconciliationNotificationCandidate> Candidates { get; }

        public string BuildPlainTextBody()
        {
            var builder = new StringBuilder();
            builder.AppendLine(Summary);
            builder.AppendLine($"Case: {CaseId}");
            builder.AppendLine($"Classification: {Classification}");
            builder.AppendLine($"Source: {SourceSeam}");
            builder.AppendLine($"Phase: {Phase}");

            if (RefusalContext != null)
            {
                builder.AppendLine();
                builder.AppendLine($"Refusal: {RefusalContext}");
            }

            if (Fields.Count > 0)
            {
                builder.AppendLine();
                builder.AppendLine("Facts:");
                foreach (var field in Fields)
                {
                    builder.AppendLine($"- {field.Name}: {field.Value}");
                }
            }

            if (Candidates.Count > 0)
            {
                builder.AppendLine();
                builder.AppendLine("Top candidates:");
                foreach (var candidate in Candidates)
                {
                    builder.AppendLine($"- #{candidate.Rank} {candidate.ArtistName} - {candidate.ReleaseTitle} ({candidate.ReleaseMusicBrainzId})");
                }
            }

            if (EvidenceLinks.Count > 0)
            {
                builder.AppendLine();
                builder.AppendLine("Links:");
                foreach (var link in EvidenceLinks)
                {
                    builder.AppendLine($"- {link.Label}: {link.Url}");
                }
            }

            if (Actions.Count > 0)
            {
                builder.AppendLine();
                builder.AppendLine("Actions:");
                foreach (var action in Actions)
                {
                    builder.AppendLine($"- {action.Label}: {action.Url}");
                }
            }

            if (DiagnosticSummary != null)
            {
                builder.AppendLine();
                builder.AppendLine($"Diagnostics: {DiagnosticSummary}");
            }

            return builder.ToString().Trim();
        }

        private static IReadOnlyList<TOutput> NormalizeList<TInput, TOutput>(IEnumerable<TInput>? values, Func<TInput, TOutput> projector)
            where TInput : class
            where TOutput : class
        {
            return (values ?? Array.Empty<TInput>())
                .Where(static value => value != null)
                .Select(projector)
                .ToArray();
        }

        private static string NormalizeRequired(string value, string paramName)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException("Value is required.", paramName);
            }

            return value.Trim();
        }

        private static string? NormalizeOptional(string? value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
    }

    public sealed class ReconciliationNotificationField
    {
        public string Name { get; set; } = string.Empty;

        public string Value { get; set; } = string.Empty;

        public ReconciliationNotificationField Normalize()
        {
            return new ReconciliationNotificationField
            {
                Name = NormalizeRequired(Name, nameof(Name)),
                Value = NormalizeRequired(Value, nameof(Value))
            };
        }

        private static string NormalizeRequired(string value, string paramName)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException("Value is required.", paramName);
            }

            return value.Trim();
        }
    }

    public sealed class ReconciliationNotificationLink
    {
        public string Label { get; set; } = string.Empty;

        public string Url { get; set; } = string.Empty;

        public ReconciliationNotificationLink Normalize()
        {
            return new ReconciliationNotificationLink
            {
                Label = NormalizeRequired(Label, nameof(Label)),
                Url = NormalizeRequired(Url, nameof(Url))
            };
        }

        private static string NormalizeRequired(string value, string paramName)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException("Value is required.", paramName);
            }

            return value.Trim();
        }
    }

    public sealed class ReconciliationNotificationAction
    {
        public string Action { get; set; } = string.Empty;

        public string Label { get; set; } = string.Empty;

        public string Url { get; set; } = string.Empty;

        public ReconciliationNotificationAction Normalize()
        {
            return new ReconciliationNotificationAction
            {
                Action = NormalizeRequired(Action, nameof(Action)),
                Label = NormalizeRequired(Label, nameof(Label)),
                Url = NormalizeRequired(Url, nameof(Url))
            };
        }

        private static string NormalizeRequired(string value, string paramName)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException("Value is required.", paramName);
            }

            return value.Trim();
        }
    }

    public sealed class ReconciliationNotificationCandidate
    {
        public int Rank { get; set; }

        public string ArtistName { get; set; } = string.Empty;

        public string ReleaseTitle { get; set; } = string.Empty;

        public string ReleaseMusicBrainzId { get; set; } = string.Empty;

        public bool SameReleaseGroup { get; set; }

        public bool HasStrongIdentitySignal { get; set; }

        public bool TitleOnlyCandidate { get; set; }

        public IReadOnlyList<string> LookupPaths { get; set; } = Array.Empty<string>();

        public ReconciliationNotificationCandidate Normalize()
        {
            return new ReconciliationNotificationCandidate
            {
                Rank = Rank,
                ArtistName = NormalizeRequired(ArtistName, nameof(ArtistName)),
                ReleaseTitle = NormalizeRequired(ReleaseTitle, nameof(ReleaseTitle)),
                ReleaseMusicBrainzId = NormalizeRequired(ReleaseMusicBrainzId, nameof(ReleaseMusicBrainzId)),
                SameReleaseGroup = SameReleaseGroup,
                HasStrongIdentitySignal = HasStrongIdentitySignal,
                TitleOnlyCandidate = TitleOnlyCandidate,
                LookupPaths = (LookupPaths ?? Array.Empty<string>())
                    .Where(static path => !string.IsNullOrWhiteSpace(path))
                    .Select(static path => path.Trim())
                    .Distinct(StringComparer.Ordinal)
                    .ToArray()
            };
        }

        private static string NormalizeRequired(string value, string paramName)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException("Value is required.", paramName);
            }

            return value.Trim();
        }
    }
}
