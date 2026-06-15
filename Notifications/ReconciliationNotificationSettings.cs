using System;
using System.Collections.Generic;
using System.Linq;

namespace NzbDrone.Core.Plugins.ReleaseReconciler.Notifications
{
    public sealed class ReconciliationNotificationSettings
    {
        public string? PublicBaseUrl { get; set; }

        public string? OperatorActionPath { get; set; } = "/release-reconciler/action/index.html";

        public string? EvidencePagePath { get; set; } = "/release-reconciler/evidence/index.html";

        public string? HarmonySearchBaseUrl { get; set; } = "https://api.lidarr.audio/api/v0.4/search";

        public int ActionTokenLifetimeMinutes { get; set; } = 240;

        public int EvidenceTokenLifetimeMinutes { get; set; } = 240;

        public DiscordNotificationSettings Discord { get; set; } = new();

        public TelegramNotificationSettings Telegram { get; set; } = new();

        public ReconciliationNotificationSettings Normalize()
        {
            return new ReconciliationNotificationSettings
            {
                PublicBaseUrl = NormalizeOptionalValue(PublicBaseUrl),
                OperatorActionPath = NormalizePath(OperatorActionPath, "/release-reconciler/action/index.html"),
                EvidencePagePath = NormalizePath(EvidencePagePath, "/release-reconciler/evidence/index.html"),
                HarmonySearchBaseUrl = NormalizeOptionalValue(HarmonySearchBaseUrl),
                ActionTokenLifetimeMinutes = Math.Clamp(ActionTokenLifetimeMinutes <= 0 ? 240 : ActionTokenLifetimeMinutes, 5, 7 * 24 * 60),
                EvidenceTokenLifetimeMinutes = Math.Clamp(EvidenceTokenLifetimeMinutes <= 0 ? 240 : EvidenceTokenLifetimeMinutes, 5, 7 * 24 * 60),
                Discord = (Discord ?? new DiscordNotificationSettings()).Normalize(),
                Telegram = (Telegram ?? new TelegramNotificationSettings()).Normalize()
            };
        }

        public IReadOnlyList<string> Validate()
        {
            var normalized = Normalize();
            var errors = new List<string>();

            if (normalized.PublicBaseUrl != null && !Uri.TryCreate(normalized.PublicBaseUrl, UriKind.Absolute, out _))
            {
                errors.Add("PublicBaseUrl must be an absolute URL when provided.");
            }

            if (normalized.HarmonySearchBaseUrl != null && !Uri.TryCreate(normalized.HarmonySearchBaseUrl, UriKind.Absolute, out _))
            {
                errors.Add("HarmonySearchBaseUrl must be an absolute URL when provided.");
            }

            errors.AddRange(normalized.Discord.Validate());
            errors.AddRange(normalized.Telegram.Validate());
            return errors;
        }

        public Uri GetPublicBaseUri()
        {
            var candidate = Normalize().PublicBaseUrl;
            return Uri.TryCreate(candidate, UriKind.Absolute, out var parsed)
                ? parsed
                : new Uri("https://lidarr.invalid", UriKind.Absolute);
        }

        public sealed class DiscordNotificationSettings
        {
            public bool Enabled { get; set; }

            public string? WebHookUrl { get; set; }

            public string? Username { get; set; } = "Release Reconciler";

            public string? AvatarUrl { get; set; }

            public DiscordNotificationSettings Normalize()
            {
                return new DiscordNotificationSettings
                {
                    Enabled = Enabled,
                    WebHookUrl = NormalizeOptionalValue(WebHookUrl),
                    Username = NormalizeOptionalValue(Username) ?? "Release Reconciler",
                    AvatarUrl = NormalizeOptionalValue(AvatarUrl)
                };
            }

            public IReadOnlyList<string> Validate()
            {
                var errors = new List<string>();
                var normalized = Normalize();

                if (!normalized.Enabled)
                {
                    return errors;
                }

                if (normalized.WebHookUrl == null)
                {
                    errors.Add("Discord.WebHookUrl is required when Discord notifications are enabled.");
                }
                else if (!Uri.TryCreate(normalized.WebHookUrl, UriKind.Absolute, out _))
                {
                    errors.Add("Discord.WebHookUrl must be an absolute URL when Discord notifications are enabled.");
                }

                if (normalized.AvatarUrl != null && !Uri.TryCreate(normalized.AvatarUrl, UriKind.Absolute, out _))
                {
                    errors.Add("Discord.AvatarUrl must be an absolute URL when provided.");
                }

                return errors;
            }
        }

        public sealed class TelegramNotificationSettings
        {
            public bool Enabled { get; set; }

            public string? BotToken { get; set; }

            public string? ChatId { get; set; }

            public int? TopicId { get; set; }

            public bool SendSilently { get; set; }

            public TelegramNotificationSettings Normalize()
            {
                return new TelegramNotificationSettings
                {
                    Enabled = Enabled,
                    BotToken = NormalizeOptionalValue(BotToken),
                    ChatId = NormalizeOptionalValue(ChatId),
                    TopicId = TopicId,
                    SendSilently = SendSilently
                };
            }

            public IReadOnlyList<string> Validate()
            {
                var errors = new List<string>();
                var normalized = Normalize();

                if (!normalized.Enabled)
                {
                    return errors;
                }

                if (normalized.BotToken == null)
                {
                    errors.Add("Telegram.BotToken is required when Telegram notifications are enabled.");
                }

                if (normalized.ChatId == null)
                {
                    errors.Add("Telegram.ChatId is required when Telegram notifications are enabled.");
                }

                if (normalized.TopicId.HasValue && normalized.TopicId.Value <= 1)
                {
                    errors.Add("Telegram.TopicId must be greater than 1 when provided.");
                }

                return errors;
            }
        }

        private static string? NormalizeOptionalValue(string? value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        private static string NormalizePath(string? value, string fallback)
        {
            var normalized = NormalizeOptionalValue(value) ?? fallback;
            if (!normalized.StartsWith("/", StringComparison.Ordinal))
            {
                normalized = "/" + normalized;
            }

            normalized = normalized.TrimEnd('/');

            if (string.Equals(normalized, "/content/release-reconciler/action", StringComparison.OrdinalIgnoreCase))
            {
                return "/release-reconciler/action/index.html";
            }

            if (string.Equals(normalized, "/content/release-reconciler/evidence", StringComparison.OrdinalIgnoreCase))
            {
                return "/release-reconciler/evidence/index.html";
            }

            return normalized;
        }
    }
}
