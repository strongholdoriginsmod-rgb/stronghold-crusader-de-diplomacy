using System;
using System.Globalization;
using System.Threading;
using CrusaderDE;
using SHCDESE.API;

namespace StrongholdEuropeDE.Runtime
{
    internal sealed partial class DiplomacyFeature
    {
        // A request is a cancellable UI intent, not a goods transfer or a demand
        // payment. Vanilla owns any request after its command is dispatched.
        private sealed class GoodsRequest
        {
            internal object Panel;
            internal long Generation, Epoch;
            internal string Fingerprint, Coalition;
            internal int LocalPlayer, Target, Good, Amount;
            internal bool ReturnToOverview, Confirmed;
            internal int RequestedDay;
            internal ulong DurableRequestId;
            internal bool DurableApproved;
            // Exact original pre-producer observation is not a Chore receipt.
            // Claim before calling the managed wrapper; never retry on throw.
            internal bool OriginalProducerEncountered, OriginalProducerAllowed,
                OriginalProducerRejected;
            internal DiplomacyPanelCommandLifetime Lifetime;
        }

        private sealed class GoodsRequestInvocation
        {
            internal DiplomacyFeature Owner;
            internal object Panel;
            internal string Command;
            internal bool PermitOriginal = true;
            internal bool DeferExit;
            internal GoodsRequest CapturedRequest;
        }

        private GoodsRequest _goodsRequest;
        [ThreadStatic]
        private static GoodsRequestInvocation _goodsRequestInvocation;
        private void ResetGoodsRequests()
        {
            GoodsRequest request = _goodsRequest;
            _goodsRequest = null;
            if (request != null)
            {
                request.Lifetime.Dispose();
                if (request.DurableRequestId != 0 &&
                    !request.OriginalProducerAllowed)
                    TryNoteDiplomaticRequestCancelled(
                        request.DurableRequestId, false);
                if (!request.OriginalProducerEncountered)
                    Log("goods_request", "stage=cancelled_before_original_producer" +
                        "|source=manual_local_hud" +
                        "|requester_claim=" + request.LocalPlayer +
                        "|requested_player=" + request.Target +
                        "|good=" + request.Good + "|amount=" + request.Amount +
                        "|generation=" + request.Generation + "|epoch=" + request.Epoch +
                        "|native_ack=false|favor_credit=false");
            }
        }

        private void InvokeOriginalGoodsButton(object panel, string command,
            bool deferExit = false, GoodsRequest capturedRequest = null)
        {
            GoodsRequestInvocation previous = _goodsRequestInvocation;
            _goodsRequestInvocation = new GoodsRequestInvocation
            {
                Owner = this,
                Panel = panel,
                Command = command,
                DeferExit = deferExit,
                CapturedRequest = capturedRequest,
            };
            try
            {
                _buttonTarget.Invoke(panel, new object[] { command });
            }
            finally
            {
                _goodsRequestInvocation = previous;
            }
        }

