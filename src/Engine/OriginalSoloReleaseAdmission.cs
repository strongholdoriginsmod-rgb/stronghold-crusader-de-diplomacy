// original solo release – release-compiled original singleplayer admission DIAGNOSTIC only.
// No native setter, no SaveData writer, no Steam command, no permission grant.
// Every observed fact comes from actual game/engine/Save owner, not UI/env.
// original solo release can refuse and explain; it CANNOT grant a native goods commit.
using System;
#if !ORIGINAL_SOLO_ADMISSION_POLICY_TEST
using System.Linq;
using StrongholdEuropeDE.TradeNegotiation;
#endif

namespace StrongholdEuropeDE.Runtime
{
    internal sealed class OriginalSoloSessionFacts
    {
        internal bool OriginalLoadedExeAndNativeFile { get; set; }
        internal bool OriginalNativeLoadedImageAbi { get; set; }
        internal bool OriginalManagedGameMethods { get; set; }
        internal bool OriginalRawraMethods { get; set; }
        internal bool ManagedDependenciesAreLoaded { get; set; }
        internal string ManagedDependencyFailure { get; set; }
        internal bool ExactOneHumanAndNoRemotePeer { get; set; }
        internal bool OriginalMapAndRosterCurrent { get; set; }
        internal bool ExclusiveEngineMonitorHeld { get; set; }
        internal bool ModOwnsAllThreeSaveHooks { get; set; }
        internal bool OriginalSaveCallbackDurableForGeneration { get; set; }
        internal bool OriginalAtomicBoundaryIdle { get; set; }
        internal bool NoPendingOrQuarantinedTransaction { get; set; }
        internal bool OriginalWarehouseCapacityAuthority { get; set; }
        internal long CurrentMapGeneration { get; set; }
        internal long OriginalSaveGeneration { get; set; }
    }

    // Pure negative-policy contract. A synthetic set of true values DOES NOT
    // represent real trusted facts; this object never returns write authority.
    internal static class OriginalSoloReleaseAdmission
    {
        internal static bool SessionPrerequisites(OriginalSoloSessionFacts x,
            out string reason)
        {
            reason = "original_session_facts_absent";
            if (x == null) return false;
            if (!x.OriginalLoadedExeAndNativeFile)
                { reason = "loaded_original_exe_or_native_sha_unverified"; return false; }
            if (!x.OriginalNativeLoadedImageAbi)
                { reason = "original_mapped_native_abi_not_independently_attested"; return false; }
            if (!x.OriginalManagedGameMethods)
                { reason = "managed_original_game_methods_not_pinned"; return false; }
            if (!x.OriginalRawraMethods)
                { reason = "managed_rawra_methods_not_pinned"; return false; }
            if (!x.ManagedDependenciesAreLoaded)
                { reason = x.ManagedDependencyFailure ?? "managed_dependencies_not_loaded"; return false; }
            if (!x.ExactOneHumanAndNoRemotePeer)
                { reason = "original_solo_registers_or_remote_peer_unverified"; return false; }
            if (!x.OriginalMapAndRosterCurrent)
                { reason = "current_map_roster_identity_unavailable"; return false; }
            if (!x.ExclusiveEngineMonitorHeld)
                { reason = "exclusive_original_engine_monitor_absent"; return false; }
            if (!x.ModOwnsAllThreeSaveHooks)
                { reason = "current_original_game_save_hooks_not_owned"; return false; }
            if (x.CurrentMapGeneration <= 0 ||
                x.CurrentMapGeneration != x.OriginalSaveGeneration ||
                !x.OriginalSaveCallbackDurableForGeneration)
                { reason = "durable_original_save_current_generation_missing"; return false; }
            if (!x.OriginalAtomicBoundaryIdle)
                { reason = "original_save_or_native_atomic_boundary_not_idle"; return false; }
            if (!x.NoPendingOrQuarantinedTransaction)
                { reason = "native_trade_or_recovery_in_progress"; return false; }
            if (!x.OriginalWarehouseCapacityAuthority)
                { reason = "unattested_original_warehouse_capacity_in_normal_build"; return false; }
            reason = "prerequisites_observed_not_a_trade_commit_grant";
            return true;
        }

        // The only actual goods-grant function in original solo release is permanently false.
        // A future separately reviewed one-shot trade admission must validate
        // the exact quote, original user consent, four native cells, and
        // post-save replay state UNDER the same exclusive engine lease.
        internal static bool AuthorizeNativeGoodsCommit(
            OriginalSoloSessionFacts untrustedFacts) { return false; }
    }

#if !ORIGINAL_SOLO_ADMISSION_POLICY_TEST
    internal sealed partial class DiplomacyFeature
    {
        private bool _releaseAdmissionLogged;

