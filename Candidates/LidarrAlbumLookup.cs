using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.Music;

namespace NzbDrone.Core.Plugins.ReleaseReconciler.Candidates
{
    public sealed class LidarrAlbumLookup : ILidarrAlbumLookup
    {
        private readonly IProvideAlbumInfo _albumInfo;
        private readonly ISearchForNewAlbum _searchForNewAlbum;

        public LidarrAlbumLookup(ISearchForNewAlbum searchForNewAlbum, IProvideAlbumInfo albumInfo)
        {
            _searchForNewAlbum = searchForNewAlbum ?? throw new ArgumentNullException(nameof(searchForNewAlbum));
            _albumInfo = albumInfo ?? throw new ArgumentNullException(nameof(albumInfo));
        }

        public LidarrAlbumLookupResult LookupCandidates(
            string albumTitle,
            string artistName,
            ReconciliationEvidence structuralEvidence,
            string? albumMusicBrainzId = null)
        {
            if (string.IsNullOrWhiteSpace(albumTitle))
            {
                throw new ArgumentException("Value cannot be null or whitespace.", nameof(albumTitle));
            }

            if (string.IsNullOrWhiteSpace(artistName))
            {
                throw new ArgumentException("Value cannot be null or whitespace.", nameof(artistName));
            }

            structuralEvidence = structuralEvidence ?? throw new ArgumentNullException(nameof(structuralEvidence));

            var attempts = new List<LidarrAlbumLookupAttempt>();
            var candidateMap = new Dictionary<string, CandidateAccumulator>(StringComparer.OrdinalIgnoreCase);
            var normalizedRecordingIds = NormalizeValues(structuralEvidence.RecordingMusicBrainzIds);
            var normalizedAlbumMusicBrainzId = NormalizeValue(albumMusicBrainzId);

            if (normalizedRecordingIds.Count > 0)
            {
                var recordingCandidates = _searchForNewAlbum.SearchForNewAlbumByRecordingIds(normalizedRecordingIds.ToList()) ?? new List<Album>();
                attempts.Add(AddCandidates(candidateMap, CandidateLookupPath.RecordingIds, normalizedRecordingIds.Count, recordingCandidates, null));
            }
            else
            {
                attempts.Add(new LidarrAlbumLookupAttempt(
                    CandidateLookupPath.RecordingIds,
                    attempted: false,
                    inputCount: 0,
                    rawCandidateCount: 0,
                    addedCandidateCount: 0,
                    deduplicatedCandidateCount: 0,
                    reason: "No recording MusicBrainz IDs were available."));
            }

            if (normalizedAlbumMusicBrainzId is not null)
            {
                var albumInfo = _albumInfo.GetAlbumInfo(normalizedAlbumMusicBrainzId);
                var hydratedAlbum = albumInfo?.Item2 is null ? Array.Empty<Album>() : new[] { albumInfo.Item2 };
                attempts.Add(AddCandidates(candidateMap, CandidateLookupPath.CurrentReleaseGroup, 1, hydratedAlbum, albumInfo?.Item3));
            }
            else
            {
                attempts.Add(new LidarrAlbumLookupAttempt(
                    CandidateLookupPath.CurrentReleaseGroup,
                    attempted: false,
                    inputCount: 0,
                    rawCandidateCount: 0,
                    addedCandidateCount: 0,
                    deduplicatedCandidateCount: 0,
                    reason: "No album MusicBrainz ID was available for current-group hydration."));
            }

            if (candidateMap.Count == 0)
            {
                var titleArtistCandidates = _searchForNewAlbum.SearchForNewAlbum(albumTitle.Trim(), artistName.Trim()) ?? new List<Album>();
                attempts.Add(AddCandidates(candidateMap, CandidateLookupPath.TitleArtistFallback, 1, titleArtistCandidates, null));
            }
            else
            {
                attempts.Add(new LidarrAlbumLookupAttempt(
                    CandidateLookupPath.TitleArtistFallback,
                    attempted: false,
                    inputCount: 0,
                    rawCandidateCount: 0,
                    addedCandidateCount: 0,
                    deduplicatedCandidateCount: 0,
                    reason: "Structural lookup already produced deterministic candidates."));
            }

            var candidates = candidateMap.Values
                .OrderBy(static candidate => candidate.Order)
                .Select(static candidate => candidate.Snapshot)
                .ToArray();

            return new LidarrAlbumLookupResult(candidates, attempts);
        }

        private static LidarrAlbumLookupAttempt AddCandidates(
            IDictionary<string, CandidateAccumulator> candidateMap,
            CandidateLookupPath path,
            int inputCount,
            IEnumerable<Album> albums,
            IReadOnlyList<ArtistMetadata>? hydratedArtists)
        {
            var rawCandidateCount = 0;
            var addedCandidateCount = 0;
            var deduplicatedCandidateCount = 0;

            foreach (var album in albums.Where(static candidate => candidate is not null))
            {
                rawCandidateCount++;
                var snapshot = ProjectAlbum(album, path, hydratedArtists);
                if (candidateMap.TryGetValue(snapshot.AlbumMusicBrainzId, out var existingCandidate))
                {
                    candidateMap[snapshot.AlbumMusicBrainzId] = existingCandidate.Merge(snapshot);
                    deduplicatedCandidateCount++;
                    continue;
                }

                candidateMap[snapshot.AlbumMusicBrainzId] = new CandidateAccumulator(candidateMap.Count, snapshot);
                addedCandidateCount++;
            }

            var reason = rawCandidateCount == 0
                ? "Lookup completed without returning any candidate albums."
                : null;

            return new LidarrAlbumLookupAttempt(
                path,
                attempted: true,
                inputCount,
                rawCandidateCount,
                addedCandidateCount,
                deduplicatedCandidateCount,
                reason);
        }

