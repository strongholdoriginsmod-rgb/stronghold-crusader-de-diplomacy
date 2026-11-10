using System;

namespace StrongholdEuropeDE.Runtime
{
    // Pure producer-bound readiness. It never compares, synthesizes or replaces
    // a persistent map/archive identity; only an original managed load can finish it.
    internal sealed class OriginalSavedWorldReadiness
    {
        internal sealed class Invocation
        {
            internal readonly OriginalSavedWorldReadiness Gate;
            internal readonly long Serial, StartEpoch;
            internal readonly int OwnerThread;
            internal Invocation Parent;
            internal long AppliedEpoch = -1;
            // 0=opened; 1=native load started; 2=original save returned;
            // 3=postLoading in progress; 4=managed applied, producer still open;
            // 5=producer returned successfully; -1=failed or ambiguous.
            internal int Phase;
            internal Invocation(OriginalSavedWorldReadiness gate, long serial, long epoch)
            {
                Gate = gate; Serial = serial; StartEpoch = epoch;
                OwnerThread = System.Threading.Thread.CurrentThread.ManagedThreadId;
            }
        }

        private readonly object _sync = new object();
        private Invocation _current;
        private long _serial, _epoch;

        internal Invocation OpenOriginalLoad(long epoch)
        {
            lock (_sync)
            {
                if (_serial == long.MaxValue)
                {
                    _current = new Invocation(this, 0, epoch) { Phase = -1 };
                    return null;
                }
                var request = new Invocation(this, ++_serial, epoch);
                _current = request;
                _epoch = epoch;
                return request;
            }
        }

        internal void Retire()
        {
            lock (_sync)
                _current = null;
        }

        internal void NativeEpochChanged(long epoch)
        {
            lock (_sync)
            {
                _epoch = epoch;
                Invocation current = _current;
                if (current == null)
                    return;
                // A load begun in the preceding native epoch may legitimately
                // return after the map replacement opened one new epoch.
                if (current.Phase == 1 && (epoch == current.StartEpoch ||
                    (epoch > current.StartEpoch && epoch - current.StartEpoch == 1)))
                    return;
                if (current.Phase == 0 && epoch == current.StartEpoch)
                    return;
                if (current.AppliedEpoch != epoch)
                    _current = null; // A fresh map cannot inherit stale failure.
            }
        }

        internal void SavePre(Invocation request, long epoch)
        {
            lock (_sync)
            {
                if (request == null)
                {
                    // Missing original producer is unproven, never an accepted
                    // completed native load, regardless of saved mod payload.
                    _current = new Invocation(this, 0, epoch) { Phase = -1 };
                    return;
                }
                if (!Object.ReferenceEquals(_current, request) ||
                    request.OwnerThread != System.Threading.Thread.CurrentThread.ManagedThreadId)
                {
                    if (_current == null)
                        _current = new Invocation(this, 0, epoch) { Phase = -1 };
                    return; // An old callback must not affect a newer request.
                }
                if (request.Phase == 0 && request.StartEpoch == epoch)
                    request.Phase = 1;
                else if (request.Phase != 1 || request.StartEpoch != epoch)
                    request.Phase = -1;
            }
        }

        internal void SavePost(Invocation request, long epoch)
        {
            lock (_sync)
            {
                if (request == null)
                {
                    _current = new Invocation(this, 0, epoch) { Phase = -1 };
                    return;
                }
                if (!Object.ReferenceEquals(_current, request) ||
                    request.OwnerThread != System.Threading.Thread.CurrentThread.ManagedThreadId)
                {
                    if (_current == null)
                        _current = new Invocation(this, 0, epoch) { Phase = -1 };
                    return;
                }
                if ((request.Phase == 2 || request.Phase == 4 || request.Phase == 5) &&
                    request.AppliedEpoch == epoch)
                    return; // Repeated R3 Post of the same original call.
                if (request.Phase != 1 ||
                    (epoch != request.StartEpoch &&
                    !(epoch > request.StartEpoch && epoch - request.StartEpoch == 1)))
                {
                    request.Phase = -1;
                    return;
                }
                request.AppliedEpoch = epoch;
                request.Phase = 2;
            }
        }

