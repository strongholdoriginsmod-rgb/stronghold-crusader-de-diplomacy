using System;
using System.Globalization;
using System.Reflection;
using System.Threading;
using APIShared.Commands;
using CrusaderDE;
#if !DIPLOMACY_PRIVATE_TESTS
using APIShared.Presentation;
using SHCDESE.Interop.Enums;
using StrongholdEuropeDE.TradeNegotiation;
#endif
using SHCDESE.API;
using SHCDESE.Interop;

namespace StrongholdEuropeDE.Runtime
{
    internal sealed partial class DiplomacyFeature
    {
        // The original HUD calls this exact four-argument GameAction overload.
        // Preserve its actual opcode (Attack0..5 is 1..6, Defend is 11), not a
        // reconstructed enemy-list index. One latest order per lord matches
        // Vanilla's single current order and bounds pending input to eight.
        private sealed class HumanAllyOrderAttempt
        {
            internal int Opcode, Consulted, Target, Requester;
            internal long Generation, Epoch;
            internal ulong DurableRequestId, SupersededRequestId;
            internal bool DurableApproved, DurableForwarded;
            internal bool? Accepted;
            internal bool SupplyVoice;
            internal int LordId;
            internal int RequestedDay, DispatchedDay = -1;
            internal int ResponseDaysRemaining = 3, LastVisibleDay = -1;
            internal bool ObservedActive;
            internal int EndedDay = -1;
            internal long DispatchedTick, ReplySequence;
#if !DIPLOMACY_PRIVATE_TESTS
            internal long ReplySession;
            internal int ReplyStartTick;
#endif
            internal bool NativeDecision, PolicyDecision, DecisionUnbound, LocalRefusal, DispatchFailed;
            internal bool ObservedWithdrawal, TrackingExpired;
            // Strict per-wrapper managed GameAction Post evidence only. Neither
            // callback nor a return value is a native military acceptance ACK.
            // Reset for every attempt, including later safe reapplication.
#if !DIPLOMACY_PRIVATE_TESTS
            internal int DispatchClaim;
#endif
            internal bool SharedManagedPostObserved, SharedManagedWasSkipped;
            // Set only by the actual original HUD CancelOrders click for this
            // lord. It suppresses future Mod reissues but says NOTHING about
            // native cancellation or the existing AI's accepted order.
            internal bool HumanCancelRequested;
            internal HumanAllyOrderAttempt HumanSource, DispatchSource, PriorStatus;
            internal HumanAllyMilitaryRequest NativeRequest;
            internal OriginalAllyOrderSnapshot NativeBefore;
            internal bool HasNativeBefore;
            internal AllyOrderActivityRelation NativeActivity;
        }

        // A timeout never invents an answer. Evaluator events lack a packet id;
        // known overlapping tuples remain unbound rather than guessing an ACK.
#if DIPLOMACY_PRIVATE_TESTS
        private const long AllyReplyWaitTicks = 256;
#endif
        private readonly HumanAllyOrderAttempt[] _allyOrders = new HumanAllyOrderAttempt[9];
        private readonly HumanAllyOrderAttempt[] _allyInFlight = new HumanAllyOrderAttempt[9];
        private readonly HumanAllyOrderAttempt[] _allyReplies = new HumanAllyOrderAttempt[9];
        private readonly HumanAllyOrderAttempt[] _allyVoices = new HumanAllyOrderAttempt[9];
        private readonly HumanAllyOrderAttempt[] _allyOrderStatus = new HumanAllyOrderAttempt[9];
        private long _allyReplySequence;
        private readonly object _humanCaptureGate = new object ();
        // Only prevents interception of our own managed GameAction replay.
        // Native evaluators normally run later on the simulation thread.
        [ThreadStatic]
        private static HumanAllyOrderAttempt _dispatchingAllyOrder;
        private MethodInfo _allyGameActionTarget;
        // APIShared owns the ONE managed GameAction detour for this process.
        // Its registration cannot be uninstalled. Feature rollback only makes
        // our static callback dormant, and a later feature activation reuses it.
        private static readonly object SharedGameActionRegistrationGate = new object();
        private const string SharedGameActionRegistrationId = "diplomacy-original-goods-and-human-orders-v1";
        private static bool _sharedGameActionRegistered = false;
        private bool _sharedAllyOrderActive;
        private bool _sharedGoodsRequestActive;
        private long _sharedGameActionCallbackFaults;

        // A foreign peer with equal maximum callback order can still veto
        // AFTER our pre-existing owner recorded OriginalSubmitted. If the
        // immediate same-frame cancellation is temporarily unavailable, retain
        // ONLY its exact known request ID, session and epoch for retry on our
        // existing managed Tick, NEVER a native command or a new journal.
        private const int SharedVetoCancelCapacity = 8;
        private const int SharedVetoCancelMaximumRetries = 64;
        private sealed class PendingSharedVetoCancel
        {
            // 0 free; 1 callback writer; 2 published; 3 managed-Tick reader.
            internal int State;
            internal ulong RequestId;
            internal long Generation, Epoch;
            internal int Retries;
        }

        private static PendingSharedVetoCancel[] CreateSharedVetoCancelSlots()
        {
            var slots = new PendingSharedVetoCancel[SharedVetoCancelCapacity];
            for (int index = 0; index < slots.Length; ++index)
                slots[index] = new PendingSharedVetoCancel();
            return slots;
        }

        // Preallocated slots: no allocation, UI, logging, native access or
        // blocking lock on the foreign managed GameAction callback.
        private readonly PendingSharedVetoCancel[] _sharedVetoCancelSlots =
            CreateSharedVetoCancelSlots();

        private bool QueueSharedVetoCancel(ulong requestId, long generation,
            long epoch)
        {
            if (requestId == 0)
                return false;
            foreach (PendingSharedVetoCancel slot in _sharedVetoCancelSlots)
            {
                if (Interlocked.CompareExchange(ref slot.State, 1, 0) != 0)
                    continue;
                slot.RequestId = requestId;
                slot.Generation = generation;
                slot.Epoch = epoch;
                slot.Retries = 0;
                Volatile.Write(ref slot.State, 2);
                return true;
            }
            return false;
        }

        // Runs only inside the existing managed owner _gate in Tick. No new
        // transport, timer, hook, native side effect or cross-map resurrection.
        private void PumpSharedVetoCancellations()
        {
            if (!Monitor.IsEntered(_gate))
                return;
            foreach (PendingSharedVetoCancel slot in _sharedVetoCancelSlots)
            {
                if (Interlocked.CompareExchange(ref slot.State, 3, 2) != 2)
                    continue;
                bool retire = false;
                try
                {
                    if (slot.RequestId == 0 ||
                        slot.Generation != _sessionGeneration ||
                        slot.Epoch != _mapEpoch)
                    {
                        retire = true;
                    }
                    else
                    {
                        DiplomaticRequestRecord record =
                            _requestHistory == null ? null :
                            _requestHistory.Find(slot.RequestId);
                        if (record != null &&
                            record.Status == DiplomaticRequestStatus.Cancelled)
                        {
                            retire = true; // Original HUD also cancelled it.
                        }
                        else if (record != null && record.Terminal)
                        {
                            retire = true; // Never reverse a different outcome.
                            Interlocked.Increment(ref _sharedGameActionCallbackFaults);
                        }
                        else if (record != null &&
                                 TryNoteDiplomaticRequestCancelled(slot.RequestId, false))
                        {
                            retire = true;
                            Log("gameaction",
                                "status=late_veto_request_history_cancelled" +
                                "|native_ack=false|original_executed=false");
                        }
                    }
                }
                catch (Exception)
                {
                    // Retry only if still same scope. Never claim a native ACK.
                    Interlocked.Increment(ref _sharedGameActionCallbackFaults);
                }
                finally
                {
                    if (!retire && ++slot.Retries >=
                        SharedVetoCancelMaximumRetries)
                    {
                        retire = true;
                        Interlocked.Increment(ref _sharedGameActionCallbackFaults);
                        try
                        {
                            Log("gameaction",
                                "status=late_veto_cancel_unresolved_bounded" +
                                "|native_ack=false|no_retry_of_original=true");
                        }
                        catch (Exception) { }
                    }
                    // Always publish slot completion even if diagnostics fault.
                    Volatile.Write(ref slot.State, retire ? 0 : 2);
                }
            }
        }

        private bool PrepareAllyOrders(out string failure)
        {
            _allyGameActionTarget = typeof(EngineInterface).GetMethod("GameAction", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(Enums.GameActionCommand), typeof(int), typeof(int), typeof(int) }, null);
            failure = _allyGameActionTarget != null && _allyGameActionTarget.ReturnType == typeof(int)
                ? null : "original managed GameAction ABI unavailable";
            return failure == null;
        }

        // APIShared's permanent final-validator registration must be logically
        // dormant outside this owner's current verified feature activation.
        private static bool IsSharedGameActionProtectionActive()
        {
            DiplomacyFeature owner = Volatile.Read(ref _activeOwner);
#if DIPLOMACY_PRIVATE_TESTS
            return owner != null && (Volatile.Read(ref owner._sharedAllyOrderActive) ||
                Volatile.Read(ref owner._sharedGoodsRequestActive));
#else
            // A replay already on this thread must still be vetoable when its
            // owner or session was retired between claiming and dispatch.
            return _dispatchingAllyOrder != null || _hudDefendCapture != null ||
                (owner != null && Volatile.Read(ref owner._sharedGoodsRequestActive));
#endif
        }

#if DIPLOMACY_PRIVATE_TESTS
        // Unrelated GameAction commands (e.g. recruitment and ordinary combat)
        // keep APIShared's documented nested-original behavior unchanged.
        private static bool IsProtectedSharedGameActionNested(
            Enums.GameActionCommand command)
        {
            return command == Enums.GameActionCommand.Ally_RequestGoods ||
                   command == Enums.GameActionCommand.Ally_Orders;
        }
#endif

