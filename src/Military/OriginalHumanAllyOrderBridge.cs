// / ORIGINAL human ally route. Stateless adapter, never an authority owner.
// Caller MUST use the existing current map/session/FIFO checks, treaty policy,
// original managed drain lane and original EngineInterface.threadLock lease.
// On dispatch the existing Controller sets its _dispatchingAllyOrder bypass
// in try/finally. This file installs no hook, acquires no lock and queues nothing.
// Verified original API bindings and native authority remain mandatory.
// Normal/MP native release is not enabled here. No AI-to-AI dispatch exists.
using System;
using System.Threading;
using CrusaderDE;
using SHCDESE.API;
using SHCDESE.EventAPI.Player;

namespace StrongholdEuropeDE.Runtime
{
    internal enum OriginalAllyDispatchStage
    {
        BlockedBeforeCall = 0,
        ManagedWrapperReturnedUnconfirmed = 1,
        CallThrewOutcomeUnknown = 2,
        // The shared managed broker returned but its paired Post was absent.
        // Original execution cannot be established, so this is neither a
        // confirmed pre-call veto nor a safely retryable original command.
        ManagedBrokerPostMissingOutcomeUnknown = 3
    }

    internal struct OriginalAllyDispatchObservation
    {
        internal readonly OriginalAllyDispatchStage Stage;
        internal readonly HumanAllyMilitaryRequest Request;
        internal readonly OriginalAllyOrderSnapshot Before;
        internal readonly int WrapperReturn;
        internal OriginalAllyDispatchObservation(OriginalAllyDispatchStage stage, HumanAllyMilitaryRequest request, OriginalAllyOrderSnapshot before, int wrapperReturn)
        {
            Stage = stage;
            Request = request;
            Before = before;
            WrapperReturn = wrapperReturn;
        }
    }

