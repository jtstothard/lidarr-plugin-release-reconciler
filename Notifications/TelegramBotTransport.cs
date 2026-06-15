using System;
using System.Web;
using NLog;
using NzbDrone.Common.Http;

namespace NzbDrone.Core.Plugins.ReleaseReconciler.Notifications
{
    public sealed class TelegramBotTransport : INotificationTransport
    {
        private const string ApiBaseUrl = "https://api.telegram.org";
        private readonly IHttpClient _httpClient;
        private readonly Logger _logger;

        public TelegramBotTransport(IHttpClient httpClient, Logger logger)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public string TransportName => "telegram";

        public bool IsEnabled(ReconciliationNotificationSettings settings)
        {
            ArgumentNullException.ThrowIfNull(settings);
            return settings.Normalize().Telegram.Enabled;
        }

        public bool TryValidate(ReconciliationNotificationSettings settings, out string? failureSummary)
        {
            ArgumentNullException.ThrowIfNull(settings);
            var normalized = settings.Normalize().Telegram;
            failureSummary = null;

            if (!normalized.Enabled)
            {
                return true;
            }

            if (string.IsNullOrWhiteSpace(normalized.BotToken))
            {
                failureSummary = "Telegram bot token is missing.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(normalized.ChatId))
            {
                failureSummary = "Telegram chat ID is missing.";
                return false;
            }

            if (normalized.TopicId.HasValue && normalized.TopicId.Value <= 1)
            {
                failureSummary = "Telegram topic ID must be greater than 1 when provided.";
                return false;
            }

            return true;
        }

        public void Send(ReconciliationNotificationEnvelope envelope, ReconciliationNotificationSettings settings)
        {
            ArgumentNullException.ThrowIfNull(envelope);
            ArgumentNullException.ThrowIfNull(settings);

            var transportSettings = settings.Normalize().Telegram;
            var text = $"<b>{HttpUtility.HtmlEncode(envelope.Title)}</b>\n{HttpUtility.HtmlEncode(envelope.BuildPlainTextBody())}";
            var requestBuilder = new HttpRequestBuilder(ApiBaseUrl).Resource("bot{token}/sendmessage").Post();
            var request = requestBuilder.SetSegment("token", transportSettings.BotToken!)
                .AddFormParameter("chat_id", transportSettings.ChatId!)
                .AddFormParameter("parse_mode", "HTML")
                .AddFormParameter("text", text);

            if (transportSettings.TopicId.HasValue)
            {
                requestBuilder.AddFormParameter("message_thread_id", transportSettings.TopicId.Value);
            }

            if (transportSettings.SendSilently)
            {
                requestBuilder.AddFormParameter("disable_notification", true);
            }

            _logger.Info("Dispatching release reconciler notification caseId={0} transport={1} actionUrlKind={2}.", envelope.CaseId, TransportName, envelope.ActionUrlKind);
            _httpClient.Post(request.Build());
        }
    }
}