        private void ApplyAllyOrders()
        {
            lock (SharedGameActionRegistrationGate)
            {
                if (!_sharedGameActionRegistered)
                {
                    string reason;
                    // LAST among ordinary third-party ordered Pre callbacks:
                    // a prior veto means our owner MUST NOT capture or forward.
                    // Equal-MaxValue registrations remain an explicit runtime
                    // compatibility boundary; no magical original ACK is assumed.
                    if (!GameActionEvents.TryRegisterWithFinalValidation(
                            CompatibilityContracts.PluginId, SharedGameActionRegistrationId,
                            OnSharedGameActionPre, OnSharedGameActionPost,
                            OnSharedGameActionFinalValidation, out reason, int.MaxValue,
                            accepted: null,
#if DIPLOMACY_PRIVATE_TESTS
                            strictNestedKinds: IsProtectedSharedGameActionNested,
#else
                            strictNestedKinds: new[] { Enums.GameActionCommand.Ally_Orders },
#endif
                            isActive: IsSharedGameActionProtectionActive))
                        throw new InvalidOperationException(
                            "APIShared shared GameAction owner unavailable: " + reason);
                    Volatile.Write(ref _sharedGameActionRegistered, true);
                }
            }
#if DIPLOMACY_PRIVATE_TESTS
            Volatile.Write(ref _sharedAllyOrderActive, true);
            Volatile.Write(ref _sharedGoodsRequestActive, true);
            Log("gameaction", "status=apishared_shared_owner_active|legacy_parallel_harmony_hook=false");
#else
            // A different game/Extender image keeps its ordinary Vanilla HUD route.
            bool verified = VerifiedManagedSurface.VerifyGameAssembly(out string provenanceReason) &&
                VerifiedManagedSurface.VerifyRawraAssembly(out provenanceReason) &&
                SerpsRawraCompat.AttestedLoadedNativeEngine(out provenanceReason);
            Volatile.Write(ref _sharedAllyOrderActive, verified);
            Log("gameaction", verified
                ? "status=public_validator_registered|native_ally_forwarding=singleplayer_hud_defend"
                : "status=public_validator_registered|native_ally_forwarding=unavailable|reason=" + Safe(provenanceReason));
#endif
        }

        private void RollbackAllyOrders()
        {
            // APIShared registrations are process-lived: never remove/re-hook
            // its detour from our feature teardown, including an error rollback.
            Volatile.Write(ref _sharedAllyOrderActive, false);
            Volatile.Write(ref _sharedGoodsRequestActive, false);
            ClearAllyOrders();
        }

        private sealed class SharedHumanReplayAttempt
        {
            internal readonly DiplomacyFeature Owner;
            internal readonly HumanAllyOrderAttempt Order;
            internal readonly int Opcode, Consulted, Target, Requester;
            internal readonly long Generation, Epoch;
            internal SharedHumanReplayAttempt(DiplomacyFeature owner,
                HumanAllyOrderAttempt order)
            {
                Owner = owner; Order = order;
                Opcode = order.Opcode; Consulted = order.Consulted;
                Target = order.Target; Requester = order.Requester;
                Generation = order.Generation; Epoch = order.Epoch;
            }
        }

        private sealed class SharedGoodsAttempt
        {
            internal readonly DiplomacyFeature Owner;
            internal readonly GoodsRequest Request;
            internal readonly ulong RequestId;
            internal readonly long Generation, Epoch;
            internal readonly int Requested, Good, Amount;
            internal SharedGoodsAttempt(DiplomacyFeature owner, GoodsRequest request,
                int requested, int good, int amount)
            {
                Owner = owner; Request = request; RequestId = request.DurableRequestId;
                Generation = request.Generation; Epoch = request.Epoch;
                Requested = requested; Good = good; Amount = amount;
            }
        }

        private static void OnSharedGameActionPre(GameActionPreEventArgs call)
        {
            // A previous APIShared veto already decided the managed command.
            // No history row, native call or human-order witness human-order capture may follow.
            if (call == null)
                return;
            DiplomacyFeature owner = Volatile.Read(ref _activeOwner);
            bool order = call.Command == Enums.GameActionCommand.Ally_Orders;
#if !DIPLOMACY_PRIVATE_TESTS
            if (order && _dispatchingAllyOrder == null && _hudDefendCapture is HudDefendCapture capture)
            {
                // Consume the first matching invocation even if a previous Pre vetoed it.
                // Vanilla's nested Back must not grant a second capture opportunity.
                if (!capture.Entered)
                {
                    capture.Entered = true;
                    call.State = capture;
                }
                return;
            }
#endif
            if (order && _dispatchingAllyOrder != null && owner == null)
            {
                call.SkipOriginalFunction = true;
                return;
            }
            if (owner == null || (!order && (call.Command != Enums.GameActionCommand.Ally_RequestGoods ||
                    !Volatile.Read(ref owner._sharedGoodsRequestActive))))
                return;
            try
            {
                HumanAllyOrderAttempt replay = _dispatchingAllyOrder;
                if (call.Command == Enums.GameActionCommand.Ally_Orders &&
                    replay != null)
                {
                    // The original human replay executes under its old managed
                    // thread-local ticket. Give this exact invocation its own
                    // Post stamp; never infer an Original enqueue merely from
                    // GameAction's zero return. No second hook/native command.
                    call.State = new SharedHumanReplayAttempt(owner, replay);
                    if (!Volatile.Read(ref owner._sharedAllyOrderActive) ||
                        !Volatile.Read(ref owner._mapActive) ||
                        Volatile.Read(ref owner._mapDisabled) ||
                        Volatile.Read(ref owner._mapBootstrapPending) ||
                        replay.Generation != owner._sessionGeneration ||
                        replay.Epoch != owner._mapEpoch ||
                        replay.Requester != owner._localPlayer ||
                        call.OriginalStructureId != replay.Opcode ||
                        call.StructureId != replay.Opcode ||
                        call.ActionState != replay.Consulted ||
                        call.Value2 != replay.Target)
                        call.SkipOriginalFunction = true;
                    return;
                }

                if (call.SkipOriginalFunction)
                    return;
                if (order)
                {
#if DIPLOMACY_PRIVATE_TESTS
                    if (!Volatile.Read(ref owner._sharedAllyOrderActive))
                        return;
#else
                    // Orders outside the explicitly claimed Defend HUD click stay Vanilla.
                    return;
#endif
                }
                int vanillaResult = 0;
                bool forward = AllyGameActionPrefix(call.Command,
                    call.StructureId, call.ActionState, call.Value2, ref vanillaResult);
                if (!forward)
                {
                    // The old Harmony prefix returned false with result=0;
                    // APIShared's skip returns the same zero, NOT native ACK.
                    call.SkipOriginalFunction = true;
                    return;
                }
                // The specific confirmed original HUD request was validated and
                // marked Forwarded BEFORE Vanilla. Retain this owner-private
                // context so a subsequent foreign Pre veto can retire it rather
                // than leaving a false OriginalSubmitted record.
                if (call.Command == Enums.GameActionCommand.Ally_RequestGoods)
                {
                    GoodsRequestInvocation ticket = _goodsRequestInvocation;
                    GoodsRequest request = ticket == null ? null : ticket.CapturedRequest;
                    if (ticket != null && ReferenceEquals(ticket.Owner, owner) &&
                        ticket.Command == "ConfirmGoods" && request != null &&
                        request.OriginalProducerAllowed && request.DurableRequestId != 0)
                        call.State = new SharedGoodsAttempt(owner, request,
                            call.StructureId, call.ActionState, call.Value2);
                }
            }
            catch (Exception)
            {
                // APIShared otherwise catches Pre exceptions and rolls back a
                // veto, accidentally running Vanilla AFTER a partial capture.
                // Consume this command instead; diagnose later on managed Tick.
                GoodsRequestInvocation ticket = _goodsRequestInvocation;
                if (ticket != null && ReferenceEquals(ticket.Owner, owner) &&
                    ticket.CapturedRequest != null &&
                    ticket.CapturedRequest.OriginalProducerAllowed)
                {
                    ticket.CapturedRequest.OriginalProducerAllowed = false;
                    ticket.CapturedRequest.OriginalProducerRejected = true;
                    bool cancelled = false;
                    try
                    {
                        cancelled = owner.TryNoteDiplomaticRequestCancelled(
                            ticket.CapturedRequest.DurableRequestId, false);
                    }
                    catch (Exception) { }
                    if (!cancelled && !owner.QueueSharedVetoCancel(
                            ticket.CapturedRequest.DurableRequestId,
                            ticket.CapturedRequest.Generation,
                            ticket.CapturedRequest.Epoch))
                        Interlocked.Increment(
                            ref owner._sharedGameActionCallbackFaults);
                }
                call.SkipOriginalFunction = true;
                Interlocked.Increment(ref owner._sharedGameActionCallbackFaults);
            }
        }