        // Reuse the ALREADY registered GameAction Harmony prefix for the exact
        // original managed goods-request producer. No extra native hook, no
        // transport, and no speculative original AI request attribution.
        private static bool OriginalGoodsRequestGameActionPrefix(
            int requestedPlayer, int good, int amount, ref int wrapperResult)
        {
            DiplomacyFeature owner = Volatile.Read(ref _activeOwner);
            if (owner == null)
                return true;
            GoodsRequestInvocation ticket = _goodsRequestInvocation;
            if (ticket == null || !ReferenceEquals(ticket.Owner, owner) ||
                ticket.Command != "ConfirmGoods" || ticket.CapturedRequest == null)
            {
                // Without the one-use HUD ticket, no RequestHistory origin is proven.
                // Bounded primitive-only capture replaces the unsafe native
                // passthrough. The original CPU producer is not reclassified.
                return CaptureUnboundOriginalGoodsManagedIngress(
                    owner, requestedPlayer, good, amount, ref wrapperResult);
            }

            GoodsRequest request = ticket.CapturedRequest;
            // Mark BEFORE possible native execution; a second/reentrant call
            // is not a second authorized request, even if the first throws.
            if (request.OriginalProducerEncountered)
            {
                request.OriginalProducerRejected = true;
                wrapperResult = 0; // original wrapper convention, NOT ACK
                return false;
            }
            request.OriginalProducerEncountered = true;
            bool matches = false;
            try
            {
                var players = GamePlayerManagerAPI.Instance;
                matches =
                    Monitor.IsEntered(owner._gate) &&
                    IsNativeDiplomacySectionHeld &&
                    owner._mapActive && !owner._mapDisabled &&
                    !owner._mapBootstrapPending &&
                    !owner._inventoryRestorePending &&
                    owner._activeSave == null &&
                    !owner._saveSuppressed &&
                    owner._pendingSavePayload == null &&
                    owner._pendingLegacySavePayload == null &&
                    owner._executingIntent == null &&
                    owner._pendingTeamTransition == null &&
                    owner._pendingPackage == null &&
                    owner._nativeTransferProbe == null &&
                    ReferenceEquals(ticket.Panel, request.Panel) &&
                    request.DurableRequestId != 0 &&
                    request.DurableApproved &&
                    request.Generation == owner._sessionGeneration &&
                    request.Epoch == owner._mapEpoch &&
                    String.Equals(request.Fingerprint, owner._mapFingerprint,
                        StringComparison.Ordinal) &&
                    request.LocalPlayer == owner._localPlayer &&
                    players != null &&
                    players.GetLocalPlayerId() == request.LocalPlayer &&
                    request.Target == requestedPlayer &&
                    request.Good == good && request.Amount == amount &&
                    good > 0 && amount > 0 && amount <= 1000000 &&
                    IsSendableGood(good) &&
                    owner.CanRequestGoodsFrom(request.Target) &&
                    owner.GoodsRequestCurrent(request);
            }
            catch (Exception) { matches = false; }
            if (!matches)
            {
                request.OriginalProducerRejected = true;
                wrapperResult = 0;
                return false;
            }
            // RequestHistory records a SINGLE original-wrapper submission attempt.
            // It does not claim that Chore113 actually entered the queue.
            if (!owner.TryNoteDiplomaticRequestForwarded(
                    request.DurableRequestId))
            {
                request.OriginalProducerRejected = true;
                wrapperResult = 0;
                return false;
            }
            request.OriginalProducerAllowed = true;
            return true; // Only the existing ORIGINAL managed wrapper can enqueue.
        }

        private void BeginGoodsRequest(object panel, int target, bool returnToOverview)
        {
            ResetGoodsRequests();
            if (!CanPresentDiplomacyUi || !_mapActive || _mapDisabled || _mapBootstrapPending || panel == null || !ReferenceEquals(panel, CurrentAlliesPanel()) || target < 1 || target > 8 || target == _localPlayer || !CanRequestGoodsFrom(target) || !MainViewModel.Instance.AlliesPanelVisible || !IsCoalitionAllied(target))
                return;
            int slot = _visiblePlayers.IndexOf(target);
            if (slot < 0)
                return;
            CancelPendingGoodsCommand("request_goods");
            _pendingGoodsTarget = 0;
            _selectedVisibleIndex = slot;
            _currentAllyField.SetValue(panel, slot);
            _overviewVisible = false;
            _updateAlliesTarget.Invoke(panel, null);
            if (SelectedPlayer(panel) != target)
                return;
            MainViewModel view = MainViewModel.Instance;
            view.Allies_OrdersViewVis = false;
            // COPY HUD.Request does NOT clear this flag, while ConfirmGoods
            // dispatches solely from it. Normalize it before the original call.
            view.Allies_SendGoodsViewVis = false;
            view.Allies_NoAlliesViewVis = false;
            InvokeOriginalGoodsButton(panel, "Request");
            _goodsRequest = new GoodsRequest
            {
                Panel = panel,
                Generation = _sessionGeneration,
                Epoch = _mapEpoch,
                Fingerprint = _mapFingerprint,
                Coalition = _playerCoalition[target],
                LocalPlayer = _localPlayer,
                Target = target,
                RequestedDay = Math.Max(0, _lastProcessedDay),
                ReturnToOverview = returnToOverview,
                Lifetime = new DiplomacyPanelCommandLifetime(view),
            };
            Log("goods_request",
                "stage=manual_hud_request_captured_before_original_producer" +
                "|requester_claim=" + _goodsRequest.LocalPlayer +
                "|requested_player=" + _goodsRequest.Target +
                "|day=" + _goodsRequest.RequestedDay +
                "|generation=" + _goodsRequest.Generation +
                "|epoch=" + _goodsRequest.Epoch +
                "|native_ack=false|favor_credit=false");
            RefreshVanillaDiplomacyUi(panel);
        }

