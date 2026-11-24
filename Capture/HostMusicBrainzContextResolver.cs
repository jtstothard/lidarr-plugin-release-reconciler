using System;
using NzbDrone.Core.Music;

namespace NzbDrone.Core.Plugins.ReleaseReconciler.Capture
{
    public sealed class HostMusicBrainzContextResolver : IHostMusicBrainzContextResolver
    {
        private readonly IAlbumService _albumService;
        private readonly IReleaseService _releaseService;

        public HostMusicBrainzContextResolver(IReleaseService releaseService, IAlbumService albumService)
        {
            _releaseService = releaseService ?? throw new ArgumentNullException(nameof(releaseService));
            _albumService = albumService ?? throw new ArgumentNullException(nameof(albumService));
        }

        public HostMusicBrainzContextResolution Resolve(string? preferredReleaseMusicBrainzId)
        {
            var normalizedPreferredReleaseMusicBrainzId = NormalizeIdentifier(preferredReleaseMusicBrainzId);
            if (normalizedPreferredReleaseMusicBrainzId is null)
            {
                return HostMusicBrainzContextResolution.Unresolved(
                    null,
                    null,
                    HostMusicBrainzContextResolution.MissingPreferredReleaseId);
            }

            var release = _releaseService.GetReleaseByForeignReleaseId(normalizedPreferredReleaseMusicBrainzId);
            if (release is null)
            {
                return HostMusicBrainzContextResolution.Unresolved(
                    normalizedPreferredReleaseMusicBrainzId,
                    normalizedPreferredReleaseMusicBrainzId,
                    HostMusicBrainzContextResolution.ReleaseNotFound);
            }

            var normalizedReleaseMusicBrainzId = NormalizeIdentifier(release.ForeignReleaseId) ?? normalizedPreferredReleaseMusicBrainzId;
            var album = _albumService.GetAlbum(release.AlbumId);
            if (album is null)
            {
                return HostMusicBrainzContextResolution.Unresolved(
                    normalizedPreferredReleaseMusicBrainzId,
                    normalizedReleaseMusicBrainzId,
                    HostMusicBrainzContextResolution.AlbumNotFound);
            }

            var normalizedAlbumMusicBrainzId = NormalizeIdentifier(album.ForeignAlbumId);
            if (normalizedAlbumMusicBrainzId is null)
            {
                return HostMusicBrainzContextResolution.Unresolved(
                    normalizedPreferredReleaseMusicBrainzId,
                    normalizedReleaseMusicBrainzId,
                    HostMusicBrainzContextResolution.AlbumMissingForeignAlbumId);
            }

            return HostMusicBrainzContextResolution.Resolved(
                normalizedPreferredReleaseMusicBrainzId,
                normalizedReleaseMusicBrainzId,
                normalizedAlbumMusicBrainzId);
        }

        private static string? NormalizeIdentifier(string? identifier)
        {
            return string.IsNullOrWhiteSpace(identifier) ? null : identifier.Trim();
        }
    }
}
