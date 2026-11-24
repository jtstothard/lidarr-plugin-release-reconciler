using Moq;
using NzbDrone.Core.Music;
using NzbDrone.Core.Plugins.ReleaseReconciler.Capture;
using Xunit;

namespace NzbDrone.Core.Plugins.ReleaseReconciler.Test.Capture
{
    public sealed class HostMusicBrainzContextResolverFixture
    {
        [Fact]
        public void resolve_should_return_release_and_album_musicbrainz_context_when_lidarr_release_and_album_exist()
        {
            var releaseService = new Mock<IReleaseService>(MockBehavior.Strict);
            var albumService = new Mock<IAlbumService>(MockBehavior.Strict);

            releaseService
                .Setup(service => service.GetReleaseByForeignReleaseId("release-preferred"))
                .Returns(new AlbumRelease
                {
                    AlbumId = 17,
                    ForeignReleaseId = " release-preferred "
                });

            albumService
                .Setup(service => service.GetAlbum(17))
                .Returns(new Album
                {
                    Id = 17,
                    ForeignAlbumId = " album-current "
                });

            var subject = new HostMusicBrainzContextResolver(releaseService.Object, albumService.Object);

            var result = subject.Resolve(" release-preferred ");

            Assert.True(result.MbContextResolved);
            Assert.True(result.MbReleaseIdPresent);
            Assert.True(result.MbAlbumIdPresent);
            Assert.Equal("release-preferred", result.PreferredReleaseMusicBrainzId);
            Assert.Equal("release-preferred", result.ReleaseMusicBrainzId);
            Assert.Equal("album-current", result.AlbumMusicBrainzId);
            Assert.Equal(HostMusicBrainzContextResolution.LocalReleaseAlbum, result.ResolutionSource);
            releaseService.Verify(service => service.GetReleaseByForeignReleaseId("release-preferred"), Times.Once);
            albumService.Verify(service => service.GetAlbum(17), Times.Once);
            releaseService.VerifyNoOtherCalls();
            albumService.VerifyNoOtherCalls();
        }

        [Fact]
        public void resolve_should_short_circuit_blank_preferred_release_musicbrainz_id_without_host_lookups()
        {
            var releaseService = new Mock<IReleaseService>(MockBehavior.Strict);
            var albumService = new Mock<IAlbumService>(MockBehavior.Strict);
            var subject = new HostMusicBrainzContextResolver(releaseService.Object, albumService.Object);

            var result = subject.Resolve("  \t ");

            Assert.False(result.MbContextResolved);
            Assert.False(result.MbReleaseIdPresent);
            Assert.False(result.MbAlbumIdPresent);
            Assert.Null(result.PreferredReleaseMusicBrainzId);
            Assert.Null(result.ReleaseMusicBrainzId);
            Assert.Null(result.AlbumMusicBrainzId);
            Assert.Equal(HostMusicBrainzContextResolution.MissingPreferredReleaseId, result.ResolutionSource);
            releaseService.VerifyNoOtherCalls();
            albumService.VerifyNoOtherCalls();
        }

        [Fact]
        public void resolve_should_return_unresolved_result_when_preferred_release_is_missing_locally()
        {
            var releaseService = new Mock<IReleaseService>(MockBehavior.Strict);
            var albumService = new Mock<IAlbumService>(MockBehavior.Strict);

            releaseService
                .Setup(service => service.GetReleaseByForeignReleaseId("release-preferred"))
                .Returns((AlbumRelease?)null);

            var subject = new HostMusicBrainzContextResolver(releaseService.Object, albumService.Object);

            var result = subject.Resolve("release-preferred");

            Assert.False(result.MbContextResolved);
            Assert.True(result.MbReleaseIdPresent);
            Assert.False(result.MbAlbumIdPresent);
            Assert.Equal("release-preferred", result.PreferredReleaseMusicBrainzId);
            Assert.Equal("release-preferred", result.ReleaseMusicBrainzId);
            Assert.Null(result.AlbumMusicBrainzId);
            Assert.Equal(HostMusicBrainzContextResolution.ReleaseNotFound, result.ResolutionSource);
            releaseService.Verify(service => service.GetReleaseByForeignReleaseId("release-preferred"), Times.Once);
            releaseService.VerifyNoOtherCalls();
            albumService.VerifyNoOtherCalls();
        }

        [Fact]
        public void resolve_should_return_unresolved_result_when_release_album_is_missing_locally()
        {
            var releaseService = new Mock<IReleaseService>(MockBehavior.Strict);
            var albumService = new Mock<IAlbumService>(MockBehavior.Strict);

            releaseService
                .Setup(service => service.GetReleaseByForeignReleaseId("release-preferred"))
                .Returns(new AlbumRelease
                {
                    AlbumId = 17,
                    ForeignReleaseId = "release-preferred"
                });

            albumService
                .Setup(service => service.GetAlbum(17))
                .Returns((Album?)null);

            var subject = new HostMusicBrainzContextResolver(releaseService.Object, albumService.Object);

            var result = subject.Resolve("release-preferred");

            Assert.False(result.MbContextResolved);
            Assert.True(result.MbReleaseIdPresent);
            Assert.False(result.MbAlbumIdPresent);
            Assert.Equal("release-preferred", result.PreferredReleaseMusicBrainzId);
            Assert.Equal("release-preferred", result.ReleaseMusicBrainzId);
            Assert.Null(result.AlbumMusicBrainzId);
            Assert.Equal(HostMusicBrainzContextResolution.AlbumNotFound, result.ResolutionSource);
            releaseService.Verify(service => service.GetReleaseByForeignReleaseId("release-preferred"), Times.Once);
            albumService.Verify(service => service.GetAlbum(17), Times.Once);
            releaseService.VerifyNoOtherCalls();
            albumService.VerifyNoOtherCalls();
        }

        [Fact]
        public void resolve_should_return_unresolved_result_when_release_album_has_blank_foreign_album_id()
        {
            var releaseService = new Mock<IReleaseService>(MockBehavior.Strict);
            var albumService = new Mock<IAlbumService>(MockBehavior.Strict);

            releaseService
                .Setup(service => service.GetReleaseByForeignReleaseId("release-preferred"))
                .Returns(new AlbumRelease
                {
                    AlbumId = 17,
                    ForeignReleaseId = "release-preferred"
                });

            albumService
                .Setup(service => service.GetAlbum(17))
                .Returns(new Album
                {
                    Id = 17,
                    ForeignAlbumId = "   "
                });

            var subject = new HostMusicBrainzContextResolver(releaseService.Object, albumService.Object);

            var result = subject.Resolve("release-preferred");

            Assert.False(result.MbContextResolved);
            Assert.True(result.MbReleaseIdPresent);
            Assert.False(result.MbAlbumIdPresent);
            Assert.Equal("release-preferred", result.PreferredReleaseMusicBrainzId);
            Assert.Equal("release-preferred", result.ReleaseMusicBrainzId);
            Assert.Null(result.AlbumMusicBrainzId);
            Assert.Equal(HostMusicBrainzContextResolution.AlbumMissingForeignAlbumId, result.ResolutionSource);
            releaseService.Verify(service => service.GetReleaseByForeignReleaseId("release-preferred"), Times.Once);
            albumService.Verify(service => service.GetAlbum(17), Times.Once);
            releaseService.VerifyNoOtherCalls();
            albumService.VerifyNoOtherCalls();
        }
    }
}