        // Reads original live game and SaveData state. This diagnostic may be
        // called only from the already owned gameplay lane; never from UI
        // input, saved data, remote packets or a headless file marker.
        // Native reads require the genuine EngineInterface.threadLock.
        private bool ObserveNormalSoloAdmission(out string reason)
        {
            reason = "singleplayer_release_runtime_unavailable";
            if (!_mapActive || _mapDisabled || _mapBootstrapPending)
            {
                reason = "active_map_not_ready";
                return false;
            }

            IDisposable lease;
            if (!TryEnterNativeInventorySection(out lease))
            {
                reason = "engine_monitor_busy";
                return false;
            }
            using (lease)
            {
                try
                {
                    OriginalSoloSessionFacts f = new OriginalSoloSessionFacts();
                    string diagnostic;
                    f.OriginalLoadedExeAndNativeFile =
                        SerpsRawraCompat.AttestedLoadedNativeEngine(out diagnostic);
                    // native image readonly independently rechecks the complete original
                    // PE function in the loaded x64 module and the managed/
                    // native building span under original monitor. READ ONLY.
                    // Does NOT enable old full Native/MP authorizer or writes.
                    f.OriginalNativeLoadedImageAbi =
                        OriginalNativeImageReadOnly.TryAttestReadOnly(out diagnostic);
                    f.OriginalManagedGameMethods =
                        VerifiedManagedSurface.VerifyGameAssembly(out diagnostic);
                    f.OriginalRawraMethods =
                        VerifiedManagedSurface.VerifyRawraAssembly(out diagnostic);
                    f.ManagedDependenciesAreLoaded =
                        SerpsRawraCompat.CheckLoadedBepInExDependencies(out diagnostic);
                    f.ManagedDependencyFailure = f.ManagedDependenciesAreLoaded ? null : diagnostic;
                    f.ExactOneHumanAndNoRemotePeer =
                        IsSoloEngineSession(out diagnostic);
                    f.OriginalMapAndRosterCurrent = !string.IsNullOrEmpty(_mapFingerprint) &&
                        !string.IsNullOrEmpty(_rosterFingerprint) && _roster != null &&
                        _localPlayer >= 1 && _localPlayer <= 8 &&
                        _roster.Contains(_localPlayer);
                    f.ExclusiveEngineMonitorHeld = IsNativeDiplomacySectionHeld;
                    f.ModOwnsAllThreeSaveHooks = OriginalMpSaveFenceReady &&
                        _saveBridgeInstalled && _saveHandlerRegistered;
                    // original save evidence: the normal Release now OBSERVES the genuinely
                    // read-back original SaveData for this EXACT generation.
                    // Private QA retains its own previously verified atomic
                    // promotion receipt. Neither witness authorizes GOODS.
                    bool actualNormalReadback = !_privateSaveScope &&
                        HasPassiveOriginalSaveReadback();
                    f.CurrentMapGeneration = _sessionGeneration;
                    f.OriginalSaveGeneration = _privateSaveScope
                        ? _coherentSaveGeneration
                        : (actualNormalReadback ? _sessionGeneration : -1L);
                    f.OriginalSaveCallbackDurableForGeneration =
                        _sessionGeneration > 0 && !_saveSuppressed &&
                        _saveFailurePending == null &&
                        (_privateSaveScope
                            ? _coherentSaveGeneration == _sessionGeneration
                            : actualNormalReadback);
                    f.OriginalAtomicBoundaryIdle = _atomicBoundary != null &&
                        !_atomicBoundary.Quarantined &&
                        !_atomicBoundary.SaveActive && !_atomicBoundary.MutationActive;
                    f.NoPendingOrQuarantinedTransaction =
                        !_tradeFaulted && !_unattestedSavedNativeTrade &&
                        !_inventoryRestorePending && !_goodsTransfersUnavailable &&
                        _pendingPackage == null && _pendingTeamTransition == null &&
                        _nativeTransferProbe == null;
                    // This provider is currently bound ONLY to exact private
                    // disposable sessions. The normal build must not silently
                    // infer capacities from building stack totals.
                    f.OriginalWarehouseCapacityAuthority =
                        _capacityAuthority is OriginalSoloWarehouseCapacityReader &&
                        OriginalSoloWarehouseCapacityReader.ProvenanceReady(out diagnostic);
                    bool observations = OriginalSoloReleaseAdmission.SessionPrerequisites(
                        f, out reason);
                    // Never enable goods, MP vote or Save commit from this
                    // diagnostic, regardless of the evidence it observes.
                    if (OriginalSoloReleaseAdmission.AuthorizeNativeGoodsCommit(f))
                        throw new InvalidOperationException("Unexpected solo admission write authority");
                    return observations;
                }
                catch (Exception e)
                {
                    reason = "release_admission_probe_exception_" + e.GetType().Name;
                    return false;
                }
            }
        }

        private void LogNormalSoloReleaseGateOnce()
        {
            if (_releaseAdmissionLogged) return;
            string reason;
            bool prerequisites = ObserveNormalSoloAdmission(out reason);
            if (string.Equals(reason,"engine_monitor_busy",StringComparison.Ordinal))
                return; // retry only an authentic busy original-engine lease
            _releaseAdmissionLogged = true;
            // one shot admission: production-side one-shot barter diagnostic is READ-ONLY.
            // A verified original save does not imply genuine AI offer,
            // human consent, native four-cell authorizer, or crash durability.
            string oneShotAdmissionReason;
            bool oneShotReady = ObserveUnapprovedOneShotReadOnly(out oneShotAdmissionReason);
            if (oneShotReady)
                throw new InvalidOperationException("Unexpected one-shot trade admission one-shot readiness");
            Log("singleplayer_release", "status=not_authorized" +
                "|one_shot_writes=false|reason=" + Safe(oneShotAdmissionReason) +
                "|reason=" + Safe(reason) +
                "|session_prerequisites=" + (prerequisites ? "observed_only" : "missing") +
                "|original_save_commit_authority=false" +
                "|native_goods_writes=false|multiplayer_authority=false");
        }
    }
#endif
}