        // The APIShared broker calls this AFTER
        // EVERY ordered Pre participant, including registrations with an equal
        // int.MaxValue order. The snapshot is frozen; false prevents original
        // execution and makes the existing Post report WasSkipped.
        // This is a pure managed identity check, never native authorization.
        private static bool OnSharedGameActionFinalValidation(
            GameActionFinalValidationEventArgs finalCall)
        {
            if (finalCall == null)
                return false;
            if (finalCall.Command == Enums.GameActionCommand.Ally_Orders)
            {
#if !DIPLOMACY_PRIVATE_TESTS
                if (_dispatchingAllyOrder == null && _hudDefendCapture is HudDefendCapture capture)
                {
                    if (ReferenceEquals(finalCall.State, capture) && capture.Request.MatchesOperands(
                        finalCall.OriginalStructureId, finalCall.OriginalActionState, finalCall.OriginalValue2,
                        finalCall.StructureId, finalCall.ActionState, finalCall.Value2))
                        capture.AcceptedForQueue = true;
                    // Suppress the immediate submission. The replay runs every validator again.
                    return false;
                }
#endif
                SharedHumanReplayAttempt replay =
                    finalCall.State as SharedHumanReplayAttempt;
                // An original unbound or non-replay human order still uses its
                // prior documented route. Our own replay is held to an exact
                // immutable ticket even when an equal-priority foreign Pre
                // mutates the final target or replaces its session replayOwner.
                if (replay == null)
                    return true;
                DiplomacyFeature currentOwner = Volatile.Read(ref _activeOwner);
                DiplomacyFeature replayOwner = replay.Owner;
                HumanAllyOrderAttempt order = replay.Order;
                return replayOwner != null && order != null &&
                    ReferenceEquals(replayOwner, currentOwner) &&
                    Volatile.Read(ref replayOwner._sharedAllyOrderActive) &&
                    Volatile.Read(ref replayOwner._mapActive) &&
                    !Volatile.Read(ref replayOwner._mapDisabled) &&
                    !Volatile.Read(ref replayOwner._mapBootstrapPending) &&
                    ReferenceEquals(_dispatchingAllyOrder, order) &&
                    order.Generation == replay.Generation &&
                    order.Epoch == replay.Epoch &&
                    replayOwner._sessionGeneration == replay.Generation &&
                    replayOwner._mapEpoch == replay.Epoch &&
                    order.Requester == replay.Requester &&
                    replayOwner._localPlayer == replay.Requester &&
                    order.Opcode == replay.Opcode &&
                    order.HumanSource != null &&
                    !Volatile.Read(ref order.HumanSource.HumanCancelRequested) &&
                    order.Consulted == replay.Consulted &&
                    order.Target == replay.Target &&
#if DIPLOMACY_PRIVATE_TESTS
                    finalCall.OriginalStructureId == replay.Opcode &&
                    finalCall.StructureId == replay.Opcode &&
                    finalCall.ActionState == replay.Consulted &&
                    finalCall.Value2 == replay.Target;
#else
                    order.NativeRequest != null && order.NativeRequest.MatchesOperands(
                        finalCall.OriginalStructureId, finalCall.OriginalActionState, finalCall.OriginalValue2,
                        finalCall.StructureId, finalCall.ActionState, finalCall.Value2) &&
                    replayOwner.IsCurrentPublicAllyReplay(order);
#endif
            }
            if (finalCall.Command != Enums.GameActionCommand.Ally_RequestGoods)
                return true;
            // Prior Pre may already have durably recorded an original attempt.
            // Once its private State exists, an owner/map/epoch transition must
            // REFUSE the original rather than fall through to Vanilla. A
            // detached, previously-bound owner has no authority to forward.
            SharedGoodsAttempt attempted = finalCall.State as SharedGoodsAttempt;
            DiplomacyFeature liveOwner = Volatile.Read(ref _activeOwner);
            if (attempted == null)
            {
                // The new APIShared registration is process-lived. Truly
                // inactive owners still leave unrelated Vanilla gameplay alone.
                // In an active map a missing exact goods ticket never becomes
                // a source identity, even with matching player/quantity.
                return liveOwner == null ||
                    !Volatile.Read(ref liveOwner._sharedGoodsRequestActive) ||
                    !Volatile.Read(ref liveOwner._mapActive) ||
                    Volatile.Read(ref liveOwner._mapDisabled);
            }
            DiplomacyFeature owner = attempted.Owner;
            if (owner == null || !ReferenceEquals(owner, liveOwner) ||
                !Volatile.Read(ref owner._sharedGoodsRequestActive) ||
                !Volatile.Read(ref owner._mapActive) ||
                Volatile.Read(ref owner._mapDisabled) ||
                Volatile.Read(ref owner._mapBootstrapPending))
                return false;
            GoodsRequest request = attempted.Request;
            GoodsRequestInvocation ticket = _goodsRequestInvocation;
            return request != null && ticket != null &&
                ReferenceEquals(ticket.Owner, owner) &&
                ReferenceEquals(ticket.CapturedRequest, request) &&
                ticket.Command == "ConfirmGoods" &&
                request.OriginalProducerEncountered &&
                request.OriginalProducerAllowed &&
                !request.OriginalProducerRejected &&
                attempted.RequestId != 0 &&
                request.DurableRequestId == attempted.RequestId &&
                request.Generation == attempted.Generation &&
                request.Epoch == attempted.Epoch &&
                owner._sessionGeneration == attempted.Generation &&
                owner._mapEpoch == attempted.Epoch &&
                request.Target == attempted.Requested &&
                request.Good == attempted.Good &&
                request.Amount == attempted.Amount &&
                // Check the BEFORE-every-Pre first operand as well. An
                // earlier foreign Pre must not turn a different original
                // request into a matching authorized HUD ticket.
                finalCall.OriginalStructureId == attempted.Requested &&
#if !DIPLOMACY_PRIVATE_TESTS
                finalCall.OriginalActionState == attempted.Good &&
                finalCall.OriginalValue2 == attempted.Amount &&
#endif
                finalCall.StructureId == attempted.Requested &&
                finalCall.ActionState == attempted.Good &&
                finalCall.Value2 == attempted.Amount;
        }

        private static void OnSharedGameActionPost(GameActionPostEventArgs call)
        {
            if (call == null)
                return;
            SharedHumanReplayAttempt replay =
                call.State as SharedHumanReplayAttempt;
            if (replay != null)
            {
                // Paired Post always reports whether the ORIGINAL managed
                // wrapper was skipped, even if the feature/map was disabled
                // by a foreign callback after our replay ticket was created.
                HumanAllyOrderAttempt order = replay.Order;
                if (order != null)
                {
                    order.SharedManagedWasSkipped = call.WasSkipped;
                    order.SharedManagedPostObserved = true;
                }
                return;
            }
            // Post never grants an original or native ACK. A foreign veto can
            // happen AFTER this registration's Pre only at equal MaxValue order.
            // It must not leave our permitted HUD request falsely Forwarded.
            SharedGoodsAttempt attempted = call == null ? null :
                call.State as SharedGoodsAttempt;
            if (attempted == null)
                return;
            DiplomacyFeature owner = attempted.Owner;
            GoodsRequest request = attempted.Request;
            if (owner == null || request == null)
                return;
            if (call.WasSkipped)
            {
                // This invocation-local state must reflect final veto even
                // when the owning map was disabled/replaced during foreign
                // callbacks. Never cancel a newer owner's journal below.
                request.OriginalProducerAllowed = false;
                request.OriginalProducerRejected = true;
            }
            if (!Volatile.Read(ref owner._sharedGoodsRequestActive) ||
                !ReferenceEquals(Volatile.Read(ref _activeOwner), owner))
                return;
            try
            {
                if (call.WasSkipped)
                {
                    if (request.DurableRequestId == attempted.RequestId &&
                        request.Generation == attempted.Generation &&
                        request.Epoch == attempted.Epoch &&
                        owner._sessionGeneration == attempted.Generation &&
                        owner._mapEpoch == attempted.Epoch &&
                        attempted.RequestId != 0 &&
                        !owner.TryNoteDiplomaticRequestCancelled(attempted.RequestId, false))
                    {
                        // Preserve an exact bounded retry if RequestHistory was
                        // temporarily unavailable at the late foreign veto.
                        // A full queue is fail-visible and NEVER sends original.
                        if (!owner.QueueSharedVetoCancel(attempted.RequestId,
                                attempted.Generation, attempted.Epoch))
                            Interlocked.Increment(
                                ref owner._sharedGameActionCallbackFaults);
                        Interlocked.Increment(ref owner._sharedGameActionCallbackFaults);
                    }
                }
                else if (call.StructureId != attempted.Requested ||
                         call.ActionState != attempted.Good ||
                         call.Value2 != attempted.Amount)
                {
                    // Once Original ran there is NO truthful rollback/ACK.
                    // Later foreign mutation is a diagnostic/compatibility
                    // violation, not permission to guess native success.
                    Interlocked.Increment(ref owner._sharedGameActionCallbackFaults);
                }
            }
            catch (Exception)
            {
                // A failed cancellation can still be safely retried on the
                // ordinary owner Tick only for an unchanged request scope.
                if (call != null && call.WasSkipped &&
                    attempted.RequestId != 0 &&
                    request.DurableRequestId == attempted.RequestId &&
                    request.Generation == attempted.Generation &&
                    request.Epoch == attempted.Epoch &&
                    owner._sessionGeneration == attempted.Generation &&
                    owner._mapEpoch == attempted.Epoch &&
                    !owner.QueueSharedVetoCancel(attempted.RequestId,
                        attempted.Generation, attempted.Epoch))
                    Interlocked.Increment(
                        ref owner._sharedGameActionCallbackFaults);
                Interlocked.Increment(ref owner._sharedGameActionCallbackFaults);
            }
        }

