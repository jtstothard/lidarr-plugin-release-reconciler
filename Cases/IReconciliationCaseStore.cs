using System.Collections.Generic;

namespace NzbDrone.Core.Plugins.ReleaseReconciler.Cases
{
    public interface IReconciliationCaseStore
    {
        string StorageDirectory { get; }

        ReconciliationCaseSnapshot Save(ReconciliationCase reconciliationCase);

        ReconciliationCaseSnapshot? Get(string caseId);

        IReadOnlyList<ReconciliationCaseSnapshot> List();
    }
}