        // Adopt a genuinely visible vanilla request modal if its earlier
        // Request UI event was not captured (e.g. a restored/foreign panel).
        // The user is not stranded by simply suppressing ConfirmGoods.
        // Strict original selected portrait + current generation/epoch are
        // captured BEFORE any original GameAction can be attempted.
        private bool TryCaptureVisibleOriginalRequestConfirm(object panel)
        {
            try
            {
                MainViewModel view = MainViewModel.Instance;
                if (!_mapActive || _mapDisabled || _mapBootstrapPending ||
                    !CanPresentDiplomacyUi || _overviewVisible ||
                    HasPendingGoodsCommand || _pendingGoodsTarget != 0 ||
                    view == null || !view.AlliesPanelVisible ||
                    !view.Allies_GoodsViewVis ||
                    !view.Allies_RequestGoodsViewVis ||
                    view.Allies_SendGoodsViewVis ||
                    !ReferenceEquals(panel, CurrentAlliesPanel()))
                    return false;
                int target = SelectedPlayer(panel);
                int good = (int)_selectedGoodsField.GetValue(panel);
                int amount = (int)_selectedGoodsAmountField.GetValue(panel);
                string coalition;
                if (target < 1 || target > 8 || target == _localPlayer ||
                    !CanRequestGoodsFrom(target) || !IsCoalitionAllied(target) ||
                    !_playerCoalition.TryGetValue(target, out coalition) ||
                    !IsSendableGood(good) || amount <= 0 || amount > 1000000)
                    return false;
                var request = new GoodsRequest
                {
                    Panel = panel,
                    Generation = _sessionGeneration,
                    Epoch = _mapEpoch,
                    Fingerprint = _mapFingerprint,
                    Coalition = coalition,
                    LocalPlayer = _localPlayer,
                    Target = target,
                    Good = good,
                    Amount = amount,
                    RequestedDay = Math.Max(0, _lastProcessedDay),
                    Confirmed = true,
                    Lifetime = new DiplomacyPanelCommandLifetime(view),
                };
                _goodsRequest = request;
                Log("goods_request",
                    "stage=visible_original_modal_captured_before_producer" +
                    "|requester_claim=" + request.LocalPlayer +
                    "|requested_player=" + target + "|good=" + good +
                    "|amount=" + amount + "|generation=" + request.Generation +
                    "|epoch=" + request.Epoch +
                    "|native_ack=false|favor_credit=false");
                PumpGoodsRequests();
                return true;
            }
            catch (Exception exception)
            {
                DegradeUiRefresh("goods_request_modal_recovery", panel, exception);
                return false;
            }
        }

