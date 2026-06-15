using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.Notifications.Discord.Payloads;

namespace NzbDrone.Core.Plugins.ReleaseReconciler.Notifications
{
    public sealed class DiscordWebhookTransport : INotificationTransport
    {
        private readonly IHttpClient _httpClient;
        private readonly Logger _logger;

        public DiscordWebhookTransport(IHttpClient httpClient, Logger logger)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public string TransportName => "discord";

        public bool IsEnabled(ReconciliationNotificationSettings settings)
        {
            ArgumentNullException.ThrowIfNull(settings);
            return settings.Normalize().Discord.Enabled;
        }

        public bool TryValidate(ReconciliationNotificationSettings settings, out string? failureSummary)
        {
            ArgumentNullException.ThrowIfNull(settings);
            var normalized = settings.Normalize().Discord;
            failureSummary = null;

            if (!normalized.Enabled)
            {
                return true;
            }

            if (string.IsNullOrWhiteSpace(normalized.WebHookUrl))
            {
                failureSummary = "Discord webhook URL is missing.";
                return false;
            }

            if (!Uri.TryCreate(normalized.WebHookUrl, UriKind.Absolute, out _))
            {
                failureSummary = "Discord webhook URL must be absolute.";
                return false;
            }

            return true;
        }

        public void Send(ReconciliationNotificationEnvelope envelope, ReconciliationNotificationSettings settings)
        {
            ArgumentNullException.ThrowIfNull(envelope);
            ArgumentNullException.ThrowIfNull(settings);

            var transportSettings = settings.Normalize().Discord;
            var payload = new DiscordPayload
            {
                Content = envelope.Summary,
                Username = transportSettings.Username,
                AvatarUrl = transportSettings.AvatarUrl,
                Embeds = new List<Embed>
                {
                    new()
                    {
                        Title = envelope.Title,
                        Description = BuildDescription(envelope),
                        Url = envelope.EvidenceLinks.FirstOrDefault()?.Url,
                        Timestamp = envelope.GeneratedAtUtc.UtcDateTime.ToString("O"),
                        Color = GetEmbedColor(envelope.Classification),
                        Fields = BuildFields(envelope)
                    }
                }
            };

            var request = new HttpRequestBuilder(transportSettings.WebHookUrl!).Post().Build();
            request.SetContent(payload.ToJson());
            _logger.Info("Dispatching release reconciler notification caseId={0} transport={1} actionUrlKind={2}.", envelope.CaseId, TransportName, envelope.ActionUrlKind);
            _httpClient.Post(request);
        }

        private static List<DiscordField> BuildFields(ReconciliationNotificationEnvelope envelope)
        {
            var fields = envelope.Fields
                .Take(6)
                .Select(field => new DiscordField
                {
                    Name = field.Name,
                    Value = field.Value,
                    Inline = field.Name is "Track Count" or "Lookup Attempts"
                })
                .ToList();

            if (envelope.Actions.Count > 0)
            {
                fields.Add(new DiscordField
                {
                    Name = "Actions",
                    Value = string.Join("\n", envelope.Actions.Select(static action => $"[{action.Label}]({action.Url})")),
                    Inline = false
                });
            }

            if (envelope.EvidenceLinks.Count > 0)
            {
                fields.Add(new DiscordField
                {
                    Name = "Evidence",
                    Value = string.Join("\n", envelope.EvidenceLinks.Select(static link => $"[{link.Label}]({link.Url})")),
                    Inline = false
                });
            }

            return fields;
        }

        private static string BuildDescription(ReconciliationNotificationEnvelope envelope)
        {
            var sections = new List<string>();
            if (!string.IsNullOrWhiteSpace(envelope.RefusalContext))
            {
                sections.Add(envelope.RefusalContext!);
            }

            if (envelope.Candidates.Count > 0)
            {
                sections.Add("Top candidates: " + string.Join(" | ", envelope.Candidates.Select(static candidate => $"#{candidate.Rank} {candidate.ArtistName} - {candidate.ReleaseTitle}")));
            }

            if (!string.IsNullOrWhiteSpace(envelope.DiagnosticSummary))
            {
                sections.Add(envelope.DiagnosticSummary!);
            }

            return string.Join("\n\n", sections.Where(static section => !string.IsNullOrWhiteSpace(section)));
        }

        private static int GetEmbedColor(string classification)
        {
            return classification switch
            {
                "Better existing release already present" => 0x2ECC71,
                "Missing release in current group" => 0xF1C40F,
                "Different release group candidate" => 0x3498DB,
                "No safe match" => 0xE74C3C,
                _ => 0x95A5A6
            };
        }
    }
}