        // Original Assembly-CSharp.dll: HUD_AlliesPanel.ButtonClicked
        // "CancelOrders" -> EngineInterface.GameAction(1066, ally[curr_ally],0,0).
        // This is NOT Ally_Orders(1061) Opcode10: the native Original command
        // must keep its Vanilla route. Only retire a still-current Mod-owned
        // human intent/maintenance source with the exact HUD ally selection.
        //
        // Caller owns _gate in HandleButton. The human capture gate has no
        // blocking Game/Native waits; other owners can never be retired here.
        private void RetireHumanOrderTrackingOnOriginalCancel(
            object panel, int consulted)
        {
            if (!Monitor.IsEntered(_gate) || !_mapActive || _mapDisabled ||
                _mapBootstrapPending || _tradeTab || _overviewVisible ||
                panel == null || !ReferenceEquals(panel, CurrentAlliesPanel()) ||
                consulted < 1 || consulted > 8 || consulted == _localPlayer)
                return;

            lock (_humanCaptureGate)
            {
                HumanAllyOrderAttempt original =
                    Volatile.Read(ref _originalHumanOrders[consulted]);
                if (original == null ||
                    !ReferenceEquals(original.HumanSource, original) ||
                    !AllyOrderIsCurrent(original, consulted) ||
                    !ReferenceEquals(Interlocked.CompareExchange(
                        ref _originalHumanOrders[consulted], null, original),
                        original))
                    return;

                // Make this logical invalidation visible BEFORE clearing
                // pending arrays. An in-progress original managed replay must
                // be rejected again at the APIShared immutable FINAL boundary.
                Volatile.Write(ref original.HumanCancelRequested, true);
                HumanAllyOrderAttempt pending =
                    Volatile.Read(ref _allyOrders[consulted]);
                if (pending != null &&
                    ReferenceEquals(pending.HumanSource, original))
                    Interlocked.CompareExchange(
                        ref _allyOrders[consulted], null, pending);

                // Cancellation of an UNSENT local tracking ID is safe.
                // NEVER cancel a Forwarded/OriginalSubmitted ID on the
                // strength of this UI click; 1066's native result is unknown.
                if (original.DispatchedDay < 0 &&
                    original.DurableRequestId != 0 &&
                    !original.DurableForwarded)
                    TryNoteDiplomaticRequestCancelled(
                        original.DurableRequestId, false);

                if (original.DispatchedDay >= 0)
                    MarkReplyAmbiguous(original);
                DiscardAllyOrderSource(_allyReplies, original);
                DiscardAllyOrderSource(_allyVoices, original);
                // No reissue even if an asynchronous evaluator accepted the
                // previous original human order before this CancelOrders click.
                _orderReapplications[consulted] =
                    MaximumOrderReapplications;
                _orderNextRetryTick[consulted] = long.MaxValue;
                Log("ally_order_cancel",
                    "stage=human_original_cancel_orders_clicked" +
                    "|consulted=" + consulted.ToString(CultureInfo.InvariantCulture) +
                    "|mod_pending_retired=true|original_1066_runs_vanilla=true" +
                    "|native_cancel_ack=false|native_effect_unconfirmed");
            }
        }

        private void ClearAllyOrders()
        {
            for (int player = 1; player <= 8; player++)
            {
                Interlocked.Exchange(ref _allyOrders[player], null);
                Interlocked.Exchange(ref _allyInFlight[player], null);
                Interlocked.Exchange(ref _allyReplies[player], null);
                _allyVoices[player] = null;
                _allyOrderStatus[player] = null;
            }

            ClearMilitaryMaintenance();
        }

        private static bool AllyGameActionPrefix(Enums.GameActionCommand __0, int __1, int __2, int __3, ref int __result)
        {
            // Reuse the same original classification under the ONE APIShared shared hook.
            // RequestGoods is original 1064 (HUD ButtonClicked IL_0B7E).
            // Do not assign original CPU actor to an unbound managed call.
            if (__0 == Enums.GameActionCommand.Ally_RequestGoods)
                return OriginalGoodsRequestGameActionPrefix(__1, __2, __3, ref __result);
            if (__0 != Enums.GameActionCommand.Ally_Orders || _dispatchingAllyOrder != null)
                return true;
            DiplomacyFeature owner = Volatile.Read(ref _activeOwner);
            if (owner == null || !Volatile.Read(ref owner._mapActive) || Volatile.Read(ref owner._mapDisabled) || __2 < 1 || __2 > 8 || !((__1 >= 1 && __1 <= 6) || __1 == 10 || __1 == 11))
                return true;
            owner.NoteLane(LaneUiPanel);
            // This is the managed, human HUD command boundary, before its
            // blocking native monitor. A busy engine never consumes the click.
            HumanAllyOrderAttempt prior =
                Volatile.Read(ref owner._allyOrderStatus[__2]);
            var order = new HumanAllyOrderAttempt
            {
                Opcode = __1,
                Consulted = __2,
                Target = __3,
                SupersededRequestId = prior == null ? 0UL :
                    prior.DurableRequestId,
                Requester = Volatile.Read(ref owner._localPlayer),
                Generation = Volatile.Read(ref owner._sessionGeneration),
                Epoch = Volatile.Read(ref owner._mapEpoch),
                RequestedDay = Math.Max(0, Volatile.Read(ref owner._lastProcessedDay)),
            };
            string requestReason;
            HumanAllyMilitaryRequest.TryCreate(order.Requester, order.Consulted, order.Opcode, order.Target, out order.NativeRequest, out requestReason);
            // HUMAN registration linearization: the private native claim
            // uses this SAME capture gate with a nonblocking TryEnter.
            // Earlier registered clicks win; later clicks remain queued.
            lock (owner._humanCaptureGate)
            {
                owner.BeginHumanOrderMaintenance(order);
                Interlocked.Exchange(ref owner._allyOrders[__2], order);
            }

            __result = 0; // The original Ally_Orders wrapper returns zero too.
            return false;
        }

        private string AllyOrderStatusText(int player)
        {
            if (player < 1 || player > 8 || !_mapActive || _mapDisabled)
                return "";
            HumanAllyOrderAttempt pending = Volatile.Read(ref _allyOrders[player]) ?? Volatile.Read(ref _allyInFlight[player]);
            if (IsLatestHumanOrder(pending))
                return AllyOrderDescription(pending) + " · " + Text("wartet", "waiting") + AllyOrderDay(pending.RequestedDay);
            HumanAllyOrderAttempt reply = _allyOrderStatus[player];
            if (IsLatestHumanOrder(reply) && reply.ResponseDaysRemaining > 0)
            {
                string status = reply.LocalRefusal ? Text("lokal nicht ausgeführt", "not dispatched locally") : reply.EndedDay >= 0 ? Text("nicht mehr aktiv", "no longer active") : reply.ObservedWithdrawal ? Text("kein nativer Auftrag beobachtet · Rücknahme unbestätigt", "no native order observed · withdrawal unconfirmed") : reply.ObservedActive && !reply.TrackingExpired ? Text("nativ aktiv beobachtet", "native activity observed") + (reply.Accepted == true ? "" : Text(" · Annahme unbestätigt", " · acceptance unconfirmed")) : reply.PolicyDecision && reply.Accepted.HasValue ? (reply.Accepted.Value ? Text("Regelzustimmung beobachtet", "policy approval observed") : Text("Regelablehnung beobachtet", "policy refusal observed")) : reply.Accepted == true ? Text("KI-Zustimmung beobachtet", "AI approval observed") : reply.Accepted == false ? Text("KI-Ablehnung beobachtet", "AI refusal observed") : Text("nicht bestätigt", "unconfirmed");
                return AllyOrderDescription(reply) + " · " + status + AllyOrderDay(reply.EndedDay >= 0 ? reply.EndedDay : reply.DispatchedDay);
            }

            // After load there is no remembered native acknowledgement. Only
            // Vanilla's current order is displayed, without invented age/reply.
            OriginalAllyOrderSnapshot observation;
            if (!TryReadNativeAllyOrder(player, out observation) || observation.Kind != 0 && observation.Requester != _localPlayer)
                return "";
            return observation.Kind == 1 ? AllyAttackDescription(player, observation.Target) + " · " + Text("aktiv", "active") : observation.Kind == 2 ? string.Format(CultureInfo.InvariantCulture, Text("{0}: Verteidigung", "{0}: Defence"), PlayerName(player)) + " · " + Text("aktiv", "active") : "";
        }

        private bool AllyOrderIsCurrent(HumanAllyOrderAttempt order, int player)
        {
            return order != null && order.Consulted == player && order.Requester == _localPlayer && order.Generation == _sessionGeneration && order.Epoch == _mapEpoch;
        }

        private string AllyAttackDescription(int consulted, int target)
        {
            return string.Format(CultureInfo.InvariantCulture, Text("{0}: Angriff auf {1}", "{0}: Attack on {1}"), PlayerName(consulted), PlayerName(target));
        }

