namespace NzbDrone.Core.Plugins.ReleaseReconciler.Notifications
{
    public interface INotificationTransport
    {
        string TransportName { get; }

        bool IsEnabled(ReconciliationNotificationSettings settings);

        bool TryValidate(ReconciliationNotificationSettings settings, out string? failureSummary);

        void Send(ReconciliationNotificationEnvelope envelope, ReconciliationNotificationSettings settings);
    }
}
