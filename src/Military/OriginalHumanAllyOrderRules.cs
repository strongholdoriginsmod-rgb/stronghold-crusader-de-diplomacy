// /: bounded contract for the SHA-pinned ORIGINAL human Ally_Orders route.
// Pure managed rules. No engine calls, queues, hooks, retries, signing or save owner.
using System;

namespace StrongholdEuropeDE.Runtime
{
    internal sealed class HumanAllyMilitaryRequest
    {
        internal readonly int Requester, Consulted, Opcode, Target;
        private HumanAllyMilitaryRequest(int r, int c, int o, int t)
        {
            Requester = r;
            Consulted = c;
            Opcode = o;
            Target = t;
        }

        internal bool IsAttack
        {
            get
            {
                return Opcode >= 1 && Opcode <= 6;
            }
        }

        internal bool MatchesOperands(int originalOpcode, int originalConsulted, int originalTarget,
            int finalOpcode, int finalConsulted, int finalTarget) =>
            originalOpcode == Opcode && originalConsulted == Consulted && originalTarget == Target &&
            finalOpcode == Opcode && finalConsulted == Consulted && finalTarget == Target;

        internal static bool TryCreate(int requester, int consulted, int opcode, int target, out HumanAllyMilitaryRequest request, out string reason)
        {
            request = null;
            reason = "invalid_original_human_order";
            if (!OriginalHumanAllyOrderRules.Slot(requester) || !OriginalHumanAllyOrderRules.Slot(consulted) || requester == consulted)
                return false;
            bool attack = opcode >= 1 && opcode <= 6;
            if (attack)
            {
                if (!OriginalHumanAllyOrderRules.Slot(target) || target == requester || target == consulted)
                    return false;
            }
            else if ((opcode != 10 && opcode != 11) || target != 0)
                return false;
            request = new HumanAllyMilitaryRequest(requester, consulted, opcode, target);
            reason = "bounded_original_hud_arguments";
            return true;
        }
    }

    internal struct OriginalHumanAllyRouteFacts
    {
        internal int NativeLocalPlayer;
        internal bool RequesterHuman, RequesterAI, RequesterAlive, ConsultedHuman, ConsultedAI, ConsultedAlive;
        internal bool RequesterAlliedToConsulted, ConsultedAlliedToRequester;
        internal bool TargetAlive, RequesterAlliedToTarget, TargetAlliedToRequester, ConsultedAlliedToTarget, TargetAlliedToConsulted;
    }

    internal enum OriginalAllyEvaluatorOrigin
    {
        None = 0,
        PreSkipPolicy = 1,
        OriginalEvaluatorPostObserved = 2
    }

    internal struct OriginalAllyEvaluatorObservation
    {
        internal readonly OriginalAllyEvaluatorOrigin Origin;
        internal readonly bool Accepted;
        internal OriginalAllyEvaluatorObservation(OriginalAllyEvaluatorOrigin origin, bool accepted)
        {
            Origin = origin;
            Accepted = accepted;
        }
    }

    // These are unmodified fields from ONE copied original PlayState, not
    // an independently attested native memory read or an order acknowledgement.
    internal enum OriginalAllyRawSource
    {
        Unavailable = 0,
        CopiedOriginalPlayStateOrderArrays = 1
    }

    internal enum OriginalAllyTargetProjection
    {
        Unavailable = 0,
        OriginalAttackTargetField = 1,
        NormalizedZeroForNonAttack = 2
    }

    internal struct OriginalAllyRawOrderTuple
    {
        internal readonly OriginalAllyRawSource Source;
        internal readonly int Kind, OrderPlayerField, FromPlayer;
        internal bool Available
        {
            get
            {
                return Source == OriginalAllyRawSource.CopiedOriginalPlayStateOrderArrays;
            }
        }

        internal OriginalAllyRawOrderTuple(int kind, int orderPlayerField, int fromPlayer)
        {
            Source = OriginalAllyRawSource.CopiedOriginalPlayStateOrderArrays;
            Kind = kind;
            OrderPlayerField = orderPlayerField;
            FromPlayer = fromPlayer;
        }
    }

    internal struct OriginalAllyOrderSnapshot
    {
        internal readonly object Snapshot;
        internal readonly long OriginalTick;
        // Target is the existing presentation/contract projection: non-attack
        // maps to zero. Raw.OrderPlayerField is never interpreted for defence.
        internal readonly int Consulted, Kind, Target, Requester;
        internal readonly OriginalAllyRawOrderTuple Raw;
        internal readonly OriginalAllyTargetProjection TargetProjection;
        // Preserve the original constructor and all current matching rules.
        // Constructed fixtures are not evidence of a copied original tuple.
        internal OriginalAllyOrderSnapshot(object snapshot, long tick, int consulted, int kind, int target, int requester) : this(snapshot, tick, consulted, kind, target, requester, default(OriginalAllyRawOrderTuple), OriginalAllyTargetProjection.Unavailable)
        {
        }

        internal OriginalAllyOrderSnapshot(object snapshot, long tick, int consulted, int kind, int target, int requester, OriginalAllyRawOrderTuple raw, OriginalAllyTargetProjection targetProjection)
        {
            Snapshot = snapshot;
            OriginalTick = tick;
            Consulted = consulted;
            Kind = kind;
            Target = target;
            Requester = requester;
            Raw = raw;
            TargetProjection = targetProjection;
        }
    }

    internal enum AllyOrderActivityRelation
    {
        Unavailable = 0,
        NoMatchingOrder = 1,
        MatchingRetainedSnapshot = 2,
        MatchingAlreadyPresent = 3,
        MatchingLaterSnapshot = 4
    }