        private string AllyOrderDescription(HumanAllyOrderAttempt order)
        {
            if (order.Opcode == 10)
                return string.Format(CultureInfo.InvariantCulture, Text("{0}: Rücknahme", "{0}: Withdrawal"), PlayerName(order.Consulted));
            if (order.Opcode == 11)
                return string.Format(CultureInfo.InvariantCulture, Text("{0}: Verteidigung für {1}", "{0}: Defence for {1}"), PlayerName(order.Consulted), PlayerName(order.Requester));
            return AllyAttackDescription(order.Consulted, order.Target);
        }

        private string AllyOrderDay(int day)
        {
            return string.Format(CultureInfo.InvariantCulture, Text(" · Tag {0}", " · Day {0}"), Math.Max(0, day));
        }

        // Called only for the actual visible details/selected overview footer,
        // never for hidden rows or when goods status covers the order feedback.
        // Distinct visible days count once; hidden gaps are never caught up.
        private void NoteAllyOrderStatusShown(int player)
        {
            if (player < 1 || player > 8 || !_mapActive || _mapDisabled)
                return;
            if (IsLatestHumanOrder(Volatile.Read(ref _allyOrders[player])) || IsLatestHumanOrder(Volatile.Read(ref _allyInFlight[player])))
                return;
            HumanAllyOrderAttempt reply = _allyOrderStatus[player];
            if (!IsLatestHumanOrder(reply) || reply.ResponseDaysRemaining <= 0)
                return;
            int day = Math.Max(0, _lastProcessedDay);
            if (reply.LastVisibleDay >= 0 && day > reply.LastVisibleDay)
                reply.ResponseDaysRemaining--;
            reply.LastVisibleDay = day;
        }

        private bool TryReadNativeAllyOrder(int player, out OriginalAllyOrderSnapshot observation)
        {
            observation = default(OriginalAllyOrderSnapshot);
            if (player < 1 || player > 8 || _localPlayer < 1 || _localPlayer > 8)
                return false;
            IDisposable lease;
            if (!TryEnterNativeInventorySection(out lease))
                return false;
            try
            {
                using (lease)
                {
                    var api = GamePlayerManagerAPI.Instance;
                    var game = GameData.Instance;
                    var live = game == null ? null : game.lastGameState;
                    if (api == null || live == null || api.GetLocalPlayerId() != _localPlayer || !live.is_valid_player(_localPlayer) || live.is_skirmish_player(_localPlayer) || live.is_valid_player(player) || !live.is_skirmish_player(player) || !api.IsAIPlayer(player) || !IsAlivePlayer(player) || !IsCoalitionAllied(player) || !api.IsPlayerAlliedTo(_localPlayer, player) || !api.IsPlayerAlliedTo(player, _localPlayer))
                        return false;
                    string reason;
                    return OriginalHumanAllyOrderBridge.TryReadActiveUnderOriginalMonitor(player, out observation, out reason);
                }
            }
            catch (Exception exception)
            {
                NoteVassalOrderFault("order_read_deferred", exception);
                return false;
            }
        }

        private void ObserveAllyOrderStatus(int player)
        {
            HumanAllyOrderAttempt reply = Volatile.Read(ref _allyOrderStatus[player]);
            if (!IsLatestHumanOrder(reply) || reply.EndedDay >= 0 || reply.TrackingExpired || reply.LocalRefusal)
                return;
            HumanAllyOrderAttempt original = reply.HumanSource;
            long tick = ReadSimulationTick();
            if (original.DispatchedDay >= 0 && (tick < original.DispatchedTick || tick - original.DispatchedTick > OrderTrackingTicks))
            {
                reply.TrackingExpired = true;
                return;
            }

            OriginalAllyOrderSnapshot observation;
            if (TryReadNativeAllyOrder(player, out observation))
                ApplyAllyOrderObservation(reply, observation, Math.Max(0, _lastProcessedDay));
        }

        private void ApplyAllyOrderObservation(HumanAllyOrderAttempt order, OriginalAllyOrderSnapshot observation, int day)
        {
            if (!IsLatestHumanOrder(order) || order.LocalRefusal || order.Accepted == false || order.EndedDay >= 0 || order.TrackingExpired || !OriginalHumanAllyOrderRules.ValidActive(observation) || observation.Consulted != order.Consulted)
                return;
            if (order.Opcode == 10)
            {
                if (observation.Kind == 0)
                    order.ObservedWithdrawal = true;
                return; // Observed absence is not a withdrawal acknowledgement.
            }

            bool previouslyActive = order.ObservedActive;
            AllyOrderActivityRelation previousActivity = order.NativeActivity;
            order.NativeActivity = order.HasNativeBefore ? OriginalHumanAllyOrderRules.Observe(order.NativeRequest, order.NativeBefore, observation) : AllyOrderActivityRelation.Unavailable;
            // A retained or pre-existing match prevents blind reissues, but
            // never proves that this dispatch caused the active native order.
            if (OriginalHumanAllyOrderRules.Matches(order.NativeRequest, observation))
                order.ObservedActive = true;
            else if (order.ObservedActive)
            {
                order.EndedDay = Math.Max(0, day);
                order.ResponseDaysRemaining = 3;
                order.LastVisibleDay = -1;
            }

            if (previouslyActive != order.ObservedActive || previousActivity != order.NativeActivity || order.EndedDay >= 0)
                Log("ally_order_observation", "consulted=" + order.Consulted + "|kind=" + observation.Kind + "|target=" + observation.Target + "|requester=" + observation.Requester + "|read_tick=" + observation.OriginalTick + "|activity=" + order.NativeActivity.ToString() + "|ended_day=" + order.EndedDay + "|causal_success=unattested");
        }

        // The ACTUAL RequestHistory owner, same partial feature and _gate, writes the
        // only persisted request register. Native scope/roles have already
        // been checked by TryValidateHumanPair under the original monitor.
        // History unavailable is RETRYABLE: the UI order remains queued.
        private bool TryBindManualAllyOrderHistory(HumanAllyOrderAttempt order)
        {
            if (order == null || order.NativeRequest == null)
                return false;
            if (order.DurableRequestId == 0)
            {
                ulong id;
                if (!TryCaptureDiplomaticRequest(DiplomaticRequestKind.Military,
                        DiplomaticRequestOrigin.OriginalHumanOrder,
                        order.Requester, order.Consulted,
                        order.Opcode >= 1 && order.Opcode <= 6 ? order.Target : 0,
                        0, 0, order.Opcode, out id))
                    return false;
                order.DurableRequestId = id;
            }
            if (!order.DurableApproved)
            {
                if (!TryDecideDiplomaticRequest(order.DurableRequestId, true,
                        DiplomaticRequestDecisionEvidence.ModPolicy))
                    return false;
                order.DurableApproved = true;
            }
            return true;
        }