        private static CandidateAlbumSnapshot ProjectAlbum(Album album, CandidateLookupPath path, IReadOnlyList<ArtistMetadata>? hydratedArtists)
        {
            var albumId = NormalizeValue(album.ForeignAlbumId);
            if (albumId is null)
            {
                throw new InvalidOperationException("Host lookup returned an album without a MusicBrainz identifier.");
            }

            var artistMetadata = ResolveArtistMetadata(album, hydratedArtists);
            var artistName = NormalizeValue(artistMetadata?.Name)
                ?? throw new InvalidOperationException($"Host lookup returned album '{album.Title}' without an artist name.");

            var releases = ReadLazyValue(album.AlbumReleases)
                .Where(static release => release is not null)
                .Select(ProjectRelease)
                .OrderBy(static release => NormalizeValue(release.ReleaseMusicBrainzId), StringComparer.OrdinalIgnoreCase)
                .ThenBy(static release => release.ReleaseDate ?? DateTime.MaxValue)
                .ThenBy(static release => release.Title, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return new CandidateAlbumSnapshot(
                albumId,
                NormalizeValue(album.Title) ?? albumId,
                NormalizeValue(artistMetadata?.ForeignArtistId),
                artistName,
                NormalizeValue(album.Disambiguation),
                album.ReleaseDate,
                NormalizeValue(album.AlbumType),
                releases,
                new[] { path });
        }

        private static CandidateReleaseSnapshot ProjectRelease(AlbumRelease release)
        {
            var tracks = ReadLazyValue(release.Tracks)
                .Where(static track => track is not null)
                .Select(ProjectTrack)
                .OrderBy(static track => track.MediumNumber)
                .ThenBy(static track => NormalizeTrackNumber(track.TrackNumber))
                .ThenBy(static track => track.AbsoluteTrackNumber <= 0 ? int.MaxValue : track.AbsoluteTrackNumber)
                .ThenBy(static track => track.Title, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var media = (release.Media ?? new List<Medium>())
                .Where(static medium => medium is not null)
                .Select(static medium => new CandidateMediumSnapshot(
                    medium.Number,
                    NormalizeValue(medium.Name),
                    NormalizeValue(medium.Format)))
                .OrderBy(static medium => medium.Number)
                .ThenBy(static medium => medium.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return new CandidateReleaseSnapshot(
                NormalizeValue(release.ForeignReleaseId) ?? string.Empty,
                NormalizeValue(release.Title) ?? string.Empty,
                NormalizeValue(release.Status),
                NormalizeValue(release.Disambiguation),
                release.ReleaseDate,
                release.TrackCount,
                release.Duration,
                NormalizeValues(release.Label),
                NormalizeValues(release.Country),
                media,
                tracks);
        }

        private static CandidateTrackSnapshot ProjectTrack(Track track)
        {
            return new CandidateTrackSnapshot(
                NormalizeValue(track.ForeignTrackId) ?? string.Empty,
                NormalizeValue(track.ForeignRecordingId),
                NormalizeValue(track.Title) ?? string.Empty,
                NormalizeValue(track.TrackNumber),
                track.AbsoluteTrackNumber,
                track.MediumNumber,
                track.Duration,
                track.Explicit);
        }

        private static ArtistMetadata? ResolveArtistMetadata(Album album, IReadOnlyList<ArtistMetadata>? hydratedArtists)
        {
            var albumArtist = ReadLazyReference(album.ArtistMetadata);
            if (albumArtist is not null)
            {
                return albumArtist;
            }

            if (hydratedArtists is null || hydratedArtists.Count == 0)
            {
                return null;
            }

            return hydratedArtists[0];
        }

        private static IReadOnlyList<TChild> ReadLazyValue<TChild>(LazyLoaded<List<TChild>>? lazyLoaded)
        {
            return lazyLoaded is null ? Array.Empty<TChild>() : lazyLoaded.Value;
        }

        private static TChild? ReadLazyReference<TChild>(LazyLoaded<TChild>? lazyLoaded)
            where TChild : class
        {
            return lazyLoaded?.Value;
        }

        private static string? NormalizeValue(string? value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        private static IReadOnlyList<string> NormalizeValues(IEnumerable<string>? values)
        {
            return (values ?? Array.Empty<string>())
                .Select(NormalizeValue)
                .Where(static value => value is not null)
                .Select(static value => value!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static int NormalizeTrackNumber(string? value)
        {
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
                ? number
                : int.MaxValue;
        }

        private sealed record CandidateAccumulator(int Order, CandidateAlbumSnapshot Snapshot)
        {
            public CandidateAccumulator Merge(CandidateAlbumSnapshot incoming)
            {
                var mergedLookupPaths = Snapshot.LookupPaths
                    .Concat(incoming.LookupPaths)
                    .Distinct()
                    .OrderBy(static path => path)
                    .ToArray();

                var mergedReleases = Snapshot.Releases
                    .Concat(incoming.Releases)
                    .GroupBy(static release => release.ReleaseMusicBrainzId, StringComparer.OrdinalIgnoreCase)
                    .Select(static group => group.First())
                    .OrderBy(static release => NormalizeValue(release.ReleaseMusicBrainzId), StringComparer.OrdinalIgnoreCase)
                    .ThenBy(static release => release.ReleaseDate ?? DateTime.MaxValue)
                    .ThenBy(static release => release.Title, StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                return this with
                {
                    Snapshot = Snapshot with
                    {
                        Releases = mergedReleases,
                        LookupPaths = mergedLookupPaths
                    }
                };
            }
        }
    }
}