    internal static class OriginalHumanAllyOrderBridge
    {
        internal const string OriginalGameSha256 = "bc8b6a395f01d48557db413600c8dd8d1fdfd3abdf97bfbbb68a3c56b04fd789";
        internal const string OriginalRawraSha256 = "5cb1c33be5463086505a17f5c48aaa357d733cfff7240e101c821f0e3c668498";
        internal const string OriginalNativeSha256 = "fbcb93195fc7efca9bdac5204852efdd76f9818f59a6711750d77c9cef2831e2";
        // The untouched original field is private. Read its exact existing object;
        // do not depend on a publicized compile reference and never allocate a lock.
        private static readonly System.Reflection.FieldInfo OriginalMonitorField = typeof(EngineInterface).GetField("threadLock", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
        private static bool OriginalMonitorEntered()
        {
            if (OriginalMonitorField == null || OriginalMonitorField.FieldType != typeof(object))
                return false;
            object original = OriginalMonitorField.GetValue(null);
            return original != null && Monitor.IsEntered(original);
        }

        private static bool ArraysReady(EngineInterface.PlayState s)
        {
            return s != null && s.player_register != null && s.player_register.Length == 9 && s.computer_register != null && s.computer_register.Length == 9 && s.lord_alive != null && s.lord_alive.Length == 9;
        }

        private static bool Alive(EngineInterface.PlayState s, int p)
        {
            return OriginalHumanAllyOrderRules.Slot(p) && s.lord_alive[p] != 0;
        }

        private static bool TryReadCurrentRoute(HumanAllyMilitaryRequest r, out EngineInterface.PlayState snapshot, out string reason)
        {
            snapshot = null;
            reason = "original_monitor_not_held";
            // Never wait for the original engine monitor while holding another gate.
            if (!OriginalMonitorEntered())
                return false;
            if (r == null)
            {
                reason = "missing_human_request";
                return false;
            }

            var data = GameData.Instance;
            var api = GamePlayerManagerAPI.Instance;
            snapshot = data == null ? null : data.lastGameState;
            if (api == null || !ArraysReady(snapshot))
            {
                reason = "original_role_snapshot_unavailable";
                return false;
            }

            var f = new OriginalHumanAllyRouteFacts
            {
                NativeLocalPlayer = api.GetLocalPlayerId(),
                RequesterHuman = snapshot.is_valid_player(r.Requester),
                RequesterAI = snapshot.is_skirmish_player(r.Requester) || api.IsAIPlayer(r.Requester),
                RequesterAlive = Alive(snapshot, r.Requester),
                ConsultedHuman = snapshot.is_valid_player(r.Consulted),
                ConsultedAI = snapshot.is_skirmish_player(r.Consulted) && api.IsAIPlayer(r.Consulted),
                ConsultedAlive = Alive(snapshot, r.Consulted),
                RequesterAlliedToConsulted = api.IsPlayerAlliedTo(r.Requester, r.Consulted),
                ConsultedAlliedToRequester = api.IsPlayerAlliedTo(r.Consulted, r.Requester)
            };
            // A neutral team-zero equality is NOT a real native human-AI alliance.
            // The positive original team allows the existing order path to run.
            int originalHumanTeam = api.GetPlayerTeam(r.Requester);
            int originalAiTeam = api.GetPlayerTeam(r.Consulted);
            if (!OriginalHumanAllyOrderRules.HasPositiveCommonTeam(originalHumanTeam, originalAiTeam))
            {
                reason = "original_native_positive_team_alliance_missing";
                return false;
            }
            if (r.IsAttack)
            {
                f.TargetAlive = Alive(snapshot, r.Target);
                f.RequesterAlliedToTarget = api.IsPlayerAlliedTo(r.Requester, r.Target);
                f.TargetAlliedToRequester = api.IsPlayerAlliedTo(r.Target, r.Requester);
                f.ConsultedAlliedToTarget = api.IsPlayerAlliedTo(r.Consulted, r.Target);
                f.TargetAlliedToConsulted = api.IsPlayerAlliedTo(r.Target, r.Consulted);
            }

            return OriginalHumanAllyOrderRules.ValidateRoute(r, f, out reason);
        }

        private static bool TryReadActive(EngineInterface.PlayState s, int consulted, out OriginalAllyOrderSnapshot observation, out string reason)
        {
            observation = default(OriginalAllyOrderSnapshot);
            reason = "original_order_snapshot_unavailable";
            if (!OriginalHumanAllyOrderRules.Slot(consulted) || s == null || s.skirmish_order == null || s.skirmish_order.Length != 9 || s.skirmish_order_player == null || s.skirmish_order_player.Length != 9 || s.skirmish_order_from_player == null || s.skirmish_order_from_player.Length != 9)
                return false;
#if DIPLOMACY_PRIVATE_TESTS
            var clock = GameTimeManagerAPI.Instance;
            if (clock == null)
            {
                reason = "original_clock_unavailable";
                return false;
            }

            int tick = clock.GetElapsedMapTicks();
#else
            if (!APIShared.Simulation.SimulationTickEvents.TryGetCurrentTick(out int tick))
            {
                reason = "original_clock_unavailable";
                return false;
            }
#endif
            if (!OriginalHumanAllyOrderRules.ValidTick(tick))
            {
                reason = "original_clock_unbound_or_overflow";
                return false;
            }

            // Capture the THREE unchanged array fields from this SAME copied
            // original PlayState, under the existing original monitor. No new
            // pointer, native call, write, dispatch or permission is introduced.
            int rawKind = s.skirmish_order[consulted];
            int rawOrderPlayerField = s.skirmish_order_player[consulted];
            int rawFromPlayer = s.skirmish_order_from_player[consulted];
            var raw = new OriginalAllyRawOrderTuple(rawKind, rawOrderPlayerField, rawFromPlayer);
            // Preserve the prior normalized contract exactly. The original
            // defender evaluator exposes no target context here; the observed
            // defend rawOrderPlayerField has NO ATTESTED target/player semantics.
            // In particular, do not compare it to the human's slot or to zero.
            int target = rawKind == 1 ? rawOrderPlayerField : 0;
            var projection = rawKind == 1 ? OriginalAllyTargetProjection.OriginalAttackTargetField : OriginalAllyTargetProjection.NormalizedZeroForNonAttack;
            observation = new OriginalAllyOrderSnapshot(s, tick, consulted, rawKind, target, rawFromPlayer, raw, projection);
            if (!OriginalHumanAllyOrderRules.ValidActive(observation))
            {
                reason = "original_order_tuple_invalid";
                observation = default(OriginalAllyOrderSnapshot);
                return false;
            }

            reason = "copied_original_order_tuple_observed";
            return true;
        }

        internal static bool TryReadActiveUnderOriginalMonitor(int consulted, out OriginalAllyOrderSnapshot observation, out string reason)
        {
            observation = default(OriginalAllyOrderSnapshot);
            reason = "original_monitor_not_held";
            if (!OriginalMonitorEntered())
                return false;
            try
            {
                var data = GameData.Instance;
                return TryReadActive(data == null ? null : data.lastGameState, consulted, out observation, out reason);
            }
            catch (Exception e)
            {
                reason = "original_order_read_failed:" + e.GetType().Name;
                return false;
            }
        }

        internal static bool CanDispatchUnderOriginalMonitor(HumanAllyMilitaryRequest request) =>
            TryReadCurrentRoute(request, out _, out _);

        internal static OriginalAllyDispatchStage DispatchCurrentHumanUnderOriginalMonitor(HumanAllyMilitaryRequest request, out OriginalAllyDispatchObservation observation, out string reason)
        {
            observation = default(OriginalAllyDispatchObservation);
            bool callStarted = false;
            OriginalAllyOrderSnapshot before = default(OriginalAllyOrderSnapshot);
            try
            {
                EngineInterface.PlayState snapshot;
                if (!TryReadCurrentRoute(request, out snapshot, out reason) || !TryReadActive(snapshot, request.Consulted, out before, out reason))
                    return OriginalAllyDispatchStage.BlockedBeforeCall;
                callStarted = true;
                int result = EngineInterface.GameAction(Enums.GameActionCommand.Ally_Orders, request.Opcode, request.Consulted, request.Target);
                observation = new OriginalAllyDispatchObservation(OriginalAllyDispatchStage.ManagedWrapperReturnedUnconfirmed, request, before, result);
                reason = "original_wrapper_return_is_not_ai_acceptance";
                return observation.Stage;
            }
            catch (Exception e)
            {
                reason = (callStarted ? "original_call_outcome_unknown:" : "original_route_read_failed:") + e.GetType().Name;
                if (!callStarted)
                    return OriginalAllyDispatchStage.BlockedBeforeCall;
                observation = new OriginalAllyDispatchObservation(OriginalAllyDispatchStage.CallThrewOutcomeUnknown, request, before, 0);
                return observation.Stage; // Existing Controller decides lifetime. Never retry here.
            }
        }

        // Feed only the existing real SHCDESE subscription. Constructed test events
        // verify classification only and can never establish a native reply.
        internal static bool TryObserveAttackEvaluation(HumanAllyMilitaryRequest request, PlayerAIEvaluateAttackOrder e, out OriginalAllyEvaluatorObservation observation)
        {
            observation = default(OriginalAllyEvaluatorObservation);
            if (e == null)
                return false;
            return OriginalHumanAllyOrderRules.TryClassifyEvaluation(request, false, (int)e.Phase, e.SkipOriginalFunction, e.TargetPlayerId, e.SourcePlayerId, e.ContextPlayerId, e.ReturnValue, out observation);
        }

        internal static bool TryObserveDefendEvaluation(HumanAllyMilitaryRequest request, PlayerAIEvaluateDefendOrder e, out OriginalAllyEvaluatorObservation observation)
        {
            observation = default(OriginalAllyEvaluatorObservation);
            if (e == null)
                return false;
            return OriginalHumanAllyOrderRules.TryClassifyEvaluation(request, true, (int)e.Phase, e.SkipOriginalFunction, e.TargetPlayerId, e.SourcePlayerId, 0, e.ReturnValue, out observation);
        }
    }
}