        private void PumpAllyOrders()
        {
            if (!_mapActive || _mapDisabled || _mapBootstrapPending)
                return;
#if !DIPLOMACY_PRIVATE_TESTS
            if (!Volatile.Read(ref _sharedAllyOrderActive))
                return;
#endif
            IDisposable lease;
            if (!TryEnterNativeInventorySection(out lease))
                return;
            using (lease)
            {
                long tick = ReadSimulationTick();
                int day = Math.Max(0, _lastProcessedDay);
                if (!ObserveMilitaryClock(tick, day))
                    return;
#if !DIPLOMACY_PRIVATE_TESTS
                var mission = _missionLifecycle.Current;
                long replySession = mission?.SessionId ?? 0;
                // The existing engine lease keeps the native map tick stable throughout this pump.
                int? replyTick = IsCurrentPublicSoloMission(mission) &&
                    APIShared.Simulation.SimulationClock.TryGetCurrentTick(mission, out int mapTick)
                    ? mapTick : (int?)null;
#endif
                for (int player = 1; player <= 8; player++)
                {
#if DIPLOMACY_PRIVATE_TESTS && HOST_DIRECTED_AI_TRIBE_CANARY
                    // A human click committed AFTER the native claim stays
                    // in the normal pending slot, undispatched until release.
                    // Do not hold or enter the HUD capture gate while pumping.
                    if (Volatile.Read(ref _originalSupportNativeClaims[player]) != null)
                        continue;
#endif
                    HumanAllyOrderAttempt tracked = Volatile.Read(ref _allyOrders[player]) ?? Volatile.Read(ref _allyInFlight[player]) ?? Volatile.Read(ref _allyOrderStatus[player]);
                    if (IsLatestHumanOrder(tracked) && !tracked.LocalRefusal)
                    {
                        try
                        {
                            bool valid;
                            if (!TryValidateHumanPair(tracked, out valid))
                                continue;
                            if (!valid)
                            {
                                RefuseAllyOrderLocally(tracked);
                                ConsumeAllyOrderReply(player);
                                continue;
                            }
                        }
                        catch (Exception exception)
                        {
                            NoteVassalOrderFault("role_observation_deferred", exception);
                            continue;
                        }
                    }

                    // Consume a real decision before examining its deadline.
                    ConsumeAllyOrderReply(player);
#if DIPLOMACY_PRIVATE_TESTS
                    ExpireAllyOrderReply(player, tick);
#else
                    ExpireAllyOrderReply(player, replySession, replyTick);
#endif
                    HumanAllyOrderAttempt order = Volatile.Read(ref _allyOrders[player]);
                    if (order != null)
                    {
                        if (order.DurableRequestId != 0 &&
                            _lastProcessedDay >= order.RequestedDay &&
                            (long)_lastProcessedDay - order.RequestedDay >=
                                DiplomaticRequestHistory.NeutralTrackingWindowDays)
                        {
                            // RequestHistory's neutral expiry is NOT a native refusal.
                            // Do not keep an already-expired approved ID queued.
                            TryNoteDiplomaticRequestExpired(order.DurableRequestId);
                            RefuseAllyOrderLocally(order);
                            continue;
                        }
                        if (!IsLatestHumanOrder(order))
                        {
                            Interlocked.CompareExchange(
                                ref _allyOrders[player], null, order);
                            if (order.DurableRequestId != 0 &&
                                !order.DurableForwarded)
                                TryNoteDiplomaticRequestCancelled(
                                    order.DurableRequestId, false);
                        }
                        else if (Volatile.Read(ref _allyInFlight[player]) == null && _pendingTeamTransition == null && !_inventoryRestorePending && _pendingPackage == null && _nativeTransferProbe == null)
                        {
                            try
                            {
                                bool valid;
                                // Missing role or clock evidence leaves the click in its original slot.
                                if (TryValidateHumanPair(order, out valid) &&
#if !DIPLOMACY_PRIVATE_TESTS
                                    (!valid || replyTick.HasValue) &&
#endif
                                    (!valid || TryBindManualAllyOrderHistory(order)))
                                {
                                    if (ReferenceEquals(Interlocked.CompareExchange(
                                            ref _allyOrders[player], null, order), order))
                                    {
                                        if (valid)
                                        {
#if !DIPLOMACY_PRIVATE_TESTS
                                            order.ReplySession = replySession;
                                            order.ReplyStartTick = replyTick.Value;
#endif
                                            DispatchAllyOrder(order, true);
                                        }
                                        else
                                            RefuseAllyOrderLocally(order);
                                    }
                                    else if (order.DurableRequestId != 0 &&
                                             !order.DurableForwarded)
                                    {
                                        // A different UI thread replaced this
                                        // attempt between validation and claim.
                                        // Cancel only the captured MOD intent,
                                        // never an original native command.
                                        TryNoteDiplomaticRequestCancelled(
                                            order.DurableRequestId, false);
                                    }
                                }
                            }
                            catch (Exception exception)
                            {
                                NoteVassalOrderFault("validation_deferred", exception);
                            }
                        }
                    }

                    ConsumeAllyOrderReply(player);
                    try
                    {
                        ObserveAllyOrderStatus(player);
                    }
                    catch (Exception exception)
                    {
                        NoteVassalOrderFault("observation_deferred", exception);
                    }
                }

                // Progress must not depend on whether a diplomacy panel can draw.
                MaintainConfirmedAllyOrders();
            }

            if (!CanPresentDiplomacyUi)
                return;
            for (int player = 1; player <= 8; player++)
            {
                HumanAllyOrderAttempt voice = Volatile.Read(ref _allyVoices[player]);
                if (voice != null && PlayAllyOrderReply(voice))
                    Interlocked.CompareExchange(ref _allyVoices[player], null, voice);
            }
        }

#if DIPLOMACY_PRIVATE_TESTS
        private void ExpireAllyOrderReply(int player, long tick)
        {
            HumanAllyOrderAttempt inflight = Volatile.Read(ref _allyInFlight[player]);
            if (inflight == null)
                return;
            if (!IsLatestHumanOrder(inflight))
                Interlocked.CompareExchange(ref _allyInFlight[player], null, inflight);
            else if (tick >= inflight.DispatchedTick && tick - inflight.DispatchedTick >= AllyReplyWaitTicks)
            {
                MarkReplyAmbiguous(inflight);
                Interlocked.CompareExchange(ref _allyInFlight[player], null, inflight);
                if (Volatile.Read(ref _allyReplies[player]) == null)
                    PublishAllyOrderReply(inflight);
            }
        }
#else
        private void ExpireAllyOrderReply(int player, long session, int? tick)
        {
            ref HumanAllyOrderAttempt slot = ref _allyInFlight[player];
            HumanAllyOrderAttempt inflight = Volatile.Read(ref slot);
            if (inflight == null)
                return;
            if (!IsLatestHumanOrder(inflight))
            {
                Interlocked.CompareExchange(ref slot, null, inflight);
                return;
            }

            int? elapsed = tick.HasValue && inflight.ReplySession == session &&
                APIShared.Simulation.SimulationClock.TryGetElapsedTicks(inflight.ReplyStartTick, tick.Value, out int distance)
                ? distance : (int?)null;
            if (!OriginalHumanAllyOrderRules.TryClaimReplyTimeout(ref slot, inflight,
                    inflight.ReplySession, session, elapsed))
                return;

            MarkReplyAmbiguous(inflight);
            if (Volatile.Read(ref _allyReplies[player]) == null)
                PublishAllyOrderReply(inflight);
        }
#endif

        private static HumanAllyOrderAttempt CopyAllyOrder(HumanAllyOrderAttempt source)
        {
            return new HumanAllyOrderAttempt
            {
                Opcode = source.Opcode,
                Consulted = source.Consulted,
                Target = source.Target,
                Requester = source.Requester,
                Generation = source.Generation,
                Epoch = source.Epoch,
                DurableRequestId = source.DurableRequestId,
                SupersededRequestId = source.SupersededRequestId,
                DurableApproved = source.DurableApproved,
                DurableForwarded = source.DurableForwarded,
                RequestedDay = source.RequestedDay,
                DispatchedDay = source.DispatchedDay,
                DispatchedTick = source.DispatchedTick,
                LordId = source.LordId,
                HumanSource = source.HumanSource,
                NativeRequest = source.NativeRequest,
                NativeBefore = source.NativeBefore,
                HasNativeBefore = source.HasNativeBefore,
            };
        }

        private static void DiscardAllyOrderSource(HumanAllyOrderAttempt[] slots, HumanAllyOrderAttempt source)
        {
            HumanAllyOrderAttempt previous = Volatile.Read(ref slots[source.Consulted]);
            if (previous != null && ReferenceEquals(previous.HumanSource, source.HumanSource))
                Interlocked.CompareExchange(ref slots[source.Consulted], null, previous);
        }

        private void RefuseAllyOrderLocally(HumanAllyOrderAttempt order)
        {
            if (!IsLatestHumanOrder(order))
                return;
            if (order.DispatchedDay >= 0)
                MarkReplyAmbiguous(order);
            if (order.DurableRequestId != 0 && !order.DurableForwarded)
                TryNoteDiplomaticRequestCancelled(order.DurableRequestId,
                    false); // local withdrawal, NEVER original AI refusal
            DiscardAllyOrderSource(_allyOrders, order);
            DiscardAllyOrderSource(_allyInFlight, order);
            DiscardAllyOrderSource(_allyReplies, order);
            DiscardAllyOrderSource(_allyVoices, order);
            HumanAllyOrderAttempt refusal = CopyAllyOrder(order);
            refusal.DispatchSource = order.DispatchSource ?? order;
            refusal.LocalRefusal = true; // Not an original AI negative evaluation.
            refusal.DispatchedDay = Math.Max(0, _lastProcessedDay);
            PublishAllyOrderReply(refusal);
        }

        // Caller owns the original monitor. GameAction's return zero is never ACK.
        private void DispatchAllyOrder(HumanAllyOrderAttempt order, bool firstDispatch)
        {
            if (!IsLatestHumanOrder(order))
                return;
#if !DIPLOMACY_PRIVATE_TESTS
            // Only a single dispatch per explicitly captured ticket; never reapply it.
            if (!firstDispatch || !Volatile.Read(ref _sharedAllyOrderActive) ||
                !OriginalHumanAllyOrderRules.TryClaimDispatch(ref order.DispatchClaim))
                return;
#endif
            int player = order.Consulted;
            order.DispatchedDay = Math.Max(0, _lastProcessedDay);
            order.DispatchedTick = ReadSimulationTick();
            order.DispatchSource = order;
            Volatile.Write(ref _allyOrderStatus[player], order);
            if (order.Opcode != 10)
                Volatile.Write(ref _allyInFlight[player], order);
            try
            {
                if (firstDispatch && order.Opcode >= 1 && order.Opcode <= 6)
                    AuthorizeAllyAttack(order);
                if (!IsLatestHumanOrder(order))
                    return;
                // The managed broker is process-lived. A stale Post stamp
                // from an earlier attempt must never attest this replay.
                order.SharedManagedPostObserved = false;
                order.SharedManagedWasSkipped = false;
                _dispatchingAllyOrder = order;
                OriginalAllyDispatchObservation observation;
                string reason;
                OriginalAllyDispatchStage stage = OriginalHumanAllyOrderBridge.DispatchCurrentHumanUnderOriginalMonitor(order.NativeRequest, out observation, out reason);
                if (stage == OriginalAllyDispatchStage.ManagedWrapperReturnedUnconfirmed)
                {
                    if (order.SharedManagedPostObserved &&
                        order.SharedManagedWasSkipped)
                    {
                        // Pre/final broker veto returned zero without calling
                        // Original. No request-forward or AI reply is earned.
                        stage = OriginalAllyDispatchStage.BlockedBeforeCall;
                        reason = "shared_managed_broker_veto_no_original";
                    }
                    else if (!order.SharedManagedPostObserved)
                    {
                        // Absence of an owner Post is NOT proof of a veto: a
                        // foreign hook, changed owner or callback failure can
                        // leave an uncertain original. Never retry it as if
                        // nothing happened, and never claim native success.
                        stage = OriginalAllyDispatchStage.ManagedBrokerPostMissingOutcomeUnknown;
                        reason = "shared_managed_broker_post_missing_outcome_unknown_no_submission_receipt";
                    }
                }
                CompleteMilitaryDispatch(order, firstDispatch, stage, observation, reason);
            }
            catch (Exception exception)
            {
                order.DispatchFailed = true;
                MarkReplyAmbiguous(order);
                Interlocked.CompareExchange(ref _allyInFlight[player], null, order);
                PublishAllyOrderReply(order);
                NoteVassalOrderFault("dispatch_unconfirmed", exception);
            }
            finally
            {
                _dispatchingAllyOrder = null;
            }
        }

#if !DIPLOMACY_PRIVATE_TESTS
        [ThreadStatic] private static HudDefendCapture _hudDefendCapture;

