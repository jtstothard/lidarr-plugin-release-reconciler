namespace NzbDrone.Core.Plugins.ReleaseReconciler.Capture
{
    public interface IHostMusicBrainzContextResolver
    {
        HostMusicBrainzContextResolution Resolve(string? preferredReleaseMusicBrainzId);
    }
}
