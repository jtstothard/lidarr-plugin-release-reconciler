using System;
using NzbDrone.Core.Plugins.ReleaseReconciler.Cases;

namespace NzbDrone.Core.Plugins.ReleaseReconciler.Switching
{
    public sealed class ReleaseSwitchApprovalResult
    {
        public required ReconciliationCaseSnapshot Snapshot { get; init; }

        public required string Result { get; init; }

        public required string Message { get; init; }

        public required DateTimeOffset ProcessedAtUtc { get; init; }
    }
}
