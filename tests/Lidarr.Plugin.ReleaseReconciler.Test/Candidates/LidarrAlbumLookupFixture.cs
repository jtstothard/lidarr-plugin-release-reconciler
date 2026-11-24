using System;
using System.Collections.Generic;
using System.Linq;
using Moq;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.Music;
using NzbDrone.Core.Plugins.ReleaseReconciler.Candidates;
using Xunit;

namespace NzbDrone.Core.Plugins.ReleaseReconciler.Test.Candidates
{
    public sealed class LidarrAlbumLookupFixture
    {
        [Fact]
        public void should_prefer_recording_id_lookup_and_skip_title_artist_fallback_when_structural_results_exist()
        {
            var search = new Mock<ISearchForNewAlbum>(MockBehavior.Strict);
            var albumInfo = new Mock<IProvideAlbumInfo>(MockBehavior.Strict);
            var evidence = new ReconciliationEvidence
            {
                Tracks = new List<ReconciliationTrackEvidence>
                {
                    new() { RecordingMusicBrainzId = " recording-1 " },
                    new() { RecordingMusicBrainzId = "recording-1" },
                    new() { RecordingMusicBrainzId = "recording-2" }
                }
            };

            search
                .Setup(service => service.SearchForNewAlbumByRecordingIds(It.Is<List<string>>(ids =>
                    ids.SequenceEqual(new[] { "recording-1", "recording-2" }))))
                .Returns(new List<Album>
                {
                    CreateAlbum("album-1", "Album One", "artist-1", "Artist One")
                });

            var subject = new LidarrAlbumLookup(search.Object, albumInfo.Object);

            var result = subject.LookupCandidates("Album One", "Artist One", evidence);

            var candidate = Assert.Single(result.Candidates);
            Assert.Equal("album-1", candidate.AlbumMusicBrainzId);
            Assert.Equal(new[] { CandidateLookupPath.RecordingIds }, candidate.LookupPaths);
            Assert.False(result.UsedTitleArtistFallback);
            Assert.Equal("Structural lookup already produced deterministic candidates.", result.Attempts.Single(attempt => attempt.Path == CandidateLookupPath.TitleArtistFallback).Reason);
            search.Verify(service => service.SearchForNewAlbumByRecordingIds(It.IsAny<List<string>>()), Times.Once);
            search.Verify(service => service.SearchForNewAlbum(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
            albumInfo.Verify(service => service.GetAlbumInfo(It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public void should_hydrate_current_release_group_when_album_musicbrainz_id_is_present()
        {
            var search = new Mock<ISearchForNewAlbum>(MockBehavior.Strict);
            var albumInfo = new Mock<IProvideAlbumInfo>(MockBehavior.Strict);
            var evidence = new ReconciliationEvidence();
            var hydratedAlbum = CreateAlbum(
                "album-2",
                "Album Two",
                "artist-2",
                "Artist Two",
                releases: new[]
                {
                    CreateRelease(
                        "release-2",
                        "Album Two",
                        tracks: new[]
                        {
                            CreateTrack("track-2", "recording-2", "Track Two", 1, "1")
                        })
                });

            albumInfo
                .Setup(service => service.GetAlbumInfo("album-2"))
                .Returns(Tuple.Create("artist-2", hydratedAlbum, new List<ArtistMetadata>()));

            var subject = new LidarrAlbumLookup(search.Object, albumInfo.Object);

            var result = subject.LookupCandidates("Album Two", "Artist Two", evidence, "album-2");

            var candidate = Assert.Single(result.Candidates);
            Assert.Equal("artist-2", candidate.ArtistMusicBrainzId);
            Assert.Equal("Artist Two", candidate.ArtistName);
            Assert.Equal(new[] { CandidateLookupPath.CurrentReleaseGroup }, candidate.LookupPaths);
            Assert.Single(candidate.Releases);
            Assert.Single(candidate.Releases[0].Tracks);
            albumInfo.Verify(service => service.GetAlbumInfo("album-2"), Times.Once);
            search.Verify(service => service.SearchForNewAlbumByRecordingIds(It.IsAny<List<string>>()), Times.Never);
            search.Verify(service => service.SearchForNewAlbum(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public void should_dedupe_repeated_albums_returned_from_multiple_lookup_paths()
        {
            var search = new Mock<ISearchForNewAlbum>(MockBehavior.Strict);
            var albumInfo = new Mock<IProvideAlbumInfo>(MockBehavior.Strict);
            var evidence = new ReconciliationEvidence
            {
                Tracks = new List<ReconciliationTrackEvidence>
                {
                    new() { RecordingMusicBrainzId = "recording-3" }
                }
            };
            var recordingCandidate = CreateAlbum("album-3", "Album Three", "artist-3", "Artist Three");
            var hydratedCandidate = CreateAlbum("album-3", "Album Three", "artist-3", "Artist Three");

            search
                .Setup(service => service.SearchForNewAlbumByRecordingIds(It.IsAny<List<string>>()))
                .Returns(new List<Album> { recordingCandidate });

            albumInfo
                .Setup(service => service.GetAlbumInfo("album-3"))
                .Returns(Tuple.Create("artist-3", hydratedCandidate, new List<ArtistMetadata>()));

            var subject = new LidarrAlbumLookup(search.Object, albumInfo.Object);

            var result = subject.LookupCandidates("Album Three", "Artist Three", evidence, "album-3");

            var candidate = Assert.Single(result.Candidates);
            Assert.Equal(new[] { CandidateLookupPath.RecordingIds, CandidateLookupPath.CurrentReleaseGroup }, candidate.LookupPaths.OrderBy(static path => path).ToArray());
            var currentGroupAttempt = result.Attempts.Single(attempt => attempt.Path == CandidateLookupPath.CurrentReleaseGroup);
            Assert.Equal(1, currentGroupAttempt.DeduplicatedCandidateCount);
            Assert.Equal(0, currentGroupAttempt.AddedCandidateCount);
            search.Verify(service => service.SearchForNewAlbum(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public void should_use_title_artist_fallback_when_recording_ids_are_absent()
        {
            var search = new Mock<ISearchForNewAlbum>(MockBehavior.Strict);
            var albumInfo = new Mock<IProvideAlbumInfo>(MockBehavior.Strict);
            var evidence = new ReconciliationEvidence
            {
                Tracks = new List<ReconciliationTrackEvidence>
                {
                    new() { RecordingMusicBrainzId = "  " },
                    new() { RecordingMusicBrainzId = null }
                }
            };

            search
                .Setup(service => service.SearchForNewAlbum("Album Four", "Artist Four"))
                .Returns(new List<Album>
                {
                    CreateAlbum("album-4", "Album Four", "artist-4", "Artist Four")
                });

            var subject = new LidarrAlbumLookup(search.Object, albumInfo.Object);

            var result = subject.LookupCandidates("Album Four", "Artist Four", evidence);

            var candidate = Assert.Single(result.Candidates);
            Assert.Equal("album-4", candidate.AlbumMusicBrainzId);
            Assert.True(result.UsedTitleArtistFallback);
            Assert.Equal("No recording MusicBrainz IDs were available.", result.Attempts.Single(attempt => attempt.Path == CandidateLookupPath.RecordingIds).Reason);
            search.Verify(service => service.SearchForNewAlbum("Album Four", "Artist Four"), Times.Once);
            search.Verify(service => service.SearchForNewAlbumByRecordingIds(It.IsAny<List<string>>()), Times.Never);
            albumInfo.Verify(service => service.GetAlbumInfo(It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public void should_use_title_artist_fallback_when_recording_lookup_is_exhausted()
        {
            var search = new Mock<ISearchForNewAlbum>(MockBehavior.Strict);
            var albumInfo = new Mock<IProvideAlbumInfo>(MockBehavior.Strict);
            var evidence = new ReconciliationEvidence
            {
                Tracks = new List<ReconciliationTrackEvidence>
                {
                    new() { RecordingMusicBrainzId = "recording-5" }
                }
            };

            search
                .Setup(service => service.SearchForNewAlbumByRecordingIds(It.IsAny<List<string>>()))
                .Returns(new List<Album>());
            search
                .Setup(service => service.SearchForNewAlbum("Album Five", "Artist Five"))
                .Returns(new List<Album>
                {
                    CreateAlbum("album-5", "Album Five", "artist-5", "Artist Five")
                });

            var subject = new LidarrAlbumLookup(search.Object, albumInfo.Object);

            var result = subject.LookupCandidates("Album Five", "Artist Five", evidence);

            var candidate = Assert.Single(result.Candidates);
            Assert.Equal("album-5", candidate.AlbumMusicBrainzId);
            Assert.True(result.UsedTitleArtistFallback);
            Assert.Equal("Lookup completed without returning any candidate albums.", result.Attempts.Single(attempt => attempt.Path == CandidateLookupPath.RecordingIds).Reason);
            search.Verify(service => service.SearchForNewAlbumByRecordingIds(It.IsAny<List<string>>()), Times.Once);
            search.Verify(service => service.SearchForNewAlbum("Album Five", "Artist Five"), Times.Once);
        }

        private static Album CreateAlbum(
            string albumId,
            string title,
            string artistId,
            string artistName,
            IEnumerable<AlbumRelease>? releases = null)
        {
            return new Album
            {
                ForeignAlbumId = albumId,
                Title = title,
                ArtistMetadata = new LazyLoaded<ArtistMetadata>(new ArtistMetadata
                {
                    ForeignArtistId = artistId,
                    Name = artistName
                }),
                AlbumReleases = new LazyLoaded<List<AlbumRelease>>((releases ?? Array.Empty<AlbumRelease>()).ToList())
            };
        }

        private static AlbumRelease CreateRelease(
            string releaseId,
            string title,
            IEnumerable<Track>? tracks = null)
        {
            return new AlbumRelease
            {
                ForeignReleaseId = releaseId,
                Title = title,
                Media = new List<Medium>
                {
                    new() { Number = 1, Name = "Disc 1", Format = "Digital Media" }
                },
                Tracks = new LazyLoaded<List<Track>>((tracks ?? Array.Empty<Track>()).ToList())
            };
        }

        private static Track CreateTrack(string trackId, string recordingId, string title, int mediumNumber, string trackNumber)
        {
            return new Track
            {
                ForeignTrackId = trackId,
                ForeignRecordingId = recordingId,
                Title = title,
                MediumNumber = mediumNumber,
                TrackNumber = trackNumber,
                AbsoluteTrackNumber = int.Parse(trackNumber)
            };
        }
    }
}