        internal Invocation BeginManagedApply(Invocation request)
        {
            lock (_sync)
            {
                if (request == null || !Object.ReferenceEquals(_current, request) ||
                    request.OwnerThread != System.Threading.Thread.CurrentThread.ManagedThreadId)
                    return null;
                if (request.Phase == 2 && request.AppliedEpoch == _epoch)
                {
                    request.Phase = 3;
                    return request;
                }
                if (request.Phase == 3)
                    request.Phase = -1; // Overlapping map writes are ambiguous.
                return null; // Sequential duplicate already applied: harmless.
            }
        }

        internal void CompleteManagedApply(Invocation request)
        {
            lock (_sync)
            {
                if (request != null && Object.ReferenceEquals(_current, request) &&
                    request.OwnerThread == System.Threading.Thread.CurrentThread.ManagedThreadId &&
                    request.Phase == 3 && request.AppliedEpoch == _epoch)
                    request.Phase = 4;
            }
        }

        internal void EndOriginalLoad(Invocation request, bool producerSucceeded)
        {
            lock (_sync)
            {
                if (request == null || !Object.ReferenceEquals(_current, request))
                    return;
                if (request.Phase == 0)
                    _current = null; // No native SaveLoad began: fresh map stays usable.
                else if (!producerSucceeded)
                    request.Phase = -1; // An exception revokes even managed-applied world.
                else if ((request.Phase == 4 || request.Phase == 5) &&
                         request.AppliedEpoch == _epoch)
                    request.Phase = 5; // Only a successful enclosing producer commits.
                else
                    request.Phase = -1; // No proven managed completion: fail closed.
            }
        }

        internal long StagingSerial()
        {
            lock (_sync)
            {
                Invocation current = _current;
                return current != null && current.Serial > 0 &&
                       current.OwnerThread == System.Threading.Thread.CurrentThread.ManagedThreadId &&
                       current.Phase >= 0 && current.Phase <= 4
                    ? current.Serial : 0L;
            }
        }

        internal bool CanRetainAcrossEpoch(long serial, long previousEpoch)
        {
            lock (_sync)
                return serial > 0 && _current != null && _current.Serial == serial &&
                       _current.Phase == 1 && _current.StartEpoch == previousEpoch;
        }

        // Retains an epoch annotation for staged bytes. It does NOT authorize
        // consuming them; IsAppliedForPayload demands producer success.
        internal bool CanRebase(long serial, long epoch)
        {
            lock (_sync)
                return serial > 0 && _current != null && _current.Serial == serial &&
                       _current.AppliedEpoch == epoch &&
                       _current.Phase >= 2 && _current.Phase <= 5;
        }

        internal bool IsApplied(long epoch)
        {
            lock (_sync)
                return _current != null && _current.Serial > 0 &&
                       _current.AppliedEpoch == epoch && _current.Phase == 5;
        }

        internal bool IsAppliedForPayload(long epoch, long serial)
        {
            lock (_sync)
                return serial > 0 && _current != null && _current.Serial == serial &&
                       _current.AppliedEpoch == epoch && _current.Phase == 5;
        }

        internal bool HasSaveObservation(long epoch)
        {
            lock (_sync)
                return _current != null && _current.Phase != 0 &&
                    (_current.StartEpoch == epoch || _current.AppliedEpoch == epoch);
        }

        internal bool IsOriginalLoadInProgress()
        {
            lock (_sync)
                return _current != null && _current.Phase >= 1 && _current.Phase < 5;
        }

        internal string PhaseLabel(long epoch)
        {
            lock (_sync)
            {
                if (_current == null) return "no_original_save_ticket";
                if (_current.StartEpoch != epoch && _current.AppliedEpoch != epoch)
                    return "different_original_generation";
                switch (_current.Phase)
                {
                    case 0: return "original_producer_entered";
                    case 1: return "original_native_save_loading";
                    case 2: return "original_native_save_returned_waiting_postloading";
                    case 3: return "original_managed_world_applying";
                    case 4: return "original_managed_world_applied_waiting_producer";
                    case 5: return "original_save_producer_returned_success";
                    default: return "original_save_application_ambiguous";
                }
            }
        }
    }

}
