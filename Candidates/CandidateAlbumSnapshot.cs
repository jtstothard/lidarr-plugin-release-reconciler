using System;
using System.Collections.Generic;
using System.Linq;

namespace NzbDrone.Core.Plugins.ReleaseReconciler.Candidates
{
    public enum CandidateLookupPath
    {
        RecordingIds = 0,
        CurrentReleaseGroup = 1,
        TitleArtistFallback = 2
    }

    public sealed class LidarrAlbumLookupAttempt
    {
        public LidarrAlbumLookupAttempt(
            CandidateLookupPath path,
            bool attempted,
            int inputCount,
            int rawCandidateCount,
            int addedCandidateCount,
            int deduplicatedCandidateCount,
            string? reason)
        {
            Path = path;
            Attempted = attempted;
            InputCount = inputCount;
            RawCandidateCount = rawCandidateCount;
            AddedCandidateCount = addedCandidateCount;
            DeduplicatedCandidateCount = deduplicatedCandidateCount;
            Reason = reason;
        }

        public CandidateLookupPath Path { get; }

        public bool Attempted { get; }

        public int InputCount { get; }

        public int RawCandidateCount { get; }

        public int AddedCandidateCount { get; }

        public int DeduplicatedCandidateCount { get; }

        public string? Reason { get; }
    }

    public sealed class LidarrAlbumLookupResult
    {
        public LidarrAlbumLookupResult(
            IReadOnlyList<CandidateAlbumSnapshot> candidates,
            IReadOnlyList<LidarrAlbumLookupAttempt> attempts)
        {
            Candidates = candidates ?? throw new ArgumentNullException(nameof(candidates));
            Attempts = attempts ?? throw new ArgumentNullException(nameof(attempts));
        }

        public IReadOnlyList<CandidateAlbumSnapshot> Candidates { get; }

        public IReadOnlyList<LidarrAlbumLookupAttempt> Attempts { get; }

        public bool UsedTitleArtistFallback => Attempts.Any(static attempt =>
            attempt.Path == CandidateLookupPath.TitleArtistFallback && attempt.Attempted);
    }

    public sealed record CandidateAlbumSnapshot(
        string AlbumMusicBrainzId,
        string Title,
        string? ArtistMusicBrainzId,
        string ArtistName,
        string? Disambiguation,
        DateTime? ReleaseDate,
        string? AlbumType,
        IReadOnlyList<CandidateReleaseSnapshot> Releases,
        IReadOnlyList<CandidateLookupPath> LookupPaths);

    public sealed record CandidateReleaseSnapshot(
        string ReleaseMusicBrainzId,
        string Title,
        string? Status,
        string? Disambiguation,
        DateTime? ReleaseDate,
        int TrackCount,
        int Duration,
        IReadOnlyList<string> Labels,
        IReadOnlyList<string> Countries,
        IReadOnlyList<CandidateMediumSnapshot> Media,
        IReadOnlyList<CandidateTrackSnapshot> Tracks);

    public sealed record CandidateMediumSnapshot(
        int Number,
        string? Name,
        string? Format);

    public sealed record CandidateTrackSnapshot(
        string TrackMusicBrainzId,
        string? RecordingMusicBrainzId,
        string Title,
        string? TrackNumber,
        int AbsoluteTrackNumber,
        int MediumNumber,
        int Duration,
        bool Explicit);
}