    internal static class OriginalHumanAllyOrderRules
    {
#if !DIPLOMACY_PRIVATE_TESTS
        // Retire a foreign session without publishing an expiry. The slot identity
        // also prevents a competing decision from being replaced by a timeout.
        internal static bool TryClaimReplyTimeout<T>(ref T slot, T expected,
            long dispatchedSession, long currentSession, int? elapsed) where T : class
        {
            bool current = dispatchedSession > 0 && dispatchedSession == currentSession;
            if (current && !(elapsed >= 256))
                return false;
            return ReferenceEquals(System.Threading.Interlocked.CompareExchange(ref slot, null, expected), expected) &&
                current;
        }
#endif

        internal const int OriginalAllyOrdersCommand = 1061;
        internal const long LastUsableOriginalTick = (long)int.MaxValue - 4096;
        internal static bool Slot(int p)
        {
            return p >= 1 && p <= 8;
        }

        internal static bool HasPositiveCommonTeam(int requesterTeam, int consultedTeam) =>
            requesterTeam > 0 && requesterTeam == consultedTeam;

        internal static bool TryClaimDispatch(ref int claim) =>
            System.Threading.Interlocked.Exchange(ref claim, 1) == 0;

        internal static bool ValidTick(long tick)
        {
            return tick > 0 && tick < LastUsableOriginalTick;
        }

        internal static bool ValidateRoute(HumanAllyMilitaryRequest r, OriginalHumanAllyRouteFacts f, out string reason)
        {
            reason = "missing_human_request";
            if (r == null)
                return false;
            if (f.NativeLocalPlayer != r.Requester)
            {
                reason = "implicit_native_sender_mismatch";
                return false;
            }

            if (!f.RequesterHuman || f.RequesterAI || !f.RequesterAlive)
            {
                reason = "requester_is_not_live_local_human";
                return false;
            }

            if (f.ConsultedHuman || !f.ConsultedAI || !f.ConsultedAlive)
            {
                reason = "consulted_is_not_live_ai";
                return false;
            }

            if (!f.RequesterAlliedToConsulted || !f.ConsultedAlliedToRequester)
            {
                reason = "reciprocal_native_alliance_missing";
                return false;
            }

            if (r.IsAttack && (!f.TargetAlive || f.RequesterAlliedToTarget || f.TargetAlliedToRequester || f.ConsultedAlliedToTarget || f.TargetAlliedToConsulted))
            {
                reason = "attack_target_not_live_mutual_enemy";
                return false;
            }

            reason = "current_local_human_to_allied_ai_route";
            return true;
        }

        // Event Phase 0=Pre,1=Post is pinned in SHCDESE. Post proves the original
        // evaluator ran, but ReturnValue is mutable by other subscribers. Never call
        // this an immutable original CPU result, troop activity, or a Steam identity.
        internal static bool TryClassifyEvaluation(HumanAllyMilitaryRequest r, bool defend, int phase, bool skipOriginal, int consulted, int requester, int context, bool accepted, out OriginalAllyEvaluatorObservation observation)
        {
            observation = default(OriginalAllyEvaluatorObservation);
            if (r == null || r.Opcode == 10 || defend != (r.Opcode == 11) || consulted != r.Consulted || requester != r.Requester || (!defend && context != r.Target))
                return false;
            OriginalAllyEvaluatorOrigin origin = phase == 1 ? OriginalAllyEvaluatorOrigin.OriginalEvaluatorPostObserved : phase == 0 && skipOriginal ? OriginalAllyEvaluatorOrigin.PreSkipPolicy : OriginalAllyEvaluatorOrigin.None;
            if (origin == OriginalAllyEvaluatorOrigin.None)
                return false;
            observation = new OriginalAllyEvaluatorObservation(origin, accepted);
            return true;
        }

        internal static bool ValidActive(OriginalAllyOrderSnapshot s)
        {
            if (s.Snapshot == null || !ValidTick(s.OriginalTick) || !Slot(s.Consulted) || s.Kind < 0 || s.Kind > 2)
                return false;
            if (s.Kind == 0)
                return s.Target == 0;
            if (!Slot(s.Requester) || s.Requester == s.Consulted)
                return false;
            return s.Kind == 1 ? Slot(s.Target) && s.Target != s.Consulted && s.Target != s.Requester : s.Target == 0;
        }

        internal static bool Matches(HumanAllyMilitaryRequest r, OriginalAllyOrderSnapshot s)
        {
            if (r == null || r.Opcode == 10 || !ValidActive(s) || s.Consulted != r.Consulted || s.Requester != r.Requester)
                return false;
            return r.IsAttack ? s.Kind == 1 && s.Target == r.Target : s.Kind == 2;
        }

        // A retained/same/pre-existing snapshot is never a new dispatch success.
        // Even MatchingLaterSnapshot describes matching state, not causal acceptance,
        // movement, arrival, combat, or victory. The existing Controller owns lifetime
        // and FIFO correlation; this stateless helper never promotes an intent.
        internal static AllyOrderActivityRelation Observe(HumanAllyMilitaryRequest r, OriginalAllyOrderSnapshot before, OriginalAllyOrderSnapshot after)
        {
            if (!ValidActive(before) || !ValidActive(after) || before.Consulted != after.Consulted || after.OriginalTick < before.OriginalTick)
                return AllyOrderActivityRelation.Unavailable;
            if (!Matches(r, after))
                return AllyOrderActivityRelation.NoMatchingOrder;
            if (Object.ReferenceEquals(before.Snapshot, after.Snapshot))
                return AllyOrderActivityRelation.MatchingRetainedSnapshot;
            return Matches(r, before) ? AllyOrderActivityRelation.MatchingAlreadyPresent : AllyOrderActivityRelation.MatchingLaterSnapshot;
        }
    }
}
