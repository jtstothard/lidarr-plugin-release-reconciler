using System;

namespace NzbDrone.Core.Plugins.ReleaseReconciler.Switching
{
    public sealed class ReleaseSwitchSettings
    {
        public bool EnableReleaseSwitchApproval { get; set; }

        public bool EnableDifferentGroupReleaseSwitchApproval { get; set; }

        public ReleaseSwitchSettings Normalize()
        {
            return new ReleaseSwitchSettings
            {
                EnableReleaseSwitchApproval = EnableReleaseSwitchApproval,
                EnableDifferentGroupReleaseSwitchApproval = EnableDifferentGroupReleaseSwitchApproval
            };
        }
    }
}