        // Return value says whether this helper decided the command. The out
        // value has HandleButton's convention: true consumes; false runs native.
        // The one-use original permit is bound to this owner, thread and panel.
        private bool TryHandleGoodsRequestCommand(object panel, string param, out bool handledReturn)
        {
            handledReturn = true;
            if (string.IsNullOrEmpty(param))
                return false;
            GoodsRequestInvocation invocation = _goodsRequestInvocation;
            if (invocation != null && ReferenceEquals(invocation.Owner, this) && ReferenceEquals(invocation.Panel, panel))
            {
                if (invocation.PermitOriginal && invocation.Command == param)
                {
                    invocation.PermitOriginal = false;
                    handledReturn = false;
                    return true;
                }

                // COPY ConfirmGoods calls Exit immediately after GameAction.
                // Suppress only that recursive UI operation while native owns
                // the monitor; navigation runs after its release below.
                if (invocation.DeferExit && param == "Exit")
                    return true;
                if (param == "ConfirmGoods")
                    return true;
            }

            if (!_mapActive || _mapDisabled || _mapBootstrapPending)
            {
                ResetGoodsRequests();
                return false;
            }

            GoodsRequest request = _goodsRequest;
            if (request == null)
            {
                // Do not turn a usable original request modal into an inert
                // Confirm button. Capture its exact current operands and use
                // the same bounded mod pipeline; if invalid, defer safely.
                if (param == "ConfirmGoods" && MainViewModel.Instance != null &&
                    MainViewModel.Instance.Allies_RequestGoodsViewVis)
                {
                    TryCaptureVisibleOriginalRequestConfirm(panel);
                    return true;
                }
                return false;
            }
            // A delayed callback from a replaced HUD cannot confirm, cancel or
            // retarget the currently displayed request's selection.
            if (!ReferenceEquals(panel, request.Panel))
                return true;
            if (param == "Back")
            {
                ResetGoodsRequests();
                ReturnFromGoodsRequest(request);
                return true;
            }

            if (param == "Exit" || param == "Send" || param == "Orders" || param == "Request" || param.StartsWith("Diplomacy:", StringComparison.Ordinal) || (param.Length == 1 && param[0] >= '0' && param[0] <= '5'))
            {
                ResetGoodsRequests();
                return false;
            }

            if (!GoodsRequestCurrent(request))
            {
                ResetGoodsRequests();
                return param == "ConfirmGoods" || param == "CancelGoods";
            }

            if (param == "ConfirmGoods")
            {
                if (!request.Confirmed)
                {
                    int good = (int)_selectedGoodsField.GetValue(panel);
                    int amount = (int)_selectedGoodsAmountField.GetValue(panel);
                    if (!IsSendableGood(good) || amount <= 0 || amount > 1000000)
                        return true;
                    request.Good = good;
                    request.Amount = amount;
                    request.Confirmed = true;
                    Log("goods_request",
                        "stage=mod_request_confirmed_pending_native" +
                        "|requester_claim=" + request.LocalPlayer +
                        "|requested_player=" + request.Target +
                        "|good=" + good + "|amount=" + amount +
                        "|generation=" + request.Generation +
                        "|epoch=" + request.Epoch +
                        "|native_ack=false|favor_credit=false");
                }

                PumpGoodsRequests();
                return true;
            }

            if (param == "CancelGoods")
            {
                // An approved RequestHistory tuple cannot be reused for another good
                // after this original CancelGoods. Keep native cancellation
                // unproven; the mod intent itself is explicitly withdrawn.
                ResetGoodsRequests();
                return false;
            }

            if (request.Confirmed && (param.StartsWith("G", StringComparison.Ordinal) || param.StartsWith("X", StringComparison.Ordinal)))
            {
                PresentGoodsRequestWaiting(request);
                return true;
            }

            return false;
        }

        private bool GoodsRequestCurrent(GoodsRequest request)
        {
            try
            {
                string coalition;
                MainViewModel view = MainViewModel.Instance;
                return _mapActive && !_mapDisabled && !_mapBootstrapPending && request.Generation == _sessionGeneration && request.Epoch == _mapEpoch && string.Equals(request.Fingerprint, _mapFingerprint, StringComparison.Ordinal) && request.LocalPlayer == _localPlayer && !request.Lifetime.Cancelled && ReferenceEquals(request.Panel, CurrentAlliesPanel()) && !_overviewVisible && SelectedPlayer(request.Panel) == request.Target && _playerCoalition.TryGetValue(request.Target, out coalition) && coalition == request.Coalition && IsCoalitionAllied(request.Target) && CanRequestGoodsFrom(request.Target) && view != null && view.Allies_GoodsViewVis && view.Allies_RequestGoodsViewVis && !view.Allies_SendGoodsViewVis;
            }
            catch (Exception exception)
            {
                DegradeUiRefresh("goods_request_selection", request.Panel, exception);
                return false;
            }
        }

