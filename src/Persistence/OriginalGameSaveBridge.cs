//  actual managed EngineInterface save connection. No native detour is added.
// The normal build fences MP consent saves after exact managed verification;
// normal solo remains passive. Native promotion retains the exact disposable scope.
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Threading;
using HarmonyLib;
using CrusaderDE;
using SHCDESE.API;
using SHCDESE.API.Components.SaveData;
using SHCDESE.API.Components.Archive;
using StrongholdEuropeDE.TradeModels;
using StrongholdEuropeDE.TradeNegotiation;

namespace StrongholdEuropeDE.Runtime
{
    internal static class DisposableGameScope
    {
        internal static string GameRoot
        {
            get
            {
                return Environment.GetEnvironmentVariable("DIPLOMACY_TEST_GAME_ROOT") ?? string.Empty;
            }
        }

        internal static string ProfileRoot
        {
            get
            {
                return Environment.GetEnvironmentVariable("DIPLOMACY_TEST_PROFILE_ROOT") ?? string.Empty;
            }
        }

        internal const string GameManagedSha = "bc8b6a395f01d48557db413600c8dd8d1fdfd3abdf97bfbbb68a3c56b04fd789";
        internal const string RawraSha = "5cb1c33be5463086505a17f5c48aaa357d733cfff7240e101c821f0e3c668498";
#if DIPLOMACY_PRIVATE_TESTS
        private static bool verified;
#endif
        internal static string HashFile(string file)
        {
            using (var f = File.OpenRead(file))
            using (var h = SHA256.Create())
                return BitConverter.ToString(h.ComputeHash(f)).Replace("-", "").ToLowerInvariant();
        }

        internal static bool IsEnabled(out string reason)
        {
            reason = "disposable_native_test_not_compiled";
#if DIPLOMACY_PRIVATE_TESTS
            try
            {
                if (string.IsNullOrWhiteSpace(GameRoot) || string.IsNullOrWhiteSpace(ProfileRoot) || !Path.IsPathRooted(GameRoot) || !Path.IsPathRooted(ProfileRoot))
                {
                    reason = "explicit_private_test_roots_required";
                    return false;
                }

                using (var p = Process.GetCurrentProcess())
                {
                    string exe = p.MainModule == null ? null : p.MainModule.FileName;
                    if (string.IsNullOrEmpty(exe) || !string.Equals(Path.GetFullPath(exe), Path.Combine(GameRoot, "Stronghold Crusader Definitive Edition.exe"), StringComparison.OrdinalIgnoreCase))
                    {
                        reason = "not_our_disposable_game";
                        return false;
                    }
                }

                if (Environment.GetEnvironmentVariable("DIPLOMACY_HEADLESS_TEST") == "1" && !string.Equals(typeof(DiplomacyFeature).Assembly.ManifestModule.ModuleVersionId.ToString("D"), Environment.GetEnvironmentVariable("DIPLOMACY_EXPECTED_MODULE_ID"), StringComparison.OrdinalIgnoreCase))
                {
                    reason = "headless_loaded_module_differs_from_reviewed_build";
                    return false;
                }

                if (!verified)
                {
                    if (!VerifiedManagedSurface.VerifyGameAssembly(out reason) || !VerifiedManagedSurface.VerifyRawraAssembly(out reason) || !SerpsRawraCompat.AttestedLoadedNativeEngine(out reason))
                        return false;
                    verified = true;
                }

                reason = "exact_disposable_game_originals_matched";
                return true;
            }
            catch (Exception e)
            {
                reason = "disposable_scope_" + e.GetType().Name;
                return false;
            }
#else
            return false;
#endif
        }
    }