        private sealed class HudDefendCapture
        {
            internal readonly HumanAllyMilitaryRequest Request;
            internal readonly long Session, Generation, Epoch;
            internal bool Entered, AcceptedForQueue;

            internal HudDefendCapture(HumanAllyMilitaryRequest request, long session, long generation, long epoch)
            {
                Request = request;
                Session = session;
                Generation = generation;
                Epoch = epoch;
            }
        }

        // The HUD broker executes the claim only after all button vetos. Keep Vanilla's
        // actual Defend and nested Back behavior; only its exact GameAction is deferred.
        private bool TryClaimHudDefend(AllyPanelButtonPreEventArgs args)
        {
            var mission = _missionLifecycle?.Current;
            if (_hudDefendCapture != null || !Volatile.Read(ref _sharedAllyOrderActive) ||
                !_mapActive || _mapDisabled || _mapBootstrapPending || _tradeTab || _overviewVisible ||
                !IsCurrentPublicSoloMission(mission) || _missionPlayState.Current?.SessionId != mission.SessionId ||
                !MainViewModel.viewModelLoaded || !ReferenceEquals(args.Panel, CurrentAlliesPanel()))
                return false;
            var view = MainViewModel.Instance;
            int consulted = SelectedPlayer(args.Panel);
            var state = GameData.Instance?.lastGameState;
            if (view == null || !view.AlliesPanelVisible || !view.Allies_OrdersViewVis ||
                state == null || !OriginalHumanAllyOrderRules.Slot(_localPlayer) ||
                !OriginalHumanAllyOrderRules.Slot(consulted) || !IsCoalitionAllied(consulted) ||
                !state.is_valid_player(_localPlayer) || state.is_skirmish_player(_localPlayer) ||
                !state.is_skirmish_player(consulted) || state.is_valid_player(consulted) ||
                !HumanAllyMilitaryRequest.TryCreate(_localPlayer, consulted, (int)AIRequestOrderType.DefendMe, 0,
                    out var request, out _))
                return false;
            var capture = new HudDefendCapture(request, mission.SessionId, _sessionGeneration, _mapEpoch);
            return args.TryClaim(invocation => ExecuteHudDefend(invocation, capture));
        }

        private bool IsCurrentHudDefend(HudDefendCapture capture)
        {
            var mission = _missionLifecycle?.Current;
            return ReferenceEquals(Volatile.Read(ref _activeOwner), this) &&
                Volatile.Read(ref _sharedAllyOrderActive) && _mapActive && !_mapDisabled && !_mapBootstrapPending &&
                IsCurrentPublicSoloMission(mission) && mission.SessionId == capture.Session &&
                capture.Generation == _sessionGeneration && capture.Epoch == _mapEpoch &&
                capture.Request.Requester == _localPlayer && IsCoalitionAllied(capture.Request.Consulted);
        }

        private void ExecuteHudDefend(AllyPanelButtonInvocation invocation, HudDefendCapture capture)
        {
            lock (_gate)
            {
                if (_hudDefendCapture != null || !IsCurrentHudDefend(capture) ||
                    !ReferenceEquals(invocation.Panel, CurrentAlliesPanel()) ||
                    SelectedPlayer(invocation.Panel) != capture.Request.Consulted)
                    return;
            }
            _hudDefendCapture = capture;
            try { invocation.InvokeOriginal(invocation.Button); }
            finally { _hudDefendCapture = null; }
            if (!capture.AcceptedForQueue)
                return;
            lock (_gate)
            {
                if (!IsCurrentHudDefend(capture))
                    return;
                var request = capture.Request;
                int unused = 0;
                AllyGameActionPrefix(Enums.GameActionCommand.Ally_Orders, request.Opcode, request.Consulted,
                    request.Target, ref unused);
            }
        }

        private bool IsCurrentPublicAllyReplay(HumanAllyOrderAttempt order)
        {
            var mission = _missionLifecycle.Current;
            return IsCurrentPublicSoloMission(mission) &&
                _missionPlayState.Current?.SessionId == mission.SessionId &&
                OriginalHumanAllyOrderBridge.CanDispatchUnderOriginalMonitor(order.NativeRequest);
        }
#endif

        private static bool MilitaryBlockIsTransient(string reason)
        {
            return reason != null && (reason == "original_monitor_not_held" || reason == "original_role_snapshot_unavailable" || reason == "original_order_snapshot_unavailable" || reason == "original_clock_unavailable" || reason == "original_clock_unbound_or_overflow" || reason.StartsWith("original_route_read_failed:", StringComparison.Ordinal));
        }

        private void CompleteMilitaryDispatch(HumanAllyOrderAttempt order, bool firstDispatch, OriginalAllyDispatchStage stage, OriginalAllyDispatchObservation observation, string reason)
        {
            int player = order.Consulted;
            if (firstDispatch && order.DurableRequestId != 0 &&
                !order.DurableForwarded &&
                stage != OriginalAllyDispatchStage.BlockedBeforeCall &&
                stage != OriginalAllyDispatchStage.ManagedBrokerPostMissingOutcomeUnknown)
            {
                // OriginalHumanAllyOrderBridge has already attempted the real
                // managed GameAction. This is a SUBMISSION, never native ACK.
                order.DurableForwarded = TryNoteDiplomaticRequestForwarded(
                    order.DurableRequestId);
                if (!order.DurableForwarded)
                    order.DispatchFailed = true; // uncertain: never reissue
            }
            if (firstDispatch &&
                stage != OriginalAllyDispatchStage.BlockedBeforeCall &&
                stage != OriginalAllyDispatchStage.ManagedBrokerPostMissingOutcomeUnknown &&
                order.SupersededRequestId != 0 &&
                order.SupersededRequestId != order.DurableRequestId)
            {
                // A new explicit human order supersedes the old MOD tracking
                // identity. Never claim the original unit order was cleared.
                TryNoteDiplomaticRequestCancelled(
                    order.SupersededRequestId, false);
            }
            Log("ally_order_dispatch", "consulted=" + player + "|opcode=" + order.Opcode + "|target=" + order.Target + "|requester=" + order.Requester + "|stage=" + stage.ToString() + "|wrapper_return=" + (stage == OriginalAllyDispatchStage.ManagedWrapperReturnedUnconfirmed ? observation.WrapperReturn.ToString(CultureInfo.InvariantCulture) : "not_observed") + "|reason=" + Safe(reason) + "|acceptance=unconfirmed");
            if (stage == OriginalAllyDispatchStage.BlockedBeforeCall)
            {
                Interlocked.CompareExchange(ref _allyInFlight[player], null, order);
                if (!IsLatestHumanOrder(order))
                    return;
                if (!firstDispatch)
                {
                    // Adapter proves no wrapper invocation. Keep the prior
                    // observed decision; unavailable metadata spends no budget.
                    _orderReapplications[player] = Math.Max(0, _orderReapplications[player] - 1);
                    Interlocked.CompareExchange(ref _allyOrderStatus[player], order.PriorStatus, order);
                    if (!MilitaryBlockIsTransient(reason))
                        RefuseAllyOrderLocally(order);
                }
                else
                {
                    order.DispatchedDay = -1;
                    order.DispatchedTick = 0;
                    Interlocked.CompareExchange(ref _allyOrderStatus[player], null, order);
                    if (MilitaryBlockIsTransient(reason))
                        Interlocked.CompareExchange(ref _allyOrders[player], order, null);
                    else
                        RefuseAllyOrderLocally(order);
                }

                return;
            }

            if (OriginalHumanAllyOrderRules.ValidActive(observation.Before))
            {
                order.NativeBefore = observation.Before;
                order.HasNativeBefore = true;
            }

            if (stage == OriginalAllyDispatchStage.CallThrewOutcomeUnknown ||
                stage == OriginalAllyDispatchStage.ManagedBrokerPostMissingOutcomeUnknown)
            {
                // Unverified original execution is terminally ambiguous for
                // this attempt: no native ACK, no fabricated submission and
                // never schedule a second GameAction just because Post vanished.
                order.DispatchFailed = true;
                MarkReplyAmbiguous(order);
                Interlocked.CompareExchange(ref _allyInFlight[player], null, order);
                PublishAllyOrderReply(order);
            }
            else if (order.Opcode == 10)
                PublishAllyOrderReply(order);
        }