        private void PumpGoodsRequests()
        {
            // RequestHistory alone owns the bounded, persisted expiry transition.
            // Expiry changes no original native goods/order state.
            DrainExpiredDiplomaticRequests();
#if DIPLOMACY_PRIVATE_TESTS
            // Aggregate immutable original native callback observations on
            // the owning frame; NEVER run logging on the simulation callback.
            PumpOriginalNativeGoodsEvidence();
#endif
            PumpUnknownOriginalGoodsManagedIngress();
            GoodsRequest request = _goodsRequest;
            if (request == null)
                return;
            // RequestHistory's neutral 7-day tracking window can expire an Approved
            // request. Never reuse that old ID or spin forever on TryDecide.
            // This is a MOD tracking timeout, not native cancellation.
            if (request.DurableRequestId != 0 &&
                _lastProcessedDay >= request.RequestedDay &&
                (long)_lastProcessedDay - request.RequestedDay >=
                    DiplomaticRequestHistory.NeutralTrackingWindowDays)
            {
                TryNoteDiplomaticRequestExpired(request.DurableRequestId);
                ResetGoodsRequests();
                return;
            }
            if (!GoodsRequestCurrent(request))
            {
                ResetGoodsRequests();
                return;
            }

            if (!request.Confirmed || !CanPresentDiplomacyUi)
                return;
#if !DIPLOMACY_PRIVATE_TESTS
            // A real producer may only run while our final validator is active.
            if (!_sharedGoodsRequestActive)
            {
                ResetGoodsRequests();
                return;
            }
#endif
            if (_inventoryRestorePending || _nativeTransferProbe != null || _pendingPackage != null || _pendingTeamTransition != null)
            {
                PresentGoodsRequestWaiting(request);
                return;
            }

            IDisposable lease;
            if (!TryEnterNativeInventorySection(out lease))
            {
                PresentGoodsRequestWaiting(request);
                return;
            }

            bool claimed = false;
            try
            {
                using (lease)
                {
                    // These are fresh native liveness/team reads. The checked
                    // panel roster supplies the same target as original Confirm.
                    if (!GoodsRequestCurrent(request) || !CanRequestGoodsFrom(request.Target) || !IsAlivePlayer(request.LocalPlayer) || !IsAlivePlayer(request.Target) || _teamPort.GetTeam(request.LocalPlayer) != _teamPort.GetTeam(request.Target) || !IsSendableGood(request.Good) || request.Amount <= 0)
                    {
                        ResetGoodsRequests();
                        return;
                    }

                    // Busy RequestHistory/save owner is retryable; no original
                    // producer/lifetime claim occurs without durable identity.
                    if (request.DurableRequestId == 0)
                    {
                        ulong id;
                        if (!TryCaptureDiplomaticRequest(
                                DiplomaticRequestKind.Goods,
                                DiplomaticRequestOrigin.OriginalHumanGoods,
                                request.LocalPlayer, request.Target,
                                0, request.Good, request.Amount, 0, out id))
                        {
                            PresentGoodsRequestWaiting(request);
                            return;
                        }
                        request.DurableRequestId = id;
                    }
                    if (!request.DurableApproved)
                    {
                        if (!TryDecideDiplomaticRequest(request.DurableRequestId,
                                true, DiplomaticRequestDecisionEvidence.ModPolicy))
                        {
                            PresentGoodsRequestWaiting(request);
                            return;
                        }
                        request.DurableApproved = true;
                    }
                    if (!request.Lifetime.TryClaim())
                    {
                        ResetGoodsRequests();
                        return;
                    }

                    claimed = true;
                    _goodsRequest = null;
                    _pendingGoodsTarget = 0;
                    // Restore only captured private fields, never later mutable
                    // selector values. No inventory read, debit or mod journal.
                    _selectedGoodsField.SetValue(request.Panel, request.Good);
                    _selectedGoodsAmountField.SetValue(request.Panel, request.Amount);
                    // The managed GameAction prefix is already installed for
                    // ally orders. Its goods branch binds the real original
                    // producer to THIS exact claimant, before native enqueue.
                    InvokeOriginalGoodsButton(request.Panel, "ConfirmGoods", true, request);
                    if (!request.OriginalProducerAllowed &&
                        request.DurableRequestId != 0 &&
                        request.Generation == _sessionGeneration &&
                        request.Epoch == _mapEpoch &&
                        string.Equals(request.Fingerprint, _mapFingerprint,
                            StringComparison.Ordinal))
                        // A foreign Pre/Final callback can switch map epoch
                        // before the original ButtonClicked reflection returns.
                        // Never cancel a request in a DIFFERENT map merely
                        // because it reused an old durable numeric ID.
                        TryNoteDiplomaticRequestCancelled(
                            request.DurableRequestId, false);
                    Log("goods_request",
                        "stage=" + (request.OriginalProducerAllowed ?
                            "original_GameAction_preflight_passed_outcome_unknown" :
                            request.OriginalProducerRejected ?
                            "original_GameAction_rejected_no_enqueue" :
                            "original_GameAction_not_reached") +
                        "|requester_claim=" + request.LocalPlayer +
                        "|requested_player=" + request.Target +
                        "|good=" + request.Good + "|amount=" + request.Amount +
                        "|day=" + request.RequestedDay +
                        "|request_id=" + request.DurableRequestId +
                        "|generation=" + request.Generation +
                        "|epoch=" + request.Epoch +
                        "|native_ack=false|delivery_unproven=true" +
                        "|favor_credit=false");
                }
            }
            catch (Exception exception)
            {
                // Native may already have accepted the request before a foreign
                // callback throws. Never retry a claimed command in that case.
                if (claimed)
                    Log("goods_request",
                        "stage=original_enqueue_outcome_unknown_no_retry" +
                        "|requester_claim=" + request.LocalPlayer +
                        "|requested_player=" + request.Target +
                        "|good=" + request.Good + "|amount=" + request.Amount +
                        "|native_ack=false|favor_credit=false");
                ResetGoodsRequests();
                DegradeUiRefresh("goods_request_dispatch", request.Panel, exception);
            }
            finally
            {
                if (claimed)
                    ReturnFromGoodsRequest(request);
            }
        }

