namespace NzbDrone.Core.Plugins.ReleaseReconciler.Capture
{
    public sealed class HostMusicBrainzContextResolution
    {
        public const string LocalReleaseAlbum = "lidarr-release-album";
        public const string MissingPreferredReleaseId = "missing-preferred-release-id";
        public const string ReleaseNotFound = "release-not-found";
        public const string AlbumNotFound = "album-not-found";
        public const string AlbumMissingForeignAlbumId = "album-missing-foreign-album-id";

        private HostMusicBrainzContextResolution(
            string? preferredReleaseMusicBrainzId,
            string? releaseMusicBrainzId,
            string? albumMusicBrainzId,
            string resolutionSource)
        {
            PreferredReleaseMusicBrainzId = preferredReleaseMusicBrainzId;
            ReleaseMusicBrainzId = releaseMusicBrainzId;
            AlbumMusicBrainzId = albumMusicBrainzId;
            ResolutionSource = resolutionSource;
        }

        public string? PreferredReleaseMusicBrainzId { get; }

        public string? ReleaseMusicBrainzId { get; }

        public string? AlbumMusicBrainzId { get; }

        public string ResolutionSource { get; }

        public bool MbContextResolved => MbReleaseIdPresent && MbAlbumIdPresent;

        public bool MbReleaseIdPresent => !string.IsNullOrWhiteSpace(ReleaseMusicBrainzId);

        public bool MbAlbumIdPresent => !string.IsNullOrWhiteSpace(AlbumMusicBrainzId);

        public static HostMusicBrainzContextResolution Resolved(
            string preferredReleaseMusicBrainzId,
            string releaseMusicBrainzId,
            string albumMusicBrainzId,
            string resolutionSource = LocalReleaseAlbum)
        {
            return new HostMusicBrainzContextResolution(
                preferredReleaseMusicBrainzId,
                releaseMusicBrainzId,
                albumMusicBrainzId,
                resolutionSource);
        }

        public static HostMusicBrainzContextResolution Unresolved(
            string? preferredReleaseMusicBrainzId,
            string? releaseMusicBrainzId,
            string resolutionSource)
        {
            return new HostMusicBrainzContextResolution(
                preferredReleaseMusicBrainzId,
                releaseMusicBrainzId,
                null,
                resolutionSource);
        }
    }
}