    internal sealed partial class DiplomacyFeature
    {
        private AtomicBoundary _atomicBoundary;
        private AtomicBoundary.MutationScope _nativeMutation;
        private SaveCall _activeSave;
        private MethodInfo _saveTarget;
        private bool _saveBridgeInstalled;
        private bool _privateSaveScope;
        private string _savePreflightReason = "no_save_attempt";
        private sealed class SaveCall
        {
            internal DiplomacyFeature Owner;
            internal IDisposable NativeLease;
            internal AtomicBoundary.SaveScope Scope;
            internal AtomicSaveFile File;
            internal byte[] NativeBefore, LegacyPayload;
            internal string OriginalPath;
            internal int ThreadId;
            internal long Generation;
            internal bool StateGateHeld, Released, MpConsentOriginalSave;
            internal byte[] MpConsentPayload;
            // Actual returned callback arrays are retained, not merely issued booleans.
            // Comparing them again in the postfix detects changed/wrong issued bytes.
            internal byte[] MpPrimaryCallbackBytes, MpLegacyCallbackBytes;
            internal int MpCallbackFault;
        }

        private AtomicBoundary TradeSaveBoundary()
        {
            RequireNativeDiplomacySection();
            if (_atomicBoundary == null)
                _atomicBoundary = new AtomicBoundary(_nativeInventoryMonitor);
            return _atomicBoundary;
        }

        private bool BeginNativeMutation(out AtomicBoundary.MutationScope scope)
        {
            RequireNativeDiplomacySection();
            scope = null;
            if (_nativeMutation != null)
                return false;
            if (!TradeSaveBoundary().TryBeginMutation(out scope))
                return false;
            _nativeMutation = scope;
            return true;
        }

        private void EndNativeMutation(AtomicBoundary.MutationScope scope)
        {
            if (scope == null)
                return;
            try
            {
                scope.Dispose();
            }
            finally
            {
                if (Object.ReferenceEquals(_nativeMutation, scope))
                    _nativeMutation = null;
            }
        }

        private void NoteNativeWrite()
        {
            if (_nativeMutation != null)
                _nativeMutation.BeforeNativeWrite();
        }

        private void NoteNativeSettled()
        {
            if (_nativeMutation != null)
                _nativeMutation.MarkSettled();
        }

        // One managed SaveSaveGame hook; installation is independent of native goods.
        // The exact original PE + runtime semantic IL pins are already shared with
        // private verification. No native attestation is granted by this path.
        private bool OriginalMpSaveFenceReady
        {
            get
            {
                if (!_saveBridgeInstalled || _saveTarget == null || _harmony == null)
                    return false;
                var patches = Harmony.GetPatchInfo(_saveTarget);
                if (patches == null)
                    return false;
                Func<Patch, string, bool> ours = (p, n) => p.owner == _harmony.Id && p.PatchMethod.DeclaringType == typeof(DiplomacyFeature) && p.PatchMethod.Name == n;
                return patches.Prefixes.Any(p => ours(p, "SavePrefix")) && patches.Postfixes.Any(p => ours(p, "SavePostfix")) && patches.Finalizers.Any(p => ours(p, "SaveFinalizer"));
            }
        }