        private void PresentGoodsRequestWaiting(GoodsRequest request)
        {
            if (!CanPresentDiplomacyUi || !GoodsRequestCurrent(request))
                return;
            try
            {
                MainViewModel.Instance.Allies_GoodConfirmVis = false;
                MainViewModel.Instance.AlliesGoods_Info = request.Amount.ToString(CultureInfo.InvariantCulture) + " " + GoodName(request.Good, request.Amount) + ": " + Text("Die Bitte wartet auf das Spiel. Zurück oder Schließen bricht sie ab.", "The request is waiting for the game. Back or Close cancels it.");
            }
            catch (Exception exception)
            {
                DegradeUiRefresh("goods_request_pending", request.Panel, exception);
            }
        }

        private void ReturnFromGoodsRequest(GoodsRequest request)
        {
            if (!CanPresentDiplomacyUi || !_mapActive || _mapDisabled || _mapBootstrapPending || request.Generation != _sessionGeneration || request.Epoch != _mapEpoch || !string.Equals(request.Fingerprint, _mapFingerprint, StringComparison.Ordinal) || !ReferenceEquals(request.Panel, CurrentAlliesPanel()) || MainViewModel.Instance == null || !MainViewModel.Instance.AlliesPanelVisible)
                return;
            try
            {
                InvokeOriginalGoodsButton(request.Panel, "Back");
                if (request.ReturnToOverview)
                    OpenAllianceOverview(request.Panel, request.Target);
                RefreshVanillaDiplomacyUi(request.Panel);
            }
            catch (Exception exception)
            {
                DegradeUiRefresh("goods_request_return", request.Panel, exception);
            }
        }
    }
}
