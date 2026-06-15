using System;

namespace NzbDrone.Core.Plugins.ReleaseReconciler.Candidates
{
    public interface ILidarrAlbumLookup
    {
        LidarrAlbumLookupResult LookupCandidates(
            string albumTitle,
            string artistName,
            ReconciliationEvidence structuralEvidence,
            string? albumMusicBrainzId = null);
    }
}