        private void InstallGameSaveBridge()
        {
            if (_saveBridgeInstalled)
                return;
            string reason;
            bool privateScope = DisposableGameScope.IsEnabled(out reason);
            if (!privateScope && (!VerifiedManagedSurface.VerifyGameAssembly(out reason) || !VerifiedManagedSurface.VerifyRawraAssembly(out reason)))
            {
                Log("trade_save", "status=mp_save_fence_unavailable|reason=" + Safe(reason));
                return;
            }

            if (!TryResolveNativeInventoryMonitor(out reason))
            {
                Log("trade_save", "status=mp_save_monitor_unavailable|reason=" + Safe(reason));
                return;
            }

            try
            {
                Type engine = typeof(MainViewModel).Assembly.GetType("EngineInterface", true);
                var target = engine.GetMethod("SaveSaveGame", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string), typeof(int), typeof(int), typeof(int), typeof(int), typeof(bool), typeof(bool), typeof(bool) }, null);
                if (target == null || target.ReturnType != typeof(bool))
                    throw new MissingMethodException("Original SaveSaveGame ABI changed");
                var flags = BindingFlags.NonPublic | BindingFlags.Static;
                _saveTarget = target; // retain target for cleanup if a patch operation fails
                _harmony.Patch(target, prefix: new HarmonyMethod(typeof(DiplomacyFeature).GetMethod("SavePrefix", flags)) { priority = Priority.First }, postfix: new HarmonyMethod(typeof(DiplomacyFeature).GetMethod("SavePostfix", flags)) { priority = Priority.Last }, finalizer: new HarmonyMethod(typeof(DiplomacyFeature).GetMethod("SaveFinalizer", flags)));
                _privateSaveScope = privateScope;
                _saveBridgeInstalled = true;
                if (!OriginalMpSaveFenceReady)
                    throw new InvalidOperationException("Original save patches incomplete");
                Log("trade_save", "status=original_managed_save_barrier_installed|scope=" + (privateScope ? "disposable_game" : "verified_mp_consent_only"));
            }
            catch (Exception e)
            {
                try
                {
                    UninstallGameSaveBridge();
                }
                catch
                {
                }

                _saveBridgeInstalled = false;
                _privateSaveScope = false;
                Log("trade_save", "status=mp_save_fence_unavailable|reason=" + Safe(e.GetType().Name));
            }
#if !DIPLOMACY_PRIVATE_TESTS
            if (_saveBridgeInstalled)
                InstallSharedSoloSaveReadback();
#endif
        }

        private void UninstallGameSaveBridge()
        {
            ClearOriginalSaveEvidence();
            _saveBridgeInstalled = false;
            _privateSaveScope = false;
            if (_saveTarget != null)
            {
                _harmony.Unpatch(_saveTarget, HarmonyPatchType.Prefix, _harmony.Id);
                _harmony.Unpatch(_saveTarget, HarmonyPatchType.Postfix, _harmony.Id);
                _harmony.Unpatch(_saveTarget, HarmonyPatchType.Finalizer, _harmony.Id);
            }

            _saveTarget = null;
        }

        private static bool SavePrefix(ref string __0, bool __7, ref bool __result, out SaveCall __state)
        {
            __state = null;
            DiplomacyFeature owner;
            lock (OwnerGate)
            {
                owner = _activeOwner;
            }

            if (owner == null || !owner._saveBridgeInstalled)
                return true;
            // EngineInterface.threadLock is a recursive monitor. A synchronous or
            // foreign-thread nested SaveSaveGame can enter this prefix while the
            // original save is still active. Block *both* map-save and game-save
            // recursion, and poison the outer payload before any nested native call.
            // Poison() is an interlocked operation: no wait on _gate or native lock.
            SaveCall alreadySaving = Volatile.Read(ref owner._activeSave);
            if (alreadySaving != null)
            {
                if (alreadySaving.Scope != null)
                    alreadySaving.Scope.Poison();
                __result = false;
                try
                {
                    owner.Log("trade_ingress", "status=nested_game_save_refused_parent_poison_requested");
                }
                catch
                {
                }

                return false;
            }

            if (owner._mpConsentMode || owner._mpBootstrapRequested || owner._mpQuarantined || owner._pendingOriginalMpLoadBytes != null)
            {
                // The selected SaveContext contract covers game saves, not map exports.
                // Refuse before original/native dispatch without quarantining the owner.
                if (__7)
                {
                    __result = false;
                    return false;
                }

                try
                {
                    SaveCall mpCall;
                    if (!owner.PrepareOriginalMpExternalSave(__0, out mpCall))
                    {
                        __result = false;
                        return false;
                    }

                    __state = mpCall;
                    return true;
                }
                catch (Exception e)
                {
                    __result = false;
                    try
                    {
                        owner.Log("trade_save", "status=mp_save_preflight_refused|reason=" + Safe(e.GetType().Name));
                    }
                    catch
                    {
                    }

                    return false;
                }
            }

            // The public solo observer lives in APIShared. Private QA keeps the
            // original readback path for its disposable normal-save diagnostic.
            if (!owner._privateSaveScope)
            {
#if DIPLOMACY_PRIVATE_TESTS
                owner.PrepareOriginalSoloSaveWitness(__0, __7, new OriginalSaveWitness());
#endif
                return true;
            }
            if (__7)
                return true;
            SaveCall call = null;
            try
            {
                if (!owner.PrepareGameSave(__0, out call))
                {
                    try
                    {
                        owner.Log("trade_session", "status=save_preflight_waiting|reason=" + Safe(owner._savePreflightReason));
                    }
                    catch
                    {
                    }

                    __result = false;
                    return false;
                }

                __state = call;
                __0 = call.File.TemporaryPath;
                return true;
            }
            catch (Exception e)
            {
                if (call != null)
                    owner.ReleaseGameSave(call);
                owner.Log("trade_save", "status=save_preflight_refused|reason=" + Safe(e.GetType().Name));
                __result = false;
                return false;
            }
        }

        private static void SavePostfix(ref string __0, ref bool __result, SaveCall __state)
        {
            if (__state == null)
            {
#if DIPLOMACY_PRIVATE_TESTS
                DiplomacyFeature savedOwner;
                lock (OwnerGate) { savedOwner = _activeOwner; }
                if (savedOwner != null)
                    savedOwner.ObservePrivateSoloSaveCompletion(__0, __result);
#endif
                return;
            }
            var c = __state;
            if (c.MpConsentOriginalSave)
            {
                try
                {
                    // An original false is an ordinary aborted save. Missing callbacks alone
                    // then give no contradiction and must not permanently disable MP.
                    if (__result && !c.Owner.MpCallbacksExact(c))
                    {
                        __result = false;
                        c.Owner._mpQuarantined = true;
                        c.Owner._confirmedOuterSaveBytes = null;
                        c.Owner.Log("trade_save", "status=mp_save_completion_unconfirmed|reason=callback_witness");
                    }
                // This result check cannot undo an already written file and is NOT a
                // physical .sav receipt, repair or private atomic promotion.
                }
                finally
                {
                    c.Owner.ReleaseGameSave(c);
                }

                return;
            }

            try
            {
                // Restore rawra's path metadata BEFORE publishing the verified file.
                var context = typeof(GameMapArchiveManagerAPI).GetMethod("SetCurrentFileContext", BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { typeof(string), typeof(bool) }, null);
                if (context == null)
                    throw new MissingMethodException("Original archive context ABI changed");
                context.Invoke(GameMapArchiveManagerAPI.Instance, new object[] { c.OriginalPath, true });
                string reason;
                bool accepted = c.File.Complete(__result, path => c.Owner.VerifySavedArchive(c, path), () => c.Scope.IsValid && c.Scope.CallbackWasIssued && c.NativeBefore.SequenceEqual(c.Owner.NativeSaveWitness()), () => c.Scope.TrySealForPublication(), out reason);
                // The atomic file's Committed flag is the publication linearization
                // point. No diagnostic exception after promotion may report "not saved".
                __result = accepted;
                if (c.File.Committed && c.Generation == c.Owner._sessionGeneration)
                    c.Owner._coherentSaveGeneration = c.Generation;
#if DIPLOMACY_PRIVATE_TESTS
                if (c.File.Committed)
                    c.Owner.SealOriginalAidHistory(c);
                if (c.File.Committed && string.Equals(Path.GetFileName(c.OriginalPath), "AfterNativeTrade.sav", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        c.Owner.WriteColdExpectation(c.OriginalPath);
                    }
                    catch (Exception evidenceError)
                    {
                        HeadlessQaDriver.Note("status=cold_expectation_write_failed|reason=" + evidenceError);
                    }
                }

#endif
                try
                {
                    c.Owner.Log("trade_save", accepted ? "status=original_game_save_verified_and_promoted|bytes=" + c.File.SavedBytes + "|sha=" + c.File.SavedSha256 : "status=save_not_promoted|reason=" + Safe(reason));
                }
                catch (Exception)
                {
                } // logging has no authority to undo a promoted save
            }
            catch (Exception e)
            {
                // A saved file may ALREADY have replaced the previous destination when
                // verification/notification failed. Observe the actual linearization
                // marker, not the exception, before reporting the original Save result.
                __result = c.File != null && c.File.Committed;
                if (__result && c.Generation == c.Owner._sessionGeneration)
                    c.Owner._coherentSaveGeneration = c.Generation;
                try
                {
                    c.Owner.Log("trade_rebellion", (__result ? "status=post_publish_callback_fault" : "status=save_verification_failed") + "|reason=" + Safe(e.GetType().Name));
                }
                catch (Exception)
                {
                }
            }
            finally
            {
                __0 = c.OriginalPath;
                try
                {
                    c.Owner.ReleaseGameSave(c);
                }
                catch (Exception cleanupError)
                {
                    // A cleanup fault does not unpublish an already replaced file. It DOES
                    // quarantine further native mutations until operator review.
                    c.Owner._tradeFaulted = true;
                    c.Owner._goodsTransfersUnavailable = true;
                    __result = c.File != null && c.File.Committed;
                    try
                    {
                        c.Owner.Log("trade_rebellion", "status=save_cleanup_fault_quarantine|published=" + (__result ? "yes" : "no") + "|reason=" + Safe(cleanupError.GetType().Name));
                    }
                    catch (Exception)
                    {
                    }
                }
            }
        }

        private static Exception SaveFinalizer(Exception __exception, SaveCall __state)
        {
            if (__state != null && !__state.Released)
            {
                if (__state.MpConsentOriginalSave)
                {
                    try
                    {
                        __state.Owner.ReleaseGameSave(__state);
                    }
                    catch
                    {
                    }

                    return __exception;
                }

                try
                {
                    var reset = typeof(GameMapArchiveManagerAPI).GetMethod("SetCurrentFileContext", BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { typeof(string), typeof(bool) }, null);
                    if (reset != null)
                        reset.Invoke(GameMapArchiveManagerAPI.Instance, new object[] { __state.OriginalPath, true });
                }
                catch
                {
                }

                try
                {
                    __state.Owner.ReleaseGameSave(__state);
                }
                catch
                {
                }
            }

            return __exception; // Never hide an original engine failure.
        }

        private bool PrepareGameSave(string destination, out SaveCall call)
        {
            call = null;
            if (!_privateSaveScope)
                return false;
            _savePreflightReason = "state_gate_busy";
            call = null;
            var c = new SaveCall
            {
                Owner = this,
                OriginalPath = destination,
                ThreadId = Environment.CurrentManagedThreadId,
                Generation = _sessionGeneration
            };
            if (!Monitor.TryEnter(_gate))
                return false;
            c.StateGateHeld = true;
            try
            {
                if (!_mapActive || _mapDisabled || _saveSuppressed || _mapBootstrapPending || _nativeTransferProbe != null || _pendingPackage != null || _executingIntent != null || _pendingTeamTransition != null || _activeSave != null)
                {
                    _savePreflightReason = "map_or_operation_not_ready";
                    return false;
                }

                if (!TryEnterNativeInventorySection(out c.NativeLease))
                {
                    _savePreflightReason = "engine_monitor_busy";
                    return false;
                }

                var boundary = TradeSaveBoundary();
                if (boundary.MutationActive || boundary.Quarantined || boundary.SaveActive)
                {
                    _savePreflightReason = "atomic_boundary_not_ready";
                    return false;
                }

#if DIPLOMACY_PRIVATE_TESTS
                if (_aidMemory != null && _aidMemory.HasUnattestedHistoricalNativeAid)
                    TryRestoreOriginalAidHistory();

                try
                {
                    if (ReferenceEquals(c.Owner, this) && c.StateGateHeld &&
                        Monitor.IsEntered(_gate) && c.NativeLease != null &&
                        c.ThreadId == Environment.CurrentManagedThreadId &&
                        c.Generation == _sessionGeneration && IsNativeDiplomacySectionHeld)
                    {
                        var game = GameData.Instance;
                        var keep = game == null ? null : game.Keep_Locations;
                        bool known = keep != null && keep.GetLength(0) == 8 && keep.GetLength(1) == 2;
                        string[] pairs = known ? new string[8] : null;
                        if (known)
                            for (int i = 0; i < 8; i++)
                                pairs[i] = keep[i, 0] + "," + keep[i, 1];
                        Log("saving", "status=original_save_preflight_managed_layout_observed" +
                            "|generation=" + c.Generation + "|thread_id=" + c.ThreadId +
                            "|cached_identity=" + (_mapIdentity == null ? "unavailable" : _mapIdentity.Fingerprint) +
                            "|map_type=" + (game == null ? "unknown" : ((int)game.mapType).ToString()) +
                            "|tilemap_size=" + (game == null ? "unknown" : GameMap.tilemapSize.ToString()) +
                            "|skirmish_game_type=" + (game == null ? "unknown" : game.SkirmishGameType.ToString()) +
                            "|coop_trail_id=" + (game == null ? "unknown" : game.coopTrailID.ToString()) +
                            "|keep_locations_known=" + known +
                            "|keep_locations=" + (known ? string.Join(";", pairs) : "unknown") +
                            "|source=managed_GameData_only|native_header_read=false|authority=none");
                    }
                }
                catch (Exception)
                {
                }
#endif
                LegacyDiplomacySave snapshot = SnapshotSave();
                byte[] payload = DiplomacyStateCodec.Serialize(snapshot, _rules);
                // This legacy slot must never replay a modern trade through legacy fallback.
                c.LegacyPayload = DiplomacySaveCodec.SerializeInactive("_primary_snapshot_owns_native_save");
                if (!boundary.TryBeginSave(payload, out c.Scope))
                {
                    _savePreflightReason = "atomic_save_begin_refused";
                    return false;
                }

                c.NativeBefore = NativeSaveWitness();
                c.File = new AtomicSaveFile(DisposableGameScope.ProfileRoot, destination);
                _activeSave = c;
                call = c;
                _savePreflightReason = "preflight_ready";
                return true;
            }
            finally
            {
                if (call == null)
                    ReleaseGameSave(c);
            }
        }

        private byte[] NativeSaveWitness()
        {
            RequireNativeDiplomacySection();
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write(_sessionGeneration);
                w.Write(_mapFingerprint ?? string.Empty);
                w.Write(_rosterFingerprint ?? string.Empty);
                w.Write(ReadSimulationTick());
                w.Write(_lastProcessedDay);
                foreach (int id in _roster.OrderBy(x => x))
                {
                    w.Write(id);
                    w.Write(_teamPort.GetTeam(id));
                    w.Write(IsAlivePlayer(id));
                    foreach (var good in GoodCatalog.All.OrderBy(x => x.Id))
                    {
                        w.Write(good.Id);
                        w.Write(GoodAmount(id, good.Id));
                    }
                }

                w.Flush();
                return ms.ToArray();
            }
        }

        private bool VerifySavedArchive(SaveCall c, string path)
        {
            MapArchive archive;
            if (!MapArchive.TryLoad(path, out archive) || archive == null)
                return false;
            using (archive)
            {
                byte[] saved = archive.TryReadBinaryFile("_SE_ModData_" + GeneratedDiplomacyContract.SaveDataIdentifier + ".msgpack", true);
                return c.Scope.Matches(saved);
            }
        }

        // Called BEFORE the old callback takes _gate. A foreign-thread callback must
        // refuse without waiting for a native caller holding the original monitor.
        private bool TryFrozenSave(SaveContext context, bool legacy, out byte[] payload)
        {
            payload = null;
            SaveCall c = Volatile.Read(ref _activeSave);
            if (c == null)
                return false;
            if (c.MpConsentOriginalSave)
            {
                // Internal projection reads are not external callback evidence.
                if (_originalOwnerProjectionStable)
                    return false;
                try
                {
                    if (context == null || !context.IsSaveFile || context.IsMapEditorSave || c.Released || !Object.ReferenceEquals(c.Owner, this) || c.Generation != _sessionGeneration || Environment.CurrentManagedThreadId != c.ThreadId || !c.StateGateHeld || !IsNativeDiplomacySectionHeld || !Monitor.IsEntered(_gate) || !string.Equals(Path.GetFullPath(context.FilePath), c.OriginalPath, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Original MP SaveData callback scope changed");
                    byte[] expected = legacy ? c.LegacyPayload : c.MpConsentPayload;
                    if (expected == null || expected.Length == 0)
                        throw new InvalidDataException("Missing frozen MP callback bytes");
                    byte[] issued = legacy ? c.MpLegacyCallbackBytes : c.MpPrimaryCallbackBytes;
                    if (issued == null)
                        issued = (byte[])expected.Clone();
                    if (!ExistingSaveReceiptGate.ExactBytes(issued, expected))
                        throw new InvalidDataException("Original MP callback bytes changed");
                    // The actual callbacks directly return this array after this helper.
                    // A duplicate callback reuses the same witnessed array, never hides a
                    // previously changed return behind a new good copy.
                    if (legacy)
                        c.MpLegacyCallbackBytes = issued;
                    else
                        c.MpPrimaryCallbackBytes = issued;
                    payload = issued;
                    return true;
                }
                catch
                {
                    Interlocked.Exchange(ref c.MpCallbackFault, 1);
                    throw;
                }
            }

            try
            {
                if (context == null || !context.IsSaveFile || Environment.CurrentManagedThreadId != c.ThreadId || !string.Equals(Path.GetFullPath(context.FilePath), c.File.TemporaryPath, StringComparison.OrdinalIgnoreCase) || !IsNativeDiplomacySectionHeld)
                {
                    c.Scope.Poison();
                    return true;
                }

                payload = legacy ? (byte[])c.LegacyPayload.Clone() : c.Scope.CopyForCallback();
            }
            catch
            {
                c.Scope.Poison();
                payload = null;
            }

            return true;
        }

        private bool MpCallbacksExact(SaveCall c)
        {
            return c != null && !c.Released && Object.ReferenceEquals(c.Owner, this) && Object.ReferenceEquals(Volatile.Read(ref _activeSave), c) && c.Generation == _sessionGeneration && c.ThreadId == Environment.CurrentManagedThreadId && c.StateGateHeld && Monitor.IsEntered(_gate) && IsNativeDiplomacySectionHeld && Volatile.Read(ref c.MpCallbackFault) == 0 && c.MpConsentPayload != null && c.LegacyPayload != null && c.MpPrimaryCallbackBytes != null && c.MpLegacyCallbackBytes != null && ExistingSaveReceiptGate.ExactBytes(c.MpPrimaryCallbackBytes, c.MpConsentPayload) && ExistingSaveReceiptGate.ExactBytes(c.MpLegacyCallbackBytes, c.LegacyPayload) && Volatile.Read(ref c.MpCallbackFault) == 0;
        }

        private void ReleaseGameSave(SaveCall c)
        {
            if (c == null || c.Released)
                return;
            c.Released = true;
            if (Object.ReferenceEquals(_activeSave, c))
                _activeSave = null;
            try
            {
                if (c.File != null)
                    c.File.Dispose();
            }
            finally
            {
                try
                {
                    if (c.Scope != null)
                        c.Scope.Dispose();
                }
                finally
                {
                    try
                    {
                        if (c.NativeLease != null)
                            c.NativeLease.Dispose();
                    }
                    finally
                    {
                        if (c.StateGateHeld)
                        {
                            c.StateGateHeld = false;
                            Monitor.Exit(_gate);
                        }
                    }
                }
            }
        }
    }
}