        private void ConsumeAllyOrderReply(int player)
        {
            HumanAllyOrderAttempt reply = Interlocked.Exchange(ref _allyReplies[player], null);
            if (!IsLatestHumanOrder(reply) || !_mapActive || _mapDisabled)
                return;
            HumanAllyOrderAttempt inflight = Volatile.Read(ref _allyInFlight[player]);
            HumanAllyOrderAttempt status = Volatile.Read(ref _allyOrderStatus[player]);
            if (reply.NativeDecision && !ReferenceEquals(reply.DispatchSource, inflight) && !ReferenceEquals(reply.DispatchSource, status))
                return;
            if (inflight != null && ReferenceEquals(reply.DispatchSource, inflight))
                Interlocked.CompareExchange(ref _allyInFlight[player], null, inflight);
            if (status != null && ReferenceEquals(status, reply.DispatchSource))
            {
                // Observed activity may precede the evaluator mailbox drain.
                // A callback must not erase that observation and rearm retries.
                reply.NativeBefore = status.NativeBefore;
                reply.HasNativeBefore = status.HasNativeBefore;
                reply.NativeActivity = status.NativeActivity;
                reply.ObservedActive |= status.ObservedActive;
                reply.ObservedWithdrawal |= status.ObservedWithdrawal;
                reply.TrackingExpired |= status.TrackingExpired;
                if (status.EndedDay >= 0)
                    reply.EndedDay = status.EndedDay;
            }

            if (reply.DispatchSource != null && reply.DispatchSource.DispatchFailed)
                reply.Accepted = null;
            // An actual matched original Post is a RequestHistory observed decision,
            // never an immutable AI/native ACK or proof of military effect.
            // PreSkip decisions and ambiguous tuples cannot be promoted.
            if (reply.DispatchSource != null &&
                reply.DispatchSource.DurableRequestId != 0 &&
                reply.DispatchSource.DurableForwarded &&
                reply.NativeDecision && !reply.DecisionUnbound &&
                !reply.PolicyDecision && reply.Accepted.HasValue)
            {
                if (reply.Accepted.Value)
                    TryNoteDiplomaticRequestObservedApproval(
                        reply.DispatchSource.DurableRequestId);
                else
                    TryNoteDiplomaticRequestRefused(
                        reply.DispatchSource.DurableRequestId);
            }
            Volatile.Write(ref _allyOrderStatus[player], reply);
            Log("ally_order", "consulted=" + player.ToString(CultureInfo.InvariantCulture) + "|target=" + reply.Target.ToString(CultureInfo.InvariantCulture) + "|requester=" + reply.Requester.ToString(CultureInfo.InvariantCulture) + "|opcode=" + reply.Opcode.ToString(CultureInfo.InvariantCulture) + "|origin=" + (reply.DecisionUnbound ? "unbound_native_evaluation" : reply.PolicyDecision ? "pre_skip_policy_value" : reply.NativeDecision ? "matched_observed_post_value" : "managed_dispatch") + "|day=" + reply.DispatchedDay.ToString(CultureInfo.InvariantCulture) + "|activity=" + reply.NativeActivity.ToString() + "|status=" + (reply.LocalRefusal ? "locally_refused_not_native_decision" : reply.Accepted.HasValue ? (reply.Accepted.Value ? "accepted" : "declined") : "unconfirmed"));
            if (reply.SupplyVoice && reply.Accepted.HasValue && !reply.DecisionUnbound)
                Volatile.Write(ref _allyVoices[player], reply);
        }

        private bool PlayAllyOrderReply(HumanAllyOrderAttempt reply)
        {
            // Original SFXManager catalogue: SK_WILL_ATTACK_ENEMY,
            // SK_WILL_SEND_TROOPS, SK_CANT_ATTACK, SK_CANT_HELP. Only our
            // PreSkip path needs replacement audio; ordinary native evaluation
            // already emits its own answer before Post.
            bool defend = reply.Opcode == 11;
            string tag = reply.Accepted == true ? (defend ? "SK_WILL_SEND_TROOPS" : "SK_WILL_ATTACK_ENEMY") : (defend ? "SK_CANT_HELP" : "SK_CANT_ATTACK");
            string stem = reply.Accepted == true ? (defend ? "helpsent" : "willattack") : (defend ? "nohelp" : "noattack");
            // Audio follows only the latest matched human-origin reply; use
            // the actual current AI identity, never a UI personality number.
            int lord;
            IDisposable lease;
            if (!TryEnterNativeInventorySection(out lease))
                return false;
            try
            {
                using (lease)
                {
                    if (!IsLatestHumanOrder(reply) || !IsAlivePlayer(reply.Consulted))
                        return true;
                    lord = (int)GamePlayerManagerAPI.Instance.GetAILord(reply.Consulted);
                }
            }
            catch (Exception exception)
            {
                NoteVassalOrderFault("reply_identity", exception);
                return true;
            }

            string path = lord >= (int)AILords.SK_X1 && lord <= (int)AILords.SK_X8 ? "*" + reply.Consulted.ToString(CultureInfo.InvariantCulture) + "*" + tag : "fx\\speech\\" + (lord >= 0 && lord < StandardLordSpeechPrefixes.Length ? StandardLordSpeechPrefixes[lord] : "all") + "_" + stem + "_01.wav";
            try
            {
                if (SFXManager.instance != null)
                    SFXManager.instance.playSpeech(1, path, 1f);
            }
            catch (Exception exception)
            {
                NoteVassalOrderFault("reply", exception);
            }

            return true;
        }

        private void PublishAllyOrderReply(HumanAllyOrderAttempt reply)
        {
            if (!IsLatestHumanOrder(reply))
                return;
            if (reply.ReplySequence == 0)
                reply.ReplySequence = Interlocked.Increment(ref _allyReplySequence);
            for (;;)
            {
                HumanAllyOrderAttempt previous = Volatile.Read(ref _allyReplies[reply.Consulted]);
                if (previous != null && (previous.ReplySequence >= reply.ReplySequence || previous.NativeDecision && !reply.NativeDecision && ReferenceEquals(previous.DispatchSource, reply.DispatchSource)))
                    return;
                if (!IsLatestHumanOrder(reply))
                    return;
                if (ReferenceEquals(Interlocked.CompareExchange(ref _allyReplies[reply.Consulted], reply, previous), previous))
                    return;
            }
        }

        private void NoteAllyOrderDecision(bool defend, int consulted, int requester, int target, bool accepted, bool supplyVoice)
        {
            if (consulted < 1 || consulted > 8 || requester != Volatile.Read(ref _localPlayer) || !Volatile.Read(ref _mapActive) || Volatile.Read(ref _mapDisabled))
                return;
            HumanAllyOrderAttempt attempt = Volatile.Read(ref _allyInFlight[consulted]);
            if (!IsLatestHumanOrder(attempt) || attempt.Requester != requester || (attempt.Opcode == 11) != defend || attempt.Opcode == 10 || (!defend && (target < 1 || target > 8 || attempt.Target != target)))
                return;
            OriginalAllyEvaluatorObservation evaluation;
            // The unchanged real R3 callers project their scalar event fields:
            // supplyVoice=true only on PreSkip paths, false only on Post.
            // Classify those fields directly; never manufacture an event object.
            if (!OriginalHumanAllyOrderRules.TryClassifyEvaluation(attempt.NativeRequest, defend, supplyVoice ? 0 : 1, supplyVoice, consulted, requester, defend ? 0 : target, accepted, out evaluation))
                return;
            HumanAllyOrderAttempt reply = CopyAllyOrder(attempt);
            reply.DispatchSource = attempt;
            reply.NativeDecision = true;
            // Existing callers: supplyVoice=true is PreSkip; false is Post.
            // Post ReturnValue is observer-mutable, not immutable CPU evidence.
            reply.PolicyDecision = evaluation.Origin == OriginalAllyEvaluatorOrigin.PreSkipPolicy;
            reply.DecisionUnbound = !ReferenceEquals(_dispatchingAllyOrder, attempt) && (Volatile.Read(ref _ambiguousReplyKeys[consulted]) & ReplyKey(attempt)) != 0;
            reply.Accepted = reply.DecisionUnbound ? (bool? )null : evaluation.Accepted;
            reply.SupplyVoice = supplyVoice;
            PublishAllyOrderReply(reply);
        }

        private void AuthorizeAllyAttack(HumanAllyOrderAttempt order)
        {
            int consulted = order.Consulted, requester = order.Requester, target = order.Target;
            if (requester != _localPlayer || !IsCoalitionAllied(consulted) || !IsAlivePlayer(target))
                return;
            string coalitionId;
            RelationState relation;
            if (!_playerCoalition.TryGetValue(target, out coalitionId) || !_relations.TryGetValue(coalitionId, out relation) || relation.Allied || !TreatyProtectsPlayers(requester, target))
                return;
            DiplomacyAgreementRules.BreakAgreementByPlayer(relation, Math.Max(0, _lastProcessedDay), _coalitions[coalitionId].Target, DiplomacyBreachKind.TreacherousAttack, _rules);
            _pendingAgreementSafety.Remove(coalitionId);
            QueueBreachNotice(coalitionId, target, "command=ally_attack|consulted=" + consulted.ToString(CultureInfo.InvariantCulture) + "|target_player=" + target.ToString(CultureInfo.InvariantCulture));
            RefreshTreatyZones(Math.Max(0, _lastProcessedDay));
            // The caller can immediately issue native orders/damage after its
            // evaluator returns. Publish the committed relation before that.
            PublishCombatProtectionSnapshot();
        }
    }
}
