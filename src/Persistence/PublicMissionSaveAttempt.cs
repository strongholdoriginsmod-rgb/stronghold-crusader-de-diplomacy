namespace StrongholdEuropeDE.Runtime
{
    // Called under the feature gate. Native save proof remains independently
    // producer-bound. This state only prevents one attempt from owning another.
    internal sealed class PublicMissionSaveAttempt
    {
        private long lastObserved;
        private long lastAccepted;
        internal long Pending { get; private set; }

        internal bool TryBegin(long session)
        {
            if (session <= lastObserved)
                return false;
            lastObserved = session;
            Pending = session;
            return true;
        }

        internal bool TryAcceptStart(long session, long current, bool save, bool nativeSaveEpoch)
        {
            if (session <= lastAccepted || session != current || session < lastObserved ||
                (save && (Pending != session || !nativeSaveEpoch)))
                return false;
            lastObserved = session;
            lastAccepted = session;
            Pending = 0;
            return true;
        }

        internal bool Retire(long session)
        {
            if (session <= 0 || Pending != session)
                return false;
            Pending = 0;
            return true;
        }
    }
}
