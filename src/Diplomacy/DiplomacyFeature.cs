#if !DIPLOMACY_PRIVATE_TESTS
using APIShared;
using APIShared.Presentation;
#endif
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using CrusaderDE;
using HarmonyLib;
using R3;
using SHCDESE.API;
using SHCDESE.API.Components.SaveData;
using SHCDESE.EventAPI;
using SHCDESE.EventAPI.Player;
using SHCDESE.EventAPI.MapLoader;
using SHCDESE.Interop;
using DiplomacyPersistenceCodec = StrongholdEuropeDE.Runtime.DiplomacyStateCodec;
using DiplomacyPolicy = StrongholdEuropeDE.Runtime.DiplomacyAgreementRules;

namespace StrongholdEuropeDE.Runtime
{
    internal sealed partial class DiplomacyFeature : IExtensionFeature
    {
        // Exactly one read-only original managed producer chain; this is NOT
        // another original save writer/owner or a native save/load invocation.
        private readonly OriginalSavedWorldReadiness _originalSavedWorld =
            new OriginalSavedWorldReadiness();
        private MethodInfo _originalSaveProducerMethod, _originalApplyMethod;
        private bool _originalSaveObserverInstalled;
        private string _originalSaveObserverReason = "observer_not_initialized";
        [ThreadStatic]
        private static OriginalSavedWorldReadiness.Invocation _originalLoadInvocation;

        private static bool CallsOriginalAt(MethodInfo method, int offset, byte opcode,
            string ownerName, string methodName)
        {
            try
            {
                var body = method.GetMethodBody();
                byte[] il = body == null ? null : body.GetILAsByteArray();
                if (il == null || offset < 0 || offset + 5 > il.Length ||
                    il[offset] != opcode)
                    return false;
                MethodBase called = method.Module.ResolveMethod(BitConverter.ToInt32(il, offset + 1));
                return called != null && called.DeclaringType != null &&
                    called.DeclaringType.FullName == ownerName &&
                    called.Name == methodName;
            }
            catch (Exception) { return false; }
        }

        // Pinned original signatures, MVID and four original managed IL callsites
        // are binding authority. Metadata row numbers are diagnostics only:
        // the Mono/preloader runtime can reorder rows without changing semantics.
        private static string OriginalLoadMethodEvidence(MethodInfo method)
        {
            if (method == null)
                return "absent";
            try
            {
                var body = method.GetMethodBody();
                byte[] bytes = body == null ? null : body.GetILAsByteArray();
                return "0x" + method.MetadataToken.ToString("X8", CultureInfo.InvariantCulture) +
                    ":il_" + (bytes == null ? "missing" :
                        bytes.Length.ToString(CultureInfo.InvariantCulture));
            }
            catch (Exception)
            {
                return "metadata_unavailable";
            }
        }

        private static bool TryFindOriginalSavedWorldMethods(
            out MethodInfo producer, out MethodInfo applier,
            out string reason, out string metadata)
        {
            producer = null;
            applier = null;
            reason = "original_contract_not_checked";
            metadata = "editor=unknown|header=unknown|producer=absent|applier=absent";
            Type editor = null, header = null;
            MethodInfo candidateSave = null, candidatePost = null;
            try
            {
                var assembly = typeof(GameData).Assembly;
                if (!string.Equals(assembly.GetName().Name, "Assembly-CSharp",
                    StringComparison.Ordinal))
                {
                    reason = "original_assembly_identity_mismatch";
                    return false;
                }

                // These are exact FullName values from the pinned original
                // Assembly-CSharp metadata (global namespace), not suffix tests.
                editor = assembly.GetType("EditorDirector", false);
                header = assembly.GetType("FileHeader", false);
                metadata = "editor=" + (editor == null ? "missing" : Safe(editor.FullName)) +
                    "|header=" + (header == null ? "missing" : Safe(header.FullName));
                if (editor == null || editor.FullName != "EditorDirector")
                {
                    reason = "editor_fullname_mismatch";
                    return false;
                }
                if (header == null || header.FullName != "FileHeader")
                {
                    reason = "file_header_fullname_mismatch";
                    return false;
                }
                if (typeof(GameData).FullName != "GameData" ||
                    typeof(GameMap).FullName != "GameMap" ||
                    typeof(EngineInterface).FullName != "EngineInterface" ||
                    typeof(EngineInterface.LoadMapReturnData).FullName !=
                        "EngineInterface+LoadMapReturnData")
                {
                    reason = "original_owner_fullname_mismatch";
                    return false;
                }
                var init = typeof(GameData).GetMethod("InitGameInfo",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                    null, new[] { typeof(EngineInterface.LoadMapReturnData) }, null);
                if (init == null || init.DeclaringType != typeof(GameData) ||
                    init.ReturnType != typeof(void))
                {
                    reason = "init_game_info_exact_signature_mismatch";
                    return false;
                }
                var loaded = typeof(GameMap).GetMethod("newMapLoaded",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                    null, new[] { typeof(int) }, null);
                if (loaded == null || loaded.DeclaringType != typeof(GameMap) ||
                    loaded.ReturnType != typeof(void))
                {
                    reason = "new_map_loaded_exact_signature_mismatch";
                    return false;
                }

                var mvid = new Guid("29ade4ef-f587-422f-a1eb-f81dff066e0a");
                if (assembly.ManifestModule.ModuleVersionId != mvid ||
                    editor.Module.ModuleVersionId != mvid ||
                    header.Module.ModuleVersionId != mvid ||
                    init.Module.ModuleVersionId != mvid ||
                    loaded.Module.ModuleVersionId != mvid ||
                    editor.Module != init.Module || init.Module != loaded.Module)
                {
                    reason = "original_module_mvid_mismatch";
                    return false;
                }
                const BindingFlags flags = BindingFlags.Instance |
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
                MethodInfo[] save = editor.GetMethods(flags)
                    .Where(m => m.Name == "loadSaveGame").ToArray();
                MethodInfo[] post = editor.GetMethods(flags)
                    .Where(m => m.Name == "postLoading").ToArray();
                if (save.Length != 1)
                {
                    reason = "load_save_game_missing_or_ambiguous";
                    return false;
                }
                if (post.Length != 1)
                {
                    reason = "post_loading_missing_or_ambiguous";
                    return false;
                }

                candidateSave = save[0];
                candidatePost = post[0];
                metadata += "|producer=" + OriginalLoadMethodEvidence(candidateSave) +
                    "|applier=" + OriginalLoadMethodEvidence(candidatePost) +
                    "|reference_tokens=0x06000539,0x0600053B";
                if (candidateSave.DeclaringType != editor ||
                    candidatePost.DeclaringType != editor ||
                    candidateSave.Module != editor.Module ||
                    candidatePost.Module != editor.Module ||
                    candidateSave.Module.ModuleVersionId != mvid ||
                    candidatePost.Module.ModuleVersionId != mvid)
                {
                    reason = "original_producer_applier_module_mvid_mismatch";
                    return false;
                }
                ParameterInfo[] saveArgs = candidateSave.GetParameters();
                ParameterInfo[] postArgs = candidatePost.GetParameters();
                if (candidateSave.ReturnType != typeof(void) || candidateSave.IsStatic ||
                    saveArgs.Length != 3 ||
                    saveArgs[0].ParameterType != typeof(string) ||
                    saveArgs[1].ParameterType != typeof(string) ||
                    saveArgs[2].ParameterType != header)
                {
                    reason = "load_save_game_exact_signature_mismatch";
                    return false;
                }
                if (candidatePost.ReturnType != typeof(void) || candidatePost.IsStatic ||
                    postArgs.Length != 3 ||
                    postArgs[0].ParameterType != typeof(EngineInterface.LoadMapReturnData) ||
                    postArgs[1].ParameterType != typeof(bool) ||
                    postArgs[2].ParameterType != typeof(bool))
                {
                    reason = "post_loading_exact_signature_mismatch";
                    return false;
                }

                // Four exact opcode/offset/fully qualified resolved callsites
                // retain the preexisting producer and managed-applier body
                // attestation. Runtime token *numbers* are never compared.
                if (!CallsOriginalAt(candidateSave, 69, 0x28,
                    "EngineInterface", "LoadSaveFile"))
                {
                    reason = "load_save_game_native_IL_0069_mismatch";
                    return false;
                }
                if (!CallsOriginalAt(candidateSave, 217, 0x28,
                    "EditorDirector", "postLoading"))
                {
                    reason = "load_save_game_applier_IL_0217_mismatch";
                    return false;
                }
                if (!CallsOriginalAt(candidatePost, 33, 0x6f,
                    "GameData", "InitGameInfo"))
                {
                    reason = "post_loading_init_IL_0033_mismatch";
                    return false;
                }
                if (!CallsOriginalAt(candidatePost, 49, 0x6f,
                    "GameMap", "newMapLoaded"))
                {
                    reason = "post_loading_new_map_IL_0049_mismatch";
                    return false;
                }

                // Untouched exact semantic CIL digests prove both managed
                // application targets; they include complete instructions,
                // resolved members, locals, branches and handlers.
                if (CanonicalManagedIL.Digest(init) !=
                    "ef3738425de997a0f1b8f2f0046ec341141e5d48fdf68b7f126a1788959aae29")
                {
                    reason = "init_game_info_semantic_IL_mismatch";
                    return false;
                }
                if (CanonicalManagedIL.Digest(loaded) !=
                    "0d447cdfa44ab02c9b439ddd8f6657741f745d1a4437fd87c3733874763c3d77")
                {
                    reason = "new_map_loaded_semantic_IL_mismatch";
                    return false;
                }

                reason = "exact_fullnames_signature_mvid_original_IL_semantic_targets";
                producer = candidateSave;
                applier = candidatePost;
                return true;
            }
            catch (Exception error)
            {
                reason = "original_metadata_or_IL_unavailable_" + error.GetType().Name;
                return false;
            }
        }

        // Passive observation only. It never initiates a callback,
        // opens a gate ticket, authorizes restore, or blocks the host thread.
        internal static bool TryObserveOriginalLoadGate(
            out bool installed, out string phaseOrReason)
        {
            installed = false;
            phaseOrReason = "owner_or_feature_not_ready";
            bool ownerTaken = false, gateTaken = false;
            DiplomacyFeature snapshotOwner = null;
            try
            {
                if (!Monitor.TryEnter(OwnerGate))
                    return false;
                ownerTaken = true;
                snapshotOwner = _activeOwner;
                if (snapshotOwner == null || !Monitor.TryEnter(snapshotOwner._gate))
                    return false;
                gateTaken = true;
                DiplomacyFeature owner = snapshotOwner;
                if (!owner._prepared || !owner._applied)
                    return false;
                installed = owner._originalSaveObserverInstalled &&
                    owner._originalSaveProducerMethod != null &&
                    owner._originalApplyMethod != null;
                phaseOrReason = installed
                    ? owner._originalSavedWorld.PhaseLabel(owner._mapEpoch)
                    : (owner._originalSaveObserverReason ?? "observer_not_installed");
                return true;
            }
            catch (Exception)
            {
                installed = false;
                phaseOrReason = "owner_snapshot_unavailable";
                return false;
            }
            finally
            {
                if (gateTaken)
                    Monitor.Exit(snapshotOwner._gate);
                if (ownerTaken)
                    Monitor.Exit(OwnerGate);
            }
        }

        private void InstallOriginalSavedWorldObserver()
        {
            MethodInfo producer, applier;
            string reason, metadata;
            _originalSaveObserverInstalled = false;
            if (!TryFindOriginalSavedWorldMethods(
                out producer, out applier, out reason, out metadata))
            {
                _originalSaveObserverReason = reason;
                Log("activation", "status=original_save_application_observer_unavailable" +
                    "|reason=" + Safe(reason) + "|" + metadata +
                    "|restore_authority=false");
                return;
            }

            const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
            MethodInfo prefix = typeof(DiplomacyFeature).GetMethod(
                "OriginalSaveProducerPrefix", flags);
            MethodInfo final = typeof(DiplomacyFeature).GetMethod(
                "OriginalSaveProducerFinalizer", flags);
            MethodInfo postPrefix = typeof(DiplomacyFeature).GetMethod(
                "OriginalManagedApplyPrefix", flags);
            MethodInfo postPostfix = typeof(DiplomacyFeature).GetMethod(
                "OriginalManagedApplyPostfix", flags);
            if (prefix == null || final == null ||
                postPrefix == null || postPostfix == null)
            {
                _originalSaveObserverReason = "original_harmony_callbacks_unavailable";
                Log("activation", "status=original_save_application_observer_unavailable" +
                    "|reason=" + _originalSaveObserverReason + "|" + metadata +
                    "|restore_authority=false");
                return;
            }
            try
            {
                _harmony.Patch(producer, prefix: new HarmonyMethod(prefix),
                    finalizer: new HarmonyMethod(final));
                _originalSaveProducerMethod = producer;
                _harmony.Patch(applier, prefix: new HarmonyMethod(postPrefix),
                    postfix: new HarmonyMethod(postPostfix));
                _originalApplyMethod = applier;
                _originalSaveObserverInstalled = true;
                _originalSaveObserverReason = null;
                Log("activation", "status=original_save_application_observer_installed" +
                    "|reason=" + Safe(reason) + "|" + metadata +
                    "|authority=original_phase5_only");
            }
            catch (Exception error)
            {
                _originalSaveObserverReason = "original_harmony_patch_failed_" +
                    error.GetType().Name;
                RemoveOriginalSavedWorldObserver();
                Log("activation", "status=original_save_application_observer_unavailable" +
                    "|reason=" + Safe(_originalSaveObserverReason) + "|" + metadata +
                    "|restore_authority=false");
            }
        }

        private void RemoveOriginalSavedWorldObserver()
        {
            _originalSaveObserverInstalled = false;
            if (_originalApplyMethod != null)
            {
                try { _harmony.Unpatch(_originalApplyMethod, HarmonyPatchType.Prefix, _harmony.Id); }
                catch (Exception) { }
                try { _harmony.Unpatch(_originalApplyMethod, HarmonyPatchType.Postfix, _harmony.Id); }
                catch (Exception) { }
                _originalApplyMethod = null;
            }
            if (_originalSaveProducerMethod != null)
            {
                try { _harmony.Unpatch(_originalSaveProducerMethod, HarmonyPatchType.Prefix, _harmony.Id); }
                catch (Exception) { }
                try { _harmony.Unpatch(_originalSaveProducerMethod, HarmonyPatchType.Finalizer, _harmony.Id); }
                catch (Exception) { }
                _originalSaveProducerMethod = null;
            }
        }

        private static void OriginalSaveProducerPrefix(
            out OriginalSavedWorldReadiness.Invocation __state)
        {
            __state = null;
            try
            {
                DiplomacyFeature owner = Volatile.Read(ref _activeOwner);
                if (owner == null || !owner._originalSaveObserverInstalled)
                    return;
                __state = owner._originalSavedWorld.OpenOriginalLoad(
                    Volatile.Read(ref owner._mapEpoch));
                if (__state != null)
                {
                    __state.Parent = _originalLoadInvocation;
                    _originalLoadInvocation = __state;
                }
            }
            catch (Exception) { }
        }

        private static Exception OriginalSaveProducerFinalizer(
            Exception __exception, OriginalSavedWorldReadiness.Invocation __state)
        {
            try
            {
                if (__state != null)
                {
                    __state.Gate.EndOriginalLoad(__state, __exception == null);
                    if (Object.ReferenceEquals(_originalLoadInvocation, __state))
                        _originalLoadInvocation = __state.Parent;
                }
            }
            catch (Exception) { }
            return __exception;
        }

        private static void OriginalManagedApplyPrefix(
            out OriginalSavedWorldReadiness.Invocation __state)
        {
            __state = null;
            try
            {
                var original = _originalLoadInvocation;
                if (original != null)
                    __state = original.Gate.BeginManagedApply(original);
            }
            catch (Exception) { }
        }

        private static void OriginalManagedApplyPostfix(
            OriginalSavedWorldReadiness.Invocation __state)
        {
            try
            {
                if (__state != null)
                    __state.Gate.CompleteManagedApply(__state);
            }
            catch (Exception) { }
        }

        private sealed class TeamPort : IDiplomacyTeamPort
        {
            public int GetTeam(int playerId)
            {
                return GamePlayerManagerAPI.Instance.GetPlayerTeam(playerId);
            }

            public void SetTeam(int playerId, int team)
            {
                RequireNativeDiplomacySection();
                GamePlayerManagerAPI.Instance.SetPlayerTeam(playerId, team);
            }
        }

        private sealed class NativeTransferProbe
        {
            // Seit dem ausdruecklich: ein Tribut kann zwischen
            // zwei Bundesgenossen flieszen, ohne den oertlichen Spieler.
            internal int SenderPlayer;
            internal int TargetPlayer;
            internal int GoodId;
            internal int Amount;
            internal int SenderBefore;
            internal int RecipientBefore;
            internal long StartedTick;
            internal bool AlliedNativeTransfer;
            internal bool CompletionObserved;
            internal bool CompletionPostInventoryExact;
            internal bool? CompletionReturnHint;
            internal int CompletionSenderAmount;
            internal int CompletionRecipientAmount;
            internal bool DirectAtomicTransfer;
            internal DiplomacyTransferPurpose Purpose;
            internal int GameDay = -1;
            internal bool RollbackPending;
            internal DiplomacyTransferProgress Progress;
            internal ulong BoundRequestId;
            internal ulong ModGiftReceiptId;
        }

        private sealed class DiplomacyManagedStateMemento
        {
            internal readonly List<CoalitionState> Coalitions = new List<CoalitionState>();
            internal readonly List<RelationState> Relations = new List<RelationState>();
            internal DiplomaticRequestHistory RequestHistory;
            internal DiplomacyPendingTeamTransition PendingTeamTransition;
#if DIPLOMACY_PRIVATE_TESTS
            internal AidLedger NativeAidMemory;
#endif
            internal long PendingTransitionGeneration;
            internal string PendingTransitionFingerprint;
            internal Dictionary<int, int> TransitionGuardPartition;
            internal Dictionary<int, int> TransitionVerificationPartition;
            internal long TransitionVerificationStartedTick;
        }

        private sealed class DiplomacyDialog
        {
            internal string Title;
            internal string Message;
            internal Action Yes;
            internal Action No;
            internal Action OnPresented;
            internal Func<bool> StillValid;
            internal bool IsOk;
            internal long Generation;
            internal string Fingerprint;
            internal string DecisionKey;
            internal bool Cancelled;
            internal object PresentedPopup;
            internal Action PresentedYes;
            internal Action PresentedNo;
        }

        private readonly DiplomacyDialogQueue<DiplomacyDialog> _dialogQueue = new DiplomacyDialogQueue<DiplomacyDialog>();
        private bool _dialogCallbackInProgress;
        private long _dialogRetryNotBeforeTick;
        private Exception _saveFailurePending;
        private bool _saveSuppressed;
        private static readonly int[] SendableGoods =
        {
            2,
            3,
            4,
            6,
            7,
            9,
            10,
            11,
            12,
            13,
            14,
            15,
            16,
            17,
            18,
            19,
            20,
            21,
            22,
            23,
            24,
        };
        private static readonly string[] StandardLordSpeechPrefixes =
        {
            "all",
            "all",
            "all",
            "all",
            "all",
            "sa",
            "ca",
            "su",
            "ri",
            "fr",
            "ph",
            "wa",
            "em",
            "ni",
            "sh",
            "ma",
            "ab",
            "je",
            "se",
            "no",
            "ka",
            "cn",
            "tr",
            "sg",
            "li",
            "cr",
            "ba",
            "bu",
            "sr",
            "sb",
        };
        private const int NativeTransferVerificationTicks = 30;
        private const long NativeEngineSize = RuntimePrerequisiteCatalog.OriginalEngineSize;
        private const string NativeEngineSha256 = RuntimePrerequisiteCatalog.OriginalEngineSha256;
        private const string NativeEngineRelativePath = "Stronghold Crusader Definitive Edition_Data/Plugins/x86_64/CrusaderDE.dll";
        private const string RuntimeLogPrefix = "DIPLOMACY_RUNTIME|lane=diplomacy";
        private static readonly object OwnerGate = new object ();
        private static DiplomacyFeature _activeOwner;
        private readonly Harmony _harmony;
        private readonly Action<string> _info;
        private readonly Action<string> _warning;
        private readonly Action<string> _error;
        private readonly object _gate = new object ();
        private readonly DiplomacyRules _rules;
        private readonly TeamPort _teamPort = new TeamPort();
        private readonly List<int> _roster = new List<int>();
        private readonly Dictionary<string, CoalitionState> _coalitions = new Dictionary<string, CoalitionState>(StringComparer.Ordinal);
        private readonly Dictionary<string, RelationState> _relations = new Dictionary<string, RelationState>(StringComparer.Ordinal);
        private readonly Dictionary<int, string> _playerCoalition = new Dictionary<int, string>();
        private readonly Dictionary<int, int> _capturedLordIds = new Dictionary<int, int>();
        private readonly Dictionary<int, int> _frozenPrices = new Dictionary<int, int>();
        private readonly Dictionary<int, int> _originalTeamPartition = new Dictionary<int, int>();
        private readonly Dictionary<int, int> _ownedOriginalTeams = new Dictionary<int, int>();
        private readonly Dictionary<int, int> _ownedAssignedTeams = new Dictionary<int, int>();
        private readonly List<int> _visiblePlayers = new List<int>();
        private readonly Dictionary<string, string> _coalitionValueLogSignatures = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly HashSet<string> _inactiveCoalitionsLogged = new HashSet<string>(StringComparer.Ordinal);
#if DIPLOMACY_PRIVATE_TESTS
        private IDisposable _mapStartSubscription;
        private IDisposable _mapLoadSubscription;
        private IDisposable _mapUnloadSubscription;
#else
        private IMissionLifecycleCapability _missionLifecycle;
        private IMissionPlayStateReadinessCapability _missionPlayState;
        private long _missionSessionId;
        private readonly PublicMissionSaveAttempt _missionSaveAttempt = new PublicMissionSaveAttempt();
#endif
        private IDisposable _saveLoadSubscription;
        private MethodInfo _buttonTarget;
        private MethodInfo _getAllyListTarget;
        private MethodInfo _updateAlliesTarget;
        private MethodInfo _rolloverTarget;
        private FieldInfo _selectedGoodsField;
        private FieldInfo _selectedGoodsAmountField;
        private FieldInfo _allyField;
        private FieldInfo _numAlliesField;
        private FieldInfo _currentAllyField;
        private bool _prepared;
        private bool _applied;
#if DIPLOMACY_PRIVATE_TESTS
        private bool _buttonPatchApplied;
#else
        private static bool _sharedAllyPanelRegistered;
#endif
        private bool _rosterPatchApplied;
        private bool _updatePatchApplied;
        private bool _rolloverPatchApplied;
        private bool _saveHandlerRegistered;
        private bool _mapActive;
        private bool _mapDisabled;
        private bool _mapFailureLatched;
        private bool _disabledTeamRecoveryPending;
        private string _deferredDisableStage;
        private Exception _deferredDisableException;
        private bool _deferredDisableExpectedTeardown;
        private long _deferredDisableEpoch;
        private bool _deferredPortraitRestore;
        private bool _mapBootstrapPending;
        private string _mapBootstrapTrigger;
        private long _mapBootstrapNotBeforeTick;
        private long _featureTicks;
        private string _mapFingerprint;
        private string _legacyMapFingerprint;
        private string _rosterFingerprint;
        private int _localPlayer;
        private int _lastProcessedDay = -1;
        private byte[] _pendingSavePayload;
        private long _pendingSaveEpoch = -1;
        private long _pendingSaveStagedFeatureTick = -1;
        // Bound to the exact original loadSaveGame invocation, not a tick.
        private long _pendingSaveApplySerial;
        private long _mapEpoch;
        private long _mapBootstrapEpoch;
        private bool _epochOpenedByMapLoad;
#if DIPLOMACY_PRIVATE_TESTS
        private long _lastMapStartFeatureTick = long.MinValue;
#endif
        private int _selectedVisibleIndex;
        private int _pendingGoodsTarget;
        private NativeTransferProbe _nativeTransferProbe;
        private DiplomaticRequestHistory _requestHistory;
        private DiplomacyPendingPackage _pendingPackage;
        private long _inventoryRecoveryRetryTick;
        private LegacyDiplomacySave _inventoryRecoveryBaseState;
        private bool _inventoryRestorePending;
        private bool _goodsTransfersUnavailable;
        // Vergebliche Ruecknahmeversuche in Folge. Ab GoodsTransferRollbackGiveUp
        // wird die schwebende Sendung verworfen statt ewig weiterprobiert.
        private int _transferRollbackAttempts;
        private const int GoodsTransferRollbackGiveUp = 10;
        private int _packageRollbackAttempts;
        private long _packageRollbackRetryTick;
        private bool _transferBookkeepingInProgress;
        private byte[] _stableSavePayload;
        private string _stableSaveReason;
        private bool? _nativeEnemyTransferSupported;
        private long _simulationTicks;
        private long _logSequence;
        private long _sessionGeneration;
        private bool _uiExtensionWarningLogged;
        private bool _uiRefreshDegradedLogged;
        private string _uiRosterSignature;
        private object _portraitLayoutPanel;
        private bool _portraitLayoutApplied;
        internal DiplomacyFeature(Harmony harmony, Action<string> info, Action<string> warning, Action<string> error)
        {
            _harmony = harmony ?? throw new ArgumentNullException("harmony");
            _info = info ?? throw new ArgumentNullException("info");
            _warning = warning ?? throw new ArgumentNullException("warning");
            _error = error ?? throw new ArgumentNullException("error");
            _rules = RulesFromGeneratedContract();
        }

        public string Id
        {
            get
            {
                return StrongholdEuropeDE.Runtime.CompatibilityContracts.FeatureId;
            }
        }

        public IReadOnlyList<ExtensionLeaseClaim> ExclusiveClaims
        {
            get
            {
                return new[]
                {
                    ExtensionLeaseClaim.Hook("CrusaderDE.HUD_AlliesPanel.GetAllyList:diplomacy"),
                    ExtensionLeaseClaim.Hook("CrusaderDE.HUD_AlliesPanel.UpdateAllies:diplomacy"),
                    ExtensionLeaseClaim.Hook("CrusaderDE.HUD_AlliesPanel.ButtonClicked:diplomacy"),
                    ExtensionLeaseClaim.Hook("CrusaderDE.HUD_AlliesPanel.MouseEnterRolloverHandler:diplomacy"),
                    ExtensionLeaseClaim.Hook("GameData.Scenarios.setGameOverState:diplomacy"),
                    ExtensionLeaseClaim.UiElement("Assets/GUI/XAMLResources/HUD_AlliesPanel.xaml", "EuropeDiplomacyVanillaExtension"),
                    ExtensionLeaseClaim.Hook(GeneratedDiplomacyContract.SaveDataIdentifier),
                };
            }
        }

        public bool Prepare(out string failure)
        {
            NoteLane(LaneLifecycle);
            if (_prepared)
            {
                failure = null;
                return true;
            }

            if (_rules.Validate() != null)
            {
                failure = "generated diplomacy rules are invalid: " + _rules.Validate();
                return false;
            }

            string nativeEnginePath = Path.GetFullPath(Path.Combine(BepInEx.Paths.GameRootPath, NativeEngineRelativePath.Replace('/', Path.DirectorySeparatorChar)));
            // Kompatibilitaetsentscheidung des Benutzers vom die
            // Engine-Beglaubigung sagt, wogegen gemessen wurde - sie ist keine
            // Bedingung fuer den Lauf. Jeder native Zugriff geht ueber die
            // typisierten Strukturen des Extenders, dessen Mitglieder der
            // Kompatibilitaetswaechter beim Start einzeln prueft. Eine fremde
            // Engine wird daher gemeldet, nicht verweigert.
            try
            {
                string nativeEngineDigest = Hashing.Sha256FileStable(nativeEnginePath, NativeEngineSize);
                if (!string.Equals(nativeEngineDigest, NativeEngineSha256, StringComparison.Ordinal))
                {
                    _warning(RuntimeLogPrefix + "|event=compatibility|status=unverified_engine" + "|digest=" + nativeEngineDigest.Substring(0, 12));
                }
            }
            catch (Exception exception)
            {
                _warning(RuntimeLogPrefix + "|event=compatibility|status=unverified_engine" + "|reason=" + Safe(exception.GetType().Name + ":" + exception.Message));
            }

            Type panel = typeof(MainViewModel).Assembly.GetType("CrusaderDE.HUD_AlliesPanel", false);
            if (panel == null)
            {
                failure = "pinned HUD_AlliesPanel type is unavailable";
                return false;
            }

            _buttonTarget = panel.GetMethod("ButtonClicked", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(string) }, null);
            _getAllyListTarget = panel.GetMethod("GetAllyList", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(int[, ]) }, null);
            _updateAlliesTarget = panel.GetMethod("UpdateAllies", BindingFlags.NonPublic | BindingFlags.Instance);
            _rolloverTarget = panel.GetMethod("MouseEnterRolloverHandler", BindingFlags.NonPublic | BindingFlags.Instance);
            _selectedGoodsField = panel.GetField("selectedGoods", BindingFlags.NonPublic | BindingFlags.Instance);
            _selectedGoodsAmountField = panel.GetField("selectedGoodsAmount", BindingFlags.NonPublic | BindingFlags.Instance);
            _allyField = panel.GetField("ally", BindingFlags.NonPublic | BindingFlags.Instance);
            _numAlliesField = panel.GetField("num_allies", BindingFlags.NonPublic | BindingFlags.Instance);
            _currentAllyField = panel.GetField("curr_ally", BindingFlags.NonPublic | BindingFlags.Instance);
            MethodInfo findName = panel.GetMethod("FindName", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(string) }, null);
            MethodInfo updateGoods = panel.GetMethod("UpdateGoods", BindingFlags.NonPublic | BindingFlags.Instance);
            FieldInfo alliesPanelField = typeof(MainViewModel).GetField("HUDAlliesPanel", BindingFlags.Public | BindingFlags.Instance);
            MethodInfo goodsIcon = typeof(MainViewModel).GetMethod("getAlliesGoodsIcon", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(int) }, null);
            MethodInfo setFace = typeof(MainViewModel).GetMethods(BindingFlags.Public | BindingFlags.Instance).SingleOrDefault(candidate => candidate.Name == "setAlliesFace" && candidate.GetParameters().Length == 7);
            Type popup = typeof(MainViewModel).Assembly.GetType("CrusaderDE.HUD_ConfirmationPopup", false);
            MethodInfo confirmation = popup == null ? null : popup.GetMethod("ShowConfirmationMessage", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string), typeof(Action), typeof(Action), typeof(string), typeof(bool), typeof(bool), }, null);
            MethodInfo okMessage = popup == null ? null : popup.GetMethod("ShowConfirmationOKMessage", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string), typeof(Action), typeof(string), typeof(bool), }, null);
            MethodInfo rosterPrefix = typeof(DiplomacyFeature).GetMethod("AlliesGetAllyListPrefix", BindingFlags.NonPublic | BindingFlags.Static);
            MethodInfo updatePostfix = typeof(DiplomacyFeature).GetMethod("AlliesUpdatePostfix", BindingFlags.NonPublic | BindingFlags.Static);
            MethodInfo rolloverPrefix = typeof(DiplomacyFeature).GetMethod("AlliesRolloverPrefix", BindingFlags.NonPublic | BindingFlags.Static);
            if (_buttonTarget == null || _buttonTarget.ReturnType != typeof(void) || _getAllyListTarget == null || _getAllyListTarget.ReturnType != typeof(void) || _updateAlliesTarget == null || _updateAlliesTarget.ReturnType != typeof(void) || _rolloverTarget == null || _rolloverTarget.ReturnType != typeof(void) || _selectedGoodsField == null || _selectedGoodsField.FieldType != typeof(int) || _selectedGoodsAmountField == null || _selectedGoodsAmountField.FieldType != typeof(int) || _allyField == null || _allyField.FieldType != typeof(int[]) || _numAlliesField == null || _numAlliesField.FieldType != typeof(int) || _currentAllyField == null || _currentAllyField.FieldType != typeof(int) || findName == null || updateGoods == null || alliesPanelField == null || goodsIcon == null || setFace == null || confirmation == null || okMessage == null || rosterPrefix == null || updatePostfix == null || rolloverPrefix == null)
            {
                failure = "pinned HUD_AlliesPanel diplomacy surface is unavailable";
                return false;
            }

#if DIPLOMACY_PRIVATE_TESTS
            if (MapLoaderR3EventHooks.OnStartMap.Observable == null || MapLoaderR3EventHooks.OnLoadMap.Observable == null || MapLoaderR3EventHooks.OnLoadSave.Observable == null || MapLoaderR3EventHooks.OnUnloadMap.Observable == null)
            {
                failure = "diplomacy map/day event streams are unavailable";
                return false;
            }
#else
            var client = APIShared.ApiShared.ForMod(CompatibilityContracts.PluginId);
            if (!client.TryGetMissionLifecycle(out _missionLifecycle, out var diagnostic) ||
                !client.TryGetMissionPlayStateReadiness(out _missionPlayState, out diagnostic))
            {
                failure = "diplomacy mission lifecycle unavailable: " + diagnostic;
                return false;
            }
            if (MapLoaderR3EventHooks.OnLoadSave.Observable == null)
            {
                failure = "original save verification event stream unavailable";
                return false;
            }
#endif

            string bridgeFailure;
            if (!ScriptExtenderNativeBridge.TryResolve(out bridgeFailure))
            {
                failure = "script extender bridge unavailable: " + bridgeFailure;
                return false;
            }

            if (!PrepareCombatSafety(out failure))
            {
                return false;
            }

            _prepared = true;
            failure = null;
            return true;
        }

        public void Apply()
        {
            NoteLane(LaneLifecycle);
            if (!_prepared || _applied)
            {
                throw new InvalidOperationException("Diplomacy must be prepared exactly once before apply");
            }

            lock (OwnerGate)
            {
                if (_activeOwner != null && !ReferenceEquals(_activeOwner, this))
                {
                    throw new InvalidOperationException("Another diplomacy feature already owns the HUD hook");
                }

                _activeOwner = this;
            }

            try
            {
                MethodInfo rosterPrefix = typeof(DiplomacyFeature).GetMethod("AlliesGetAllyListPrefix", BindingFlags.NonPublic | BindingFlags.Static);
                MethodInfo updatePostfix = typeof(DiplomacyFeature).GetMethod("AlliesUpdatePostfix", BindingFlags.NonPublic | BindingFlags.Static);
                MethodInfo rolloverPrefix = typeof(DiplomacyFeature).GetMethod("AlliesRolloverPrefix", BindingFlags.NonPublic | BindingFlags.Static);
                _harmony.Patch(_getAllyListTarget, prefix: new HarmonyMethod(rosterPrefix));
                _rosterPatchApplied = true;
                _harmony.Patch(_updateAlliesTarget, postfix: new HarmonyMethod(updatePostfix));
                _updatePatchApplied = true;
#if DIPLOMACY_PRIVATE_TESTS
                MethodInfo prefix = typeof(DiplomacyFeature).GetMethod("AlliesButtonPrefix", BindingFlags.NonPublic | BindingFlags.Static);
                MethodInfo buttonPostfix = typeof(DiplomacyFeature).GetMethod("AlliesButtonPostfix", BindingFlags.NonPublic | BindingFlags.Static);
                _harmony.Patch(_buttonTarget, prefix: new HarmonyMethod(prefix), postfix: new HarmonyMethod(buttonPostfix));
                _buttonPatchApplied = true;
#else
                if (!_sharedAllyPanelRegistered)
                {
                    if (!AllyPanelButtonEvents.TryRegister(CompatibilityContracts.PluginId,
                            "diplomacy-allies-panel", OnSharedAlliesButtonPre, OnSharedAlliesButtonPost, out string buttonFailure))
                        throw new InvalidOperationException("Diplomacy ally panel unavailable: " + buttonFailure);
                    _sharedAllyPanelRegistered = true;
                }
#endif
                _harmony.Patch(_rolloverTarget, prefix: new HarmonyMethod(rolloverPrefix));
                _rolloverPatchApplied = true;
                if (!ModSaveDataAPI.Instance.RegisterModDataHandler(GeneratedDiplomacyContract.SaveDataIdentifier, SaveData, LoadData))
                {
                    throw new InvalidOperationException("diplomacy save-data identifier is already registered");
                }

                _saveHandlerRegistered = true;
#if DIPLOMACY_PRIVATE_TESTS
                // Map identity is owned by the primary ExtendedState save envelope.
#endif
                RegisterLegacySaveImport();
#if DIPLOMACY_PRIVATE_TESTS
                _mapStartSubscription = ObservableSubscribeExtensions.Subscribe(MapLoaderR3EventHooks.OnStartMap.Observable, OnStartMap);
                _mapLoadSubscription = ObservableSubscribeExtensions.Subscribe(MapLoaderR3EventHooks.OnLoadMap.Observable, OnLoadMap);
                _mapUnloadSubscription = ObservableSubscribeExtensions.Subscribe(MapLoaderR3EventHooks.OnUnloadMap.Observable, OnUnloadMap);
#endif
                // This event supplies the native stage of the original-save proof.
                // In the public build, it never starts or ends a session.
                _saveLoadSubscription = ObservableSubscribeExtensions.Subscribe(MapLoaderR3EventHooks.OnLoadSave.Observable, OnLoadSave);
                // Observes the original managed producer/application only.
                // Unbound original saves remain pending, never early-restored.
                InstallOriginalSavedWorldObserver();
                string inventoryMonitorFailure;
                if (!TryResolveNativeInventoryMonitor(out inventoryMonitorFailure))
                    throw new InvalidOperationException(inventoryMonitorFailure);
                WarmUpNativeManagers();
                ApplyCombatSafety();
                GameTimeManagerAPI.Instance.OnTick += OnSimulationTick;
                _applied = true;
#if DIPLOMACY_PRIVATE_TESTS
                // Strict startup admission and original monitor binding have
                // already succeeded. Optional isolated QA observation only.
                InstallPrivateOriginalMapPhaseObserver();
#else
                if (!_missionLifecycle.TryRegisterObserver("diplomacy-mission", OnSharedMissionStart,
                        OnSharedMissionEnd, OnSharedMissionInitialization, out var diagnostic))
                    throw new InvalidOperationException("Diplomacy mission registration failed: " + diagnostic);
#endif
                Log("activation", "status=active|canary=" + BoolText(GeneratedDiplomacyContract.IsolatedCanary));
            }
            catch
            {
                RollbackOwned();
                throw;
            }
        }

        internal bool HasActiveMissionTick => _mapActive && !_mapDisabled && !_mapBootstrapPending;

        public void Tick()
        {
            lock (_gate)
            {
                _featureTicks++;
                // Reconcile optional chat even if this frame exits early.
                MaintainLordChatPresentation();
#if !DIPLOMACY_PRIVATE_TESTS
                if (_missionSessionId == 0 || _missionLifecycle.Current?.SessionId != _missionSessionId)
                    return;
#endif
                // A late foreign broker veto may need one later metadata-only
                // RequestHistory cancellation under this already-owned gate.
                // This never retries or alters the original/native action.
                PumpSharedVetoCancellations();
                // Never log or allocate in the shared GameAction callback;
                // surface isolated failures on the existing managed owner lane.
                long brokerFaults = Interlocked.Exchange(
                    ref _sharedGameActionCallbackFaults, 0);
                if (brokerFaults != 0)
                    Log("gameaction", "status=shared_callback_fail_closed_or_late_mutation" +
                        "|count=" + brokerFaults.ToString(CultureInfo.InvariantCulture) +
                        "|native_ack=false");
#if DIPLOMACY_PRIVATE_TESTS
                ReportNativeTruceAdmission();
#endif
                if (_deferredPortraitRestore && CanPresentDiplomacyUi)
                {
                    _deferredPortraitRestore = false;
                    try
                    {
                        RestoreVanillaPortraitLayout(CurrentAlliesPanel());
                    }
                    catch (Exception uiException)
                    {
                        Log("ui", "status=portrait_restore_failed|reason=" + uiException.GetType().Name);
                    }
                }

                if (_deferredDisableStage != null)
                {
                    if (_deferredDisableEpoch == _mapEpoch)
                        DisableMap(_deferredDisableStage, _deferredDisableException, _deferredDisableExpectedTeardown);
                    else
                    {
                        _deferredDisableStage = null;
                        _deferredDisableException = null;
                    }

                    if (_deferredDisableStage != null)
                        return;
                }

                if (_saveFailurePending != null)
                {
                    Exception saveFailure = _saveFailurePending;
                    _saveFailurePending = null;
                    DisableMap("save_serialize", saveFailure);
                    try
                    {
                        ShowOkMessageNative(Text("Diplomatie", "Diplomacy"), null, Text("Die Diplomatie konnte nicht sicher gespeichert werden und wurde für diese Partie abgeschaltet. Der Spielstand enthält einen ausdrücklichen Inaktiv-Vermerk.", "Diplomacy could not be saved safely and has been disabled for this match. The save contains an explicit inactive marker."));
                    }
                    catch
                    {
                    }
                }

                // Original saved world may still be loading while the old
                // bootstrap world is technically playable. No runtime work.
                if (_originalSavedWorld.IsOriginalLoadInProgress())
                    return;

                if (_mapBootstrapPending)
                {
                    if ((_originalSavedWorld.HasSaveObservation(_mapEpoch) ||
                         _pendingSavePayload != null || _pendingLegacySavePayload != null) &&
                        !_originalSavedWorld.IsApplied(_mapEpoch))
                        return;
#if !DIPLOMACY_PRIVATE_TESTS
                    if (_missionPlayState.Current?.SessionId != _missionSessionId)
                        return;
#endif
                    if (_featureTicks < _mapBootstrapNotBeforeTick || !IsPlayableMapRuntimeReady())
                    {
                        return;
                    }

                    BeginMapSession(_mapBootstrapTrigger ?? "lifecycle");
                }

                PumpSignedNetwork();
                PumpOriginalMpConsentOnly();
                if (!_mapActive || _mapDisabled)
                {
                    return;
                }

                try
                {
                    LogLaneIdentityOnce();
                    PumpHumanWarIntents();
                    GateStepBegin();
                    // Vollstaendig aus der nativen Spur hierher geholt
                    // (). Im Kontexthaken toetet schon das Ausloesen
                    // einer verwalteten Ausnahme den Prozess; siehe
                    // OnSimulationTick. Der Teamuebergang wirft an vier
                    // Stellen bewusst, der Tageswechsel oeffnet
                    // Meldungsfenster, die Einmarschpruefung zaehlt die ganze
                    // Karte durch - nichts davon darf dort stehen.
                    if (_inventoryRestorePending)
                    {
                        if (_nativeTransferProbe != null)
                            ReconcileSavedTransferProbe();
                        if (_pendingPackage != null)
                            RestorePendingPackage(_pendingPackage);
                        // dieses `return` haelt den GANZEN Takt an -
                        // Tageswechsel, Kampfsicherung, Befehle, Druckaufbau.
                        // Zurueckgesetzt wird die Marke aber nur von den beiden
                        // Zeilen darueber, und die laufen nur mit Probe bzw.
                        // Paket. Faellt beides weg (etwa weil eine aussichtslose
                        // Ruecknahme aufgegeben wurde), gibt es nichts mehr
                        // wiederherzustellen - dann darf die Marke den Takt
                        // nicht weiter anhalten.
                        if (_nativeTransferProbe == null && _pendingPackage == null)
                        {
                            _inventoryRestorePending = false;
                        }

                        if (_inventoryRestorePending)
                            return;
                    }

                    if (!ProcessTeamTransitionFrame())
                        return;
                    GateStep("team_transition");
                    PollNativeDay();
                    if (!CanonicalOwnsTradeLane)
                        PumpAuthenticatedSteamPackets();
                    PumpTradeMailbox();
                    PumpAutonomousNativeTrading();
#if DIPLOMACY_PRIVATE_TESTS
                    PumpAutonomousAiAid();
#endif
                    GateStep("day");
                    RetryPackageRecovery();
                    FlushDeferredNotice();
                    GateStep("deferred_notice");
                    PumpAllyOrders();
#if DIPLOMACY_PRIVATE_TESTS && HOST_DIRECTED_AI_TRIBE_CANARY
                    PumpHostDirectedAiTribe();
#endif
                    PumpHumanWarIntents();
                    TickCastleBoundaries();
                    EvaluateMilitaryIncursions();
                    RetryAgreementSafety();
                    GateStep("incursions");
                    TickProbe();
                    GateStep("probe");
                    if (_nativeTransferProbe != null)
                    {
                        VerifyNativeTransferProbe();
                    }

                    RetryPendingPackageRollback();
                    NormalizeVassalDemands();
                    PumpPendingGoodsCommand();
                    PumpSubmissionAssessments();
                    PumpFealtyCommand();
                    PumpVassalActions();
                    PumpVassalGoodsRequests();
                    PumpGoodsRequests();
                    PumpPendingRelationshipDecisions();
                    PumpTributeReassessments();
                    PumpDiplomacyDialogs();
                    // Only the verified original solo Unity presentation lane.
                    PumpOriginalLordChat();
#if DIPLOMACY_PRIVATE_TESTS
                    RunDisposableQaFrame();
#endif
                }
                catch (Exception exception)
                {
                    DisableMap("runtime_frame", exception);
                }
                finally
                {
                    PublishCombatProtectionSnapshot();
                }
            }
        }

        /// <summary>
        /// Meldet einmal je Karte, ob Simulationstakt und Unity-Bild
        /// verschiedene Faeden sind.
        /// </summary>
        /// <remarks>
        /// Beantwortet die Frage, die der alte Kommentar in
        /// <c>ProcessTeamTransitionFrame</c> nur behauptet hat: dort hiess es,
        /// die native Spur muesse den Partitionsschreibvorgang besitzen, damit
        /// kein Simulationsfaden eine halb geschriebene Partition sieht. Sind
        /// beide Kennungen gleich, gab es nie zwei Faeden und die Zusicherung
        /// war gegenstandslos.
        /// </remarks>
        /// <summary>
        /// Faehrt die nativen Verwalter einmal im Unity-Bild hoch.
        /// </summary>
        /// <remarks>
        /// Warum (): SE erzeugt diese Verwalter beim ERSTEN Zugriff.
        /// Faellt dieser erste Zugriff in einen Kampfwaechter, laeuft die
        /// Erzeugung auf dem Simulationsfaden, waehrend der Waechter die
        /// Sperre haelt - und das Spiel haengt sich auf. Genau das ist um
        /// 12:08 passiert; die letzten Protokollzeilen waren
        /// `GameProjectileManagerAPI .ctor`.
        ///
        /// Hier wird nur die Eigenschaft gelesen, kein Aufruf darauf gemacht.
        /// Ein Fehlschlag ist harmlos: dann faellt die Erzeugung eben wieder
        /// dorthin, wo sie vorher lag, und die Wartebegrenzung im Waechter
        /// faengt es ab.
        /// </remarks>
        private void WarmUpNativeManagers()
        {
            string[] namen =
            {
                "players",
                "units",
                "buildings",
                "tribes",
                "tiles",
                "projectiles",
                "time",
            };
            object[] werte = new object[namen.Length];
            int hochgefahren = 0;
            for (int index = 0; index < namen.Length; index++)
            {
                try
                {
                    switch (namen[index])
                    {
                        case "players":
                            werte[index] = GamePlayerManagerAPI.Instance;
                            break;
                        case "units":
                            werte[index] = GameUnitManagerAPI.Instance;
                            break;
                        case "buildings":
                            werte[index] = GameBuildingManagerAPI.Instance;
                            break;
                        case "tribes":
                            werte[index] = GameTribeManagerAPI.Instance;
                            break;
                        case "tiles":
                            werte[index] = GameTileManagerAPI.Instance;
                            break;
                        case "projectiles":
                            werte[index] = GameProjectileManagerAPI.Instance;
                            break;
                        default:
                            werte[index] = GameTimeManagerAPI.Instance;
                            break;
                    }

                    if (werte[index] != null)
                    {
                        hochgefahren++;
                    }
                }
                catch (Exception exception)
                {
                    Log("warmup", "status=failed|manager=" + Safe(namen[index]) + "|reason=" + Safe(exception.GetType().Name));
                }
            }

            Log("warmup", "status=done|managers=" + hochgefahren.ToString(CultureInfo.InvariantCulture) + "/" + namen.Length.ToString(CultureInfo.InvariantCulture));
        }

        // Haltezeitmessung des Unity-Bilds. Nur hier, nie im heiklen Faden.
        private readonly System.Diagnostics.Stopwatch _gateWatch = new System.Diagnostics.Stopwatch();
        private long _gateStepStartTicks;
        private string _gateWorstStep;
        private double _gateWorstMilliseconds;
        private bool _gateWorstLogged;
        /// <summary>Beginnt die Messung eines Bildes.</summary>
        private void GateStepBegin()
        {
            if (!_gateWatch.IsRunning)
            {
                _gateWatch.Start();
            }

            _gateStepStartTicks = _gateWatch.ElapsedTicks;
        }

        /// <summary>
        /// Schliesst einen Schritt ab und merkt sich den langsamsten.
        /// </summary>
        /// <remarks>
        /// Warum (): der Kampfwaechter wartet hoechstens
        /// <see cref = "CombatGateWaitMilliseconds"/> auf die Sperre. Dauert ein
        /// Schritt hier laenger, wird jedes Schadensereignis in diesem Fenster
        /// ungeprueft durchgelassen. Gemeldet wird deshalb nur, was ueber der
        /// Frist liegt - dann steht der Schuldige namentlich im Protokoll.
        /// </remarks>
        private void GateStep(string step)
        {
            long jetzt = _gateWatch.ElapsedTicks;
            double dauer = (jetzt - _gateStepStartTicks) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            _gateStepStartTicks = jetzt;
            if (dauer <= _gateWorstMilliseconds)
            {
                return;
            }

            _gateWorstMilliseconds = dauer;
            _gateWorstStep = step;
            if (dauer > CombatGateWaitMilliseconds && !_gateWorstLogged)
            {
                _gateWorstLogged = true;
                Log("gate_hold", "status=over_wait|step=" + Safe(step) + "|ms=" + dauer.ToString("F1", CultureInfo.InvariantCulture) + "|wait_ms=" + CombatGateWaitMilliseconds.ToString(CultureInfo.InvariantCulture));
            }
        }

        private void LogLaneIdentityOnce()
        {
            NoteLane(LaneFrame);
            ReportLaneMap();
            _frameThreadId = Environment.CurrentManagedThreadId;
            if (_frameStackAlign < 0)
            {
                _frameStackAlign = StackAlignment();
                // Einmal hier rufen, damit der Marshalling-Rumpf steht.
                _frameNativeThreadId = GetCurrentThreadId();
                _nativeThreadIdWarm = true;
            }

            if (!_laneSimulationLogged && _simulationThreadId != 0)
            {
                _laneSimulationLogged = true;
                Log("lane_identity", "lane=simulation_tick" + "|thread=" + _simulationThreadId.ToString(CultureInfo.InvariantCulture) + "|stack_align=" + _simulationStackAlign.ToString(CultureInfo.InvariantCulture) + "|frame_thread=" + _frameThreadId.ToString(CultureInfo.InvariantCulture) + "|frame_stack_align=" + _frameStackAlign.ToString(CultureInfo.InvariantCulture) + "|native_thread=" + _simulationNativeThreadId.ToString(CultureInfo.InvariantCulture) + "|frame_native_thread=" + _frameNativeThreadId.ToString(CultureInfo.InvariantCulture) + "|same_thread=" + BoolText(_simulationThreadId == _frameThreadId));
            }

            // Gedrosselt: der erste Lauf hat mit dieser Zeile 1482 Eintraege
            // erzeugt und alles andere verdeckt ().
            int auslassungen = Volatile.Read(ref _combatGateSkips);
            int snapshotBlocks = Volatile.Read(ref _combatSnapshotBlocks);
            int snapshotFaults = Volatile.Read(ref _combatSnapshotFaults);
            if ((auslassungen != _combatGateSkipsLogged || snapshotBlocks != _combatSnapshotBlocksLogged || snapshotFaults != _combatSnapshotFaultsLogged) && _gateWatch.ElapsedMilliseconds - _combatGateSkipLogAt >= 5000L)
            {
                _combatGateSkipLogAt = _gateWatch.ElapsedMilliseconds;
                _combatGateSkipsLogged = auslassungen;
                _combatSnapshotBlocksLogged = snapshotBlocks;
                _combatSnapshotFaultsLogged = snapshotFaults;
                Log("combat_guard", "status=gate_skipped|count=" + auslassungen.ToString(CultureInfo.InvariantCulture) + "|snapshot_blocks=" + snapshotBlocks.ToString(CultureInfo.InvariantCulture) + "|snapshot_faults=" + snapshotFaults.ToString(CultureInfo.InvariantCulture) + "|worst_step=" + Safe(_gateWorstStep ?? "none") + "|worst_ms=" + _gateWorstMilliseconds.ToString("F1", CultureInfo.InvariantCulture));
            }

            if (!_laneDetourLogged && _detourThreadId != 0)
            {
                _laneDetourLogged = true;
                Log("lane_identity", "lane=combat_detour" + "|thread=" + _detourThreadId.ToString(CultureInfo.InvariantCulture) + "|stack_align=" + _detourStackAlign.ToString(CultureInfo.InvariantCulture) + "|frame_stack_align=" + _frameStackAlign.ToString(CultureInfo.InvariantCulture) + "|source=" + Safe(_detourLaneName) + "|native_thread=" + _detourNativeThreadId.ToString(CultureInfo.InvariantCulture) + "|frame_native_thread=" + _frameNativeThreadId.ToString(CultureInfo.InvariantCulture) + "|same_thread=" + BoolText(_detourThreadId == _frameThreadId));
            }
        }

        /// <summary>
        /// Vermerkt die Spur eines Kampfwaechters. Darf nicht werfen: nur
        /// Feldschreibvorgaenge, kein Aufruf ausser der Messhilfe.
        /// </summary>
        private void NoteDetourLane(string lane)
        {
            if (_detourThreadId != 0)
            {
                return;
            }

            _detourStackAlign = StackAlignment();
            _detourLaneName = lane;
            _detourNativeThreadId = SafeNativeThreadId();
            _detourThreadId = Environment.CurrentManagedThreadId;
        }

        public void RollbackOwned()
        {
            NoteLane(LaneLifecycle);
#if DIPLOMACY_PRIVATE_TESTS
            // Stop private original phase hooks even when native rollback
            // protection requires the normal hooks to remain installed.
            StopPrivateOriginalMapPhaseObserver();
#endif
            bool safeToDetach;
            lock (_gate)
            {
                ResetLordChatSession();
                bool guardedRollbackRequired = _mapActive || _nativeTransferProbe != null || _pendingPackage != null || _ownedAssignedTeams.Count > 0 || _disabledTeamRecoveryPending;
                if (guardedRollbackRequired)
                {
                    DisableMap("feature_rollback", new InvalidOperationException("runtime requested diplomacy rollback"));
                }

                safeToDetach = !_rollbackGuardActive && _nativeTransferProbe == null && _pendingPackage == null && !_disabledTeamRecoveryPending;
                if (safeToDetach)
                {
                    _sessionGeneration++;
                    _mapActive = false;
                    _mapBootstrapPending = false;
                    _mapBootstrapTrigger = null;
                    _pendingGoodsTarget = 0;
                    _ownedAssignedTeams.Clear();
                    _ownedOriginalTeams.Clear();
                    ClearMapState();
                }
                else
                {
                    _error(RuntimeLogPrefix + "|event=fail_closed" + "|status=hooks_retained" + "|reason=rollback_unproven" + "|seq=" + Interlocked.Increment(ref _logSequence).ToString(CultureInfo.InvariantCulture));
                }
            }

            if (!safeToDetach)
            {
                return;
            }

            GameTimeManagerAPI.Instance.OnTick -= OnSimulationTick;
            RemoveOriginalSavedWorldObserver();
            RollbackCombatSafety();
#if DIPLOMACY_PRIVATE_TESTS
            Dispose(ref _mapUnloadSubscription);
            Dispose(ref _mapLoadSubscription);
            Dispose(ref _mapStartSubscription);
#endif
            Dispose(ref _saveLoadSubscription);
            UnregisterLegacySaveImport();
            if (_saveHandlerRegistered)
            {
                _saveHandlerRegistered = false;
#if DIPLOMACY_PRIVATE_TESTS
                // Map identity is owned by the primary ExtendedState save envelope.
#endif
                ModSaveDataAPI.Instance.UnregisterModDataHandler(GeneratedDiplomacyContract.SaveDataIdentifier);
            }

#if DIPLOMACY_PRIVATE_TESTS
            if (_buttonPatchApplied && _buttonTarget != null)
            {
                _buttonPatchApplied = false;
                _harmony.Unpatch(_buttonTarget, HarmonyPatchType.Prefix, _harmony.Id);
                _harmony.Unpatch(_buttonTarget, HarmonyPatchType.Postfix, _harmony.Id);
            }
#endif

            if (_rolloverPatchApplied && _rolloverTarget != null)
            {
                _rolloverPatchApplied = false;
                _harmony.Unpatch(_rolloverTarget, HarmonyPatchType.Prefix, _harmony.Id);
            }

            if (_updatePatchApplied && _updateAlliesTarget != null)
            {
                _updatePatchApplied = false;
                _harmony.Unpatch(_updateAlliesTarget, HarmonyPatchType.Postfix, _harmony.Id);
            }

            if (_rosterPatchApplied && _getAllyListTarget != null)
            {
                _rosterPatchApplied = false;
                _harmony.Unpatch(_getAllyListTarget, HarmonyPatchType.Prefix, _harmony.Id);
            }

            lock (OwnerGate)
            {
                if (ReferenceEquals(_activeOwner, this))
                {
                    _activeOwner = null;
                }
            }

            _applied = false;
        }

        internal bool RequiresFailClosedHooks
        {
            get
            {
                lock (_gate)
                {
                    return _rollbackGuardActive || _nativeTransferProbe != null || _pendingPackage != null || _disabledTeamRecoveryPending;
                }
            }
        }

        private static bool AlliesGetAllyListPrefix(object __instance, int[, ] lordData)
        {
            DiplomacyFeature owner;
            lock (OwnerGate)
            {
                owner = _activeOwner;
            }

            if (owner == null)
            {
                return true;
            }

            try
            {
                owner.NoteLane(LaneUiPanel);
                return !owner.PopulateCombinedRoster(__instance, lordData);
            }
            catch (Exception exception)
            {
                owner.DisableMap("ui_roster", exception);
                return true;
            }
        }

        private static void AlliesUpdatePostfix(object __instance)
        {
            DiplomacyFeature owner;
            lock (OwnerGate)
            {
                owner = _activeOwner;
            }

            if (owner == null)
            {
                return;
            }

            try
            {
                owner.RefreshVanillaDiplomacyUi(__instance);
            }
            catch (Exception exception)
            {
                owner.DegradeUiRefresh("ui_refresh", __instance, exception);
            }
        }

        /// <summary>
        /// Eine fehlgeschlagene Anzeige-Aktualisierung darf die Diplomatie
        /// nicht beenden.
        /// </summary>
        /// <remarks>
        /// Gemessen am auf SHC:DE 2.80: ein Typfehler beim
        /// Schreiben von Opacity (Double statt Single) rief hier DisableMap,
        /// und die Diplomatie war fuer die ganze Partie aus - obwohl Lager,
        /// Teams und Beziehungen unberuehrt waren. Die Aktualisierung liest
        /// nur; sie schreibt weder Lager noch Teams. Deshalb faellt sie auf die
        /// native Detailansicht zurueck und protokolliert einmal je Karte -
        /// genau der Rueckfall, den die ExtendedState-Uebergabe fuer eine unvollstaendige
        /// Oberflaeche ohnehin vorsieht. Der Aufstellungsaufbau (ui_roster)
        /// bleibt bewusst fail-closed: eine halb aufgebaute Aufstellung kann
        /// eine Sendung an den falschen Fuersten lenken.
        /// </remarks>
        private void DegradeUiRefresh(string stage, object panel, Exception exception)
        {
            lock (_gate)
            {
                try
                {
                    HideDetailPanel(panel);
                }
                catch
                {
                // Der Rueckfall selbst darf nichts beenden koennen.
                }

                try
                {
                    NotifyUiFallback();
                }
                catch
                {
                } // A failed fallback must still get its own notice attempt.

                if (_uiRefreshDegradedLogged)
                {
                    return;
                }

                _uiRefreshDegradedLogged = true;
                try
                {
                    _warning(RuntimeLogPrefix + "|event=ui_integration|status=refresh_degraded" + "|stage=" + Safe(stage) + "|reason=" + Safe(exception == null ? "Unknown:unspecified" : exception.GetType().Name + ":" + exception.Message) + "|seq=" + Interlocked.Increment(ref _logSequence).ToString(CultureInfo.InvariantCulture));
                }
                catch
                {
                }
            }
        }

        private static bool AlliesRolloverPrefix(object sender)
        {
            DiplomacyFeature owner;
            lock (OwnerGate)
            {
                owner = _activeOwner;
            }

            if (owner == null)
            {
                return true;
            }

            try
            {
                return !owner.HandleRollover(sender);
            }
            catch (Exception exception)
            {
                // Der Rollover setzt nur einen Hinweistext. Ein Fehler darin
                // ist kein Grund, die Diplomatie fuer die Partie zu beenden.
                owner.DegradeUiRefresh("ui_rollover", null, exception);
                return true;
            }
        }

#if !DIPLOMACY_PRIVATE_TESTS
        private static void OnSharedAlliesButtonPre(AllyPanelButtonPreEventArgs args)
        {
            var owner = Volatile.Read(ref _activeOwner);
            if (owner == null || args.SkipOriginalFunction)
                return;
            lock (owner._gate)
            {
                if (args.Button == "Defend")
                {
                    owner.TryClaimHudDefend(args);
                    return;
                }
                if (!owner.ClaimsAllyPanelButton(args.Panel, args.Button))
                    return;

                long generation = owner._sessionGeneration;
                args.TryClaim(invocation =>
                {
                    bool forward;
                    lock (owner._gate)
                    {
                        if (!ReferenceEquals(Volatile.Read(ref _activeOwner), owner) ||
                            owner._sessionGeneration != generation ||
                            !owner.ClaimsAllyPanelButton(invocation.Panel, invocation.Button))
                            return;
                        forward = AlliesButtonPrefix(invocation.Panel, invocation.Button);
                    }
                    if (forward)
                        invocation.InvokeOriginal(invocation.Button);
                });
            }
        }

        private bool ClaimsAllyPanelButton(HUD_AlliesPanel panel, string button)
        {
            if (string.IsNullOrEmpty(button))
                return false;
            if (button == "Diplomacy:Trade:Close")
                return true;
            if (!_mapActive || _mapDisabled || _mapBootstrapPending || !MainViewModel.viewModelLoaded)
                return false;
            var view = MainViewModel.Instance;
            if (view == null || !ReferenceEquals(panel, view.HUDAlliesPanel))
                return false;
            if (button.StartsWith("Diplomacy:", StringComparison.Ordinal))
                return true;

            var replay = _goodsRequestInvocation;
            if (replay != null && ReferenceEquals(replay.Owner, this) && ReferenceEquals(replay.Panel, panel) &&
                (replay.Command == button || (replay.DeferExit && button == "Exit")))
                return true;

            switch (button)
            {
                case "ConfirmGoods":
                    return _pendingGoodsTarget > 0 || _goodsRequest != null || HasPendingGoodsCommand ||
                        (_sharedGoodsRequestActive && view.Allies_RequestGoodsViewVis);
                case "CancelGoods":
                    return _goodsRequest != null;
                case "Back":
                case "Exit":
                case "Send":
                case "Orders":
                case "CancelOrders":
                case "Request":
                case "0":
                case "1":
                case "2":
                case "3":
                case "4":
                case "5":
                    return true;
                default:
                    // Ordinary quantity/ware clicks belong to Vanilla or QoL.
                    return _goodsRequest != null && _goodsRequest.Confirmed &&
                        (button[0] == 'G' || button[0] == 'X');
            }
        }

        private static void OnSharedAlliesButtonPost(AllyPanelButtonPostEventArgs args)
        {
            if (args.OriginalCompleted)
                AlliesButtonPostfix(args.Button);
        }
#endif

        private static bool AlliesButtonPrefix(object __instance, string param)
        {
            DiplomacyFeature owner;
            lock (OwnerGate)
            {
                owner = _activeOwner;
            }

            if (owner == null)
            {
                return true;
            }

            try
            {
                return !owner.HandleButton(__instance, param);
            }
            catch (Exception exception)
            {
                owner.DisableMap("ui_command", exception);
                // Never fall through to the native button action after our
                // command interception itself failed. In particular, a
                // ConfirmGoods click may target a hostile player, for whom
                // Crusader's Ally_SendGoods path is not a safe fallback.
                // DisableMap restores owned state; later clicks see the
                // inactive feature and may use Vanilla normally again.
                return false;
            }
            finally
            {
                lock (owner._gate)
                {
                    owner.PublishCombatProtectionSnapshot();
                }
            }
        }

        private static void AlliesButtonPostfix(string param)
        {
            DiplomacyFeature owner;
            lock (OwnerGate)
            {
                owner = _activeOwner;
            }

            if (owner == null || param != "ConfirmGoods")
            {
                return;
            }

            try
            {
                owner.NoteLane(LaneUiPanel);
                lock (owner._gate)
                {
                    if (owner._nativeTransferProbe != null && owner._nativeTransferProbe.AlliedNativeTransfer && !owner._mapDisabled)
                    {
                        owner.VerifyNativeTransferProbe();
                    }
                }
            }
            catch (Exception exception)
            {
                owner.DisableMap("transfer_postfix", exception);
            }
        }

        private bool HandleButton(object panel, string param)
        {
            NoteLane(LaneUiPanel);
            lock (_gate)
            {
                // Closing the modal remains possible during map teardown.
                // This exact command has no trade or inventory authority.
                if (param == "Diplomacy:Trade:Close")
                    return HandleTradeButton(panel, param);
                if (HandleMpTradeOnlyButton(panel, param))
                    return true;
                bool requestHandled;
                if (TryHandleGoodsRequestCommand(panel, param, out requestHandled))
                    return requestHandled;
                if (!_mapActive || _mapDisabled || string.IsNullOrEmpty(param))
                {
                    return false;
                }

                if (param == "ConfirmGoods" && _pendingGoodsTarget > 0)
                {
                    if (HasPendingGoodsCommand)
                    {
                        PresentPendingGoodsCommand();
                        return true;
                    }

                    int goodId = (int)_selectedGoodsField.GetValue(panel);
                    int amount = (int)_selectedGoodsAmountField.GetValue(panel);
                    int target = _pendingGoodsTarget;
                    QueueGoodsCommand(panel, target, goodId, amount);
                    return true;
                }

                if (param == "ConfirmGoods")
                {
                    int staleTarget = SelectedPlayer(panel);
                    // A send always needs our captured command. In particular,
                    // a second click after Busy must not escape into untracked
                    // Vanilla sending. Preserve only its explicit request view.
                    if (HasPendingGoodsCommand || MainViewModel.Instance == null || !MainViewModel.Instance.Allies_RequestGoodsViewVis || staleTarget <= 0 || !IsCoalitionAllied(staleTarget))
                    {
                        Log("transfer", "status=rejected|reason=stale_send_confirm");
                        return true;
                    }
                }

                if (param == "Back" && _pendingGoodsTarget > 0)
                {
                    CancelPendingGoodsCommand("back");
                    _pendingGoodsTarget = 0;
                    return false;
                }

                if (param == "Back" || param == "Exit")
                {
                    CloseTradeWindow(panel);
                    CancelPendingGoodsCommand(param == "Back" ? "back" : "exit");
                    _pendingGoodsTarget = 0;
                }

                int nativeSlot;
                if (int.TryParse(param, NumberStyles.None, CultureInfo.InvariantCulture, out nativeSlot) && nativeSlot >= 0 && nativeSlot <= 5)
                {
                    CancelPendingGoodsCommand("target_selection");
                    if (nativeSlot < _visiblePlayers.Count)
                    {
                        _selectedVisibleIndex = nativeSlot;
                        _overviewVisible = false;
                    }

                    return false;
                }

                if (param == "Diplomacy:Select:6")
                {
                    CancelPendingGoodsCommand("target_selection");
                    if (_visiblePlayers.Count > 6)
                    {
                        _selectedVisibleIndex = 6;
                        _overviewVisible = false;
                        _currentAllyField.SetValue(panel, 6);
                        _updateAlliesTarget.Invoke(panel, null);
                    }

                    return true;
                }

                int selectedPlayer = SelectedPlayer(panel);
                if (param == "CancelOrders" && !_tradeTab && !_overviewVisible)
                {
                    // A genuine original HUD CancelOrders click is
                    // EngineInterface.GameAction(1066,consulted,0,0), not
                    // a replay of our Ally_Orders(1061) Opcode10. Retire
                    // only outstanding MOD-owned human intent/maintenance.
                    // Return FALSE to preserve Vanilla's actual cancel call.
                    // Never claim its native cancellation succeeded.
                    RetireHumanOrderTrackingOnOriginalCancel(panel, selectedPlayer);
                    return false;
                }
                if (HandleTradeButton(panel, param))
                    return true;
                if (_tradeTab && (param == "Request" || param == "Send" || param == "Orders" || param == "Diplomacy:Fealty" || param == "Diplomacy:Break" || param == "Diplomacy:DeclineDemand"))
                    return true;
                if (TryHandleAllianceUiCommand(panel, param))
                    return true;
                // A tab changes the available controls, not just their paint.
                // Reject stale/focused detail commands while overview owns the
                // page, before any inventory, order or relationship action.
                if (_overviewVisible && (param == "Request" || param == "Send" || param == "Orders" || param == "Diplomacy:Fealty" || param == "Diplomacy:Break" || param == "Diplomacy:DeclineDemand"))
                    return true;
                if (param == "Diplomacy:Fealty" && selectedPlayer > 0)
                {
                    HandleFealtyAction(selectedPlayer);
                    RefreshVanillaDiplomacyUi(panel);
                    return true;
                }

                // Verhandlungsbedingungen entstehen bei Fuersten
                // ohne Buendnis. Der Ablehnen-Knopf der Buendnisuebersicht war
                // fuer sie unerreichbar - hier ist derselbe Weg aus der
                // Detailansicht.
                if (param == "Diplomacy:DeclineDemand" && selectedPlayer > 0)
                {
                    HandleDeclineDemandForPlayer(selectedPlayer);
                    RefreshVanillaDiplomacyUi(panel);
                    return true;
                }

                if (param == "Request" && selectedPlayer > 0)
                {
                    string demandCoalition;
                    RelationState demandRelation;
                    if (_playerCoalition.TryGetValue(selectedPlayer, out demandCoalition) && _relations.TryGetValue(demandCoalition, out demandRelation) && demandRelation.Demand != null)
                    {
                        QueueGoodsCommand(panel, selectedPlayer, 0, 0, demandRelation.Demand);
                        RefreshVanillaDiplomacyUi(panel);
                        return true;
                    }

                    if (IsCoalitionAllied(selectedPlayer))
                    {
#if !DIPLOMACY_PRIVATE_TESTS
                        if (!_sharedGoodsRequestActive)
                            return false;
#endif
                        BeginGoodsRequest(panel, selectedPlayer, false);
                        return true;
                    }
                }

                if (param == "Send" && selectedPlayer > 0)
                {
                    if (IsCoalitionAllied(selectedPlayer))
                    {
#if DIPLOMACY_PRIVATE_TESTS
                        _pendingGoodsTarget = selectedPlayer;
#endif
                        return false;
                    }

                    BeginGoodsSelection(panel, selectedPlayer);
                    return true;
                }

                if (param == "Request" && selectedPlayer > 0 && !IsCoalitionAllied(selectedPlayer))
                {
                    RequestAlliance(selectedPlayer);
                    RefreshVanillaDiplomacyUi(panel);
                    return true;
                }

                if (param == "Orders" && selectedPlayer > 0 && !IsCoalitionAllied(selectedPlayer))
                {
                    return true;
                }

                if (param == "Diplomacy:Overview" || param == "Diplomacy:Details")
                {
                    _tradeTab = false;
                    _draftPartner = 0; // Other tabs revoke old trade-target authority.
                    // Reine Anzeige - kein Spielzustand aendert sich.
                    if (param == "Diplomacy:Overview")
                        OpenAllianceOverview(panel, selectedPlayer);
                    else
                        _overviewVisible = false;
                    RefreshVanillaDiplomacyUi(panel);
                    return true;
                }

                if (param == "Diplomacy:Break")
                {
                    if (selectedPlayer > 0 && IsCoalitionAllied(selectedPlayer))
                    {
                        ConfirmPlayerBreakOne(selectedPlayer);
                    }
                    else if (selectedPlayer > 0)
                    {
                        ConfirmBreakAgreement(selectedPlayer);
                    }

                    return true;
                }

                return param.StartsWith("Diplomacy:", StringComparison.Ordinal);
            }
        }

#if DIPLOMACY_PRIVATE_TESTS
        private void OnLoadMap(MapLoadEventArgs e)
        {
            NoteLane(LaneMapLoader);
            if (e != null && e.Phase == EventHookPhase.Post)
            {
                lock (_gate)
                {
                    BeginNativeMapEpoch("map_load");
                    _epochOpenedByMapLoad = true;
                }

                ScheduleMapBootstrap("map_load");
            }
        }

#endif

        private void OnLoadSave(LoadSaveGameEventArgs e)
        {
#if !DIPLOMACY_PRIVATE_TESTS
            if (_originalLoadInvocation == null || _missionSaveAttempt.Pending == 0)
                return;
#endif
            NoteLane(LaneMapLoader);
            if (e != null && e.Phase == EventHookPhase.Pre)
            {
                lock (_gate)
                    _originalSavedWorld.SavePre(_originalLoadInvocation, _mapEpoch);
            }
            if (e != null && e.Phase == EventHookPhase.Post)
            {
                lock (_gate)
                {
                    ResetGoodsRequests();
                    bool currentEpochAwaitingBootstrap = _mapBootstrapPending && _mapBootstrapEpoch == _mapEpoch && !_mapActive;
                    if (!_epochOpenedByMapLoad && !currentEpochAwaitingBootstrap)
                    {
                        BeginNativeMapEpoch("save_load");
                        _epochOpenedByMapLoad = true;
                    }

                    _originalSavedWorld.SavePost(_originalLoadInvocation, _mapEpoch);
                    if (_pendingSavePayload != null && _pendingSaveEpoch != _mapEpoch)
                    {
                        if (_originalSavedWorld.CanRebase(_pendingSaveApplySerial, _mapEpoch))
                            _pendingSaveEpoch = _mapEpoch;
                        else
                        {
                            _pendingSavePayload = null;
                            _pendingSaveEpoch = -1;
                            _pendingSaveStagedFeatureTick = -1;
                            _pendingSaveApplySerial = 0;
                            Log("saving", "status=stale_payload_discarded" + "|trigger=save_load_epoch");
                        }
                    }

                    ReconcileLegacySaveEpoch();
                }

#if DIPLOMACY_PRIVATE_TESTS
                try
                {
                    if (IsPlayableMapRuntimeReady())
                    {
                        PauseRestoredSession();
                    }
                }
                catch (Exception exception)
                {
                    DisableMap("save_load_pause", exception);
                    return;
                }

                ScheduleMapBootstrap("save_load");
#endif
            }
        }

        private void ScheduleMapBootstrap(string trigger)
        {
            lock (_gate)
            {
                if (_mapFailureLatched)
                {
                    Log("activation", "status=bootstrap_ignored|reason=failure_latched" + "|trigger=" + Safe(trigger));
                    return;
                }

                _mapBootstrapPending = true;
                _mapBootstrapTrigger = trigger;
                _mapBootstrapEpoch = _mapEpoch;
                // The lifecycle callback, the mod-save callback and the HUD
                // reconstruction are synchronous parts of the same load. One
                // Unity update of distance prevents an old save without an
                // OnStartMap event from being initialized before its embedded
                // diplomacy payload has been staged.
                _mapBootstrapNotBeforeTick = _featureTicks + 1;
                Log("activation", "status=bootstrap_pending|trigger=" + Safe(trigger));
            }
        }

        private void BeginNativeMapEpoch(string trigger)
        {
            long previousEpoch = _mapEpoch;
            _mapEpoch = checked(_mapEpoch + 1L);
            _originalSavedWorld.NativeEpochChanged(_mapEpoch);
            ResetGoodsRequests();
            BeginLegacyMapEpoch(previousEpoch);
            if (_pendingSavePayload != null && _pendingSaveEpoch == previousEpoch &&
                !_originalSavedWorld.CanRetainAcrossEpoch(_pendingSaveApplySerial, previousEpoch) &&
                _pendingSaveStagedFeatureTick < _featureTicks)
            {
                _pendingSavePayload = null;
                _pendingSaveEpoch = -1;
                _pendingSaveStagedFeatureTick = -1;
                _pendingSaveApplySerial = 0;
                Log("saving", "status=stale_payload_discarded" + "|trigger=native_epoch");
            }

            _sessionGeneration++;
            _mapIdentity = null;
            ResetTradeUiForMap();
            ResetTradeSession();
            ResetTradeRuntime();
            _mapActive = false;
            _mapDisabled = false;
            _mapFailureLatched = false;
            _mapBootstrapPending = false;
            _mapBootstrapTrigger = null;
            _pendingGoodsTarget = 0;
            _ownedAssignedTeams.Clear();
            _ownedOriginalTeams.Clear();
            _nativeTransferProbe = null;
            _pendingPackage = null;
            _inventoryRecoveryBaseState = null;
            _inventoryRecoveryRetryTick = 0;
            _inventoryRestorePending = false;
            _transferBookkeepingInProgress = false;
            _lastObservedNativeDay = -1L;
            _uiRefreshInProgress = false;
            _uiStepBudget = 40;
            _uiCycleLogged = false;
            _rareStepBudget = 200;
            _overviewVisible = false;
            // Der Militaerwert haengt am Spieltag, und der faengt bei einer
            // neuen Karte wieder bei 0 an - ohne Verwerfen traefe der Wert der
            // vorigen Partie zu ().
            _militaryByPlayer = null;
            _militaryByPlayerDay = -1;
            _vassalOrdersSeen = 0;
            _vassalOrdersForced = 0;
            _vassalOrdersMismatched = 0;
            _vassalOrderLogLines = 0;
            _vassalOrderFaults = 0;
            _nativeEnemyTransferSupported = null;
            ClearMapState();
            Log("activation", "status=native_epoch_started|epoch=" + _mapEpoch.ToString(CultureInfo.InvariantCulture) + "|trigger=" + Safe(trigger));
        }

#if DIPLOMACY_PRIVATE_TESTS
        private void OnStartMap(MapStartEventArgs e)
        {
            NoteLane(LaneMapLoader);
            if (e == null || e.Phase != EventHookPhase.Post)
            {
                return;
            }

            lock (_gate)
            {
                if (_epochOpenedByMapLoad)
                {
                    _epochOpenedByMapLoad = false;
                }
                else if (_lastMapStartFeatureTick == _featureTicks)
                {
                    Log("activation", "status=bootstrap_ignored|reason=duplicate_map_start" + "|epoch=" + _mapEpoch.ToString(CultureInfo.InvariantCulture));
                    return;
                }
                else
                {
                    BeginNativeMapEpoch("map_start");
                }

                _lastMapStartFeatureTick = _featureTicks;
                if (_mapActive && !_mapBootstrapPending)
                {
                    return;
                }
            }

            ScheduleMapBootstrap("map_start");
        }

#else
        private void OnSharedMissionInitialization(MissionLifecycleNotification notification)
        {
            if (!ReferenceEquals(Volatile.Read(ref _activeOwner), this) ||
                notification.Phase != MissionInitializationPhase.BeforeLoad ||
                !notification.Context.IsSave)
                return;
            lock (_gate)
            {
                if (_missionLifecycle.Current == null && _missionSaveAttempt.TryBegin(notification.Context.SessionId))
                    DiscardStagedPublicSave();
            }
        }

        private void DiscardStagedPublicSave()
        {
            _epochOpenedByMapLoad = false;
            _pendingSavePayload = null;
            _pendingSaveEpoch = -1;
            _pendingSaveStagedFeatureTick = -1;
            _pendingSaveApplySerial = 0;
            ConsumePendingLegacySave();
        }

        private static bool IsSupportedPublicSoloMission(MissionContext mission) =>
            mission != null && !mission.IsEditor &&
            mission.Mode.IsSingleplayerSkirmish && !mission.Mode.IsRealMultiplayer &&
            RuntimeSessionScopeDecision.IsOrdinarySkirmishPath(mission.Mode.SkirmishGameType, mission.Mode.CoopTrailId);

        private bool IsCurrentPublicSoloMission(MissionContext mission) =>
            IsSupportedPublicSoloMission(mission) && mission.SessionId == _missionSessionId;

        private void OnSharedMissionStart(MissionLifecycleNotification notification)
        {
            if (!ReferenceEquals(Volatile.Read(ref _activeOwner), this))
                return;

            NoteLane(LaneMapLoader);
            var context = notification.Context;
            lock (_gate)
            {
                if (!IsSupportedPublicSoloMission(context) ||
                    !_missionSaveAttempt.TryAcceptStart(context.SessionId, _missionLifecycle.Current?.SessionId ?? 0,
                        context.IsSave, _epochOpenedByMapLoad))
                    return;

                if (!context.IsSave)
                {
                    DiscardStagedPublicSave();
                    BeginNativeMapEpoch("map_start");
                }
                _epochOpenedByMapLoad = false;
                _missionSessionId = context.SessionId;
                // The original producer finalizer may still be on the stack.
                // Tick waits for both a fresh PlayState and the saved-world proof.
                ScheduleMapBootstrap(context.IsSave ? "save_load" : "map_start");
            }
        }

        private void OnSharedMissionEnd(MissionLifecycleNotification notification)
        {
            if (!ReferenceEquals(Volatile.Read(ref _activeOwner), this))
                return;

            NoteLane(LaneMapLoader);
            lock (_gate)
            {
                long ending = notification.Context.SessionId;
                if (_missionSaveAttempt.Retire(ending))
                {
                    DiscardStagedPublicSave();
                    _originalSavedWorld.Retire();
                }
                if (ending != _missionSessionId)
                    return;

                // Nested lifecycle notifications can be delivered after the
                // native loader returns. End retires ownership but does not
                // authorize writes to the previous world's player slots.
                _missionSessionId = 0;
                // The image attestation lasts for this process. Mission state and
                // queued/replaying tickets are retired here, not the shared capability.
                Volatile.Write(ref _sharedGoodsRequestActive, false);
                ClearAllyOrders();
                ResetGoodsRequests();
                CancelPendingGoodsCommand("mission_end");
                _pendingGoodsTarget = 0;
                _dialogQueue.Clear();
                ClearDeferredDiplomacyUi();
                _mapBootstrapPending = false;
                _mapBootstrapTrigger = null;
                _mapActive = false;
                _mapDisabled = false;
                _sessionGeneration++;
                _deferredDisableStage = null;
                _deferredDisableException = null;
                _saveFailurePending = null;
                SetCombatProtectionSnapshot(CombatProtectionSnapshot.Empty);
                Log("activation", "status=mission_retired|reason=" + notification.EndReason);
            }
        }
#endif

        private void BeginMapSession(string trigger)
        {
            if (_mapBootstrapEpoch != _mapEpoch)
            {
                _mapBootstrapPending = false;
                _mapBootstrapTrigger = null;
                Log("activation", "status=bootstrap_ignored|reason=stale_epoch" + "|trigger=" + Safe(trigger));
                return;
            }

            if (_mapFailureLatched)
            {
                _mapBootstrapPending = false;
                _mapBootstrapTrigger = null;
                Log("activation", "status=bootstrap_ignored|reason=failure_latched" + "|trigger=" + Safe(trigger));
                return;
            }

            // Acquire before consuming staged payloads or resetting the model.
            // A busy engine leaves the complete bootstrap request untouched.
            IDisposable bootstrapLease;
            if (!TryEnterNativeInventorySection(out bootstrapLease))
                return;
            try
            {
                byte[] stagedSavePayload = _pendingSaveEpoch == _mapEpoch ? _pendingSavePayload : null;
                byte[] stagedLegacySavePayload = StageLegacySaveForBootstrap();
                PersistentMapIdentity provenOriginalIdentity = null;
                bool hasOriginalSavedWorld = _originalSavedWorld.HasSaveObservation(_mapEpoch) ||
                    _pendingSavePayload != null || _pendingLegacySavePayload != null;
                if (hasOriginalSavedWorld)
                {
                    // A callback/order witness is necessary but not enough:
                    // the full original source bytes and the exact 8x2 live
                    // layout must also be available under the EXISTING lock.
                    if (!_originalSavedWorld.IsApplied(_mapEpoch) ||
                        (_pendingSavePayload != null &&
                         (_pendingSaveEpoch != _mapEpoch ||
                          !_originalSavedWorld.IsAppliedForPayload(_mapEpoch, _pendingSaveApplySerial))) ||
                        (_pendingLegacySavePayload != null &&
                         (_pendingLegacySaveEpoch != _mapEpoch ||
                          !_originalSavedWorld.IsAppliedForPayload(_mapEpoch, _pendingLegacyLoadSerial))))
                        return;
                    try
                    {
                        provenOriginalIdentity = CaptureMapIdentity();
                    }
                    catch (Exception readinessError)
                    {
                        // A temporary missing source/8x2 layout is NOT a
                        // historical identity contradiction. Retain bytes,
                        // and retry only on a later managed feature tick.
                        _mapBootstrapNotBeforeTick = _featureTicks + 8;
                        Log("activation", "status=original_saved_world_not_ready" +
                            "|reason=" + Safe(readinessError.GetType().Name));
                        return;
                    }
                }
                if (_pendingSavePayload != null && stagedSavePayload == null)
                {
                    Log("saving", "status=stale_payload_discarded|trigger=bootstrap");
                }

                _mapBootstrapPending = false;
                _mapBootstrapTrigger = null;
                _epochOpenedByMapLoad = false;
                _sessionGeneration++;
                _pendingSavePayload = null;
                _pendingSaveEpoch = -1;
                _pendingSaveStagedFeatureTick = -1;
                _pendingSaveApplySerial = 0;
                ConsumePendingLegacySave();
                ClearMapState();
#if DIPLOMACY_PRIVATE_TESTS && HOST_DIRECTED_AI_TRIBE_CANARY
                // Old save intents are NEVER replayed. In a restored map only a
                // NEW observed relief-emergency edge after full bootstrap may
                // become a mod-authored candidate; routine cold load is not a
                // permanent feature shutdown.
                _hostDirectedColdNeedsBaseline =
                    stagedSavePayload != null || stagedLegacySavePayload != null ||
                    string.Equals(trigger, "save_load", StringComparison.Ordinal);
                _hostDirectedRequireFreshReliefEdge = _hostDirectedColdNeedsBaseline;
#endif
                // Native map replacement invalidates the previous map's team
                // journal. Never replay those player IDs into the new roster.
                _ownedAssignedTeams.Clear();
                _ownedOriginalTeams.Clear();
                _nativeTransferProbe = null;
                _pendingPackage = null;
                _inventoryRecoveryBaseState = null;
                _inventoryRecoveryRetryTick = 0;
                _inventoryRestorePending = false;
                _transferBookkeepingInProgress = false;
                _lastObservedNativeDay = -1L;
                _uiRefreshInProgress = false;
                _uiStepBudget = 40;
                _uiCycleLogged = false;
                _rareStepBudget = 200;
                _overviewVisible = false;
                // Der Militaerwert haengt am Spieltag, und der faengt bei einer
                // neuen Karte wieder bei 0 an - ohne Verwerfen traefe der Wert der
                // vorigen Partie zu ().
                _militaryByPlayer = null;
                _militaryByPlayerDay = -1;
                _vassalOrdersSeen = 0;
                _vassalOrdersForced = 0;
                _vassalOrdersMismatched = 0;
                _vassalOrderLogLines = 0;
                _vassalOrderFaults = 0;
                _mapActive = false;
                _mapDisabled = false;
                _nativeEnemyTransferSupported = null;
                try
                {
                    bool networked = GameNetworkAPI.IsNetworkedEnvironment();
                    bool localHost = true;
                    int activePlayers = -1;
                    int otherHumanPlayers = -1;
                    string sessionRosterReason = "offline_no_network_roster";
#if DIPLOMACY_PRIVATE_TESTS
                    bool lobbyBlocksSolo = GameNetworkAPI.IsLobbyEnvironment();
#else
                    var mission = _missionLifecycle.Current;
                    // A local skirmish can retain its lobby. Only the current APIShared mission classifies it.
                    bool lobbyBlocksSolo = !IsCurrentPublicSoloMission(mission);
#endif
                    if (networked)
                    {
                        localHost = GameNetworkAPI.IsLocalHost();
                        activePlayers = GameNetworkAPI.GetNumActivePlayers();
                        var players = GameNetworkAPI.GetPlayers();
                        var live = GameData.Instance == null ?
                            null : GameData.Instance.lastGameState;
                        if (players != null && live != null)
                        {
                            // A loaded SOLO skirmish can expose synthetic
                            // MPGameMember entries, but an actual remote
                            // original human may have SteamId=0 while joining.
                            // Only original PlayState human/AI evidence can
                            // disambiguate; otherwise SP activation refuses.
                            var human = new bool[9];
                            var ai = new bool[9];
                            for (int slot = 1; slot <= 8; ++slot)
                            {
                                human[slot] = live.is_valid_player(slot);
                                ai[slot] = live.is_skirmish_player(slot);
                            }
                            var members = new System.Collections.Generic.List<ObservedSkirmishMember>(8);
                            foreach (var player in players)
                            {
                                members.Add(player == null ? null :
                                    new ObservedSkirmishMember
                                    {
                                        Slot = player.playerID,
                                        Self = player.isSelf,
                                        Kicked = player.kicked,
                                        SkirmishAi = player.skirmishAI,
                                        SteamId = player.steamID
                                    });
                            }
                            otherHumanPlayers =
                                RuntimeSessionScopeDecision.CountOtherOriginalHumanPlayers(
                                    GameData.Instance.playerID, human, ai,
                                    members, lobbyBlocksSolo,
                                    out sessionRosterReason);
                        }
                    }

#if DIPLOMACY_PRIVATE_TESTS
                    int skirmishPath = GameData.Instance == null ? -1 : GameData.Instance.SkirmishGameType;
                    int coopTrail = GameData.Instance == null ? -1 : GameData.Instance.coopTrailID;
#else
                    int skirmishPath = mission?.Mode.SkirmishGameType ?? -1;
                    int coopTrail = mission?.Mode.CoopTrailId ?? -1;
#endif
                    bool supportedSkirmishPath =
                        RuntimeSessionScopeDecision.IsOrdinarySkirmishPath(skirmishPath, coopTrail);
                    if (networked && otherHumanPlayers < 0)
                        Log("session_scope", "status=roster_unproven|reason=" +
                            Safe(sessionRosterReason));
                    Log("session_scope", "status=" + RuntimeSessionScopeDecision.Status(networked, localHost, otherHumanPlayers) + "|networked=" + (networked ? "true" : "false") + "|local_host=" + (localHost ? "true" : "false") + "|active_players=" + activePlayers.ToString(CultureInfo.InvariantCulture) + "|other_humans=" + otherHumanPlayers.ToString(CultureInfo.InvariantCulture) + "|skirmish_path=" + skirmishPath.ToString(CultureInfo.InvariantCulture) + "|coop_trail=" + coopTrail.ToString(CultureInfo.InvariantCulture) + "|trigger=" + Safe(trigger));
#if DIPLOMACY_PRIVATE_TESTS
                    bool supportedByOriginalSession = RuntimeSessionScopeDecision.AllowsDiplomacyMap(networked, localHost, otherHumanPlayers, GameData.Instance != null, GameData.Instance != null && GameData.Instance.lastGameState != null, GameData.Instance != null && GameData.Instance.multiplayerMap, Director.instance != null, Director.instance != null && Director.instance.MultiplayerGame, MainViewModel.Instance != null, MainViewModel.Instance != null && MainViewModel.Instance.SkirmishModeVisAlly);
                    if (!supportedByOriginalSession && supportedSkirmishPath)
                    {
                        string scopeReason;
                        // The original menu property is presentation. Accept a
                        // cold private load only with exact process provenance,
                        // complete native solo roles AND its original skirmish map.
                        bool verifiedSolo = DisposableGameScope.IsEnabled(out scopeReason) && IsSoloEngineSession(out scopeReason);
                        var menu = MainViewModel.Instance == null ? null : MainViewModel.Instance.HUDIngameMenu;
                        var restart = menu == null ? null : menu.restartSkirmishMapInfo;
                        verifiedSolo = verifiedSolo && restart != null && restart.selectedHeader != null && restart.selectedHeader.skirmishMap;
                        if (verifiedSolo)
                        {
                            CaptureMapIdentity(); // Full source read, never a partial header CRC.
                            supportedByOriginalSession = true;
                        }

                        Log("trade_session", "status=private_bootstrap_native_session_proof|verified=" + verifiedSolo + "|reason=" + Safe(scopeReason));
                    }

#else
                    bool supportedByOriginalSession = !lobbyBlocksSolo &&
                        RuntimeSessionScopeDecision.AllowsLocalMutation(networked, localHost, otherHumanPlayers);
#endif
                    if (supportedSkirmishPath && !supportedByOriginalSession && BootstrapOriginalMpTradeOnly(stagedSavePayload, stagedLegacySavePayload))
                        return;
                    if (!supportedSkirmishPath || !supportedByOriginalSession)
                    {
                        _mapDisabled = true;
                        Log("activation", "status=inactive|reason=not_offline_skirmish");
                        return;
                    }

                    bool explicitlyInactive = DiplomacyPersistenceCodec.IsInactive(stagedSavePayload);
                    explicitlyInactive = explicitlyInactive || stagedSavePayload == null && DiplomacySaveCodec.IsInactive(stagedLegacySavePayload);
                    if (explicitlyInactive)
                    {
                        _mapDisabled = true;
                        _saveSuppressed = true;
                        Log("saving", "status=inactive_marker_restored");
                        bootstrapLease.Dispose();
                        bootstrapLease = null;
                        try
                        {
                            ShowOkMessageNative(Text("Diplomatie", "Diplomacy"), null, Text("Dieser Spielstand hat die Diplomatie ausdrücklich abgeschaltet. Die gespeicherten Mannschaften bleiben erhalten.", "Diplomacy is explicitly disabled in this save. Its saved teams are preserved."));
                        }
                        catch
                        {
                        }

                        return;
                    }

                    CaptureRosterAndCoalitions();
                    if (_coalitions.Count < 1)
                    {
                        _mapDisabled = true;
                        Log("activation", "status=inactive|reason=no_ai_coalitions");
                        return;
                    }

                    FreezePrices();
                    InitializeMapIdentity(provenOriginalIdentity);
                    _mapFingerprint = _mapIdentity == null ? Fingerprint(MapMaterial()) : _mapIdentity.Fingerprint;
                    _legacyMapFingerprint = Fingerprint(LegacyMapMaterial());
                    _rosterFingerprint = Fingerprint(RosterMaterial());
#if DIPLOMACY_PRIVATE_TESTS
                    HeadlessQaDriver.Note("status=bootstrap_identity|map_hash=" + _mapFingerprint + "|roster_hash=" + _rosterFingerprint + "|map_material=" + MapMaterial() + "|roster_material=" + RosterMaterial());
#endif
                    _lastProcessedDay = DiplomacyLogicalClock.NewSessionDay;
                    if (stagedSavePayload != null)
                    {
                        PauseRestoredSession();
                        RestorePrimaryOrLegacySave(stagedSavePayload, stagedLegacySavePayload);
                    }
                    else if (stagedLegacySavePayload != null)
                    {
                        PauseRestoredSession();
                        RestoreLegacySave(stagedLegacySavePayload);
                    }
                    else
                    {
                        InitializeRelations();
                    }

                    if (_lastObservedNativeDay < 0)
                        _lastObservedNativeDay = ScriptExtenderNativeBridge.CurrentDay();
                    _mapActive = true;
#if DIPLOMACY_PRIVATE_TESTS && HOST_DIRECTED_AI_TRIBE_CANARY
                    // World/save bootstrap is complete. Only a strictly later
                    // native tick may create a fresh, never-restored intent.
                    _hostDirectedFreshAfterTick = ReadSimulationTick();
#endif
                    RetryAgreementSafety();
                    PublishCombatProtectionSnapshot();
                    Log("activation", "status=map_active|coalitions=" + _coalitions.Count.ToString(CultureInfo.InvariantCulture) + "|roster=" + _rosterFingerprint + "|trigger=" + Safe(trigger));
                    bootstrapLease.Dispose();
                    bootstrapLease = null;
                    ShowNextPendingOffer();
                    if (stagedSavePayload != null || stagedLegacySavePayload != null)
                    {
                        RestorePendingDecisions();
                    }

                    PumpDiplomacyDialogs();
                }
                catch (Exception exception)
                {
                    if (bootstrapLease != null)
                    {
                        bootstrapLease.Dispose();
                        bootstrapLease = null;
                    }

                    DisableMap("map_bootstrap_" + Safe(trigger), exception);
                }
            }
            finally
            {
                if (bootstrapLease != null)
                    bootstrapLease.Dispose();
            }
        }

        private static bool IsPlayableMapRuntimeReady()
        {
            try
            {
                return GameData.Instance != null && GameData.Instance.lastGameState != null && GameData.Instance.playerID >= 1 && GameData.Instance.playerID <= 8 && Director.instance != null && MainViewModel.Instance != null && GameMap.tilemapSize > 0 && HasLivingLocalPlayer();
            }
            catch
            {
                return false;
            }
        }

        private static bool HasLivingLocalPlayer()
        {
            GamePlayerManagerAPI players = GamePlayerManagerAPI.Instance;
            if (players == null)
            {
                return false;
            }

            int localPlayer = players.GetLocalPlayerId();
            int[] alive = players.GetAlivePlayerIds();
            return localPlayer >= 1 && localPlayer <= 8 && alive != null && alive.Contains(localPlayer);
        }

#if DIPLOMACY_PRIVATE_TESTS
        private void OnUnloadMap(MapUnloadEventArgs e)
        {
            NoteLane(LaneMapLoader);
            if (e == null || e.Phase != EventHookPhase.Pre)
            {
                return;
            }

            lock (_gate)
            {
                bool guardedRollbackRequired = _mapActive || _nativeTransferProbe != null || _pendingPackage != null || _ownedAssignedTeams.Count > 0 || _disabledTeamRecoveryPending;
                if (guardedRollbackRequired)
                {
                    DisableMap("map_unload", new InvalidOperationException("native map unload requested diplomacy rollback"), true);
                }

                bool stable = !_rollbackGuardActive && _nativeTransferProbe == null && _pendingPackage == null && !_disabledTeamRecoveryPending;
                if (stable)
                {
                    _sessionGeneration++;
                    _ownedAssignedTeams.Clear();
                    _ownedOriginalTeams.Clear();
                    ClearMapState();
                    _mapFailureLatched = false;
                    _mapActive = false;
                    _mapDisabled = false;
                    Log("activation", "status=map_unloaded");
                }
                else
                {
                    Log("activation", "status=map_unload_recovery_retained" + "|epoch=" + _mapEpoch.ToString(CultureInfo.InvariantCulture));
                }
            }
        }

#endif

        // Der Tageszaehler des Extenders ist seit SE 2.0 ein uint; bis 1.45
        // war er short. Gemessen am im Spiel: gleicher Feldname,
        // anderer Typ - der Mitgliedervertrag sah das nicht (Name plus
        // Parameterzahl), der JIT schon: MissingFieldException beim Start.
        // Zuletzt gesehener Spieltag der Abfrage; -1 heisst "noch keiner".
        private long _lastObservedNativeDay = -1L;
        // Wiedereintrittsschutz und Schrittmarken der Oberflaeche
        // (, Absturzsuche).
        private bool _uiRefreshInProgress;
        private int _uiStepBudget = 40;
        private bool _uiCycleLogged;
        private int _rareStepBudget = 200;
        /// <summary>
        /// Schrittmarke VOR dem naechsten nativen Block. Endet das Protokoll
        /// hier, ist genau dieser Block die Absturzstelle.
        /// </summary>
        private void UiStep(string step)
        {
            // Der UI-Pfad laeuft je Bild. Ein Budget allein reicht nicht: es
            // war am nach sieben Zyklen aufgebraucht, und die
            // Spur endete dort - nicht am Absturz. Deshalb nur der erste
            // vollstaendige Zyklus je Karte, danach nur noch Seltenes.
            if (_uiStepBudget <= 0 || _uiCycleLogged)
            {
                return;
            }

            _uiStepBudget--;
            if (string.Equals(step, "_visibility", StringComparison.Ordinal))
            {
                _uiCycleLogged = true;
            }

            Log("ui_step", "at=" + step);
        }

        /// <summary>
        /// Marke aus statischem Zusammenhang (Meldungsfenster).
        /// </summary>
        private static void RareStepStatic(string step)
        {
            DiplomacyFeature owner;
            lock (OwnerGate)
            {
                owner = _activeOwner;
            }

            if (owner != null)
            {
                owner.RareStep(step);
            }
        }

        /// <summary>
        /// Marke fuer seltene Ereignisse - eigenes Budget, damit der
        /// Bildpfad sie nicht verdraengt.
        /// </summary>
        private void RareStep(string step)
        {
            if (_rareStepBudget <= 0)
            {
                return;
            }

            _rareStepBudget--;
            Log("step", "at=" + step);
        }

        /// <summary>
        /// Erkennt den Tageswechsel im Simulationstakt. Frueher kam er aus
        /// GameTimeManagerAPI.CurrentDayReactive; dessen Typ hat der Extender
        /// zwischen seinen Reihen gewechselt, was die DLL an eine Reihe band.
        /// </summary>
        private void PollNativeDay()
        {
            long day;
            try
            {
                day = ScriptExtenderNativeBridge.CurrentDay();
            }
            catch (Exception exception)
            {
                DisableMap("day_poll", exception);
                return;
            }

            if (day == _lastObservedNativeDay)
            {
                return;
            }

            long previousAnchor = _lastObservedNativeDay;
            _lastObservedNativeDay = day;
            if (!OnDayChanged())
                _lastObservedNativeDay = previousAnchor;
        }

        private bool OnDayChanged()
        {
            lock (_gate)
            {
                if (!_mapActive || _mapDisabled)
                {
                    return false;
                }

                int previousModelDay = _lastProcessedDay;
                try
                {
                    if (_nativeTransferProbe != null)
                    {
                        VerifyNativeTransferProbe();
                    }

                    if (_nativeTransferProbe != null)
                    {
                        string transferFailure = AbortNativeTransferProbe();
                        if (transferFailure == "native_inventory_busy")
                            return false;
                        if (transferFailure != null)
                        {
                            DisableGoodsTransfers("day_boundary_rollback:" + transferFailure);
                        }
                        else
                        {
                            Log("transfer", "status=aborted_for_day_boundary");
                        }
                    }

                    // An unresolved inventory journal still belongs to the
                    // coherent model/day captured before its first write.
                    // Keep that clock until commit/rollback actually finishes;
                    // otherwise a later saved answer outdates its recovery seed.
                    if (_nativeTransferProbe != null || _pendingPackage != null)
                        return false;
                    if (!DrainTeamTransitionsAtDayBoundary("day_boundary_before"))
                        return false;
                    ProcessDay(DiplomacyLogicalClock.NextDay(_lastProcessedDay));
                    DrainTeamTransitionsAtDayBoundary("day_boundary_after");
                }
                catch (Exception exception)
                {
                    DisableMap("day_change", exception);
                }

                // ProcessDay commits all pure models before native effects.
                // A later effect failure must not reset an already consumed
                // native day and manufacture a second logical day after load.
                return _lastProcessedDay != previousModelDay;
            }
        }

        // Verwaltete Fadenkennungen der Spuren. Nur zur Messung:
        // sie beantworten, ob der Simulationstakt ueberhaupt auf einem
        // anderen Faden laeuft als das Unity-Bild ().
        // Gemessen: frame_thread=1, simulation_thread=22 - sie sind getrennt.
        private int _simulationThreadId;
        // ------------------------------------------------------------------
        // Spurenregister ()
        // ------------------------------------------------------------------
        // Fuer jede Spur: auf welchem Faden lief sie zuerst, wie oft wurde sie
        // betreten, und ist sie jemals auf einem ZWEITEN Faden aufgetaucht.
        // Der letzte Punkt ist der wichtige: wandert eine Spur, sind alle
        // Annahmen ueber Sperren hinfaellig.
        // Feste Groesse, keine Belegung, keine Sperre - `NoteLane` wird aus
        // nativen Rueckrufen gerufen. Ein verlorener Zaehlerschritt bei
        // gleichzeitigem Betreten ist hinnehmbar: gebraucht wird die
        // Groessenordnung und die Fadenkennung, nicht die exakte Zahl.
        private const int LaneCount = 9;
        private const int LaneFrame = 0;
        private const int LaneSimulation = 1;
        private const int LaneNative = 2;
        private const int LaneMapLoader = 3;
        private const int LaneSave = 4;
        private const int LaneInput = 5;
        private const int LaneUiPanel = 6;
        private const int LaneUiDialog = 7;
        private const int LaneLifecycle = 8;
        private static readonly string[] LaneNames =
        {
            "frame",
            "simulation",
            "native",
            "maploader",
            "save",
            "input",
            "ui_panel",
            "ui_dialog",
            "lifecycle",
        };
        private readonly int[] _laneThread = new int[LaneCount];
        private readonly int[] _laneEntries = new int[LaneCount];
        private readonly int[] _laneThreadDrift = new int[LaneCount];
        private readonly int[] _laneSecondThread = new int[LaneCount];
        private readonly int[] _laneStackAlign = new int[LaneCount];
        private int _laneReportPending;
        private long _laneNextReportTimestamp;
        /// <summary>
        /// Vermerkt, dass eine Spur betreten wurde.
        /// </summary>
        /// <remarks>
        /// Darf NICHTS tun, was werfen kann: sie laeuft auch in nativen
        /// Rueckrufen, und dort beendet schon das Ausloesen einer verwalteten
        /// Ausnahme den Prozess (siehe docs/SE_KONTEXTHAKEN_AUSRICHTUNG.md).
        /// Kein Protokoll, keine Belegung, keine Sperre - nur Zaehler.
        /// </remarks>
        private void NoteLane(int lane)
        {
            if (lane < 0 || lane >= LaneCount)
                return;
            int faden = Environment.CurrentManagedThreadId;
            DiplomacyRuntimeSafety.SaturatingIncrement(ref _laneEntries[lane]);
            int bekannt = Interlocked.CompareExchange(ref _laneThread[lane], faden, 0);
            if (bekannt == 0)
            {
                _laneStackAlign[lane] = StackAlignment();
                Interlocked.Exchange(ref _laneReportPending, 1);
                return;
            }

            if (bekannt != faden)
            {
                DiplomacyRuntimeSafety.SaturatingIncrement(ref _laneThreadDrift[lane]);
                // Count every foreign entry, announce discovery only once.
                if (Interlocked.CompareExchange(ref _laneSecondThread[lane], faden, 0) == 0)
                    Interlocked.Exchange(ref _laneReportPending, 1);
            }
        }

        /// <summary>
        /// Meldet die gemessene Fadenverteilung, sobald sie sich geaendert hat.
        /// </summary>
        /// <remarks>
        /// Laeuft auf der Bildschleife, darf also protokollieren. Gedrosselt
        /// ueber `_laneReportPending`: ohne neue Erkenntnis keine Zeile.
        /// </remarks>
        private void ReportLaneMap()
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (Volatile.Read(ref _laneReportPending) == 0 || now < _laneNextReportTimestamp)
            {
                return;
            }

            if (Interlocked.Exchange(ref _laneReportPending, 0) == 0)
                return;
            _laneNextReportTimestamp = now + System.Diagnostics.Stopwatch.Frequency * 5L;
            StringBuilder bericht = new StringBuilder(220);
            int fremde = 0;
            int wandernde = 0;
            for (int i = 0; i < LaneCount; i++)
            {
                if (_laneEntries[i] == 0)
                {
                    continue;
                }

                if (bericht.Length > 0)
                {
                    bericht.Append(',');
                }

                bericht.Append(LaneNames[i]);
                bericht.Append(':');
                bericht.Append(_laneThread[i].ToString(CultureInfo.InvariantCulture));
                bericht.Append('/');
                bericht.Append(_laneStackAlign[i].ToString(CultureInfo.InvariantCulture));
                bericht.Append('/');
                bericht.Append(_laneEntries[i].ToString(CultureInfo.InvariantCulture));
                if (_laneThreadDrift[i] > 0)
                {
                    bericht.Append("/DRIFT");
                    bericht.Append(_laneThreadDrift[i].ToString(CultureInfo.InvariantCulture));
                    wandernde++;
                }

                if (_laneThread[i] != _laneThread[LaneFrame] && _laneThread[LaneFrame] != 0)
                {
                    fremde++;
                }
            }

            Log("lane_map", "lanes=" + bericht.ToString() + "|format=thread/stack_align/entries" + "|foreign_threads=" + fremde.ToString(CultureInfo.InvariantCulture) + "|drifting=" + wandernde.ToString(CultureInfo.InvariantCulture));
        }

        private int _frameThreadId;
        private int _detourThreadId;
        private int _combatGateSkipsLogged;
        private int _combatSnapshotBlocksLogged;
        private int _combatSnapshotFaultsLogged;
        private long _combatGateSkipLogAt;
        private bool _laneSimulationLogged;
        private bool _laneDetourLogged;
        // Stapelausrichtung je Spur, -1 heisst "noch nicht gemessen".
        private uint _frameNativeThreadId;
        private uint _simulationNativeThreadId;
        private uint _detourNativeThreadId;
        private int _frameStackAlign = -1;
        private int _simulationStackAlign = -1;
        private int _detourStackAlign = -1;
        private string _detourLaneName;
        /// <summary>
        /// Wie ist der Stapel dieser Spur ausgerichtet? 0 = sauber auf 16
        /// Byte, 8 = um acht Byte verschoben.
        /// </summary>
        /// <remarks>
        /// Warum das zaehlt (): `movaps`, mit dem ntdll die
        /// XMM-Register in einen CONTEXT sichert, verlangt eine
        /// 16-Byte-Ausrichtung. Ist der Stapel verschoben, stirbt der Prozess
        /// dort - und ntdll kommt genau dann dorthin, wenn eine verwaltete
        /// Ausnahme AUSGELOEST wird. Eine Spur mit verschobenem Stapel darf
        /// also nichts enthalten, was werfen kann.
        ///
        /// Die Adresse einer lokalen Variablen haengt am Rahmen des Aufrufers.
        /// Dieselbe Funktion aus verschiedenen Spuren gerufen macht den
        /// Unterschied sichtbar. Rein lesend: kein Aufruf, keine Zuteilung,
        /// nichts, was werfen koennte.
        /// </remarks>
        private static unsafe int StackAlignment()
        {
            int marker = 0;
            return (int)((long)(&marker) & 15L);
        }

        /// <summary>
        /// Die native Fadenkennung - dieselbe Zahl, die im Absturzabzug des
        /// Extenders unter "Thread ID" steht.
        /// </summary>
        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();
        // Erst wenn das Unity-Bild den Marshalling-Rumpf einmal uebersetzt
        // hat, darf ihn eine gefaehrliche Spur rufen. Der erste Aufruf teilt
        // Speicher zu, und das ist im Kontexthaken verboten.
        private static volatile bool _nativeThreadIdWarm;
        private static uint SafeNativeThreadId()
        {
            return _nativeThreadIdWarm ? GetCurrentThreadId() : 0u;
        }

        // The native tick writer does not take _gate. Read its 64-bit clock
        // atomically on every consuming lane, including the Unity frame.
        private long ReadSimulationTick()
        {
            return Volatile.Read(ref _simulationTicks);
        }

        /// <summary>
        /// Der Simulationstakt des Extenders. Zaehlt nur mit.
        /// </summary>
        /// <remarks>
        /// ACHTUNG - hier darf NICHTS stehen, was werfen kann.
        ///
        /// Dieser Rueckruf laeuft in einem **rohen CPU-Kontexthaken**: SE
        /// loest ihn aus <c>c_game_handle_time_stuff</c> aus, einem
        /// <c>AddContextHook</c>. Das Spiel haelt dafuer mitten in einer
        /// nativen Funktion an, und der Stapel ist dabei um 8 Byte gegen die
        /// Aufrufkonvention verschoben.
        ///
        /// Gemessen am aus dem Absturzabzug des Extenders: wird in
        /// diesem Rahmen eine verwaltete Ausnahme AUSGELOEST, stirbt der
        /// Prozess sofort in <c>ntdll+0x123CA7</c> - <c>movaps</c> auf einen
        /// nicht 16-Byte-ausgerichteten <c>CONTEXT</c>, gerufen aus
        /// <c>RaiseException</c> ueber die Mono-Laufzeit. Fuenf Abstuerze an
        /// einem Vormittag, immer dieselbe Adresse.
        ///
        /// Nicht das Fangen ist das Problem, sondern das Werfen: der Prozess
        /// endet, bevor irgendein <c>catch</c> erreicht wird. Ein
        /// <c>try/catch</c> um diesen Rueckruf schuetzt also nicht - nur
        /// Abwesenheit schuetzt.
        ///
        /// Erlaubt ist deshalb ausschliesslich, was nicht werfen kann:
        /// <c>Interlocked.Increment</c> und Feldschreibvorgaenge. Kein
        /// Aufruf, keine Sperre, keine Zuteilung, keine Oberflaeche. Die
        /// gesamte Arbeit liegt im Unity-Bild (<see cref = "Tick"/>).
        /// </remarks>
        private void OnSimulationTick(int ignored)
        {
            NoteLane(LaneSimulation);
            Interlocked.Increment(ref _simulationTicks);
            _simulationThreadId = Environment.CurrentManagedThreadId;
            _simulationStackAlign = StackAlignment();
            _simulationNativeThreadId = SafeNativeThreadId();
        }

        private byte[] SaveData(SaveContext context)
        {
            // Internal one-owner projection must not wait on its own ACK.
            if (_originalOwnerProjectionStable)
                return SerializeInternalOwnerReadback();
            byte[] Frozen;
            if (TryFrozenSave(context, false, out Frozen))
                return Frozen;
            NoteLane(LaneSave);
            lock (_gate)
            {
                bool recoveryOnly = _mapDisabled && (_nativeTransferProbe != null || _pendingPackage != null || _disabledTeamRecoveryPending) && _coalitions.Count > 0 && _relations.Count == _coalitions.Count && _roster.Count > 1;
                if (context == null || !context.IsSaveFile)
                {
                    return null;
                }

                byte[] mpPayload;
                if (TryReadExistingMpSave(context, out mpPayload))
                    return mpPayload;
                if (_saveSuppressed || (!_mapActive && !recoveryOnly) || (_mapDisabled && !recoveryOnly))
                    return DiplomacyPersistenceCodec.SerializeInactive("map_inactive");
                try
                {
                    if (_stableSavePayload != null)
                    {
                        byte[] stablePayload = (byte[])_stableSavePayload.Clone();
                        Log("saving", "status=stable_snapshot|reason=" + Safe(_stableSaveReason ?? "native_operation") + "|bytes=" + stablePayload.Length.ToString(CultureInfo.InvariantCulture));
                        return stablePayload;
                    }

                    PumpHumanWarIntents(false);
                    LegacyDiplomacySave state = recoveryOnly ? SnapshotRecoverySave() : SnapshotSave();
                    byte[] payload = DiplomacyPersistenceCodec.Serialize(state, _rules);
                    Log("saving", "status=" + (recoveryOnly ? "recovery_saved" : "saved") + "|bytes=" + payload.Length.ToString(CultureInfo.InvariantCulture));
                    // original save readback passively captures the EXACT original callback
                    // return bytes, only while this owner already holds _gate.
                    ObservePrimarySaveCallback(context,payload);
                    return payload;
                }
                catch (Exception exception)
                {
                    byte[] recoveryPayload;
                    if (TrySerializeInventoryRecovery(out recoveryPayload))
                    {
                        _saveFailurePending = exception;
                        Log("saving", "status=inventory_recovery_saved|reason=" + Safe(exception.GetType().Name));
                        return recoveryPayload;
                    }

                    _saveSuppressed = true;
                    _saveFailurePending = exception;
                    Log("saving", "status=inactive_marker_written|reason=" + Safe(exception.GetType().Name));
                    return DiplomacyPersistenceCodec.SerializeInactive("serialization_failed");
                }
            }
        }

        private void LoadData(byte[] payload, LoadContext context)
        {
            NoteLane(LaneSave);
            lock (_gate)
            {
                if (context == null || !context.IsSaveFile)
                {
                    return;
                }

#if !DIPLOMACY_PRIVATE_TESTS
                if (_missionSaveAttempt.Pending == 0 || _originalSavedWorld.StagingSerial() == 0)
                    return;
#endif
                _loadedSavePath = context.FilePath;
#if DIPLOMACY_PRIVATE_TESTS
                NoteOriginalAidLoad(context.FilePath, payload);
#endif
                ObserveStagedOriginalLoad(payload);
                _pendingSavePayload = payload == null ? null : (byte[])payload.Clone();
                _pendingSaveEpoch = payload == null ? -1 : _mapEpoch;
                _pendingSaveStagedFeatureTick = payload == null ? -1 : _featureTicks;
                _pendingSaveApplySerial = payload == null ? 0 : _originalSavedWorld.StagingSerial();
                Log("saving", "status=load_staged|bytes=" + (payload == null ? "0" : payload.Length.ToString(CultureInfo.InvariantCulture)));
            }
        }

        private void CaptureRosterAndCoalitions(bool verifiedMpTradeOnly = false)
        {
            GamePlayerManagerAPI players = GamePlayerManagerAPI.Instance;
            _localPlayer = players.GetLocalPlayerId();
            if (_localPlayer < 1 || _localPlayer > 8 || _localPlayer != GameData.Instance.playerID)
            {
                throw new InvalidOperationException("local player identity is not exact");
            }

            int[] alive = players.GetAlivePlayerIds();
            if (!alive.Contains(_localPlayer) || GameData.Instance.lastGameState.is_skirmish_player(_localPlayer))
            {
                throw new InvalidOperationException("offline skirmish does not have exactly one local human");
            }

            _roster.Add(_localPlayer);
            int localTeam = players.GetPlayerTeam(_localPlayer);
            _originalTeamPartition.Add(_localPlayer, localTeam);
            var grouped = new SortedDictionary<string, List<int>>(StringComparer.Ordinal);
            foreach (int player in players.GetAllPlayerIds().OrderBy(value => value))
            {
                if (player == _localPlayer)
                {
                    continue;
                }

                if (!GameData.Instance.lastGameState.is_skirmish_player(player))
                {
                    if (GameData.Instance.lastGameState.is_valid_player(player))
                    {
                        if (!verifiedMpTradeOnly || _latestOriginalFrame == null || !_latestOriginalFrame.TryAuthoritativeHumanActor((byte)player, _boundSteamParticipants[player]))
                            throw new InvalidOperationException("human-human diplomacy is not supported");
                        _roster.Add(player);
                        int nativeTeam = players.GetPlayerTeam(player);
                        _originalTeamPartition.Add(player, nativeTeam);
                        string nativeKey = nativeTeam > 0 ? "team:" + nativeTeam : "player:" + player;
                        List<int> humanGroup;
                        if (!grouped.TryGetValue(nativeKey, out humanGroup))
                        {
                            humanGroup = new List<int>();
                            grouped.Add(nativeKey, humanGroup);
                        }

                        humanGroup.Add(player);
                    }

                    continue;
                }

                _roster.Add(player);
                int lord = GameData.Instance.lastGameState.computer_register[player];
                if (lord < 0 || lord >= ScriptExtenderNativeBridge.AicCount())
                {
                    throw new InvalidOperationException("AI lord identity is unavailable for player " + player.ToString(CultureInfo.InvariantCulture));
                }

                _capturedLordIds.Add(player, lord);
                int team = players.GetPlayerTeam(player);
                _originalTeamPartition.Add(player, team);
                string key = team > 0 ? "team:" + team.ToString(CultureInfo.InvariantCulture) : "player:" + player.ToString(CultureInfo.InvariantCulture);
                List<int> members;
                if (!grouped.TryGetValue(key, out members))
                {
                    members = new List<int>();
                    grouped.Add(key, members);
                }

                members.Add(player);
            }

            foreach (KeyValuePair<string, List<int>> entry in grouped)
            {
                int[] members = entry.Value.OrderBy(value => value).ToArray();
                var coalition = new CoalitionState
                {
                    CoalitionId = entry.Key,
                    MemberPlayerIds = members,
                    OriginalTeams = members.Select(players.GetPlayerTeam).ToArray(),
                    StartingAlly = localTeam > 0 && players.GetPlayerTeam(members[0]) == localTeam,
                };
                _coalitions.Add(entry.Key, coalition);
                foreach (int player in members)
                {
                    _playerCoalition.Add(player, entry.Key);
                }
            }
        }

        private void FreezePrices()
        {
            GamePlayerManagerAPI players = GamePlayerManagerAPI.Instance;
            foreach (int goodId in SendableGoods)
            {
                int value = goodId == 15 ? 1 : DiplomacyBalance.UnitValueFromBaseSellPrice(players.GetDefaultTradeBasePrice((eGoods)goodId).SellPrice);
                if (value <= 0)
                {
                    throw new InvalidOperationException("default sell price is not positive for good " + goodId);
                }

                _frozenPrices.Add(goodId, value);
            }
        }

        private void InitializeRelations()
        {
            int day = AbsoluteGameDay();
            foreach (CoalitionState coalition in _coalitions.Values.OrderBy(value => value.CoalitionId, StringComparer.Ordinal))
            {
                RefreshCoalition(coalition);
            }

            foreach (CoalitionState coalition in _coalitions.Values)
            {
                var relation = new RelationState
                {
                    CoalitionId = coalition.CoalitionId,
                    LastProcessedDay = day,
                };
                InitializeRelation(relation, coalition, day);
                if (coalition.StartingAlly)
                {
                    relation.Goodwill = coalition.Target;
                    DiplomacyPolicy.BeginAlliance(relation, day, true, _rules);
                    coalition.Allied = true;
                }

                _relations.Add(relation.CoalitionId, relation);
            }

            // Strategic context must see the complete, freshly measured
            // coalition graph. Updating it while relations are still being
            // inserted makes results depend on dictionary iteration order.
            foreach (CoalitionState coalition in _coalitions.Values.OrderBy(value => value.CoalitionId, StringComparer.Ordinal))
            {
                RefreshStrategicContext(coalition, day);
            }
        }

        private void RefreshCoalition(CoalitionState coalition)
        {
            GamePlayerManagerAPI players = GamePlayerManagerAPI.Instance;
            GameBuildingManagerAPI buildings = GameBuildingManagerAPI.Instance;
            GameUnitManagerAPI units = GameUnitManagerAPI.Instance;
            HashSet<int> liveMembers = new HashSet<int>(players.GetAlivePlayerIds().Where(value => coalition.MemberPlayerIds.Contains(value)));
            long economy = 0L;
            foreach (int player in liveMembers)
            {
                economy = SaturatingAdd(economy, Math.Max(0, players.GetPlayerGold(player)));
                foreach (int goodId in SendableGoods)
                {
                    if (goodId == 15)
                    {
                        continue;
                    }

                    int amount = Math.Max(0, players.GetGoodAmount(player, (eGoods)goodId));
                    economy = SaturatingAdd(economy, SaturatingMultiply(amount, _frozenPrices[goodId]));
                }
            }

            economy = SaturatingAdd(economy, ProductionEconomy(liveMembers));
            long military = 0L;
            foreach (int unitId in units.GetAllAliveUnits())
            {
                if (!liveMembers.Contains(units.GetOwner(unitId)))
                {
                    continue;
                }

                eChimps type = units.GetType(unitId);
                if (type == eChimps.CHIMP_TYPE_LORD)
                {
                    military = SaturatingAdd(military, _rules.LordMilitaryValue);
                    continue;
                }

                if (!IsCombatUnit(type))
                {
                    continue;
                }

                long cost = Math.Max(0, units.GetUnitGoldCost(type));
                UnitGoodCosts goods = units.GetUnitGoodCosts(type);
                cost = SaturatingAdd(cost, EquipmentValue(goods.cost1));
                cost = SaturatingAdd(cost, EquipmentValue(goods.cost2));
                cost = SaturatingAdd(cost, EquipmentValue(goods.cost3));
                cost = SaturatingAdd(cost, EquipmentValue(goods.cost4));
                if (cost <= 0)
                {
                    cost = DiplomacyBalance.UnknownUnitValue((int)units.GetDefaultHealth(type), _rules);
                }

                int maximumHealth = Math.Max(1, units.GetMaxHealth(unitId));
                int currentHealth = Math.Max(0, Math.Min(maximumHealth, units.GetCurrentHealth(unitId)));
                long weighted = decimal.ToInt64(decimal.Ceiling((decimal)cost * currentHealth / maximumHealth));
                military = SaturatingAdd(military, weighted);
            }

            coalition.Economy = economy;
            coalition.Military = military;
            coalition.SendGoodsMargin = CoalitionMargin(coalition);
            coalition.Target = DiplomacyBalance.CalculateTarget(economy, military, coalition.SendGoodsMargin, _rules);
            string logSignature = coalition.SendGoodsMargin.ToString(CultureInfo.InvariantCulture) + ":" + coalition.Target.ToString(CultureInfo.InvariantCulture);
            string previousSignature;
            if (!_coalitionValueLogSignatures.TryGetValue(coalition.CoalitionId, out previousSignature) || !string.Equals(previousSignature, logSignature, StringComparison.Ordinal))
            {
                _coalitionValueLogSignatures[coalition.CoalitionId] = logSignature;
                Log("value", "coalition=" + coalition.CoalitionId + "|economy=" + economy.ToString(CultureInfo.InvariantCulture) + "|military=" + military.ToString(CultureInfo.InvariantCulture) + "|margin=" + coalition.SendGoodsMargin.ToString(CultureInfo.InvariantCulture) + "|target=" + coalition.Target.ToString(CultureInfo.InvariantCulture));
            }
        }

        private int CoalitionMargin(CoalitionState coalition)
        {
            int margin = 0;
            foreach (int player in coalition.MemberPlayerIds)
            {
                if (!IsAlivePlayer(player))
                {
                    continue;
                }

                int lord;
                if (!_capturedLordIds.TryGetValue(player, out lord))
                {
                    // Humans have no AIC margin. Zero is inert legacy compatibility
                    // policy here, never claimed as observed AI personality.
                    if (_mpConsentMode && _latestOriginalFrame != null && _latestOriginalFrame.Players().Any(x => x.Slot == player && x.Human))
                        continue;
                    throw new InvalidOperationException("Missing original AI personality");
                }

                InternalAIC aic = ScriptExtenderNativeBridge.Aic(lord);
                margin = Math.Max(margin, DiplomacyBalance.ClampMargin(aic.send_goods_margin));
            }

            return margin;
        }

        private List<GoodNeed> CoalitionNeeds(CoalitionState coalition)
        {
            var totals = new Dictionary<int, decimal>();
            foreach (int player in coalition.MemberPlayerIds)
            {
                if (!IsAlivePlayer(player))
                {
                    continue;
                }

                int lord = _capturedLordIds[player];
                InternalAIC aic = ScriptExtenderNativeBridge.Aic(lord);
                // Nur fordern, was dieser Empfaenger nachweislich annehmen
                // kann (). Sonst kappt das Spiel die Zubuchung auf
                // null und das ganze Gueterpaket faellt zurueck.
                HashSet<int> annehmbar = ReceivableGoods(player, aic);
                AddNeed(totals, player, (int)eGoods.STORED_FOOD_FRUIT, aic.buy_apples_at, annehmbar);
                AddNeed(totals, player, (int)eGoods.STORED_FOOD_CHEESE, aic.buy_cheese_at, annehmbar);
                AddNeed(totals, player, (int)eGoods.STORED_FOOD_BREAD, aic.buy_bread_at, annehmbar);
                AddNeed(totals, player, (int)eGoods.STORED_RAW_WHEAT, aic.buy_wheat_at, annehmbar);
                AddNeed(totals, player, (int)eGoods.STORED_RAW_HOPS, aic.buy_hops_at, annehmbar);
                AddNeed(totals, player, (int)eGoods.STORED_WOOD_PLANKS, aic.trade_wood_at, annehmbar);
                AddNeed(totals, player, (int)eGoods.STORED_STONE_BLOCKS, aic.trade_stone_at, annehmbar);
                AddNeed(totals, player, (int)eGoods.STORED_IRON_INGOTS, aic.trade_resources_at, annehmbar);
                AddNeed(totals, player, (int)eGoods.STORED_FLOUR, aic.trade_flour_at, annehmbar);
                AddNeed(totals, player, (int)eGoods.STORED_FOOD_ALE, aic.trade_ale_at, annehmbar);
                AddNeed(totals, player, (int)eGoods.STORED_PITCH_RAW, aic.trade_pitch_at, annehmbar);
                AddNeed(totals, player, (int)eGoods.STORED_GOLD, aic.base_gold_reserves, annehmbar);
                int weaponReserve = Math.Max(aic.trade_weapons_at, aic.buy_weapons);
                foreach (int weapon in new[]
                {
                    17,
                    18,
                    19,
                    20,
                    21,
                    22,
                    23,
                    24
                }

                )
                {
                    AddNeed(totals, player, weapon, weaponReserve, annehmbar);
                }

                AddProductionAndRecruitmentNeeds(totals, player, aic, annehmbar);
            }

            if (totals.Count == 0)
            {
                totals[15] = 1m;
            }

            return totals.Select(entry => new GoodNeed { GoodId = entry.Key, RelativeNeed = entry.Value, UnitValue = _frozenPrices[entry.Key], }).ToList();
        }

        /// <summary>
        /// Welche Gueter kann dieser Spieler nachweislich annehmen?
        /// </summary>
        /// <remarks>
        /// Warum (): eine Forderung nach einem Gut, fuer das der
        /// Empfaenger keinen Lagerplatz hat, wird vom Spiel auf null gekappt
        /// (`recipient_capacity_clamped`) und reisst das ganze Gueterpaket in
        /// die Ruecknahme. Fordert die KI so etwas als Buendnisbedingung, ist
        /// das Buendnis unerreichbar.
        ///
        /// Gezaehlt wird nur, was belegt ist:
        /// Gold immer; jedes Gut, von dem er mehr als 0 haelt (dann gibt es
        /// den Lagerplatz); jedes Gut, das eines seiner wachen Gebaeude
        /// gerade erzeugt; und jedes Gut, fuer das seine Fuerstendatei eine
        /// Kaufschwelle >= 0 nennt. Minus eins heisst dort ausdruecklich
        /// "kauft dieser Fuerst nie".
        ///
        /// Faellt die Bestimmung aus, wird `null` geliefert - dann gilt die
        /// alte, ungefilterte Auswahl. Eine Forderung, die nichts verlangt,
        /// waere schlimmer als eine unpassende: es kaeme nie ein Buendnis
        /// zustande.
        /// </remarks>
        private HashSet<int> ReceivableGoods(int player, InternalAIC aic)
        {
            try
            {
                var annehmbar = new HashSet<int>();
                annehmbar.Add(15); // Gold
                GamePlayerManagerAPI players = GamePlayerManagerAPI.Instance;
                for (int index = 0; index < SendableGoods.Length; index++)
                {
                    int goodId = SendableGoods[index];
                    if (goodId == 15)
                    {
                        continue;
                    }

                    if (players.GetGoodAmount(player, (eGoods)goodId) > 0)
                    {
                        annehmbar.Add(goodId);
                    }
                }

                GameBuildingManagerAPI buildings = GameBuildingManagerAPI.Instance;
                foreach (int buildingId in buildings.GetAllAliveBuildings())
                {
                    if (buildings.GetOwner(buildingId) != player || buildings.IsSleeping(buildingId))
                    {
                        continue;
                    }

                    int erzeugt = (int)buildings.GetCurrentlyProducedGood(buildingId);
                    if (IsSendableGood(erzeugt))
                    {
                        annehmbar.Add(erzeugt);
                    }
                }

                AddBuyThresholdGood(annehmbar, (int)eGoods.STORED_FOOD_FRUIT, aic.buy_apples_at);
                AddBuyThresholdGood(annehmbar, (int)eGoods.STORED_FOOD_CHEESE, aic.buy_cheese_at);
                AddBuyThresholdGood(annehmbar, (int)eGoods.STORED_FOOD_BREAD, aic.buy_bread_at);
                AddBuyThresholdGood(annehmbar, (int)eGoods.STORED_RAW_WHEAT, aic.buy_wheat_at);
                AddBuyThresholdGood(annehmbar, (int)eGoods.STORED_RAW_HOPS, aic.buy_hops_at);
                return annehmbar.Count > 1 ? annehmbar : null;
            }
            catch (Exception exception)
            {
                // Die Bestimmung darf nie die Diplomatie anhalten - ohne sie
                // gilt einfach die alte Auswahl.
                Log("needs", "status=receivable_probe_failed|player=" + player.ToString(CultureInfo.InvariantCulture) + "|reason=" + Safe(exception.GetType().Name));
                return null;
            }
        }

        /// <summary>
        /// Eine Kaufschwelle unter 0 heisst "kauft dieser Fuerst nie".
        /// </summary>
        private static void AddBuyThresholdGood(HashSet<int> annehmbar, int goodId, int schwelle)
        {
            if (schwelle >= 0 && IsSendableGood(goodId))
            {
                annehmbar.Add(goodId);
            }
        }

        private void AddProductionAndRecruitmentNeeds(Dictionary<int, decimal> totals, int player, InternalAIC aic, HashSet<int> annehmbar)
        {
            AddFlatNeed(totals, NormalizeProducedGood(aic.blacksmiths_make), annehmbar);
            AddFlatNeed(totals, NormalizeProducedGood(aic.fletchers_make), annehmbar);
            AddFlatNeed(totals, NormalizeProducedGood(aic.poleturners_make), annehmbar);
            foreach (FieldInfo field in typeof(InternalAIC).GetFields(BindingFlags.Public | BindingFlags.Instance).Where(field => field.FieldType == typeof(int) && (field.Name.StartsWith("defensive_troops", StringComparison.Ordinal) || field.Name.StartsWith("harrasment_troops", StringComparison.Ordinal) || field.Name.StartsWith("siege_wall_troops", StringComparison.Ordinal) || field.Name.EndsWith("_troop", StringComparison.Ordinal))).OrderBy(field => field.Name, StringComparer.Ordinal))
            {
                AddRecruitmentNeeds(totals, (int)field.GetValue(aic), annehmbar);
            }

            GameBuildingManagerAPI buildings = GameBuildingManagerAPI.Instance;
            foreach (int buildingId in buildings.GetAllAliveBuildings())
            {
                if (buildings.GetOwner(buildingId) != player || buildings.IsSleeping(buildingId))
                {
                    continue;
                }

                AddFlatNeed(totals, (int)buildings.GetCurrentlyProducedGood(buildingId), annehmbar);
            }
        }

        private static void AddRecruitmentNeeds(Dictionary<int, decimal> totals, int troopType, HashSet<int> annehmbar)
        {
            if (troopType <= 0 || troopType > ushort.MaxValue)
            {
                return;
            }

            // eChimps is backed by UInt16. Passing the AIC's boxed Int32 to
            // Enum.IsDefined throws even for a numerically valid unit ID.
            eChimps troop = (eChimps)(ushort)troopType;
            if (!Enum.IsDefined(typeof(eChimps), troop) || !IsCombatUnit(troop))
            {
                return;
            }

            UnitGoodCosts costs = GameUnitManagerAPI.Instance.GetUnitGoodCosts(troop);
            AddFlatNeed(totals, (int)costs.cost1, annehmbar);
            AddFlatNeed(totals, (int)costs.cost2, annehmbar);
            AddFlatNeed(totals, (int)costs.cost3, annehmbar);
            AddFlatNeed(totals, (int)costs.cost4, annehmbar);
        }

        private void AddNeed(Dictionary<int, decimal> totals, int player, int goodId, int desired, HashSet<int> annehmbar)
        {
            if (!IsSendableGood(goodId) || desired <= 0)
            {
                return;
            }

            if (annehmbar != null && !annehmbar.Contains(goodId))
            {
                return;
            }

            int current = goodId == 15 ? Math.Max(0, GamePlayerManagerAPI.Instance.GetPlayerGold(player)) : Math.Max(0, GamePlayerManagerAPI.Instance.GetGoodAmount(player, (eGoods)goodId));
            int missing = Math.Max(0, desired - current);
            if (missing <= 0)
            {
                return;
            }

            decimal relative = (decimal)missing / Math.Max(1, desired);
            decimal existing;
            totals.TryGetValue(goodId, out existing);
            totals[goodId] = existing + relative;
        }

        private static void AddFlatNeed(Dictionary<int, decimal> totals, int goodId, HashSet<int> annehmbar)
        {
            if (!IsSendableGood(goodId))
            {
                return;
            }

            // Ein Gut, das der Empfaenger nicht lagern kann, ist kein Bedarf -
            // es ist eine unerfuellbare Bedingung ().
            if (annehmbar != null && !annehmbar.Contains(goodId))
            {
                return;
            }

            decimal existing;
            totals.TryGetValue(goodId, out existing);
            totals[goodId] = existing + 0.25m;
        }

        private void ProcessDay(int day)
        {
            if (day <= _lastProcessedDay)
            {
                return;
            }

            // A coalition defeat can make a pending alliance offer invalid
            // by leaving only one hostile coalition. Reconcile that invariant
            // at the deterministic day boundary. ExtendedState treaty, restitution and
            // terms demands remain actionable; only alliance formation is
            // prohibited until another hostile coalition exists.
            CancelBlockedEnemyDemands();
            CoalitionState[] orderedCoalitions = _coalitions.Values.OrderBy(value => value.CoalitionId, StringComparer.Ordinal).ToArray();
            // Two phases: first measure every living coalition, then derive
            // every strategic relationship from that same snapshot.
            foreach (CoalitionState coalition in orderedCoalitions.Where(value => value.MemberPlayerIds.Any(IsAlivePlayer)))
            {
                RefreshCoalition(coalition);
            }

            foreach (CoalitionState coalition in orderedCoalitions.Where(value => value.MemberPlayerIds.Any(IsAlivePlayer)))
            {
                RefreshStrategicContext(coalition, day);
            }

            // payment completion serializes a recovery journal.
            // First finish every model clock; native effects must never see
            // half the relations at N and half at N+1. This is sequencing, not
            // a serializer exception or a rewritten snapshot date.
            DiplomacyRuntimeSafety.RunDayPhases(orderedCoalitions, delegate (CoalitionState coalition)
            {
                RelationState relation = _relations[coalition.CoalitionId];
                if (!coalition.MemberPlayerIds.Any(IsAlivePlayer))
                {
                    DiplomacyAgreementRules.ProcessInactiveDay(relation, day, coalition.Target);
                    if (_inactiveCoalitionsLogged.Add(coalition.CoalitionId))
                        Log("coalition", "coalition=" + coalition.CoalitionId + "|status=inactive|reason=no_living_members");
                    return false;
                }

                _inactiveCoalitionsLogged.Remove(coalition.CoalitionId);
                return DiplomacyPolicy.ProcessDay(relation, day, coalition.Target, coalition.SendGoodsMargin, CoalitionNeeds(coalition), _rules);
            }, delegate
            {
                _lastProcessedDay = day;
            }, delegate (CoalitionState coalition, bool expired)
            {
                // A preceding effect may have restored a managed memento;
                // reacquire its objects instead of modifying stale copies.
                coalition = _coalitions[coalition.CoalitionId];
                if (!coalition.MemberPlayerIds.Any(IsAlivePlayer))
                    return;
                RelationState relation = _relations[coalition.CoalitionId];
                HandleDayOutcome(coalition, relation, day, expired);
                if (_pendingTeamTransition == null)
                {
                    ProcessTribute(coalition, relation, day);
                    coalition = _coalitions[coalition.CoalitionId];
                    relation = _relations[coalition.CoalitionId];
                    ProcessVassalage(coalition, relation, day);
                // Submission samples the engine and popup ownership in
                // the frame pump, retrying a busy monitor in this day.
                }
            });
            PublishCombatProtectionSnapshot();
        }

        /// <summary>Ein haengendes Gueterpaket erneut zuruecknehmen.</summary>
        /// <remarks>
        /// Scheiterte AbortPendingPackage, blieb `_pendingPackage`
        /// stehen und die Sperre galt bis zum Kartenwechsel - anders als bei
        /// `_nativeTransferProbe` gab es hier gar keinen zweiten Versuch. Ein
        /// belegtes Inventar ist aber ein Augenblickszustand, kein Urteil.
        /// Dieselbe Taktung und dieselbe Obergrenze wie dort.
        /// </remarks>
        private void RetryPendingPackageRollback()
        {
            DiplomacyPendingPackage package = _pendingPackage;
            if (package == null || !package.RollbackPending)
            {
                _packageRollbackAttempts = 0;
                return;
            }

            if (_featureTicks < _packageRollbackRetryTick)
                return;
            _packageRollbackRetryTick = _featureTicks + 60;
            string failure = AbortPendingPackage();
            if (failure == null)
            {
                _packageRollbackAttempts = 0;
                Log("demand_package", "status=rolled_back|reason=retry");
                return;
            }

            if (failure == "native_inventory_busy")
                return;
            _packageRollbackAttempts = checked(_packageRollbackAttempts + 1);
            if (_packageRollbackAttempts < GoodsTransferRollbackGiveUp)
                return;
            Log("demand_package", "status=rollback_abandoned|reason=" + Safe(failure) + "|attempts=" + _packageRollbackAttempts.ToString(CultureInfo.InvariantCulture));
            _pendingPackage = null;
            _packageRollbackAttempts = 0;
            if (_nativeTransferProbe == null)
            {
                _goodsTransfersUnavailable = false;
                Log("transfer", "status=transfers_resumed|reason=package_abandoned");
            }
        }

        private bool HasUnsettledTransfer()
        {
            return _inventoryRestorePending || _nativeTransferProbe != null || _pendingPackage != null;
        }

        private bool RejectUnavailableGoodsTransfer()
        {
            // , aus einem Spiellauf: die Sperre galt fuer die ganze
            // Partie. Eine einzige nicht abgleichbare Sendung machte "Gueter
            // senden" bis zum Kartenwechsel unbenutzbar - der Benutzer hat
            // danach zehnmal vergeblich zu senden versucht. Ein Buchungsfehler
            // ist ein Zustand, kein Urteil: sobald nichts mehr schwebt, ist der
            // Grund weg und die Sendungen sind wieder frei.
            if (_goodsTransfersUnavailable && !HasUnsettledTransfer())
            {
                _goodsTransfersUnavailable = false;
                _transferRollbackAttempts = 0;
                Log("transfer", "status=transfers_resumed|reason=journal_settled");
            }

            if (!_goodsTransfersUnavailable)
            {
                return false;
            }

            ShowMessage(Text("Gütersendungen sind für diese Partie gesperrt; die übrige Diplomatie läuft weiter.", "Goods transfers are locked for this match; the rest of diplomacy remains active."));
            Log("transfer", "status=rejected|reason=transfers_disabled");
            return true;
        }

        private void DisableGoodsTransfers(string reason)
        {
            if (_nativeTransferProbe != null)
                _nativeTransferProbe.RollbackPending = true;
            if (_pendingPackage != null)
                _pendingPackage.RollbackPending = true;
            if (_goodsTransfersUnavailable)
            {
                return;
            }

            _goodsTransfersUnavailable = true;
            Log("transfer", "status=transfers_disabled|reason=" + Safe(reason));
            ShowMessage(Text("Die Gütersendung konnte nicht sicher abgeglichen werden. Weitere Sendungen sind für diese Partie gesperrt; die übrige Diplomatie läuft weiter.", "The goods transfer could not be reconciled safely. Further transfers are locked for this match; the rest of diplomacy remains active."));
        }

        private bool StartAlliedNativeTransferProbe(int targetPlayer, int goodId, int amount)
        {
            if (RejectUnavailableGoodsTransfer())
            {
                return false;
            }

            if (_nativeTransferProbe != null || _pendingPackage != null || _pendingTeamTransition != null)
            {
                ShowMessage(_nativeTransferProbe != null ? Text("Eine Gütersendung wird noch geprüft.", "A goods transfer is still being verified.") : Text("Eine Beziehungsänderung wird noch abgeschlossen.", "A relationship change is still being completed."));
                Log("transfer", "status=rejected|reason=" + (_nativeTransferProbe != null ? "probe_busy" : "team_transition_busy"));
                return false;
            }

            if (!IsCoalitionAllied(targetPlayer) || !_playerCoalition.ContainsKey(targetPlayer) || !IsSendableGood(goodId) || amount <= 0 || amount > 1000000 || !IsAlivePlayer(targetPlayer) || targetPlayer == _localPlayer)
            {
                ShowMessage(Text("Ungültige Gütersendung.", "Invalid goods transfer."));
                Log("transfer", "status=rejected|reason=invalid_allied_input");
                return false;
            }

            bool amountInvalid;
            if (!TryPrepareNativeTransferProbe(targetPlayer, goodId, amount, true, out amountInvalid))
            {
                if (amountInvalid)
                {
                    ShowMessage(Text("Nicht genügend Güter.", "Not enough goods."));
                    Log("transfer", "status=rejected|reason=allied_amount_bounds");
                }

                return false;
            }

            Log("transfer", "status=native_allied_started|target=" + targetPlayer.ToString(CultureInfo.InvariantCulture) + "|good=" + goodId.ToString(CultureInfo.InvariantCulture) + "|amount=" + amount.ToString(CultureInfo.InvariantCulture));
            return true;
        }

        private void StartTransfer(int targetPlayer, int goodId, int amount)
        {
            if (RejectUnavailableGoodsTransfer())
            {
                return;
            }

            if (_nativeTransferProbe != null || _pendingPackage != null || _pendingTeamTransition != null)
            {
                ShowMessage(_nativeTransferProbe != null ? Text("Eine Gütersendung wird noch geprüft.", "A goods transfer is still being verified.") : Text("Eine Beziehungsänderung wird noch abgeschlossen.", "A relationship change is still being completed."));
                Log("transfer", "status=rejected|reason=" + (_nativeTransferProbe != null ? "probe_busy" : "team_transition_busy"));
                return;
            }

            if (!IsSendableGood(goodId) || amount <= 0 || amount > 1000000 || !IsAlivePlayer(targetPlayer) || targetPlayer == _localPlayer || !_playerCoalition.ContainsKey(targetPlayer) || IsCoalitionAllied(targetPlayer))
            {
                ShowMessage(Text("Ungültige Gütersendung.", "Invalid goods transfer."));
                Log("transfer", "status=rejected|reason=invalid_input|target=" + targetPlayer.ToString(CultureInfo.InvariantCulture) + "|good=" + goodId.ToString(CultureInfo.InvariantCulture) + "|amount=" + amount.ToString(CultureInfo.InvariantCulture) + "|alive=" + IsAlivePlayer(targetPlayer).ToString());
                return;
            }

            if (GeneratedDiplomacyContract.IsolatedCanary && _nativeEnemyTransferSupported != false && !IsCoalitionAllied(targetPlayer))
            {
                bool firstCanary = _nativeEnemyTransferSupported == null;
                bool amountInvalid;
                if (!TryPrepareNativeTransferProbe(targetPlayer, goodId, amount, false, out amountInvalid))
                {
                    if (amountInvalid)
                    {
                        ShowMessage(Text("Nicht genügend Güter.", "Not enough goods."));
                        Log("transfer", "status=rejected|reason=enemy_canary_amount_bounds");
                    }

                    return;
                }

                EngineInterface.GameAction(Enums.GameActionCommand.Ally_SendGoods, targetPlayer, goodId, amount);
                Log("transfer", "status=" + (firstCanary ? "native_canary_started" : "native_enemy_started") + "|target=" + targetPlayer.ToString(CultureInfo.InvariantCulture) + "|good=" + goodId.ToString(CultureInfo.InvariantCulture) + "|amount=" + amount.ToString(CultureInfo.InvariantCulture));
                return;
            }

            string failure;
            if (!TryAtomicTransfer(targetPlayer, goodId, amount, out failure))
            {
                if (failure == "native_inventory_busy")
                    return;
                if (failure != null && failure.IndexOf("rollback_failed", StringComparison.Ordinal) >= 0)
                {
                    DisableGoodsTransfers("atomic_rollback:" + failure);
                    return;
                }

                ShowMessage(failure != null && failure.StartsWith("recipient_capacity_clamped", StringComparison.Ordinal) ? Text("Der Empfänger kann diese Menge nicht lagern. Die Sendung wurde vollständig zurückgenommen.", "The recipient cannot store that amount. The transfer was rolled back in full.") : Text("Die Gütersendung ist fehlgeschlagen.", "The goods transfer failed."));
                Log("transfer", "status=failed_closed|reason=" + Safe(failure) + "|target=" + targetPlayer.ToString(CultureInfo.InvariantCulture) + "|good=" + goodId.ToString(CultureInfo.InvariantCulture) + "|amount=" + amount.ToString(CultureInfo.InvariantCulture));
                return;
            }

            NativeTransferProbe atomicProbe = _nativeTransferProbe;
            if (atomicProbe == null)
            {
                throw new InvalidOperationException("atomic transfer completed without a recovery journal");
            }

            atomicProbe.CompletionObserved = true;
            atomicProbe.CompletionPostInventoryExact = true;
            atomicProbe.CompletionSenderAmount = atomicProbe.SenderBefore - atomicProbe.Amount;
            atomicProbe.CompletionRecipientAmount = atomicProbe.RecipientBefore + atomicProbe.Amount;
            Log("transfer", "status=atomic_staged|target=" + targetPlayer.ToString(CultureInfo.InvariantCulture) + "|good=" + goodId.ToString(CultureInfo.InvariantCulture) + "|amount=" + amount.ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>
        /// Der Absender einer laufenden Uebertragung.
        /// </summary>
        /// <remarks>
        /// Seit dem nicht mehr zwangslaeufig der oertliche Spieler:
        /// ein Tribut kann zwischen zwei Bundesgenossen flieszen. Sonden ohne
        /// gesetzten Absender (0) stammen aus einem Aufruf von vorher; fuer
        /// die gilt weiter der oertliche Spieler.
        /// </remarks>
        private bool TryPrepareNativeTransferProbe(int targetPlayer, int goodId, int amount, bool allied, out bool amountInvalid)
        {
            amountInvalid = false;
            IDisposable lease;
            if (!TryEnterNativeInventorySection(out lease))
                return false;
            using (lease)
            {
                int senderBefore, recipientBefore;
                if (!TryReadTransferAmount(_localPlayer, goodId, out senderBefore) || !TryReadTransferAmount(targetPlayer, goodId, out recipientBefore) || senderBefore < amount || recipientBefore > int.MaxValue - amount)
                {
                    amountInvalid = true;
                    return false;
                }

                _inventoryRecoveryBaseState = CaptureInventoryRecoveryBaseState();
                _nativeTransferProbe = new NativeTransferProbe
                {
                    SenderPlayer = _localPlayer,
                    TargetPlayer = targetPlayer,
                    GoodId = goodId,
                    Amount = amount,
                    SenderBefore = senderBefore,
                    RecipientBefore = recipientBefore,
                    StartedTick = ReadSimulationTick(),
                    AlliedNativeTransfer = allied,
                    GameDay = _lastProcessedDay,
                };
                return true;
            }
        }

        private int ProbeSender(NativeTransferProbe probe)
        {
            return probe != null && probe.SenderPlayer > 0 ? probe.SenderPlayer : _localPlayer;
        }

        private void VerifyNativeTransferProbe()
        {
            NativeTransferProbe probe = _nativeTransferProbe;
            if (probe == null)
            {
                return;
            }

            if (probe.RollbackPending)
            {
                if (_featureTicks >= _inventoryRecoveryRetryTick)
                {
                    _inventoryRecoveryRetryTick = _featureTicks + 60;
                    string pendingFailure = AbortNativeTransferProbe();
                    if (pendingFailure == null)
                        _transferRollbackAttempts = 0;
                    else if (pendingFailure != "native_inventory_busy")
                    {
                        DisableGoodsTransfers(pendingFailure);
                        _transferRollbackAttempts = checked(_transferRollbackAttempts + 1);
                        // Die Ruecknahme prueft dieselben Bestaende, die sich
                        // laufend aendern - sie kann dauerhaft scheitern. Dann
                        // wird der Vorgang aufgegeben statt die Diplomatie bis
                        // zum Kartenwechsel lahmzulegen. Was gebucht wurde,
                        // bleibt gebucht; das steht so im Protokoll.
                        if (_transferRollbackAttempts >= GoodsTransferRollbackGiveUp)
                        {
                            Log("transfer", "status=rollback_abandoned|reason=" + Safe(pendingFailure) + "|attempts=" + _transferRollbackAttempts.ToString(CultureInfo.InvariantCulture));
                            _nativeTransferProbe = null;
                            // _inventoryRecoveryBaseState bleibt unangetastet:
                            // es wird aus mehreren Spuren beschrieben und hier
                            // ohne Sperre erreicht. Der naechste Transferstart
                            // setzt es ohnehin neu.
                            _transferRollbackAttempts = 0;
                            if (_pendingPackage == null)
                            {
                                _goodsTransfersUnavailable = false;
                                Log("transfer", "status=transfers_resumed|reason=rollback_abandoned");
                            }
                        }
                    }
                }

                return;
            }

            if (probe.CompletionObserved && probe.CompletionPostInventoryExact)
            {
                if (probe.DirectAtomicTransfer)
                {
                    CompleteVerifiedNativeTransfer(probe, "atomic");
                    return;
                }

                if (probe.AlliedNativeTransfer)
                {
                    Log("transfer", "status=native_allied_confirmed" + "|proof=post_inventory" + "|return_hint=" + NullableBoolText(probe.CompletionReturnHint));
                    CompleteVerifiedNativeTransfer(probe, "native_allied");
                    return;
                }

                _nativeEnemyTransferSupported = true;
                Log("transfer", "status=native_canary_passed" + "|proof=post_inventory" + "|return_hint=" + NullableBoolText(probe.CompletionReturnHint));
                CompleteVerifiedNativeTransfer(probe, "native");
                return;
            }

            if (!probe.CompletionObserved && ReadSimulationTick() - probe.StartedTick < NativeTransferVerificationTicks)
            {
                return;
            }

            int sender, recipient;
            IDisposable readLease;
            if (!TryEnterNativeInventorySection(out readLease))
                return;
            using (readLease)
            {
                sender = GoodAmount(ProbeSender(probe), probe.GoodId);
                recipient = GoodAmount(probe.TargetPlayer, probe.GoodId);
            }

            string rollbackFailure;
            rollbackFailure = AbortNativeTransferProbe();
            if (rollbackFailure == "native_inventory_busy")
                return;
            if (rollbackFailure == null)
            {
                _nativeTransferProbe = null;
            }

            if (probe.AlliedNativeTransfer)
            {
                Log("transfer", "status=native_allied_failed|sender_delta=" + (sender - probe.SenderBefore).ToString(CultureInfo.InvariantCulture) + "|recipient_delta=" + (recipient - probe.RecipientBefore).ToString(CultureInfo.InvariantCulture) + "|post_sender_delta=" + (probe.CompletionSenderAmount - probe.SenderBefore).ToString(CultureInfo.InvariantCulture) + "|post_recipient_delta=" + (probe.CompletionRecipientAmount - probe.RecipientBefore).ToString(CultureInfo.InvariantCulture) + "|return_hint=" + NullableBoolText(probe.CompletionReturnHint) + "|rollback=" + Safe(rollbackFailure ?? "ok"));
                if (rollbackFailure != null)
                {
                    DisableGoodsTransfers("native_allied_rollback:" + rollbackFailure);
                }
                else
                {
                    string directFailure;
                    if (TryAtomicTransfer(probe.TargetPlayer, probe.GoodId, probe.Amount, out directFailure))
                    {
                        NativeTransferProbe fallback = _nativeTransferProbe;
                        fallback.CompletionObserved = true;
                        fallback.CompletionPostInventoryExact = true;
                        fallback.CompletionSenderAmount = fallback.SenderBefore - fallback.Amount;
                        fallback.CompletionRecipientAmount = fallback.RecipientBefore + fallback.Amount;
                        Log("transfer", "status=atomic_allied_fallback_staged|target=" + fallback.TargetPlayer.ToString(CultureInfo.InvariantCulture) + "|good=" + fallback.GoodId.ToString(CultureInfo.InvariantCulture) + "|amount=" + fallback.Amount.ToString(CultureInfo.InvariantCulture));
                        CompleteVerifiedNativeTransfer(fallback, "atomic_allied_fallback");
                        return;
                    }

                    if (directFailure != null && directFailure.IndexOf("rollback_failed", StringComparison.Ordinal) >= 0)
                    {
                        DisableGoodsTransfers("atomic_allied_fallback:" + directFailure);
                        return;
                    }

                    Log("transfer", "status=atomic_allied_fallback_failed|reason=" + Safe(directFailure));
                    ShowMessage(Text("Die Gütersendung ist fehlgeschlagen.", "The goods transfer failed."));
                }

                return;
            }

            _nativeEnemyTransferSupported = false;
            Log("transfer", "status=native_canary_failed|sender_delta=" + (sender - probe.SenderBefore).ToString(CultureInfo.InvariantCulture) + "|recipient_delta=" + (recipient - probe.RecipientBefore).ToString(CultureInfo.InvariantCulture) + "|post_sender_delta=" + (probe.CompletionSenderAmount - probe.SenderBefore).ToString(CultureInfo.InvariantCulture) + "|post_recipient_delta=" + (probe.CompletionRecipientAmount - probe.RecipientBefore).ToString(CultureInfo.InvariantCulture) + "|return_hint=" + NullableBoolText(probe.CompletionReturnHint) + "|rollback=" + Safe(rollbackFailure ?? "ok"));
            if (rollbackFailure != null)
            {
                DisableGoodsTransfers("native_canary_rollback:" + rollbackFailure);
                return;
            }

            ShowMessage(Text("Die Gütersendung wurde sicher abgebrochen. Weitere Sendungen verwenden den direkten Transfer.", "The goods transfer was safely aborted. Further transfers use the direct path."));
        }

        private void CompleteVerifiedNativeTransfer(NativeTransferProbe probe, string transport)
        {
            if (probe.RollbackPending)
                throw new InvalidOperationException("rollback cannot grant a transfer");
            DiplomacyManagedStateMemento memento = CaptureManagedStateMemento();
            bool stable = false;
            try
            {
                BeginStableSaveSection("transfer_bookkeeping", null);
                stable = true;
                CommitTransferBookkeeping(probe, transport);
                _nativeTransferProbe = null;
                _inventoryRecoveryBaseState = null;
                _goodsTransfersUnavailable = false;
#if DIPLOMACY_PRIVATE_TESTS
                ObserveCommittedNativeAid(probe);
#endif
            }
            catch (Exception exception)
            {
                RestoreManagedStateMemento(memento);
                probe.RollbackPending = true;
                // Publish the restored model and undo intent before a refund
                // can trigger another save callback.
                if (stable)
                {
                    EndStableSaveSection();
                    stable = false;
                }

                string failure = AbortNativeTransferProbe();
                if (failure != null)
                    DisableGoodsTransfers("bookkeeping_rollback_pending");
                throw new InvalidOperationException("native transfer bookkeeping failed" + (failure == null ? "" : ";rollback=" + failure), exception);
            }
            finally
            {
                if (stable)
                    EndStableSaveSection();
                PublishCombatProtectionSnapshot();
            }

            // Gifts/tribute already observed by the original transfer path
            // are external flows, not permanent native consumption rates.
            try
            {
                RecordCommittedTransfer(probe);
            }
            catch (Exception)
            {
                _economy.Reset();
            }

            if (probe.Purpose == DiplomacyTransferPurpose.Gift && ProbeSender(probe) == _localPlayer)
                ClearGoodsOutcome(probe.TargetPlayer);
        }

        private void CommitTransferBookkeeping(NativeTransferProbe probe, string transport)
        {
            if (probe.Purpose == DiplomacyTransferPurpose.Tribute)
            {
                RelationState relation = _relations.Values.SingleOrDefault(value => value.Allied && value.ExtendedState != null && value.ExtendedState.VassalPlayerId == ProbeSender(probe) && value.ExtendedState.VassalOverlordPlayerId == probe.TargetPlayer);
                if (relation == null || !DiplomacyTribute.IsDue(relation.ExtendedState, probe.GameDay) || !DiplomacyTribute.AcceptsSettlement(relation.ExtendedState, probe.GoodId, probe.Amount, _frozenPrices))
                    throw new InvalidOperationException("tribute journal has no matching obligation");
                DiplomacyTribute.RegisterPayment(relation.ExtendedState, probe.GameDay);
                DiplomacyAgreementRules.RecordTributePaid(relation.ExtendedState);
                return;
            }

#if DIPLOMACY_PRIVATE_TESTS
            if (probe.Purpose == DiplomacyTransferPurpose.Gift && ProbeSender(probe) != _localPlayer)
            {
                CommitNativeAidBookkeeping(probe);
                return;
            }

#endif
            if (probe.Purpose != DiplomacyTransferPurpose.Gift || ProbeSender(probe) != _localPlayer)
                throw new InvalidOperationException("unsupported transfer bookkeeping purpose");
            // For request-bound Mod gifts, commit the proven journal receipt
            // BEFORE goodwill, diplomacy changes or any UI callbacks below.
            // The existing outer memento still undoes both managed updates
            // if subsequent bookkeeping throws.
            if (probe.BoundRequestId != 0 || probe.ModGiftReceiptId != 0)
            {
                if (!HasVerifiedOwnedRequestGiftJournal(probe) ||
                    _requestHistory == null ||
                    !_requestHistory.TryCommitModGiftJournal(probe.BoundRequestId,
                        probe.ModGiftReceiptId, ProbeSender(probe), probe.TargetPlayer,
                        probe.GoodId, probe.Amount, _lastProcessedDay))
                    throw new InvalidOperationException("bound gift has no verified owner journal or live request");
            }
            CompleteTransfer(probe.TargetPlayer, probe.GoodId, probe.Amount, transport);
        }

        private static bool HasVerifiedOwnedRequestGiftJournal(NativeTransferProbe probe)
        {
            if (probe == null || probe.BoundRequestId == 0 ||
                probe.ModGiftReceiptId == 0 || probe.RollbackPending ||
                probe.Purpose != DiplomacyTransferPurpose.Gift ||
                !probe.DirectAtomicTransfer || probe.Progress == null ||
                !DiplomacyTransferRecovery.HasCompletedNativeWrites(CopyPendingTransfer(probe)))
                return false;
            return probe.Progress.RecipientObserved &&
                probe.Progress.RecipientAfter == probe.RecipientBefore + probe.Amount;
        }

        private bool TryAtomicTransfer(int targetPlayer, int goodId, int amount, out string failure)
        {
            return TryAtomicTransferFrom(_localPlayer, targetPlayer, goodId, amount, out failure);
        }

        /// <summary>
        /// Verschiebt eine Menge von einem Spieler zum anderen und prueft
        /// beide Lagerstaende zurueck.
        /// </summary>
        /// <remarks>
        /// Der Absender wurde am zum Parameter, weil ein Tribut
        /// zwischen zwei Bundesgenossen flieszen kann, an denen der oertliche
        /// Spieler nicht beteiligt ist. Die Pruef- und Ruecknahmelogik ist
        /// unveraendert - sie ist das empfindlichste Stueck des Mods, und ein
        /// halb zurueckgenommener Transfer waere schlimmer als ein
        /// gescheiterter.
        /// </remarks>
        private bool TryAtomicTransferFrom(int senderPlayer, int targetPlayer, int goodId, int amount, out string failure, DiplomacyTransferPurpose purpose = DiplomacyTransferPurpose.Gift, int transferDay = -1, ulong boundRequestId = 0)
        {
            IDisposable lease;
            if (!TryEnterNativeInventorySection(out lease))
            {
                failure = "native_inventory_busy";
                return false;
            }

            bool result;
            using (lease)
                result = TryAtomicTransferFromCore(senderPlayer, targetPlayer, goodId, amount, out failure, purpose, transferDay, boundRequestId);
            if (!result && failure != null && failure.Contains("rollback_failed"))
                DisableGoodsTransfers("atomic_rollback_pending");
            return result;
        }

        private bool TryAtomicTransferFromCore(int senderPlayer, int targetPlayer, int goodId, int amount, out string failure, DiplomacyTransferPurpose purpose = DiplomacyTransferPurpose.Gift, int transferDay = -1, ulong boundRequestId = 0)
        {
            if (senderPlayer < 1 || senderPlayer > 8 || targetPlayer < 1 || targetPlayer > 8 || senderPlayer == targetPlayer || amount <= 0)
            {
                failure = "invalid_transfer_parties";
                return false;
            }

            bool nonlocalGift = purpose == DiplomacyTransferPurpose.Gift && senderPlayer != _localPlayer;
#if DIPLOMACY_PRIVATE_TESTS
            bool privateAiAid = nonlocalGift && IsActiveAidMutation(senderPlayer, targetPlayer, goodId, amount, transferDay >= 1 ? transferDay : Math.Max(1, _lastProcessedDay));
#else
            bool privateAiAid = false;
#endif
            if ((purpose != DiplomacyTransferPurpose.Gift && purpose != DiplomacyTransferPurpose.Tribute) || (nonlocalGift && !privateAiAid))
            {
                failure = "invalid_transfer_purpose";
                return false;
            }

            if (_nativeTransferProbe != null || _pendingPackage != null || _goodsTransfersUnavailable)
            {
                failure = "transfer_journal_busy";
                return false;
            }

            int senderBefore, recipientBefore;
            if (!TryReadTransferAmount(senderPlayer, goodId, out senderBefore) || !TryReadTransferAmount(targetPlayer, goodId, out recipientBefore))
            {
                failure = "inventory_amount_out_of_range";
                return false;
            }

            if (senderBefore < amount)
            {
                failure = "insufficient_sender_stock";
                return false;
            }

            if (recipientBefore > int.MaxValue - amount)
            {
                failure = "recipient_amount_overflow";
                return false;
            }

            // Reserve the owner-scoped identity BEFORE the recovery snapshot and
            // BEFORE the very first Mod-owned inventory write. A failed/aborted
            // attempt consumes a serial but can never yield a receipt/credit.
            ulong modGiftReceiptId = 0;
            if (boundRequestId != 0 &&
                (purpose != DiplomacyTransferPurpose.Gift || senderPlayer != _localPlayer ||
                 !RequestHistoryOwnerReady() || _requestHistory == null ||
                 _requestHistory.MapFingerprint != _mapFingerprint ||
                 _requestHistory.RosterFingerprint != _rosterFingerprint ||
                 !_requestHistory.TryReserveModGiftJournal(boundRequestId,
                     senderPlayer, targetPlayer, goodId, amount,
                     _lastProcessedDay, out modGiftReceiptId)))
            {
                failure = "request_journal_binding_refused";
                return false;
            }

            _inventoryRecoveryBaseState = CaptureInventoryRecoveryBaseState();
            var probe = new NativeTransferProbe
            {
                BoundRequestId = boundRequestId,
                ModGiftReceiptId = modGiftReceiptId,
                SenderPlayer = senderPlayer,
                TargetPlayer = targetPlayer,
                GoodId = goodId,
                Amount = amount,
                SenderBefore = senderBefore,
                RecipientBefore = recipientBefore,
                StartedTick = ReadSimulationTick(),
                AlliedNativeTransfer = privateAiAid ? GamePlayerManagerAPI.Instance.IsPlayerAlliedTo(senderPlayer, targetPlayer) : (targetPlayer == _localPlayer ? IsCoalitionAllied(senderPlayer) : IsCoalitionAllied(targetPlayer)),
                DirectAtomicTransfer = true,
                Purpose = purpose,
                GameDay = transferDay >= 0 ? transferDay : AbsoluteGameDay(),
                Progress = new DiplomacyTransferProgress(),
            };
            _nativeTransferProbe = probe;
            DiplomacyPendingTransfer line = CopyPendingTransfer(probe);
            // Both views share the live checkpoints. Saves between native
            // calls therefore retain every completed/attempted half-step.
            line.Progress = probe.Progress;
            try
            {
                DiplomacyTransferRecovery.ApplyLine(line, GoodAmount, ChangeTransferAmount);
                failure = null;
                return true;
            }
            catch (Exception exception)
            {
                bool clamped = probe.Progress.RecipientObserved && probe.Progress.RecipientAfter >= recipientBefore && probe.Progress.RecipientAfter < recipientBefore + amount;
                string rollbackFailure = AbortNativeTransferProbe();
                failure = (clamped ? "recipient_capacity_clamped" : exception.GetType().Name) + ":" + Safe(exception.Message) + (rollbackFailure == null ? "" : ";rollback_failed");
                return false;
            }
        }

        private void ChangeTransferAmount(int player, int goodId, int delta)
        {
            // Native return flags can be changed by observers. The coordinator
            // establishes progress using inventory readback in a finally block.
            if (goodId == 15)
                GamePlayerManagerAPI.Instance.AddPlayerGold(player, delta);
            else if (delta < 0)
                RemoveGoodWithExactHookArguments(player, goodId, -delta);
            else if (delta > 0)
                AddGoodWithExactHookArguments(player, goodId, delta);
        }

        private void CompleteTransfer(int targetPlayer, int goodId, int amount, string transport)
        {
            if (_transferBookkeepingInProgress)
            {
                throw new InvalidOperationException("nested diplomacy transfer bookkeeping is not allowed");
            }

            DiplomacyManagedStateMemento memento = CaptureManagedStateMemento();
            _transferBookkeepingInProgress = true;
            try
            {
                CompleteTransferCore(targetPlayer, goodId, amount, transport);
            }
            catch
            {
                RestoreManagedStateMemento(memento);
                throw;
            }
            finally
            {
                _transferBookkeepingInProgress = false;
            }
        }

        private void CompleteTransferCore(int targetPlayer, int goodId, int amount, string transport)
        {
            string coalitionId;
            if (!_playerCoalition.TryGetValue(targetPlayer, out coalitionId))
            {
                throw new InvalidOperationException("confirmed transfer target has no diplomacy coalition");
            }

            CoalitionState coalition = _coalitions[coalitionId];
            RelationState relation = _relations[coalitionId];
            RefreshInteractionContext();
            decimal value = DiplomacyBalance.GiftValue(goodId, amount, _frozenPrices);
            bool allianceAvailable = CanJoinAlliance(coalitionId);
            int acceptedForDemand = 0;
            if (relation.Demand != null)
            {
                acceptedForDemand = relation.Demand.ApplyDelivery(goodId, amount);
            }

            bool offered;
            DiplomacyGiftResult giftResult = DiplomacyPolicy.RecordGiftDetailed(relation, AbsoluteGameDay(), goodId, value, coalition.Target, coalition.SendGoodsMargin, _mapFingerprint, CoalitionNeeds(coalition), allianceAvailable, _rules);
            offered = giftResult.OfferAlliance;
            Log("transfer", "status=confirmed|transport=" + transport + "|coalition=" + coalitionId + "|good=" + goodId.ToString(CultureInfo.InvariantCulture) + "|amount=" + amount.ToString(CultureInfo.InvariantCulture) + "|value=" + value.ToString(CultureInfo.InvariantCulture) + "|credited=" + giftResult.CreditedValue.ToString(CultureInfo.InvariantCulture) + "|demand_accepted=" + acceptedForDemand.ToString(CultureInfo.InvariantCulture));
            if (string.Equals(transport, "atomic", StringComparison.Ordinal) || string.Equals(transport, "atomic_allied_fallback", StringComparison.Ordinal))
            {
                PlayCoalitionSpeech(coalition, "SK_THANK_GOODS", "thanks");
            }

            if (relation.Demand != null && relation.Demand.Complete)
            {
                DiplomacyDemandResolution resolution = DiplomacyPolicy.ResolveCompletedDemand(relation, AbsoluteGameDay(), coalition.Target, _rules);
                HandleDemandResolution(coalition, relation, resolution);
                return;
            }

            // Partial demand payments are shown by the remaining demand items.
            if (!allianceAvailable && !relation.Allied && DiplomacyAgreementRules.CanFormAlliance(relation, coalition.Target))
            {
                ShowMessage(Text("Bündnis nicht möglich: Ein feindlicher KI-Verbund muss verbleiben.", "Alliance unavailable: one hostile AI coalition must remain."));
                return;
            }

            if (offered)
            {
                ConfirmSpontaneousOffer(coalition);
            }
        }

        private void RequestAlliance(int player)
        {
            string coalitionId = _playerCoalition[player];
            CoalitionState coalition = _coalitions[coalitionId];
            RelationState relation = _relations[coalitionId];
            if (relation.Allied)
            {
                return;
            }

            RefreshInteractionContext();
            DiplomacyRequestResult result = DiplomacyPolicy.RequestAlliance(relation, AbsoluteGameDay(), coalition.Target, CanJoinAlliance(coalitionId), CoalitionNeeds(coalition), _rules);
            switch (result.Outcome)
            {
                case DiplomacyRequestOutcome.Rejected:
                    PlayCoalitionSpeech(coalition, "SK_WONT_HELP", "nohelp_02");
                    if (relation.ExtendedState != null && relation.ExtendedState.AllianceTermsFulfilled)
                    {
                        ShowMessage(DiplomacyAgreementRules.PermanentHistoryPreventsAlliance(relation) ? Text("Frühere Vertragsbrüche schließen dieses Bündnis dauerhaft aus.", "Earlier treaty breaches permanently rule out this alliance.") : Text("Die Bedingungen sind erfüllt, doch Vertrauen und Ansehen reichen noch nicht aus.", "The terms are fulfilled, but trust and standing are not yet sufficient."));
                        break;
                    }

                    ShowMessage(Text("Der Gegner weist die Anfrage zurück.", "The opponent rejects the request."));
                    break;
                case DiplomacyRequestOutcome.DemandIssued:
                    PlayCoalitionSpeech(coalition, "SK_REQUEST_GOODS", "req");
                    ShowMessage(DemandText(result.Demand));
                    Log("demand", "coalition=" + coalitionId + "|status=issued|expires=" + result.Demand.ExpiresDay.ToString(CultureInfo.InvariantCulture));
                    break;
                case DiplomacyRequestOutcome.AllianceReady:
                    FormAlliance(coalition, "direct_request");
                    break;
                case DiplomacyRequestOutcome.AllianceBlocked:
                    PlayCoalitionSpeech(coalition, "SK_WONT_HELP", "nohelp_02");
                    ShowMessage(Text("Bündnis nicht möglich: Ein feindlicher KI-Verbund muss verbleiben.", "Alliance unavailable: one hostile AI coalition must remain."));
                    break;
                case DiplomacyRequestOutcome.Locked:
                    PlayCoalitionSpeech(coalition, "SK_WONT_HELP", "nohelp_02");
                    int waitDays = DiplomacyAgreementRules.NegotiationWaitDays(relation, AbsoluteGameDay());
                    ShowMessage(relation.PendingOffer ? Text("Eine Bündnisentscheidung ist bereits offen.", "An alliance decision is already pending.") : waitDays > 0 ? Text("Die Gesandten benötigen Zeit. Neue Gespräche: ", "The envoys need time. New talks: ") + DurationText(waitDays) : Text("Neue Verhandlungen sind derzeit gesperrt.", "New negotiations are currently locked."));
                    break;
            }
        }

        private void FormAlliance(CoalitionState coalition, string reason)
        {
            if (!CanJoinAlliance(coalition.CoalitionId))
            {
                return;
            }

            Dictionary<int, int> partition = BuildTeamPartition(coalition.CoalitionId, null, false);
            QueueTeamTransition(DiplomacyTeamTransitionKind.FormAlliance, coalition.CoalitionId, AbsoluteGameDay(), reason, partition);
        }

        private void CancelBlockedEnemyDemands()
        {
            foreach (CoalitionState candidate in _coalitions.Values)
            {
                RelationState relation = _relations[candidate.CoalitionId];
                if (relation.Allied || CanJoinAlliance(candidate.CoalitionId))
                {
                    continue;
                }

                if (relation.Demand != null && !relation.Demand.IsUltimatum)
                {
                // A ExtendedState truce, trade pact, restitution or terms package
                // never changes native teams by itself. It remains valid
                // even when this coalition is the final native enemy; only
                // the eventual alliance transition is prohibited.
                }

                if (relation.PendingOffer)
                {
                    relation.PendingOffer = false;
                    relation.PendingOfferIssuedDay = -1;
                    CancelDiplomacyDialog("alliance_offer:" + candidate.CoalitionId);
                    Log("offer", "coalition=" + candidate.CoalitionId + "|status=cancelled" + "|reason=last_hostile_coalition");
                }
            }
        }

        private void BreakCoalition(CoalitionState coalition, int gameDay, string reason)
        {
            Dictionary<int, int> partition = BuildTeamPartition(null, coalition.CoalitionId, false);
            QueueTeamTransition(DiplomacyTeamTransitionKind.AiBreak, coalition.CoalitionId, gameDay, reason, partition);
        }

        private void ConfirmPlayerBreakOne(int selectedPlayer)
        {
            string id;
            if (!_playerCoalition.TryGetValue(selectedPlayer, out id))
                return;
            RelationshipDecisionQuote quote = CaptureRelationshipDecision(id, selectedPlayer, DiplomacyRelationshipDecisionKind.EndAlliance);
            if (quote == null)
                return;
            ShowConfirmation(quote.Names + " · " + Text("Bündnis aufkündigen", "End alliance"), delegate
            {
                StageRelationshipDecision(quote);
            }, delegate
            {
            }, quote.Names + " · " + Text("Dieses Bündnis wird sofort beendet. Andere feindliche Fürsten werden Euren Vertragsbruch berücksichtigen.", "This alliance ends immediately. Other hostile lords will remember your breach."), null, quote.Key, delegate
            {
                lock (_gate)
                    return IsCurrentRelationshipDecision(quote);
            });
        }

        private void PlayerBreakOne(string coalitionId)
        {
            lock (_gate)
            {
                RelationState relation;
                if (string.IsNullOrEmpty(coalitionId) || !_relations.TryGetValue(coalitionId, out relation) || !relation.Allied)
                {
                    Log("break", "actor=player|status=rejected" + "|reason=no_selected_alliance" + "|coalition=" + Safe(coalitionId));
                    return;
                }

                int day = AbsoluteGameDay();
                Dictionary<int, int> partition = BuildTeamPartition(null, coalitionId, false);
                QueueTeamTransition(DiplomacyTeamTransitionKind.PlayerBreakOne, coalitionId, day, "player_request", partition);
            }
        }

        private void ConfirmPlayerBreakAll(int selectedPlayer)
        {
            string coalitionId;
            if (!_playerCoalition.TryGetValue(selectedPlayer, out coalitionId) || !_relations.ContainsKey(coalitionId) || !_relations[coalitionId].Allied)
            {
                return;
            }

            long generation = _sessionGeneration;
            string fingerprint = _mapFingerprint;
            ShowConfirmation(Text("Bündnis aufkündigen", "End alliance"), delegate
            {
                lock (_gate)
                {
                    if (_mapActive && !_mapDisabled && generation == _sessionGeneration && string.Equals(fingerprint, _mapFingerprint, StringComparison.Ordinal) && _relations.Values.Any(relation => relation.Allied))
                    {
                        PlayerBreakAll();
                    }
                    else
                    {
                        Log("break", "actor=player" + "|status=stale_callback_ignored");
                    }
                }
            }, delegate
            {
            }, Text("Alle Bündnisse werden sofort beendet. Sämtliche KI-Koalitionen werden feindlich.", "All alliances end immediately. Every AI coalition becomes hostile."));
        }

        private void PlayerBreakAll()
        {
            lock (_gate)
            {
                if (!_relations.Values.Any(relation => relation.Allied))
                {
                    Log("break", "actor=player|status=rejected|reason=no_alliance");
                    return;
                }

                int day = AbsoluteGameDay();
                Dictionary<int, int> partition = BuildTeamPartition(null, null, true);
                QueueTeamTransition(DiplomacyTeamTransitionKind.PlayerBreakAll, null, day, "player_request", partition);
            }
        }

        private Dictionary<int, int> BuildTeamPartition(string joiningCoalition, string leavingCoalition, bool playerBreaksAll)
        {
            var groups = new List<List<int>>();
            var allied = new List<int>
            {
                _localPlayer
            };
            foreach (CoalitionState coalition in _coalitions.Values.OrderBy(value => value.CoalitionId, StringComparer.Ordinal))
            {
                bool isAllied = _relations[coalition.CoalitionId].Allied;
                if (coalition.CoalitionId == joiningCoalition)
                {
                    isAllied = true;
                }

                if (coalition.CoalitionId == leavingCoalition || playerBreaksAll)
                {
                    isAllied = false;
                }

                if (isAllied)
                {
                    allied.AddRange(coalition.MemberPlayerIds);
                }
                else
                {
                    groups.Add(coalition.MemberPlayerIds.ToList());
                }
            }

            groups.Insert(0, allied);
            if (groups.Count > 8)
            {
                throw new InvalidOperationException("team partition cannot be represented by native teams 1..8");
            }

            var result = new Dictionary<int, int>();
            var used = new HashSet<int>();
            for (int index = 0; index < groups.Count; index++)
            {
                List<int> group = groups[index];
                int preferred = index == 0 ? GamePlayerManagerAPI.Instance.GetPlayerTeam(_localPlayer) : _coalitions[_playerCoalition[group[0]]].OriginalTeams[0];
                int team = preferred >= 1 && preferred <= 8 && !used.Contains(preferred) ? preferred : Enumerable.Range(1, 8).First(value => !used.Contains(value));
                used.Add(team);
                foreach (int player in group)
                {
                    result[player] = team;
                }
            }

            return result;
        }

        private bool ApplyTeamPartition(Dictionary<int, int> partition, string reason)
        {
            RequireNativeDiplomacySection();
            TeamTransactionResult result = DiplomacyTeamTransaction.Apply(_teamPort, partition);
            if (result.OriginalTeams != null)
            {
                foreach (KeyValuePair<int, int> original in result.OriginalTeams)
                {
                    if (!_ownedOriginalTeams.ContainsKey(original.Key))
                    {
                        _ownedOriginalTeams[original.Key] = original.Value;
                    }
                }
            }

            if (!result.Applied)
            {
                if (result.ResidualAssignments != null)
                {
                    foreach (KeyValuePair<int, int> residual in result.ResidualAssignments)
                    {
                        _ownedAssignedTeams[residual.Key] = residual.Value;
                    }
                }

                Log("team_transaction", "status=failed_closed|reason=" + Safe(result.Failure) + "|residual=" + PartitionText(result.ResidualAssignments));
                ShowMessage(Text("Die Teamänderung wurde sicher abgebrochen.", "The team change was safely aborted."));
                return false;
            }

            _ownedAssignedTeams.Clear();
            foreach (KeyValuePair<int, int> value in partition)
            {
                _ownedAssignedTeams[value.Key] = value.Value;
            }

            Log("team_transaction", "status=committed|reason=" + reason + "|partition=" + PartitionText(partition));
            return true;
        }

        private bool CanJoinAlliance(string coalitionId)
        {
            CoalitionState target;
            if (string.IsNullOrEmpty(coalitionId) || !_coalitions.TryGetValue(coalitionId, out target) || !target.MemberPlayerIds.Any(IsAlivePlayer))
            {
                return false;
            }

            return _coalitions.Values.Count(coalition => coalition.CoalitionId != coalitionId && !_relations[coalition.CoalitionId].Allied && coalition.MemberPlayerIds.Any(IsAlivePlayer)) > 0;
        }

        private void ConfirmSpontaneousOffer(CoalitionState coalition)
        {
            RelationState relation = _relations[coalition.CoalitionId];
            bool restored = relation.PendingOffer;
            if (!restored)
            {
                DiplomacyPolicy.BeginPendingOffer(relation, AbsoluteGameDay());
            }

            long generation = _sessionGeneration;
            string fingerprint = _mapFingerprint;
            string coalitionId = coalition.CoalitionId;
            Log("offer", "coalition=" + coalition.CoalitionId + "|status=" + (restored ? "restored" : "offered"));
            if (!restored)
            {
                PlayCoalitionSpeech(coalition, "SK_ADD_PLAYER", "add_player");
            }

            RelationshipDecisionQuote quote = CaptureRelationshipDecision(coalitionId, coalition.MemberPlayerIds.OrderBy(player => player).First(), DiplomacyRelationshipDecisionKind.AcceptAllianceOffer);
            if (quote == null)
                return;
            ShowConfirmation(quote.Names + " · " + Text("Bündnisangebot", "Alliance offer"), delegate
            {
                StageRelationshipDecision(quote, true);
            }, delegate
            {
                StageRelationshipDecision(quote, false);
            }, quote.Names + " · " + Text("Der Gegner bietet ein Bündnis an.", "The opponent offers an alliance."), null, quote.Key, delegate
            {
                lock (_gate)
                    return IsCurrentRelationshipDecision(quote);
            });
        }

        private void ResolveSpontaneousOffer(string coalitionId, long generation, string fingerprint, bool accepted)
        {
            lock (_gate)
            {
                RelationState relation;
                CoalitionState coalition;
                if (!_mapActive || _mapDisabled || generation != _sessionGeneration || !string.Equals(fingerprint, _mapFingerprint, StringComparison.Ordinal) || !_relations.TryGetValue(coalitionId, out relation) || !_coalitions.TryGetValue(coalitionId, out coalition) || !relation.PendingOffer || relation.Allied)
                {
                    Log("offer", "coalition=" + Safe(coalitionId) + "|status=stale_callback_ignored");
                    return;
                }

                if (accepted)
                {
                    if (CanJoinAlliance(coalitionId))
                    {
                        FormAlliance(coalition, "spontaneous_offer");
                    }
                    else
                    {
                        relation.PendingOffer = false;
                        relation.PendingOfferIssuedDay = -1;
                        ShowMessage(Text("Bündnis nicht möglich: Ein feindlicher KI-Verbund muss verbleiben.", "Alliance unavailable: one hostile AI coalition must remain."));
                        Log("offer", "coalition=" + coalitionId + "|status=blocked_on_accept");
                    }
                }
                else
                {
                    DiplomacyPolicy.RejectPendingOffer(relation, AbsoluteGameDay(), _rules);
                    Log("offer", "coalition=" + coalitionId + "|status=rejected");
                }

                if (!relation.PendingOffer)
                {
                    ShowNextPendingOffer();
                }
            }
        }

        private void ShowNextPendingOffer()
        {
            if (!_mapActive || _mapDisabled)
            {
                return;
            }

            RelationState pending = _relations.Values.Where(relation => relation.PendingOffer && _coalitions.ContainsKey(relation.CoalitionId) && _coalitions[relation.CoalitionId].MemberPlayerIds.Any(IsAlivePlayer)).OrderBy(relation => relation.CoalitionId, StringComparer.Ordinal).FirstOrDefault();
            if (pending != null)
            {
                ConfirmSpontaneousOffer(_coalitions[pending.CoalitionId]);
            }
        }

        private void BeginGoodsSelection(object panel, int player)
        {
            _pendingGoodsTarget = player;
            _selectedGoodsField.SetValue(panel, -1);
            _selectedGoodsAmountField.SetValue(panel, -1);
            MainViewModel.Instance.Allies_MainViewVis = false;
            MainViewModel.Instance.Allies_NoAlliesViewVis = false;
            MainViewModel.Instance.Allies_GoodsViewVis = true;
            MainViewModel.Instance.Allies_SendGoodsViewVis = true;
            MainViewModel.Instance.Allies_RequestGoodsViewVis = false;
            MethodInfo updateGoods = panel.GetType().GetMethod("UpdateGoods", BindingFlags.NonPublic | BindingFlags.Instance);
            if (updateGoods == null)
            {
                throw new MissingMethodException("HUD_AlliesPanel.UpdateGoods");
            }

            updateGoods.Invoke(panel, null);
        }

        private bool PopulateCombinedRoster(object panel, int[, ] lordData)
        {
            lock (_gate)
            {
                if (!_mapActive || _mapDisabled)
                {
                    return false;
                }

                if (lordData == null || lordData.Rank != 2 || lordData.GetLength(1) < 2)
                {
                    throw new InvalidOperationException("HUD ally roster received invalid merit data");
                }

                int[] previousRoster = (int[])_allyField.GetValue(panel);
                int previousIndex = (int)_currentAllyField.GetValue(panel);
                int previousPlayer = previousRoster != null && previousIndex >= 0 && previousIndex < previousRoster.Length ? previousRoster[previousIndex] : 0;
                var alivePlayers = new HashSet<int>(GamePlayerManagerAPI.Instance.GetAlivePlayerIds());
                List<int> combined = _playerCoalition.Keys.Where(player => player != _localPlayer && alivePlayers.Contains(player) && player >= 0 && player < lordData.GetLength(0) && lordData[player, 1] >= 0).OrderBy(player => player).ToList();
                if (combined.Count > 7)
                {
                    throw new InvalidOperationException("combined diplomacy roster exceeds seven AI lords");
                }

                int current = combined.IndexOf(previousPlayer);
                if (current < 0)
                {
                    current = Math.Min(Math.Max(0, previousIndex), Math.Max(0, combined.Count - 1));
                }

                var nativeRoster = new int[7];
                for (int index = 0; index < combined.Count; index++)
                {
                    nativeRoster[index] = combined[index];
                }

                _allyField.SetValue(panel, nativeRoster);
                _numAlliesField.SetValue(panel, combined.Count);
                _currentAllyField.SetValue(panel, current);
                _visiblePlayers.Clear();
                _visiblePlayers.AddRange(combined);
                _selectedVisibleIndex = current;
                string signature = string.Join(",", combined.Select(player => player.ToString(CultureInfo.InvariantCulture)));
                if (!string.Equals(_uiRosterSignature, signature, StringComparison.Ordinal))
                {
                    _uiRosterSignature = signature;
                    Log("ui_roster", "status=combined|players=" + Safe(signature));
                }

                return true;
            }
        }

        private void RefreshVanillaDiplomacyUi(object panel)
        {
            NoteLane(LaneUiPanel);
            lock (_gate)
            {
                if (panel != null && RefreshOriginalMpTradeOnlyFacade(panel))
                    return;
                if (!_mapActive || _mapDisabled || panel == null)
                {
                    return;
                }

                // Ein Noesis-Schreibvorgang kann einen Layoutlauf ausloesen,
                // und der kann diese Aktualisierung erneut aufrufen. Der
                // Monitor oben haelt das nicht auf - er ist fuer denselben
                // Faden wiedereintrittsfaehig.
                if (_uiRefreshInProgress)
                {
                    UiStep("reentered");
                    return;
                }

                _uiRefreshInProgress = true;
                try
                {
                    RefreshVanillaDiplomacyUiCore(panel);
                }
                catch (Exception exception)
                {
                    // The same display path is reached from both refresh and
                    // command callbacks. A display failure must never undo an
                    // already valid payment, offer or team transition.
                    DegradeUiRefresh("ui_refresh", panel, exception);
                }
                finally
                {
                    _uiRefreshInProgress = false;
                }
            }
        }

#if DIPLOMACY_PRIVATE_TESTS
#if DIPLOMACY_PRIVATE_TESTS
        private bool _hudProofEmitted;
        private bool _hudInvalidRosterEmitted;
#endif
        internal static bool ShowOriginalDiplomacyActivationPopup()
        {
            DiplomacyFeature owner;
            lock (OwnerGate)
                owner = _activeOwner;
            if (owner == null || !owner._mapActive || owner._mapDisabled ||
                owner._mapBootstrapPending ||
                SHCDESE.API.GameNetworkAPI.IsLobbyEnvironment())
                return false;
            try
            {
                // Real original HUD OK popup, not a synthetic mouse click
                // or an invented standalone free-text lord chat.
                ShowOkMessageNative(
                    "Diplomatie-Mod aktiv",
                    null,
                    "Diplomatie wurde in dieser Einzelspieler-Partie geladen. " +
                    "Oeffne das Verbuendetenfenster, waehle einen Fuersten und " +
                    "nutze die Handels- und Buendnisaktionen. Ein eigenstaendiger " +
                    "Freitext-Fuerstenchat ist noch nicht implementiert. " +
                    "Bugfixes & QoL: Besiegte Spieler bleiben bei aktivem " +
                    "Zuschauermodus im Spiel.");
                owner.Log("ui_integration",
                    "status=original_HUD_popup_method_invoked" +
                    "|human_physical_click_proven=false");
                return true;
            }
            catch (Exception exception)
            {
                owner.Log("ui_integration",
                    "status=original_HUD_popup_unavailable" +
                    "|reason=" + Safe(exception.GetType().Name));
                return false;
            }
        }
#endif

        private void RefreshVanillaDiplomacyUiCore(object panel)
        {
            {
                bool portraitLayoutComplete;
                UiStep("portraits");
                if (!ReferenceEquals(_portraitLayoutPanel, panel))
                {
                    _portraitLayoutPanel = panel;
                    _portraitLayoutApplied = false;
                }

                portraitLayoutComplete = EnsureActivePortraitLayout(panel);
                UiStep("title");
                object title = FindElement(panel, "EuropeDiplomacyTitle");
                if (title != null)
                {
                    SetProperty(title, "Text", Text("Diplomatie", "Diplomacy"));
                }

                UiStep("native_fields");
                int[] nativeRoster = (int[])_allyField.GetValue(panel);
                int count = (int)_numAlliesField.GetValue(panel);
                int current = (int)_currentAllyField.GetValue(panel);
#if DIPLOMACY_PRIVATE_TESTS
                // This is a real Noesis runtime readback, not a guess based
                // on presence of the separate XAML PATCH file.
                bool actualPanelRosterReady=nativeRoster != null &&
                    count > 0 && count <= nativeRoster.Length &&
                    current >= 0 && current < count &&
                    nativeRoster[current] >= 1 &&
                    nativeRoster[current] <= 8;
                if (!actualPanelRosterReady)
                {
                    if (!_hudInvalidRosterEmitted)
                    {
                        _hudInvalidRosterEmitted = true;
                        Log("ui_integration",
                            "status=original_AlliesPanel_roster_not_ready" +
                            "|count=" + count + "|current=" + current +
                            "|native_array=" + (nativeRoster != null) +
                            "|never_claim_visible_GUI=true");
                    }
                }
                else if (!_hudProofEmitted)
                {
                    // A failed optional diagnostic lookup must never
                    // destroy functional diplomacy or prevent an opening.
                    bool trade=false,alliance=false,modal=false,visible=false;
                    try
                    {
                        trade=FindElement(panel,"TradeTab") != null;
                        alliance=FindElement(panel,"AllianceRootPanel") != null;
                        modal=FindElement(panel,"TradeModal") != null;
                        visible=MainViewModel.Instance != null &&
                            MainViewModel.Instance.AlliesPanelVisible;
                    }
                    catch (Exception lookupFault)
                    {
                        Log("ui_integration",
                            "status=optional_HUD_check_failed" +
                            "|reason=" + Safe(lookupFault.GetType().Name) +
                            "|model_not_disabled=true");
                    }
                    _hudProofEmitted = trade && alliance && modal && visible;
                    Log("ui_integration",
                        "status=original_AlliesPanel_actual_HUD_readback" +
                        "|trade_tab=" + trade +
                        "|alliance_root=" + alliance +
                        "|trade_modal=" + modal +
                        "|panel_visible=" + visible +
                        "|roster_count=" + count +
                        "|current=" + current +
                        "|GUI_ELEMENTS_PROVEN=" + _hudProofEmitted +
                        "|human_actual_click_not_proven=true");
                }
#endif
                if (nativeRoster == null || count <= 0 || current < 0 || current >= count || count > nativeRoster.Length)
                {
                    SetVisibilityIfPresent(FindElement(panel, "EuropeDiplomacySlot6"), false);
                    _tradeTab = false; // No stale trade commands after roster invalidation.
                    HideDetailPanel(panel);
                    return;
                }

                _visiblePlayers.Clear();
                for (int index = 0; index < count; index++)
                {
                    _visiblePlayers.Add(nativeRoster[index]);
                }

                _selectedVisibleIndex = current;
                UiStep("slot_lookup");
                object slot6 = FindElement(panel, "EuropeDiplomacySlot6");
                object orders = FindElement(panel, "EuropeDiplomacyOrders");
                object send = FindElement(panel, "EuropeDiplomacySend");
                object request = FindElement(panel, "EuropeDiplomacyRequest");
                object breakButton = FindElement(panel, "EuropeDiplomacyBreak");
                bool extensionComplete = slot6 != null && title != null && orders != null && send != null && request != null // Current official rawra base has no standalone Break
 // button: ExtendedState provides AllianceDetailsBreak instead.
                // Never exclude diplomacy or trade for that legacy alias.
                && portraitLayoutComplete;
                if (!extensionComplete && !_uiExtensionWarningLogged)
                {
                    _uiExtensionWarningLogged = true;
                    Log("ui_integration", "status=degraded|reason=xaml_extension_missing");
                }

                UiStep("slot6");
                bool showSlot6 = count > 6 && portraitLayoutComplete;
                SetVisibilityIfPresent(slot6, showSlot6);
                if (showSlot6)
                {
                    SetNumericProperty(slot6, "Opacity", current == 6 ? 1.0 : 0.5);
                }
                else
                {
                    ClearAlliesFace(6);
                    MainViewModel.Instance.AlliesHumanFaceVis[6] = false;
                    SetPropertyIfPresent(FindElement(panel, "EuropeDiplomacyHelp6"), "Source", null);
                    SetPropertyIfPresent(FindElement(panel, "EuropeDiplomacyOrdersPip6"), "Source", null);
                }

                UiStep("slots");
                MainViewModel viewModel = MainViewModel.Instance;
                for (int slot = 0; slot < count; slot++)
                {
                    int player = nativeRoster[slot];
                    // Portrait faces can be absent before Noesis completes
                    // layout, or for a partially populated skirmish roster.
                    // Missing optional tooltips must never abort refresh.
                    SetPropertyIfPresent(FindElement(panel, "EuropeDiplomacyFace" + slot.ToString(CultureInfo.InvariantCulture)), "ToolTip", PlayerName(player));
                    string coalitionId;
                    if (!_playerCoalition.TryGetValue(player, out coalitionId))
                    {
                        continue;
                    }

                    RelationState relation = _relations[coalitionId];
                    DemandItem demandItem = relation.Demand == null ? null : relation.Demand.Items.FirstOrDefault(item => item.RemainingAmount > 0);
                    if (demandItem != null)
                    {
                        SetProperty(viewModel, "AlliesRequestedGoods" + slot.ToString(CultureInfo.InvariantCulture), GetAlliesGoodsIcon(demandItem.GoodId));
                    }
                    else if (!relation.Allied)
                    {
                        SetProperty(viewModel, "AlliesRequestedGoods" + slot.ToString(CultureInfo.InvariantCulture), null);
                    }

                    if (slot == 6)
                    {
                        object help = null;
                        object order = null;
                        if (relation.Allied)
                        {
                            EngineInterface.PlayState live = GameData.Instance.lastGameState;
                            if (live.skirmish_needs_help[player] != 0)
                            {
                                help = GetAlliesGoodsIcon(-2);
                            }

                            if (live.skirmish_order[player] == 1)
                            {
                                order = GetAlliesGoodsIcon(-3);
                            }
                            else if (live.skirmish_order[player] == 2)
                            {
                                order = GetAlliesGoodsIcon(-4);
                            }
                        }

                        SetPropertyIfPresent(FindElement(panel, "EuropeDiplomacyHelp6"), "Source", help);
                        SetPropertyIfPresent(FindElement(panel, "EuropeDiplomacyOrdersPip6"), "Source", order);
                    }
                }

                int selected = nativeRoster[current];
                string selectedCoalitionId;
                if (!_playerCoalition.TryGetValue(selected, out selectedCoalitionId))
                {
                    HideDetailPanel(panel);
                    return;
                }

                CoalitionState coalition = _coalitions[selectedCoalitionId];
                RelationState selectedRelation = _relations[selectedCoalitionId];
                bool allied = selectedRelation.Allied;
                bool breakableAgreement = HasBreakableAgreement(selectedRelation);
                SetVisibilityIfPresent(orders, allied);
                SetVisibilityIfPresent(send, true);
                SetVisibilityIfPresent(request, true);
                SetVisibilityIfPresent(breakButton, allied || breakableAgreement);
                if (!allied)
                {
                    DemandItem selectedDemand = selectedRelation.Demand == null ? null : selectedRelation.Demand.Items.FirstOrDefault(item => item.RemainingAmount > 0);
                    string enemyStatus = Text("Feind — ", "Enemy — ") + StatusText(selectedRelation, coalition);
                    viewModel.AlliesSelectedGoods = selectedDemand == null ? enemyStatus : WithVanillaGoodsIconSpacing(enemyStatus);
                    SetProperty(viewModel, "AlliesRequestedGoodsCurrent", selectedDemand == null ? null : GetAlliesGoodsIcon(selectedDemand.GoodId));
                    viewModel.AlliesSelectedOrder = ActionHint(selectedRelation, coalition);
                    viewModel.AlliesSelectedHelp = selectedRelation.Demand == null ? RelationshipSummary(selectedRelation) : DemandPackageHint();
                    SetProperty(viewModel, "AlliesOrderCurrentBackground", null);
                    SetProperty(viewModel, "AlliesOrderCurrentFace", null);
                    SetProperty(viewModel, "AlliesHelpCurrentBackground", null);
                    SetProperty(viewModel, "AlliesHelpCurrentFace", null);
                    viewModel.Allies_OrdersConfirmVis = false;
                    RefreshDetailPanel(panel, selectedRelation, coalition, selected);
                    return;
                }

                DemandItem alliedDemand = selectedRelation.Demand == null ? null : selectedRelation.Demand.Items.FirstOrDefault(item => item != null && item.RemainingAmount > 0);
                if (alliedDemand != null)
                {
                    viewModel.AlliesSelectedGoods = WithVanillaGoodsIconSpacing(Text("Verbündet — ", "Allied — ") + NegotiationStatusText(selectedRelation, coalition, selected));
                    SetProperty(viewModel, "AlliesRequestedGoodsCurrent", GetAlliesGoodsIcon(alliedDemand.GoodId));
                }
                else if (GameData.Instance.lastGameState.skirmish_player_requesting_type[selected] <= 0)
                {
                    viewModel.AlliesSelectedGoods = Text("Verbündet — ", "Allied — ") + NegotiationStatusText(selectedRelation, coalition, selected);
                    SetProperty(viewModel, "AlliesRequestedGoodsCurrent", null);
                }

                if (selectedRelation.Demand != null && selectedRelation.Demand.IsUltimatum)
                {
                    viewModel.AlliesSelectedHelp = DemandPackageHint();
                    SetProperty(viewModel, "AlliesHelpCurrentBackground", null);
                    SetProperty(viewModel, "AlliesHelpCurrentFace", null);
                }
                else
                {
                    viewModel.AlliesSelectedHelp = RelationshipSummary(selectedRelation);
                }

                viewModel.AlliesSelectedOrder = ActionHint(selectedRelation, coalition);
                RefreshDetailPanel(panel, selectedRelation, coalition, selected);
            }
        }

        private bool HandleRollover(object sender)
        {
            NoteLane(LaneUiPanel);
            lock (_gate)
            {
                if (!_mapActive || _mapDisabled || sender == null)
                {
                    return false;
                }

                PropertyInfo commandParameter = sender.GetType().GetProperty("CommandParameter", BindingFlags.Public | BindingFlags.Instance);
                string command = commandParameter == null ? null : commandParameter.GetValue(sender, null) as string;
                int portraitSlot;
                if (command == "Diplomacy:Select:6")
                    portraitSlot = 6;
                else if (!int.TryParse(command, NumberStyles.None, CultureInfo.InvariantCulture, out portraitSlot))
                    portraitSlot = -1;
                if (portraitSlot >= 0 && portraitSlot < _visiblePlayers.Count && portraitSlot <= 6)
                {
                    MainViewModel.Instance.AlliesCommandsRolloverText = PlayerName(_visiblePlayers[portraitSlot]);
                    return true;
                }

                if (command != null && command.StartsWith("Diplomacy:Alliance", StringComparison.Ordinal))
                {
                    MainViewModel.Instance.AlliesCommandsRolloverText = Text("Den ausgewählten Verbündeten verwalten", "Manage the selected ally");
                    return true;
                }

                if (command == "Diplomacy:Overview")
                {
                    MainViewModel.Instance.AlliesCommandsRolloverText = _overviewVisible ? Text("Zurück zur Einzelansicht", "Back to the single view") : Text("Bündnismitglieder und Lehnsverhältnisse verwalten", "Manage alliance members and fealty");
                    return true;
                }

                if (command == "Diplomacy:Break")
                {
                    int breakPlayer = SelectedPlayer(CurrentAlliesPanel());
                    string breakCoalition;
                    RelationState breakRelation;
                    bool treaty = breakPlayer > 0 && _playerCoalition.TryGetValue(breakPlayer, out breakCoalition) && _relations.TryGetValue(breakCoalition, out breakRelation) && HasBreakableAgreement(breakRelation);
                    MainViewModel.Instance.AlliesCommandsRolloverText = treaty ? Text("Abkommen aufkündigen", "End agreement") : Text("Bündnis aufkündigen", "End alliance");
                    return true;
                }

                object panel = CurrentAlliesPanel();
                int selected = SelectedPlayer(panel);
                if (selected <= 0)
                {
                    return false;
                }

                string rolloverCoalition;
                RelationState rolloverRelation;
                if (command == "Request" && _playerCoalition.TryGetValue(selected, out rolloverCoalition) && _relations.TryGetValue(rolloverCoalition, out rolloverRelation))
                {
                    MainViewModel.Instance.AlliesCommandsRolloverText = RequestRolloverText(rolloverRelation, selected);
                    return true;
                }

                if (IsCoalitionAllied(selected))
                {
                    return false;
                }

                if (command == "Request")
                {
                    MainViewModel.Instance.AlliesCommandsRolloverText = Text("Bündnis anfragen", "Request alliance");
                    return true;
                }

                if (command == "Orders")
                {
                    MainViewModel.Instance.AlliesCommandsRolloverText = Text("Befehle sind nur für Verbündete verfügbar.", "Orders are available to allies only.");
                    return true;
                }

                return false;
            }
        }

        private bool EnsureActivePortraitLayout(object panel)
        {
            if (_portraitLayoutApplied)
            {
                return true;
            }

            for (int slot = 0; slot < 6; slot++)
            {
                string suffix = slot.ToString(CultureInfo.InvariantCulture);
                if (FindElement(panel, "EuropeDiplomacyFaceBackground" + suffix) == null || FindElement(panel, "EuropeDiplomacyFace" + suffix) == null || FindElement(panel, "EuropeDiplomacyHumanBorder" + suffix) == null || FindElement(panel, "EuropeDiplomacyRequested" + suffix) == null || FindElement(panel, "EuropeDiplomacyHelp" + suffix) == null || FindElement(panel, "EuropeDiplomacyOrdersPip" + suffix) == null)
                {
                    return false;
                }
            }

            if (FindElement(panel, "EuropeDiplomacyFaceBackground6") == null || FindElement(panel, "EuropeDiplomacyFace6") == null || FindElement(panel, "EuropeDiplomacyHumanBorder6") == null || FindElement(panel, "EuropeDiplomacyRequested6") == null || FindElement(panel, "EuropeDiplomacyHelp6") == null || FindElement(panel, "EuropeDiplomacyOrdersPip6") == null)
            {
                return false;
            }

            for (int slot = 0; slot < 6; slot++)
            {
                string suffix = slot.ToString(CultureInfo.InvariantCulture);
                float faceLeft = 10f + 100f * slot;
                SetThicknessProperty(FindElement(panel, "EuropeDiplomacyFaceBackground" + suffix), "Margin", faceLeft, 60f, 0f, 0f);
                SetThicknessProperty(FindElement(panel, "EuropeDiplomacyFace" + suffix), "Margin", faceLeft, 60f, 0f, 0f);
                SetThicknessProperty(FindElement(panel, "EuropeDiplomacyHumanBorder" + suffix), "Margin", faceLeft, 60f, 0f, 0f);
                SetThicknessProperty(FindElement(panel, "EuropeDiplomacyRequested" + suffix), "Margin", faceLeft + 60f, 48f, 0f, 0f);
                SetThicknessProperty(FindElement(panel, "EuropeDiplomacyHelp" + suffix), "Margin", faceLeft + 60f, 120f, 0f, 0f);
                SetThicknessProperty(FindElement(panel, "EuropeDiplomacyOrdersPip" + suffix), "Margin", faceLeft - 8f, 120f, 0f, 0f);
            }

            _portraitLayoutApplied = true;
            Log("ui_integration", "status=vanilla_portraits_extended");
            return true;
        }

        private void RestoreVanillaPortraitLayout(object panel)
        {
            if (panel == null)
            {
                _portraitLayoutApplied = false;
                _portraitLayoutPanel = null;
                return;
            }

            object title = FindElement(panel, "EuropeDiplomacyTitle");
            if (title != null)
            {
                SetProperty(title, "Text", VanillaAlliesTitle());
            }

            if (!_portraitLayoutApplied || !ReferenceEquals(_portraitLayoutPanel, panel))
            {
                _portraitLayoutApplied = false;
                _portraitLayoutPanel = null;
                return;
            }

            for (int slot = 0; slot < 6; slot++)
            {
                string suffix = slot.ToString(CultureInfo.InvariantCulture);
                float faceLeft = 60f + 100f * slot;
                SetThicknessPropertyIfPresent(FindElement(panel, "EuropeDiplomacyFaceBackground" + suffix), faceLeft, 60f);
                SetThicknessPropertyIfPresent(FindElement(panel, "EuropeDiplomacyFace" + suffix), faceLeft, 60f);
                SetThicknessPropertyIfPresent(FindElement(panel, "EuropeDiplomacyHumanBorder" + suffix), faceLeft, 60f);
                SetThicknessPropertyIfPresent(FindElement(panel, "EuropeDiplomacyRequested" + suffix), faceLeft + 60f, 48f);
                SetThicknessPropertyIfPresent(FindElement(panel, "EuropeDiplomacyHelp" + suffix), faceLeft + 60f, 120f);
                SetThicknessPropertyIfPresent(FindElement(panel, "EuropeDiplomacyOrdersPip" + suffix), faceLeft - 8f, 120f);
            }

            SetVisibilityIfPresent(FindElement(panel, "EuropeDiplomacySlot6"), false);
            SetVisibilityIfPresent(FindElement(panel, "EuropeDiplomacyBreak"), false);
            SetVisibilityIfPresent(FindElement(panel, "EuropeDiplomacyOrders"), true);
            SetVisibilityIfPresent(FindElement(panel, "EuropeDiplomacySend"), true);
            SetVisibilityIfPresent(FindElement(panel, "EuropeDiplomacyRequest"), true);
            _portraitLayoutApplied = false;
            _portraitLayoutPanel = null;
        }

        private string StatusText(RelationState relation, CoalitionState coalition)
        {
            return NegotiationStatusText(relation, coalition);
        }

        private string DemandText(GoodsDemand demand)
        {
            return DiplomacyDemandText(demand);
        }

        // Caller holds the existing diplomacy state gate. A recorded request
        // is evidence, never authority to enqueue, debit goods or award favor.
        private bool RequestHistoryOwnerReady()
        {
            return Monitor.IsEntered(_gate) && _mapActive && !_mapDisabled &&
                !_mapBootstrapPending && !_mpConsentMode && !_mpBootstrapRequested &&
                !_mpQuarantined && DiplomacyLogicalClock.IsValidSavedDay(_lastProcessedDay) &&
                !string.IsNullOrEmpty(_mapFingerprint) &&
                !string.IsNullOrEmpty(_rosterFingerprint) && _roster.Contains(_localPlayer);
        }

        // Request-bound gift API for authenticated request-aware ingress only.
        // This does not claim any vanilla/native Ally_SendGoods completion:
        // it performs the existing Mod-owned atomic two-cell journal itself.
        // Caller already holds _gate and must not also dispatch a native gift.
        internal bool TryExecuteRequestBoundModGift(ulong requestId,
            long expectedGeneration, string expectedMapFingerprint,
            string expectedRosterFingerprint, int targetPlayer, int goodId,
            int amount, out string failure)
        {
            failure = "request_bound_transfer_not_authorized";
            if (expectedGeneration != _sessionGeneration ||
                !string.Equals(expectedMapFingerprint, _mapFingerprint, StringComparison.Ordinal) ||
                !string.Equals(expectedRosterFingerprint, _rosterFingerprint, StringComparison.Ordinal) ||
                !RequestHistoryOwnerReady() || !NativeAuthorized() ||
                _requestHistory == null || !_playerCoalition.ContainsKey(targetPlayer) ||
                !IsAlivePlayer(targetPlayer) || targetPlayer == _localPlayer ||
                !IsSendableGood(goodId) || amount < 1 || amount > 1000000)
                return false;

            if (!TryAtomicTransferFrom(_localPlayer, targetPlayer, goodId, amount,
                    out failure, DiplomacyTransferPurpose.Gift, _lastProcessedDay,
                    requestId))
                return false;

            NativeTransferProbe probe = _nativeTransferProbe;
            if (!HasVerifiedOwnedRequestGiftJournal(probe) ||
                probe.BoundRequestId != requestId)
            {
                string undo = AbortNativeTransferProbe();
                if (undo != null)
                    DisableGoodsTransfers("bound_gift_unverified_rollback:" + undo);
                failure = undo == null ? "bound_gift_not_two_cell_verified" :
                    "bound_gift_rollback_pending";
                return false;
            }

            probe.CompletionObserved = true;
            probe.CompletionPostInventoryExact = true;
            probe.CompletionSenderAmount = probe.Progress.SenderAfter;
            probe.CompletionRecipientAmount = probe.Progress.RecipientAfter;
            CompleteVerifiedNativeTransfer(probe, "request_bound_mod_atomic");
            failure = null;
            return true;
        }

        internal bool TryCaptureDiplomaticRequest(DiplomaticRequestKind kind,
            DiplomaticRequestOrigin origin, int requester, int counterparty,
            int subject, int good, int amount, int opcode, out ulong requestId)
        {
            requestId = 0;
            if (!RequestHistoryOwnerReady())
                return false;
            if (_requestHistory == null)
                _requestHistory = new DiplomaticRequestHistory(_mapFingerprint, _rosterFingerprint);
            if (_requestHistory.MapFingerprint != _mapFingerprint ||
                _requestHistory.RosterFingerprint != _rosterFingerprint)
                return false;
            var members = _roster.ToDictionary(player => player, player => 0);
            return _requestHistory.TryCapture(kind, origin, requester, counterparty,
                subject, good, amount, opcode, _lastProcessedDay, members, _localPlayer,
                out requestId);
        }

        internal bool TryDecideDiplomaticRequest(ulong requestId, bool allowed,
            DiplomaticRequestDecisionEvidence evidence)
        {
            return RequestHistoryOwnerReady() && _requestHistory != null &&
                _requestHistory.TryDecide(requestId, allowed, evidence, _lastProcessedDay);
        }

        internal bool TryNoteDiplomaticRequestForwarded(ulong requestId)
        {
            return RequestHistoryOwnerReady() && _requestHistory != null &&
                _requestHistory.TryForward(requestId, _lastProcessedDay);
        }

        // The request ingress may call this only for a causally bound original reply.
        // It preserves the initial policy decision and never credits success.
        internal bool TryNoteDiplomaticRequestObservedApproval(ulong requestId)
        {
            return RequestHistoryOwnerReady() && _requestHistory != null &&
                _requestHistory.TryObserveApproval(requestId, _lastProcessedDay);
        }

        internal bool TryNoteDiplomaticRequestRefused(ulong requestId)
        {
            return RequestHistoryOwnerReady() && _requestHistory != null &&
                _requestHistory.TryObserveRefusal(requestId, _lastProcessedDay);
        }

        internal bool TryNoteDiplomaticRequestCancelled(ulong requestId,
            bool nativeCancellationIndependentlyObserved)
        {
            if (!RequestHistoryOwnerReady() || _requestHistory == null ||
                (nativeCancellationIndependentlyObserved && !IsNativeDiplomacySectionHeld))
                return false;
            return _requestHistory.TryCancel(requestId,
                nativeCancellationIndependentlyObserved, _lastProcessedDay);
        }

        internal bool TryNoteDiplomaticRequestExpired(ulong requestId)
        {
            return RequestHistoryOwnerReady() && _requestHistory != null &&
                _requestHistory.TryExpire(requestId, _lastProcessedDay);
        }

        // No real goods reconciliation is exposed: the existing NativeTransferProbe
        // carries no durable original-gift receipt identity. This must be bound by
        // the existing native journal owner before a request can be fulfilled.

        // Only retires unconfirmed tracking after seven observed logical days.
        // No original command is cancelled, retried or interpreted as successful.
        internal int DrainExpiredDiplomaticRequests()
        {
            return RequestHistoryOwnerReady() && _requestHistory != null ?
                _requestHistory.ExpireUnresolved(_lastProcessedDay) : 0;
        }

        internal DiplomaticRequestHistory SnapshotDiplomaticRequestHistory()
        {
            if (!Monitor.IsEntered(_gate))
                return null;
            return _requestHistory == null ? null : _requestHistory.Copy();
        }

        private LegacyDiplomacySave SnapshotSave()
        {
            PrepareCanonicalSaveDay();
            var state = new LegacyDiplomacySave
            {
                MapFingerprint = _mapFingerprint,
                RosterFingerprint = _rosterFingerprint,
                LocalPlayerId = _localPlayer,
                LastProcessedDay = _lastProcessedDay,
                NativeDayAnchor = _lastObservedNativeDay,
                FrozenPrices = new Dictionary<int, int>(_frozenPrices),
                TeamPartition = CurrentSavedPartition(),
                OriginalTeamPartition = new Dictionary<int, int>(_originalTeamPartition),
                PendingTransfer = CopyPendingTransfer(_nativeTransferProbe),
                PendingPackage = DiplomacyTransferRecovery.Copy(_pendingPackage),
                MapIdentity = _mapIdentity == null ? null : _mapIdentity.Copy(),
                TradeArchive = _archive == null ? null : _archive.Copy(),
                AidMemory = _aidMemory == null ? null : _aidMemory.Copy(),
                TradeMailbox = _mailbox == null ? null : _mailbox.Copy(),
                RequestHistory = _requestHistory == null ? null : _requestHistory.Copy(),
                CanonicalTradeState = CanonicalSaveBytes(),
                PendingTeamTransition = CopyPendingTeamTransition(_pendingTeamTransition),
            };
            state.Coalitions.AddRange(_coalitions.Values.Select(CopyCoalition));
            state.Relations.AddRange(_relations.Values.Select(CopyRelation));
            return state;
        }

        private void BeginStableSaveSection(string reason, IDictionary<int, int> logicalTeamPartition)
        {
            if (_stableSavePayload != null)
            {
                throw new InvalidOperationException("nested diplomacy stable-save section is not allowed");
            }

            LegacyDiplomacySave state = SnapshotSave();
            if (logicalTeamPartition != null)
            {
                state.TeamPartition = new Dictionary<int, int>(logicalTeamPartition);
            }

            _stableSavePayload = DiplomacyPersistenceCodec.Serialize(state, _rules);
            _stableSaveReason = string.IsNullOrEmpty(reason) ? "native_operation" : reason;
        }

        private void EndStableSaveSection()
        {
            _stableSavePayload = null;
            _stableSaveReason = null;
        }

        private LegacyDiplomacySave SnapshotRecoverySave()
        {
            LegacyDiplomacySave state = SnapshotSave();
            // DisableMap deliberately rolls native teams back before leaving
            // the feature inactive. If an inventory rollback could not be
            // proven, however, its journal still has to survive the game's
            // save callback. Persist the last managed relationship partition
            // instead of combining those relationships with the temporarily
            // restored native teams. RestoreSave will transactionally rebuild
            // this partition before reconciling the transfer journal.
            state.TeamPartition = BuildTeamPartition(null, null, false);
            // This recovery projection abandons native team intent. Its
            // submission consent must end as well, on SnapshotSave's detached
            // relation clones only. A waiting live request remains untouched.
            foreach (RelationState relation in state.Relations)
                DiplomacySubmission.CancelAcceptedTeamTransition(relation.ExtendedState, state.LastProcessedDay);
            state.PendingTeamTransition = null;
            return state;
        }

        private LegacyDiplomacySave CaptureInventoryRecoveryBaseState()
        {
            // Validate and detach a coherent model before the first native
            // write. This seed survives a later unrelated serialization fault.
            if (_canonicalTransition != null && _pendingPackage == null && _canonicalRecoverySeed != null)
                return DiplomacyPersistenceCodec.Deserialize(DiplomacyPersistenceCodec.Serialize(_canonicalRecoverySeed, _rules), _rules);
            return DiplomacyPersistenceCodec.Deserialize(DiplomacyPersistenceCodec.Serialize(SnapshotSave(), _rules), _rules);
        }

        private bool TrySerializeInventoryRecovery(out byte[] payload)
        {
            payload = null;
            if (_inventoryRecoveryBaseState == null || (_nativeTransferProbe == null && _pendingPackage == null))
                return false;
            try
            {
                // A broken current model may not award gifts on the next load.
                // Preserve the original model, current checkpoints and explicit
                // undo intent; never substitute a journal-free last-good save.
                if (_nativeTransferProbe != null)
                    _nativeTransferProbe.RollbackPending = true;
                if (_pendingPackage != null)
                    _pendingPackage.RollbackPending = true;
                LegacyDiplomacySave recovery = DiplomacyTransferRecovery.RecoverySnapshot(_inventoryRecoveryBaseState, CopyPendingTransfer(_nativeTransferProbe), _pendingPackage, _rules);
                payload = DiplomacyPersistenceCodec.Serialize(recovery, _rules);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private void RestoreSave(byte[] payload)
        {
            LegacyDiplomacySave saved;
            try
            {
                saved = DiplomacyPersistenceCodec.Deserialize(payload, _rules);
            }
            catch (Exception exception)
            {
                throw new InvalidDataException("diplomacy save is corrupt or incompatible", exception);
            }

            RestoreMapIdentity(saved, payload);
            bool currentMapFingerprint = string.Equals(saved.MapFingerprint, _mapFingerprint, StringComparison.Ordinal);
            bool legacyMapFingerprint = !currentMapFingerprint && string.Equals(saved.MapFingerprint, _legacyMapFingerprint, StringComparison.Ordinal);
            if ((!currentMapFingerprint && !legacyMapFingerprint) || !string.Equals(saved.RosterFingerprint, _rosterFingerprint, StringComparison.Ordinal) || saved.LocalPlayerId != _localPlayer)
            {
                throw new InvalidDataException("diplomacy save belongs to another map or roster" + 
#if DIPLOMACY_PRIVATE_TESTS
                " savedMap=" + saved.MapFingerprint + " actualMap=" + _mapFingerprint + " savedRoster=" + saved.RosterFingerprint + " actualRoster=" + _rosterFingerprint + 
#endif
                "");
            }

            var roster = new HashSet<int>(_roster);
            var aiRoster = new HashSet<int>(_roster.Where(player => player != _localPlayer));
            int[] savedMembers = saved.Coalitions.SelectMany(coalition => coalition.MemberPlayerIds).ToArray();
            if (!new HashSet<int>(saved.TeamPartition.Keys).SetEquals(roster) || savedMembers.Length != savedMembers.Distinct().Count() || !new HashSet<int>(savedMembers).SetEquals(aiRoster))
            {
                throw new InvalidDataException("diplomacy save player partition does not match the roster");
            }

            if (legacyMapFingerprint && !LegacySaveMatchesNativePartition(saved))
            {
                throw new InvalidDataException("legacy diplomacy save does not match the native team state");
            }

            _originalTeamPartition.Clear();
            foreach (KeyValuePair<int, int> original in saved.OriginalTeamPartition)
            {
                _originalTeamPartition.Add(original.Key, original.Value);
            }

            if (legacyMapFingerprint)
            {
                Log("saving", "status=legacy_map_fingerprint_migrated");
            }

            var savedRelations = saved.Relations.ToDictionary(relation => relation.CoalitionId, StringComparer.Ordinal);
            int localSavedTeam = saved.TeamPartition[_localPlayer];
            foreach (CoalitionState coalition in saved.Coalitions)
            {
                RelationState relation = savedRelations[coalition.CoalitionId];
                int[] memberTeams = coalition.MemberPlayerIds.Select(player => saved.TeamPartition[player]).ToArray();
                bool internallyUnited = memberTeams.Distinct().Count() == 1;
                bool alliedByTeam = localSavedTeam > 0 && internallyUnited && memberTeams[0] == localSavedTeam;
                if (!internallyUnited || coalition.Allied != relation.Allied || relation.Allied != alliedByTeam)
                {
                    throw new InvalidDataException("diplomacy save coalition/team state is inconsistent");
                }
            }

            // Diplomacy prices are frozen at the original map start and are
            // therefore save-authoritative. A later runtime/profile update
            // must not revalue an already issued demand or gift history.
            _frozenPrices.Clear();
            foreach (KeyValuePair<int, int> price in saved.FrozenPrices)
            {
                _frozenPrices.Add(price.Key, price.Value);
            }

            bool containsTeamZero = saved.TeamPartition.Values.Any(team => team == 0);
            Dictionary<int, int> livePartition = CurrentSavedPartition();
            bool livePartitionMatches = saved.TeamPartition.All(entry => livePartition[entry.Key] == entry.Value);
            bool pendingTeamWasNativeApplied = false;
            if (saved.PendingTeamTransition != null)
            {
                DiplomacyTeamJournalDisposition disposition = DiplomacyJournalRecovery.ClassifyTeamTransition(livePartition, saved.PendingTeamTransition);
                pendingTeamWasNativeApplied = disposition == DiplomacyTeamJournalDisposition.TargetApplied;
                if (disposition == DiplomacyTeamJournalDisposition.Conflict)
                {
                    throw new InvalidDataException("saved team transition journal does not match the native save");
                }
            }
            else if (containsTeamZero && !livePartitionMatches)
            {
                throw new InvalidDataException("raw team-zero save state does not match the native save");
            }

            if (!pendingTeamWasNativeApplied && !containsTeamZero && !livePartitionMatches)
            {
                if (!ApplyTeamPartition(saved.TeamPartition, "save_restore"))
                {
                    throw new InvalidOperationException("saved diplomacy team partition was rejected");
                }
            }

            Dictionary<int, int> attestedPartition = pendingTeamWasNativeApplied ? livePartition : CurrentSavedPartition();
            IReadOnlyDictionary<int, int> expectedAttestedPartition = pendingTeamWasNativeApplied ? saved.PendingTeamTransition.TargetPartition : saved.TeamPartition;
            if (!attestedPartition.OrderBy(entry => entry.Key).SequenceEqual(expectedAttestedPartition.OrderBy(entry => entry.Key)))
            {
                throw new InvalidOperationException("attested save partition changed before ownership adoption");
            }

            RebuildTeamOwnership(attestedPartition);
            if (!pendingTeamWasNativeApplied)
            {
                int restoredTribes = QuarantineAlliedTribes(saved.TeamPartition);
                int restoredProjectiles = QuarantineAlliedProjectiles(saved.TeamPartition);
                int restoredBuildingFires = QuarantineAlliedBuildingFires(saved.TeamPartition);
                int restoredAiTargets = QuarantineAlliedAiTargets(saved.TeamPartition);
                if (!LivePartitionMatches(saved.TeamPartition))
                {
                    throw new InvalidOperationException("saved diplomacy team partition changed during quarantine");
                }

                Log("team_transaction", "status=save_partition_quarantined|tribes_stopped=" + restoredTribes.ToString(CultureInfo.InvariantCulture) + "|projectiles_deleted=" + restoredProjectiles.ToString(CultureInfo.InvariantCulture) + "|building_fires_cleared=" + restoredBuildingFires.ToString(CultureInfo.InvariantCulture) + "|ai_targets_cleared=" + restoredAiTargets.ToString(CultureInfo.InvariantCulture));
            }

            _coalitions.Clear();
            _playerCoalition.Clear();
            foreach (CoalitionState coalition in saved.Coalitions)
            {
                _coalitions.Add(coalition.CoalitionId, coalition);
                foreach (int player in coalition.MemberPlayerIds)
                {
                    _playerCoalition.Add(player, coalition.CoalitionId);
                }
            }

            _relations.Clear();
            foreach (RelationState relation in saved.Relations)
            {
                _relations.Add(relation.CoalitionId, relation);
                if (relation.ExtendedState != null && !relation.Allied && DiplomacyCombatRules.TreatyProtects(relation.Allied, relation.ExtendedState.Agreement, relation.ExtendedState.AgreementUntilDay, saved.LastProcessedDay))
                    _pendingAgreementSafety.Add(relation.CoalitionId);
            }

            if (!pendingTeamWasNativeApplied)
            {
                _transitionVerificationPartition = new Dictionary<int, int>(saved.TeamPartition);
                _transitionVerificationStartedTick = ReadSimulationTick();
            }

            _lastProcessedDay = saved.LastProcessedDay;
            // Old schemas had no native anchor. Seed without manufacturing a
            // day;  preserves a genuine pending day boundary exactly once.
            _lastObservedNativeDay = saved.NativeDayAnchor >= 0 ? saved.NativeDayAnchor : ScriptExtenderNativeBridge.CurrentDay();
            _pendingTeamTransition = CopyPendingTeamTransition(saved.PendingTeamTransition);
            _pendingTransitionGeneration = _pendingTeamTransition == null ? 0 : _sessionGeneration;
            _pendingTransitionFingerprint = _pendingTeamTransition == null ? null : _mapFingerprint;
            _inventoryRecoveryBaseState = DiplomacyPersistenceCodec.Deserialize(payload, _rules);
            // Restore the exact owner-scoped request serial and records BEFORE
            // any cold journal reconciliation is allowed to award a receipt.
            _requestHistory = saved.RequestHistory == null ?
                new DiplomaticRequestHistory(_mapFingerprint, _rosterFingerprint) : saved.RequestHistory.Copy();
            if (saved.PendingTransfer != null)
            {
                _nativeTransferProbe = RearmSavedTransferProbe(saved.PendingTransfer);
                _inventoryRestorePending = true;
#if DIPLOMACY_PRIVATE_TESTS
                if (saved.PendingTransfer.Purpose == DiplomacyTransferPurpose.Gift && saved.PendingTransfer.SenderPlayer > 0 && saved.PendingTransfer.SenderPlayer != _localPlayer)
                {
                    _unattestedSavedAid = true;
                    _goodsTransfersUnavailable = true;
                    Log("aid_runtime", "status=loaded_prepared_aid_quarantined|native_credit=false|native_refund=false");
                }
                else
#endif
                    ReconcileSavedTransferProbe();
            }

            // Restore barter identity/relationships BEFORE any pending native
            // package can resume/complete after a SaveData callback.
            RestoreTradeArchive(saved.TradeArchive);
            RestoreTradeRuntime(saved.TradeMailbox);
            RestoreCanonicalState(saved.CanonicalTradeState);
            // The aid ledger only restores information, not game goods,
            // Native packages, goodwill changes or military orders.
            if (saved.AidMemory != null)
            {
                AidPromiseLedgerCodec.ValidateScope(saved.AidMemory, _mapFingerprint, _rosterFingerprint, Math.Max(1, _lastProcessedDay));
                _aidMemory = saved.AidMemory.Copy();
            }
            else
                _aidMemory = null;
            RestorePendingPackage(saved.PendingPackage);
            if (_nativeTransferProbe == null && _pendingPackage == null)
                _inventoryRecoveryBaseState = null;
            if (pendingTeamWasNativeApplied)
            {
                ProcessPendingTeamTransition("save_journal");
            }

            RebuildSavedTeamOwnership();
            Log("saving", "status=restored|day=" + _lastProcessedDay.ToString(CultureInfo.InvariantCulture) + "|clock=logical");
        }

        private void ReconcileSavedTransferProbe()
        {
#if DIPLOMACY_PRIVATE_TESTS
            if (_unattestedSavedAid)
                return;
#endif
            NativeTransferProbe probe = _nativeTransferProbe;
            if (probe == null)
            {
                _inventoryRestorePending = false;
                return;
            }

            // A stored progress flag cannot attest the native world after
            // cold load: the game save and Mod payload are not atomic.
            // Bound gifts must use the exact live two-cell classification below.
            if (probe.BoundRequestId == 0 && probe.ModGiftReceiptId == 0 &&
                DiplomacyTransferRecovery.HasCompletedNativeWrites(CopyPendingTransfer(probe)))
            {
                _inventoryRestorePending = false;
                CompleteVerifiedNativeTransfer(probe, "save_journal");
                return;
            }

            IDisposable lease;
            if (!TryEnterNativeInventorySection(out lease))
            {
                _inventoryRestorePending = true;
                _goodsTransfersUnavailable = true;
                return;
            }

            DiplomacyTransferJournalDisposition disposition;
            using (lease)
                disposition = DiplomacyJournalRecovery.ClassifyTransfer(CopyPendingTransfer(probe), GoodAmount(ProbeSender(probe), probe.GoodId), GoodAmount(probe.TargetPlayer, probe.GoodId));
            _inventoryRestorePending = false;
            if (!probe.RollbackPending && disposition == DiplomacyTransferJournalDisposition.FullyApplied && (probe.Progress == null || probe.Progress.SenderTouched && probe.Progress.RecipientTouched))
            {
                CompleteVerifiedNativeTransfer(probe, "save_journal");
                _goodsTransfersUnavailable = false;
            }
            else if (probe.RollbackPending || probe.DirectAtomicTransfer || disposition != DiplomacyTransferJournalDisposition.Unchanged)
            {
                bool rearmNative = !probe.RollbackPending && !probe.DirectAtomicTransfer && disposition != DiplomacyTransferJournalDisposition.Conflict;
                string failure = AbortNativeTransferProbe();
                if (failure == "native_inventory_busy")
                    _goodsTransfersUnavailable = true;
                else if (failure != null)
                    DisableGoodsTransfers(failure);
                else if (rearmNative)
                {
                    probe.RollbackPending = false;
                    probe.StartedTick = ReadSimulationTick();
                    _nativeTransferProbe = probe;
                }
                else if (probe.DirectAtomicTransfer && probe.Purpose == DiplomacyTransferPurpose.Gift)
                    ShowMessage(Text("Eine beim Speichern noch unvollständige Gütersendung wurde zurückgenommen. Ihr könnt sie erneut senden.", "A goods transfer that was incomplete when saving has been rolled back. You can send it again."));
            }

            Log("transfer", "status=save_journal_reconciled|disposition=" + disposition.ToString() + "|rollback_pending=" + BoolText(_nativeTransferProbe != null && _nativeTransferProbe.RollbackPending));
        }

        private bool LegacySaveMatchesNativePartition(LegacyDiplomacySave saved)
        {
            Dictionary<int, int> livePartition = CurrentSavedPartition();
            if (saved.PendingTeamTransition != null)
            {
                return DiplomacyJournalRecovery.ClassifyTeamTransition(livePartition, saved.PendingTeamTransition) != DiplomacyTeamJournalDisposition.Conflict;
            }

            return saved.TeamPartition.All(entry =>
            {
                int liveTeam;
                return livePartition.TryGetValue(entry.Key, out liveTeam) && liveTeam == entry.Value;
            });
        }

        private void PauseRestoredSession()
        {
            GamePlayerManagerAPI players = GamePlayerManagerAPI.Instance;
            bool alreadyPaused = players.IsLocalPaused();
            if (!alreadyPaused)
            {
                players.SetLocalPaused(true);
            }

            if (!players.IsLocalPaused())
            {
                throw new InvalidOperationException("restored diplomacy session could not be paused safely");
            }

            Log("saving", "status=restore_paused|already_paused=" + BoolText(alreadyPaused));
        }

        private Dictionary<int, int> CurrentSavedPartition()
        {
            var result = new Dictionary<int, int>();
            foreach (int player in _roster)
            {
                result[player] = _teamPort.GetTeam(player);
            }

            return result;
        }

        private void RebuildSavedTeamOwnership()
        {
            RebuildTeamOwnership(CurrentSavedPartition());
        }

        private void RebuildTeamOwnership(IReadOnlyDictionary<int, int> current)
        {
            if (current == null || !new HashSet<int>(current.Keys).SetEquals(_originalTeamPartition.Keys))
            {
                throw new InvalidOperationException("saved original team ownership does not match the roster");
            }

            _ownedAssignedTeams.Clear();
            _ownedOriginalTeams.Clear();
            foreach (KeyValuePair<int, int> entry in current)
            {
                int original = _originalTeamPartition[entry.Key];
                if (entry.Value == original)
                {
                    continue;
                }

                _ownedOriginalTeams.Add(entry.Key, original);
                _ownedAssignedTeams.Add(entry.Key, entry.Value);
            }

            Log("team_transaction", "status=save_ownership_rebuilt|owned=" + _ownedAssignedTeams.Count.ToString(CultureInfo.InvariantCulture));
        }

        private void DisableMap(string stage, Exception exception, bool expectedTeardown = false)
        {
            lock (_gate)
            {
                ResetLordChatSession();
                ResetGoodsRequests();
                _sessionGeneration++;
                _dialogQueue.Clear();
                ClearDeferredDiplomacyUi();
                _mapBootstrapPending = false;
                _mapBootstrapTrigger = null;
                _pendingGoodsTarget = 0;
                string uiFailure = null;
                try
                {
                    if (CanPresentDiplomacyUi)
                        RestoreVanillaPortraitLayout(CurrentAlliesPanel());
                    else
                        _deferredPortraitRestore = true;
                }
                catch (Exception uiException)
                {
                    uiFailure = uiException.GetType().Name + ":" + Safe(uiException.Message);
                }

                IDisposable rollbackLease;
                if (!TryEnterNativeInventorySection(out rollbackLease))
                {
                    // Retain ownership and the independent damage guard until
                    // the engine releases its current tick. No native read or
                    // write is allowed in this branch, including pause flags.
                    bool firstDeferral = _deferredDisableStage == null;
                    _deferredDisableStage = stage;
                    _deferredDisableException = exception;
                    _deferredDisableExpectedTeardown = expectedTeardown;
                    _deferredDisableEpoch = _mapEpoch;
                    _disabledTeamRecoveryPending = _ownedAssignedTeams.Count > 0;
                    _rollbackGuardActive = _disabledTeamRecoveryPending;
                    _rollbackGuardBeforePartition = null;
                    _rollbackGuardAfterPartition = null;
                    _mapDisabled = true;
                    _mapActive = false;
                    PublishCombatProtectionSnapshot();
                    if (firstDeferral)
                        Log("team_transaction", "status=rollback_deferred|reason=native_engine_busy");
                    return;
                }

                using (rollbackLease)
                {
                    _deferredDisableStage = null;
                    _deferredDisableException = null;
                    bool teamRollbackRequired = _ownedAssignedTeams.Count > 0;
                    string rollbackSnapshotFailure = null;
                    Dictionary<int, int> expectedAfterRollback = teamRollbackRequired ? ArmTeamRollbackGuard(out rollbackSnapshotFailure) : null;
                    bool nativeRollbackRequired = teamRollbackRequired || _nativeTransferProbe != null || _pendingPackage != null;
                    string pauseFailure = null;
                    if (nativeRollbackRequired)
                    {
                        try
                        {
                            GamePlayerManagerAPI players = GamePlayerManagerAPI.Instance;
                            if (!players.IsLocalPaused())
                            {
                                players.SetLocalPaused(true);
                            }

                            if (!players.IsLocalPaused())
                            {
                                pauseFailure = "pause_readback_mismatch";
                            }
                        }
                        catch (Exception pauseException)
                        {
                            pauseFailure = pauseException.GetType().Name + ":" + Safe(pauseException.Message);
                        }
                    }

                    string transferFailure;
                    string teamFailure;
                    if (pauseFailure == null)
                    {
                        transferFailure = AbortNativeTransferProbe();
                        string packageFailure = AbortPendingPackage();
                        if (packageFailure != null)
                            transferFailure = (transferFailure ?? "") + ";" + packageFailure;
                        teamFailure = DiplomacyTeamTransaction.RollbackOwned(_teamPort, _ownedAssignedTeams, _ownedOriginalTeams);
                    }
                    else
                    {
                        transferFailure = _nativeTransferProbe == null && _pendingPackage == null ? null : "not_attempted_without_pause";
                        teamFailure = teamRollbackRequired ? "not_attempted_without_pause" : null;
                    }

                    bool teamStable = !teamRollbackRequired;
                    if (teamRollbackRequired && expectedAfterRollback != null && pauseFailure == null)
                    {
                        try
                        {
                            teamStable = LivePartitionMatches(expectedAfterRollback);
                        }
                        catch (Exception readbackException)
                        {
                            teamFailure = (teamFailure == null ? string.Empty : teamFailure + ";") + "readback=" + readbackException.GetType().Name + ":" + Safe(readbackException.Message);
                        }
                    }

                    if (rollbackSnapshotFailure != null)
                    {
                        teamFailure = (teamFailure == null ? string.Empty : teamFailure + ";") + "snapshot=" + rollbackSnapshotFailure;
                    }

                    if (teamRollbackRequired && !teamStable)
                    {
                        teamFailure = (teamFailure == null ? string.Empty : teamFailure + ";") + "stable_readback_unproven";
                    }

                    if (teamStable)
                    {
                        _ownedAssignedTeams.Clear();
                        _ownedOriginalTeams.Clear();
                    }

                    _disabledTeamRecoveryPending = !teamStable;
                    ResetCombatSafetyMapState(!teamStable);
                    // UI and normal diplomacy callbacks become inactive only
                    // after every native rollback write and its exact readback.
                    // A failed readback retains the independent rollback guard.
                    _mapDisabled = true;
                    _mapActive = false;
                    PublishCombatProtectionSnapshot();
                    bool cleanTeardown = expectedTeardown && pauseFailure == null && uiFailure == null && transferFailure == null && teamFailure == null && teamStable;
                    _mapFailureLatched = !cleanTeardown;
                    if (cleanTeardown)
                    {
                        Log("activation", "status=map_teardown_rollback_complete" + "|stage=" + Safe(stage) + "|team_stable=true");
                    }
                    else
                    {
                        _error(RuntimeLogPrefix + "|event=fail_closed" + "|status=aborted|stage=" + Safe(stage) + "|reason=" + Safe((exception == null ? "Unknown:unspecified" : exception.GetType().Name + ":" + exception.Message)) + "|pause=" + Safe(pauseFailure ?? "ok") + "|ui_rollback=" + Safe(uiFailure ?? "ok") + "|transfer_rollback=" + Safe(transferFailure ?? "ok") + "|team_rollback=" + Safe(teamFailure ?? "ok") + "|team_stable=" + BoolText(teamStable) + "|seq=" + Interlocked.Increment(ref _logSequence).ToString(CultureInfo.InvariantCulture));
                    }
                }
            }
        }

        private void ClearMapState()
        {
            ResetLordChatSession();
#if DIPLOMACY_PRIVATE_TESTS && HOST_DIRECTED_AI_TRIBE_CANARY
            ResetHostDirectedAiTribeForMap();
#endif
            CancelPendingGoodsCommand("map_reset");
            _dialogQueue.Clear();
            ClearDeferredDiplomacyUi();
            _deferredDisableStage = null;
            _deferredDisableException = null;
            _dialogCallbackInProgress = false;
            _dialogRetryNotBeforeTick = 0;
            _saveFailurePending = null;
            _saveSuppressed = false;
            for (int lane = 0; lane < LaneCount; lane++)
            {
                Interlocked.Exchange(ref _laneThread[lane], 0);
                Interlocked.Exchange(ref _laneSecondThread[lane], 0);
                Interlocked.Exchange(ref _laneEntries[lane], 0);
                Interlocked.Exchange(ref _laneThreadDrift[lane], 0);
                _laneStackAlign[lane] = -1;
            }

            Interlocked.Exchange(ref _laneReportPending, 0);
            _laneNextReportTimestamp = 0;
            _combatGateSkipsLogged = 0;
            _combatSnapshotBlocksLogged = 0;
            _combatSnapshotFaultsLogged = 0;
            _combatGateSkipLogAt = 0;
            _laneSimulationLogged = false;
            _laneDetourLogged = false;
            _simulationThreadId = 0;
            _detourThreadId = 0;
            _frameStackAlign = -1;
            _simulationStackAlign = -1;
            _detourStackAlign = -1;
            _roster.Clear();
            _coalitions.Clear();
            _relations.Clear();
            _requestHistory = null;
            _playerCoalition.Clear();
            _capturedLordIds.Clear();
            _frozenPrices.Clear();
            _originalTeamPartition.Clear();
            _visiblePlayers.Clear();
            _mapFingerprint = null;
            _legacyMapFingerprint = null;
            _rosterFingerprint = null;
            _localPlayer = 0;
            _lastProcessedDay = -1;
            _selectedVisibleIndex = 0;
            _goodsTransfersUnavailable = false;
            _uiExtensionWarningLogged = false;
            _uiRefreshDegradedLogged = false;
            _uiRosterSignature = null;
            _portraitLayoutPanel = null;
            _portraitLayoutApplied = false;
            _disabledTeamRecoveryPending = false;
            _stableSavePayload = null;
            _stableSaveReason = null;
            _coalitionValueLogSignatures.Clear();
            _inactiveCoalitionsLogged.Clear();
            _mapIdentity = null;
            RevokeOriginalMpLifecycle(true);
            // Actual map replacement is distinct from transient rejoin.
            _mpBootstrapRequested = false;
            _mpQuarantined = false;
            ResetTradeUiForMap();
            ResetTradeSession();
            ResetTradeRuntime();
            ResetRuntimeMapState();
            ResetCombatSafetyMapState();
        }

        private int AbsoluteGameDay()
        {
            if (!DiplomacyLogicalClock.IsValidSavedDay(_lastProcessedDay))
            {
                throw new InvalidOperationException("diplomacy logical calendar is not initialized");
            }

            return _lastProcessedDay;
        }

        private string RosterMaterial()
        {
            var builder = new StringBuilder("roster|local=").Append(_localPlayer.ToString(CultureInfo.InvariantCulture));
            foreach (int player in _roster.OrderBy(value => value))
            {
                builder.Append('|').Append(player.ToString(CultureInfo.InvariantCulture));
                builder.Append(':').Append(GameData.Instance.lastGameState.is_skirmish_player(player) ? "ai" : "human");
                if (player != _localPlayer)
                {
                    builder.Append(':').Append(_capturedLordIds[player].ToString(CultureInfo.InvariantCulture));
                }
            }

            return builder.ToString();
        }

        private static string MapMaterial()
        {
            GameData data = GameData.Instance;
            var builder = new StringBuilder(StrongholdEuropeDE.Runtime.CompatibilityContracts.LegacyMapFingerprintDomain);
            string mapName = data.currentMapName ?? "";
            builder.Append("|name=").Append(mapName.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(mapName);
            builder.Append("|mode=").Append(((int)data.mapType).ToString(CultureInfo.InvariantCulture));
            builder.Append("|size=").Append(GameMap.tilemapSize.ToString(CultureInfo.InvariantCulture));
            builder.Append("|game=").Append(data.game_type.ToString(CultureInfo.InvariantCulture));
            builder.Append("|mission=").Append(data.mission_level.ToString(CultureInfo.InvariantCulture));
            builder.Append("|text=").Append(data.mission_text_id.ToString(CultureInfo.InvariantCulture));
            builder.Append("|skirmish=").Append(data.SkirmishGameType.ToString(CultureInfo.InvariantCulture));
            builder.Append("|difficulty=").Append(((int)data.difficulty_level).ToString(CultureInfo.InvariantCulture));
            for (int slot = 0; slot < 8; slot++)
            {
                builder.Append("|keep").Append(slot.ToString(CultureInfo.InvariantCulture)).Append('=').Append(data.Keep_Locations[slot, 0].ToString(CultureInfo.InvariantCulture)).Append(',').Append(data.Keep_Locations[slot, 1].ToString(CultureInfo.InvariantCulture)).Append(',').Append(data.start_keep_location_order[slot].ToString(CultureInfo.InvariantCulture));
            }

            EngineInterface.ScenarioOverview overview = data.scenarioOverview;
            if (overview == null)
            {
                builder.Append("|scenario=null");
                return builder.ToString();
            }

            builder.Append("|scenario=").Append(overview.startMonth.ToString(CultureInfo.InvariantCulture)).Append(',').Append(overview.startYear.ToString(CultureInfo.InvariantCulture)).Append(',').Append(overview.scenario_start_popularity.ToString(CultureInfo.InvariantCulture)).Append(',').Append(overview.scenario_buildings_count.ToString(CultureInfo.InvariantCulture)).Append(',').Append(overview.special_start_gold.ToString(CultureInfo.InvariantCulture));
            AppendFingerprintValues(builder, "start_goods", overview.scenario_start_goods);
            AppendFingerprintValues(builder, "trader_goods", overview.scenario_trader_goods_available);
            AppendFingerprintValues(builder, "start_troops", overview.scenario_start_troops);
            AppendFingerprintValues(builder, "start_siege", overview.scenario_start_siege_equipment);
            AppendFingerprintValues(builder, "buildings", overview.scenario_buildings_available);
            builder.Append("|events=").Append(overview.entries.Count.ToString(CultureInfo.InvariantCulture));
            foreach (EngineInterface.ScenarioOverviewEntry entry in overview.entries)
            {
                builder.Append(';').Append(entry.month.ToString(CultureInfo.InvariantCulture)).Append(',').Append(entry.year.ToString(CultureInfo.InvariantCulture)).Append(',').Append(entry.entryType.ToString(CultureInfo.InvariantCulture)).Append(',').Append(entry.data1.ToString(CultureInfo.InvariantCulture)).Append(',').Append(entry.data2.ToString(CultureInfo.InvariantCulture)).Append(',').Append(entry.message.ToString(CultureInfo.InvariantCulture)).Append(',').Append(entry.repeatDuration.ToString(CultureInfo.InvariantCulture)).Append(',').Append(entry.repeatCount.ToString(CultureInfo.InvariantCulture));
            }

            return builder.ToString();
        }

        private static string LegacyMapMaterial()
        {
            return "map|" + GameData.Instance.currentMapName + "|" + ((int)GameData.Instance.mapType).ToString(CultureInfo.InvariantCulture) + "|" + GameMap.tilemapSize.ToString(CultureInfo.InvariantCulture);
        }

        private static void AppendFingerprintValues(StringBuilder builder, string label, int[] values)
        {
            if (values == null)
            {
                throw new InvalidOperationException("map fingerprint array is unavailable: " + label);
            }

            builder.Append('|').Append(label).Append('=').Append(values.Length.ToString(CultureInfo.InvariantCulture));
            foreach (int value in values)
            {
                builder.Append(',').Append(value.ToString(CultureInfo.InvariantCulture));
            }
        }

        private int SelectedPlayer(object panel)
        {
            if (panel == null)
            {
                return 0;
            }

            int[] nativeRoster = (int[])_allyField.GetValue(panel);
            int count = (int)_numAlliesField.GetValue(panel);
            int current = (int)_currentAllyField.GetValue(panel);
            return nativeRoster != null && current >= 0 && current < count && current < nativeRoster.Length ? nativeRoster[current] : 0;
        }

        private bool IsCoalitionAllied(int player)
        {
            string coalition;
            return _playerCoalition.TryGetValue(player, out coalition) && _relations.ContainsKey(coalition) && _relations[coalition].Allied;
        }

        private static bool IsAlivePlayer(int player)
        {
            if (player < 1 || player > 8)
                return false;
            // Commits already own the engine monitor and require fresh native
            // liveness. UI callbacks use Vanilla's managed PlayState snapshot;
            // this helper never grants permission for a native mutation.
            if (IsNativeDiplomacySectionHeld)
                return GamePlayerManagerAPI.Instance.GetAlivePlayerIds().Contains(player);
            GameData game = GameData.Instance;
            EngineInterface.PlayState live = game == null ? null : game.lastGameState;
            byte[] alive = live == null ? null : live.lord_alive;
            // An unavailable UI snapshot is not evidence of a ruler's death.
            // Map/session guards and the fresh commit check remain authoritative.
            return alive == null || player >= alive.Length || alive[player] != 0;
        }

        private bool TryReadTransferAmount(int player, int goodId, out int amount)
        {
            amount = 0;
            IDisposable lease;
            if (!TryEnterNativeInventorySection(out lease))
                return false;
            try
            {
                using (lease)
                {
                    amount = GoodAmount(player, goodId);
                    return amount >= 0;
                }
            }
            catch (OverflowException)
            {
                amount = 0;
                return false;
            }
        }

        private int GoodAmount(int player, int goodId)
        {
            if (goodId != 15)
            {
                return GamePlayerManagerAPI.Instance.GetGoodAmount(player, (eGoods)goodId);
            }

            return Math.Max(0, GamePlayerManagerAPI.Instance.GetPlayerGold(player));
        }

        private static bool AddGoodWithExactHookArguments(int player, int goodId, int amount)
        {
            bool preObserved = false;
            int ownerThread = Environment.CurrentManagedThreadId;
            IDisposable guard = ObservableSubscribeExtensions.Subscribe(PlayerR3EventHooks.OnPlayerAddResource.Observable, delegate (PlayerAddResourceEventArgs e)
            {
                if (e == null || e.Phase != EventHookPhase.Pre || preObserved || !DiplomacyRuntimeSafety.MatchesResourceCall(ownerThread, Environment.CurrentManagedThreadId, player, e.PlayerId, goodId, (int)e.Good, amount, e.Amount))
                {
                    return;
                }

                preObserved = true;
            });
            try
            {
#if DIPLOMACY_PRIVATE_TESTS
                Interlocked.Increment(ref _nativeWriteCalls);
                ObserveActualAidWrite(player, goodId, amount);
#endif
                GamePlayerManagerAPI.Instance.TryAddGood(player, (eGoods)goodId, amount);
            }
            finally
            {
                guard.Dispose();
            }

            if (!preObserved)
            {
                throw new InvalidOperationException("native add-good hook did not attest its exact arguments");
            }

            // A Post observer may alter the API's boolean return, so the
            // exact inventory readback in the caller is authoritative.
            return true;
        }

        private static void RemoveGoodWithExactHookArguments(int player, int goodId, int amount)
        {
            bool preObserved = false;
            int ownerThread = Environment.CurrentManagedThreadId;
            IDisposable guard = ObservableSubscribeExtensions.Subscribe(PlayerR3EventHooks.OnPlayerSubtractResource.Observable, delegate (PlayerSubtractResourceEventArgs e)
            {
                if (e == null || e.Phase != EventHookPhase.Pre || preObserved || !DiplomacyRuntimeSafety.MatchesResourceCall(ownerThread, Environment.CurrentManagedThreadId, player, e.PlayerId, goodId, (int)e.Good, amount, e.Amount))
                {
                    return;
                }

                preObserved = true;
            });
            try
            {
#if DIPLOMACY_PRIVATE_TESTS
                Interlocked.Increment(ref _nativeWriteCalls);
                ObserveActualAidWrite(player, goodId, -amount);
#endif
                GamePlayerManagerAPI.Instance.RemoveGood(player, (eGoods)goodId, amount);
            }
            finally
            {
                guard.Dispose();
            }

            if (!preObserved)
            {
                throw new InvalidOperationException("native remove-good hook did not attest its exact arguments");
            }
        }

        private bool RestoreGoodAmount(int player, int goodId, int desired)
        {
            int current = GoodAmount(player, goodId);
            int delta = desired - current;
            if (delta == 0)
            {
                return true;
            }

            if (goodId == 15)
            {
                GamePlayerManagerAPI.Instance.AddPlayerGold(player, delta);
            }
            else if (delta > 0)
            {
                AddGoodWithExactHookArguments(player, goodId, delta);
            }
            else
            {
                RemoveGoodWithExactHookArguments(player, goodId, -delta);
            }

            return GoodAmount(player, goodId) == desired;
        }

        private void RollbackObservedTransfer(NativeTransferProbe probe, int sender, int recipient, out string failure)
        {
            probe.RollbackPending = true;
            DiplomacyPendingTransfer journal = CopyPendingTransfer(probe);
            journal.SenderPlayer = ProbeSender(probe);
            journal.Progress = probe.Progress;
            bool restored = DiplomacyTransferRecovery.Rollback(journal, GoodAmount, RestoreGoodAmount);
            failure = restored ? null : "rollback_pending_inventory_conflict";
        }

        private string AbortNativeTransferProbe()
        {
#if DIPLOMACY_PRIVATE_TESTS
            if (_unattestedSavedAid)
                return "unattested_loaded_native_aid_quarantine";
#endif
            NativeTransferProbe probe = _nativeTransferProbe;
            if (probe == null)
                return null;
            IDisposable lease;
            if (!TryEnterNativeInventorySection(out lease))
                return "native_inventory_busy";
            using (lease)
                try
                {
                    string failure;
                    RollbackObservedTransfer(probe, 0, 0, out failure);
                    if (failure == null)
                    {
                        _nativeTransferProbe = null;
                        if (_pendingPackage == null)
                        {
                            _inventoryRecoveryBaseState = null;
                            _goodsTransfersUnavailable = false;
                        }
                    }

                    return failure;
                }
                catch (Exception exception)
                {
                    return exception.GetType().Name + ":" + Safe(exception.Message);
                }
        }

        private long EquipmentValue(eGoods32 good)
        {
            int goodId = (int)good;
            return IsSendableGood(goodId) ? _frozenPrices[goodId] : 0L;
        }

        private static bool IsCombatUnit(eChimps type)
        {
            switch (type)
            {
                case eChimps.CHIMP_TYPE_TUNNELER:
                case eChimps.CHIMP_TYPE_ARCHER:
                case eChimps.CHIMP_TYPE_XBOWMAN:
                case eChimps.CHIMP_TYPE_SPEARMAN:
                case eChimps.CHIMP_TYPE_PIKEMAN:
                case eChimps.CHIMP_TYPE_MACEMAN:
                case eChimps.CHIMP_TYPE_SWORDSMAN:
                case eChimps.CHIMP_TYPE_KNIGHT:
                case eChimps.CHIMP_TYPE_LADDERMAN:
                case eChimps.CHIMP_TYPE_ENGINEER:
                case eChimps.CHIMP_TYPE_MONK:
                case eChimps.CHIMP_TYPE_CATAPULT:
                case eChimps.CHIMP_TYPE_TREBUCHET:
                case eChimps.CHIMP_TYPE_MANGONEL:
                case eChimps.CHIMP_TYPE_SIEGE_TOWER:
                case eChimps.CHIMP_TYPE_BATTERING_RAM:
                case eChimps.CHIMP_TYPE_PORTABLE_SHIELD:
                case eChimps.CHIMP_TYPE_BALLISTA:
                case eChimps.CHIMP_TYPE_WAR_DOG:
                case eChimps.CHIMP_TYPE_ARAB_BOW:
                case eChimps.CHIMP_TYPE_ARAB_SLAVE:
                case eChimps.CHIMP_TYPE_ARAB_SLINGER:
                case eChimps.CHIMP_TYPE_ARAB_ASSASIN:
                case eChimps.CHIMP_TYPE_ARAB_HORSEMAN:
                case eChimps.CHIMP_TYPE_ARAB_SWORDSMAN:
                case eChimps.CHIMP_TYPE_ARAB_GRENADIER:
                case eChimps.CHIMP_TYPE_ARAB_BALLISTA:
                case eChimps.CHIMP_TYPE_BEDOUIN_CAMEL_LANCER:
                case eChimps.CHIMP_TYPE_BEDOUIN_EUNUCH:
                case eChimps.CHIMP_TYPE_BEDOUIN_AMBUSHER:
                case eChimps.CHIMP_TYPE_BEDOUIN_SKIRMISHER:
                case eChimps.CHIMP_TYPE_BEDOUIN_HEAVY_CAMEL:
                case eChimps.CHIMP_TYPE_BEDOUIN_SAPPER:
                case eChimps.CHIMP_TYPE_BEDOUIN_DEMOLISHER:
                    return true;
                default:
                    return false;
            }
        }

        private static int NormalizeProducedGood(int value)
        {
            return IsSendableGood(value) ? value : 0;
        }

        private static bool IsSendableGood(int goodId)
        {
            return Array.IndexOf(SendableGoods, goodId) >= 0;
        }

        private string GoodName(int goodId, int amount)
        {
            if (amount != 1)
            {
                return GoodName(goodId);
            }

            switch (goodId)
            {
                case 2:
                    return Text("Holz", "Wood");
                case 3:
                    return Text("Hopfen", "Hops");
                case 4:
                    return Text("Stein", "Stone");
                case 6:
                    return Text("Eisen", "Iron");
                case 7:
                    return Text("Pech", "Pitch");
                case 9:
                    return Text("Weizen", "Wheat");
                case 10:
                    return Text("Brot", "Bread");
                case 11:
                    return Text("Käse", "Cheese");
                case 12:
                    return Text("Fleisch", "Meat");
                case 13:
                    return Text("Apfel", "Apple");
                case 14:
                    return Text("Bier", "Ale");
                case 15:
                    return Text("Gold", "Gold");
                case 16:
                    return Text("Mehl", "Flour");
                case 17:
                    return Text("Bogen", "Bow");
                case 18:
                    return Text("Armbrust", "Crossbow");
                case 19:
                    return Text("Lanze", "Spear");
                case 20:
                    return Text("Pike", "Pike");
                case 21:
                    return Text("Streitkolben", "Mace");
                case 22:
                    return Text("Schwert", "Sword");
                case 23:
                    return Text("Lederrüstung", "Leather armour");
                case 24:
                    return Text("Rüstung", "Armour");
                default:
                    return GoodName(goodId);
            }
        }

        private string GoodName(int goodId)
        {
            try
            {
                return Translate.Instance.lookUpText(Enums.eTextSections.TEXT_GOODS, goodId);
            }
            catch
            {
                return "Good " + goodId.ToString(CultureInfo.InvariantCulture);
            }
        }

        private static string WithVanillaGoodsIconSpacing(string value)
        {
            return FatControler.arabic && ConfigSettings.Settings_ArabicL2R ? value + "       " : "       " + value;
        }

        private static string VanillaAlliesTitle()
        {
            try
            {
                return Translate.Instance.lookUpText(Enums.eTextSections.TEXT_ALLIES, 0);
            }
            catch
            {
                return Text("Verbündete", "Allies");
            }
        }

        /// <summary>
        /// Bruecke zu den beiden Speicherbibliotheken des Script Extenders.
        /// </summary>
        /// <remarks>
        /// Gemessen am SE 2.0.0 ersetzt Zhuqiaomon durch RedBird
        /// (<c>Zhuqiaomon.Memory.NativePointer`1</c> ->
        /// <c>RedBird.Core.Memory.NativePointer`1</c>, ebenso
        /// <c>SimpleNativeArray`1</c>) - eine reine Umbenennung, die
        /// Mitgliedernamen sind gleich. Eine fest gebundene Fremdassembly
        /// waere damit die einzige Stelle, an der die Diplomatie an einer
        /// Extender-Hauptversion zerbricht. Diese Bruecke fragt stattdessen
        /// den Rueckgabetyp der SHCDESE-Methode selbst und nennt keine der
        /// beiden Bibliotheken; sie traegt darum von SE 1.39 bis 2.x.
        ///
        /// Die Aufloesung geschieht einmal und wird vom Startwaechter
        /// geprueft (<c>DiplomatieCompatibilityGuard</c>), damit ein
        /// fehlendes Mitglied vor der Karte auffaellt und nicht mitten im
        /// Spiel. Kein Aufruf hier wirft; jeder meldet seinen Fehlschlag.
        /// </remarks>
        internal static class ScriptExtenderNativeBridge
        {
            private const BindingFlags PublicInstance = BindingFlags.Public | BindingFlags.Instance;
            private static readonly object Gate = new object ();
            private static bool _resolved;
            private static string _failure;
            private static MethodInfo _getAicArray;
            private static PropertyInfo _aicLength;
            private static MethodInfo _aicGetValue;
            private static MethodInfo _getCursorManager;
            private static PropertyInfo _cursorIsNull;
            private static MethodInfo _getCurrentDay;
            /// <summary>
            /// Loest alle gebrueckten Mitglieder auf. Gibt den ersten
            /// Fehlschlag zurueck, damit der Waechter ihn melden kann.
            /// </summary>
            internal static bool TryResolve(out string failure)
            {
                lock (Gate)
                {
                    if (_resolved)
                    {
                        failure = _failure;
                        return _failure == null;
                    }

                    _resolved = true;
                    _failure = Resolve();
                    failure = _failure;
                    return _failure == null;
                }
            }

            private static string Resolve()
            {
                try
                {
                    _getAicArray = typeof(GameAIManagerAPI).GetMethod("GetAICArray", PublicInstance, null, Type.EmptyTypes, null);
                    if (_getAicArray == null)
                    {
                        return "GameAIManagerAPI.GetAICArray()";
                    }

                    // Der Rueckgabetyp ist SimpleNativeArray<InternalAIC> -
                    // aus Zhuqiaomon oder RedBird, je nach Extender. Genau
                    // deshalb wird er hier erfragt statt benannt.
                    Type aicArray = _getAicArray.ReturnType;
                    _aicLength = aicArray.GetProperty("Length", PublicInstance);
                    _aicGetValue = aicArray.GetMethod("GetValue", PublicInstance, null, new[] { typeof(int) }, null);
                    if (_aicLength == null || _aicLength.PropertyType != typeof(int) || !_aicLength.CanRead)
                    {
                        return aicArray.FullName + ".Length";
                    }

                    if (_aicGetValue == null || _aicGetValue.ReturnType != typeof(InternalAIC))
                    {
                        return aicArray.FullName + ".GetValue(int)";
                    }

                    // Der Tageszaehler ist in SE 1.x ein int, ab 2.x ein
                    // uint. Ueber die Bruecke ist der Rueckgabetyp
                    // gleichgueltig; gelesen wird als long.
                    _getCurrentDay = typeof(GameTimeManagerAPI).GetMethod("GetCurrentDay", PublicInstance, null, Type.EmptyTypes, null);
                    if (_getCurrentDay == null || _getCurrentDay.ReturnType == typeof(void))
                    {
                        return "GameTimeManagerAPI.GetCurrentDay()";
                    }

                    _getCursorManager = typeof(GamePlayerManagerAPI).GetMethod("GetCursorManager", PublicInstance, null, Type.EmptyTypes, null);
                    if (_getCursorManager == null)
                    {
                        return "GamePlayerManagerAPI.GetCursorManager()";
                    }

                    Type cursor = _getCursorManager.ReturnType;
                    _cursorIsNull = cursor.GetProperty("IsNull", PublicInstance);
                    if (_cursorIsNull == null || _cursorIsNull.PropertyType != typeof(bool) || !_cursorIsNull.CanRead)
                    {
                        return cursor.FullName + ".IsNull";
                    }

                    return null;
                }
                catch (Exception exception)
                {
                    return exception.GetType().Name + ":" + exception.Message;
                }
            }

            private static void Require()
            {
                string failure;
                if (!TryResolve(out failure))
                {
                    throw new MissingMemberException("script extender memory bridge is unavailable: " + failure);
                }
            }

            /// <summary>
            /// Der laufende Spieltag. Als long, weil der Extender den Typ
            /// zwischen seinen Reihen gewechselt hat (int bzw. uint).
            /// </summary>
            internal static long CurrentDay()
            {
                Require();
                object value = _getCurrentDay.Invoke(GameTimeManagerAPI.Instance, null);
                return value == null ? 0L : Convert.ToInt64(value, CultureInfo.InvariantCulture);
            }

            /// <summary>Anzahl der KI-Charakterprofile.</summary>
            internal static int AicCount()
            {
                Require();
                return (int)_aicLength.GetValue(_getAicArray.Invoke(GameAIManagerAPI.Instance, null), null);
            }

            /// <summary>Ein KI-Charakterprofil.</summary>
            internal static InternalAIC Aic(int index)
            {
                Require();
                return (InternalAIC)_aicGetValue.Invoke(_getAicArray.Invoke(GameAIManagerAPI.Instance, null), new object[] { index });
            }

            /// <summary>
            /// Ob der native Zeigerverwalter des Mauszeigers fehlt. Ohne
            /// diese Pruefung greift <c>IsCursorInGame</c> auf einen
            /// Nullzeiger zu und beendet das Spiel wortlos.
            /// </summary>
            internal static bool CursorManagerIsNull(GamePlayerManagerAPI players)
            {
                Require();
                return (bool)_cursorIsNull.GetValue(_getCursorManager.Invoke(players, null), null);
            }
        }

        private static string Text(string german, string english)
        {
            // ExtendedState (): Deutsch und Englisch sind Quelltext; FR ES PT
            // PL IT kommen aus GeneratedDiplomacyTexts (texte/<locale>.json,
            // beim Bau gegen jeden Text(-Aufruf auf Vollstaendigkeit
            // geprueft). Jede andere Spielsprache und jeder unbekannte Text
            // faellt auf Englisch zurueck; die Nachschlagehilfe wirft nie.
            string locale = FatControler.locale;
            if (string.Equals(locale, Translate.locale_german, StringComparison.OrdinalIgnoreCase))
            {
                return german;
            }

            string translated;
            if (GeneratedDiplomacyTexts.TryTranslate(locale, german, out translated))
            {
                return translated;
            }

            return english;
        }

        private static void ShowMessage(string message)
        {
            ShowOkMessage(Text("Diplomatie", "Diplomacy"), delegate
            {
            }, message);
        }

        private void PlayCoalitionSpeech(CoalitionState coalition, string mediaTag, string fileStem)
        {
            lock (_gate)
            {
                if (!CanPresentDiplomacyUi || !TryPlayCoalitionSpeechNow(coalition, mediaTag, fileStem))
                    DeferCoalitionSpeech(coalition, mediaTag, fileStem);
            }
        }

        private bool TryPlayCoalitionSpeechNow(CoalitionState coalition, string mediaTag, string fileStem)
        {
            if (coalition == null || coalition.MemberPlayerIds == null)
            {
                return true;
            }

            try
            {
                int player;
                int lordIndex;
                IDisposable nativeRead;
                if (!TryEnterNativeInventorySection(out nativeRead))
                    return false;
                using (nativeRead)
                {
                    player = coalition.MemberPlayerIds.Where(IsAlivePlayer).DefaultIfEmpty(0).First();
                    if (player <= 0)
                        return true;
                    lordIndex = checked((int)GamePlayerManagerAPI.Instance.GetAILord(player));
                }

                // Only immutable identifiers cross the native read boundary.
                // Unity's audio manager must be called after the lease ends.
                SFXManager manager = SFXManager.instance;
                if (manager == null)
                {
                    Log("speech", "status=skipped|reason=manager_unavailable" + "|coalition=" + coalition.CoalitionId);
                    return true;
                }

                string path;
                if (lordIndex >= (int)AILords.SK_X1 && lordIndex <= (int)AILords.SK_X8)
                {
                    path = "*" + player.ToString(CultureInfo.InvariantCulture) + "*" + mediaTag;
                }
                else
                {
                    string prefix = lordIndex >= 0 && lordIndex < StandardLordSpeechPrefixes.Length ? StandardLordSpeechPrefixes[lordIndex] : "all";
                    bool originalLordMembershipLine = lordIndex >= (int)AILords.SK_RAT && lordIndex <= (int)AILords.SK_WOLF && (string.Equals(fileStem, "add_player", StringComparison.Ordinal) || string.Equals(fileStem, "kick_player", StringComparison.Ordinal));
                    if (originalLordMembershipLine)
                    {
                        string[] originalPrefixes =
                        {
                            "",
                            "rt",
                            "sn",
                            "pg",
                            "wf",
                        };
                        path = "fx\\speech\\" + originalPrefixes[lordIndex] + "_" + fileStem + ".wav";
                    }
                    else
                    {
                        path = "fx\\speech\\" + prefix + "_" + fileStem + "_01.wav";
                    }
                }

                manager.playSpeech(1, path, 1f);
                Log("speech", "status=played|coalition=" + coalition.CoalitionId + "|player=" + player.ToString(CultureInfo.InvariantCulture) + "|tag=" + mediaTag);
            }
            catch (Exception exception)
            {
                Log("speech", "status=skipped|reason=" + Safe(exception.GetType().Name + ":" + exception.Message) + "|coalition=" + coalition.CoalitionId);
            }

            return true;
        }

        private static string Fingerprint(string material)
        {
            using (SHA256 sha = SHA256.Create())
            {
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(material ?? ""))).Replace("-", "").ToLowerInvariant();
            }
        }

        private static CoalitionState CopyCoalition(CoalitionState source)
        {
            return new CoalitionState
            {
                CoalitionId = source.CoalitionId,
                MemberPlayerIds = (int[])source.MemberPlayerIds.Clone(),
                OriginalTeams = (int[])source.OriginalTeams.Clone(),
                SendGoodsMargin = source.SendGoodsMargin,
                Economy = source.Economy,
                Military = source.Military,
                Target = source.Target,
                StartingAlly = source.StartingAlly,
                Allied = source.Allied,
            };
        }

        private static RelationState CopyRelation(RelationState source)
        {
            return new RelationState
            {
                CoalitionId = source.CoalitionId,
                Goodwill = source.Goodwill,
                LastGiftDay = source.LastGiftDay,
                LastProcessedDay = source.LastProcessedDay,
                LastOfferAttemptDay = source.LastOfferAttemptDay,
                OfferAttemptCounter = source.OfferAttemptCounter,
                GiftValueSinceOfferAttempt = source.GiftValueSinceOfferAttempt,
                SpontaneousOfferBlockedUntilDay = source.SpontaneousOfferBlockedUntilDay,
                RelationshipBlockedUntilDay = source.RelationshipBlockedUntilDay,
                AllianceGraceUntilDay = source.AllianceGraceUntilDay,
                Demand = CopyDemand(source.Demand),
                PendingOffer = source.PendingOffer,
                PendingOfferIssuedDay = source.PendingOfferIssuedDay,
                Allied = source.Allied,
                StartingAlliance = source.StartingAlliance,
                ExtendedState = source.ExtendedState == null ? null : source.ExtendedState.Clone(),
            };
        }

        private static GoodsDemand CopyDemand(GoodsDemand source)
        {
            if (source == null)
            {
                return null;
            }

            return new GoodsDemand
            {
                IssuedDay = source.IssuedDay,
                ExpiresDay = source.ExpiresDay,
                IsUltimatum = source.IsUltimatum,
                DemandPurposeDescription = source.DemandPurposeDescription,
                DiplomacyUrgency = source.DiplomacyUrgency,
                Items = source.Items.Select(item => new DemandItem { GoodId = item.GoodId, UnitValue = item.UnitValue, RequiredAmount = item.RequiredAmount, DeliveredAmount = item.DeliveredAmount, }).ToArray(),
            };
        }

        private static DiplomacyRules RulesFromGeneratedContract()
        {
            return new DiplomacyRules
            {
                EconomyBuildingMaterialPercent = GeneratedDiplomacyContract.EconomyBuildingMaterialPercent,
                LordMilitaryValue = GeneratedDiplomacyContract.LordMilitaryValue,
                UnknownUnitHealthDivisor = GeneratedDiplomacyContract.UnknownUnitHealthDivisor,
                UnknownUnitValueMinimum = GeneratedDiplomacyContract.UnknownUnitValueMinimum,
                UnknownUnitValueMaximum = GeneratedDiplomacyContract.UnknownUnitValueMaximum,
                TargetBaseValue = GeneratedDiplomacyContract.TargetBaseValue,
                EconomyWeightBasisPoints = GeneratedDiplomacyContract.EconomyWeightBasisPoints,
                MilitaryWeightBasisPoints = GeneratedDiplomacyContract.MilitaryWeightBasisPoints,
                PersonalityBaseBasisPoints = GeneratedDiplomacyContract.PersonalityBaseBasisPoints,
                PersonalityMarginBasisPoints = GeneratedDiplomacyContract.PersonalityMarginBasisPoints,
                TargetMinimum = GeneratedDiplomacyContract.TargetMinimum,
                TargetMaximum = GeneratedDiplomacyContract.TargetMaximum,
                TargetRoundStep = GeneratedDiplomacyContract.TargetRoundStep,
                GoodwillGraceDays = GeneratedDiplomacyContract.GoodwillGraceDays,
                GoodwillDecayPercent = GeneratedDiplomacyContract.GoodwillDecayPercent,
                OfferBasePercent = GeneratedDiplomacyContract.OfferBasePercent,
                OfferMarginTenthsPercent = GeneratedDiplomacyContract.OfferMarginTenthsPercent,
                OfferMinimumPercent = GeneratedDiplomacyContract.OfferMinimumPercent,
                OfferMaximumPercent = GeneratedDiplomacyContract.OfferMaximumPercent,
                OfferAdditionalGiftPercent = GeneratedDiplomacyContract.OfferAdditionalGiftPercent,
                OfferRejectionLockDays = GeneratedDiplomacyContract.OfferRejectionLockDays,
                DemandLifetimeDays = GeneratedDiplomacyContract.DemandLifetimeDays,
                MaximumDemandGoods = GeneratedDiplomacyContract.MaximumDemandGoods,
                NewAllianceGraceDays = GeneratedDiplomacyContract.NewAllianceGraceDays,
                StartingAllianceGraceDays = GeneratedDiplomacyContract.StartingAllianceGraceDays,
                LoyaltyBaseDecayBasisPoints = GeneratedDiplomacyContract.LoyaltyBaseDecayBasisPoints,
                LoyaltyMarginDecayBasisPoints = GeneratedDiplomacyContract.LoyaltyMarginDecayBasisPoints,
                UltimatumThresholdPercent = GeneratedDiplomacyContract.UltimatumThresholdPercent,
                UltimatumRestorePercent = GeneratedDiplomacyContract.UltimatumRestorePercent,
                UltimatumLifetimeDays = GeneratedDiplomacyContract.UltimatumLifetimeDays,
                BreakLockDays = GeneratedDiplomacyContract.BreakLockDays,
            };
        }

        private static object FindElement(object panel, string name)
        {
            if (panel == null)
            {
                return null;
            }

            MethodInfo method = panel.GetType().GetMethod("FindName", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(string) }, null);
            if (method == null)
            {
                throw new MissingMethodException("HUD_AlliesPanel.FindName(string)");
            }

            return method.Invoke(panel, new object[] { name });
        }

        private static object CurrentAlliesPanel()
        {
            MainViewModel viewModel = MainViewModel.Instance;
            if (viewModel == null)
            {
                return null;
            }

            FieldInfo field = viewModel.GetType().GetField("HUDAlliesPanel", BindingFlags.Public | BindingFlags.Instance);
            if (field == null)
            {
                throw new MissingFieldException(viewModel.GetType().FullName, "HUDAlliesPanel");
            }

            return field.GetValue(viewModel);
        }

        private static object GetAlliesGoodsIcon(int goodId)
        {
            MainViewModel viewModel = MainViewModel.Instance;
            if (viewModel == null)
            {
                return null;
            }

            MethodInfo method = viewModel.GetType().GetMethod("getAlliesGoodsIcon", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(int) }, null);
            if (method == null)
            {
                throw new MissingMethodException(viewModel.GetType().FullName, "getAlliesGoodsIcon(int)");
            }

            return method.Invoke(viewModel, new object[] { goodId });
        }

        private static DiplomacyPendingTransfer CopyPendingTransfer(NativeTransferProbe source)
        {
            if (source == null)
            {
                return null;
            }

            return new DiplomacyPendingTransfer
            {
                SenderPlayer = source.SenderPlayer,
                TargetPlayer = source.TargetPlayer,
                GoodId = source.GoodId,
                Amount = source.Amount,
                SenderBefore = source.SenderBefore,
                RecipientBefore = source.RecipientBefore,
                AlliedNativeTransfer = source.AlliedNativeTransfer,
                Purpose = source.Purpose,
                DirectAtomicTransfer = source.DirectAtomicTransfer,
                GameDay = source.GameDay,
                RollbackPending = source.RollbackPending,
                Progress = DiplomacyTransferRecovery.Copy(source.Progress),
                BoundRequestId = source.BoundRequestId,
                ModGiftReceiptId = source.ModGiftReceiptId,
            };
        }

        private NativeTransferProbe RearmSavedTransferProbe(DiplomacyPendingTransfer source)
        {
            if (source == null)
            {
                throw new ArgumentNullException("source");
            }

            return new NativeTransferProbe
            {
                SenderPlayer = source.SenderPlayer,
                TargetPlayer = source.TargetPlayer,
                GoodId = source.GoodId,
                Amount = source.Amount,
                SenderBefore = source.SenderBefore,
                RecipientBefore = source.RecipientBefore,
                StartedTick = ReadSimulationTick(),
                AlliedNativeTransfer = source.AlliedNativeTransfer,
                Purpose = source.Purpose,
                DirectAtomicTransfer = source.DirectAtomicTransfer,
                GameDay = source.GameDay,
                RollbackPending = source.RollbackPending,
                Progress = DiplomacyTransferRecovery.Copy(source.Progress),
                BoundRequestId = source.BoundRequestId,
                ModGiftReceiptId = source.ModGiftReceiptId,
            };
        }

        private DiplomacyManagedStateMemento CaptureManagedStateMemento()
        {
            var memento = new DiplomacyManagedStateMemento
            {
                PendingTeamTransition = CopyPendingTeamTransition(_pendingTeamTransition),
                PendingTransitionGeneration = _pendingTransitionGeneration,
                PendingTransitionFingerprint = _pendingTransitionFingerprint,
                TransitionGuardPartition = CopyPartition(_transitionGuardPartition),
                TransitionVerificationPartition = CopyPartition(_transitionVerificationPartition),
                TransitionVerificationStartedTick = _transitionVerificationStartedTick,
                RequestHistory = _requestHistory == null ? null : _requestHistory.Copy(),
#if DIPLOMACY_PRIVATE_TESTS
                NativeAidMemory = _aidMemory == null ? null : _aidMemory.Copy(),
#endif
            };
            memento.Coalitions.AddRange(_coalitions.Values.Select(CopyCoalition));
            memento.Relations.AddRange(_relations.Values.Select(CopyRelation));
            return memento;
        }

        private void RestoreManagedStateMemento(DiplomacyManagedStateMemento memento)
        {
            if (memento == null)
            {
                throw new ArgumentNullException("memento");
            }

            _coalitions.Clear();
            _playerCoalition.Clear();
            foreach (CoalitionState coalition in memento.Coalitions)
            {
                CoalitionState restored = CopyCoalition(coalition);
                _coalitions.Add(restored.CoalitionId, restored);
                foreach (int player in restored.MemberPlayerIds)
                {
                    _playerCoalition.Add(player, restored.CoalitionId);
                }
            }

            _relations.Clear();
            foreach (RelationState relation in memento.Relations)
            {
                RelationState restored = CopyRelation(relation);
                _relations.Add(restored.CoalitionId, restored);
            }

            _pendingTeamTransition = CopyPendingTeamTransition(memento.PendingTeamTransition);
            _pendingTransitionGeneration = memento.PendingTransitionGeneration;
            _pendingTransitionFingerprint = memento.PendingTransitionFingerprint;
            _transitionGuardPartition = CopyPartition(memento.TransitionGuardPartition);
            _transitionVerificationPartition = CopyPartition(memento.TransitionVerificationPartition);
            _transitionVerificationStartedTick = memento.TransitionVerificationStartedTick;
            _requestHistory = memento.RequestHistory == null ? null : memento.RequestHistory.Copy();
#if DIPLOMACY_PRIVATE_TESTS
            _aidMemory = memento.NativeAidMemory == null ? null : memento.NativeAidMemory.Copy();
#endif
        }

        private static Dictionary<int, int> CopyPartition(IReadOnlyDictionary<int, int> source)
        {
            if (source == null)
            {
                return null;
            }

            var copy = new Dictionary<int, int>();
            foreach (KeyValuePair<int, int> entry in source)
            {
                copy.Add(entry.Key, entry.Value);
            }

            return copy;
        }

        private static void ClearAlliesFace(int slot)
        {
            MainViewModel viewModel = MainViewModel.Instance;
            if (viewModel == null)
            {
                return;
            }

            MethodInfo method = viewModel.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance).SingleOrDefault(candidate => candidate.Name == "setAlliesFace" && candidate.GetParameters().Length == 7);
            if (method == null)
            {
                throw new MissingMethodException(viewModel.GetType().FullName, "setAlliesFace");
            }

            method.Invoke(viewModel, new object[] { slot, null, null, null, null, null, 1f, });
        }

        private static bool ShowConfirmation(string title, Action yes, Action no, string message, Action onPresented = null, string decisionKey = null, Func<bool> stillValid = null)
        {
            DiplomacyFeature owner;
            lock (OwnerGate)
            {
                owner = _activeOwner;
            }

            if (owner == null)
            {
                ShowConfirmationNative(title, yes, no, message);
                if (onPresented != null)
                    onPresented();
                return true;
            }

            return owner.EnqueueDiplomacyDialog(title, yes, no, message, false, onPresented, decisionKey, stillValid);
        }

        private bool EnqueueDiplomacyDialog(string title, Action yes, Action no, string message, bool isOk, Action onPresented, string decisionKey, Func<bool> stillValid)
        {
            lock (_gate)
            {
                if (_mapDisabled)
                    return false;
                bool routine = isOk && decisionKey == null;
                if (routine)
                    decisionKey = "routine:" + (title ?? string.Empty).Length.ToString(CultureInfo.InvariantCulture) + ":" + title + ":" + message;
                var dialog = new DiplomacyDialog
                {
                    Title = title,
                    Message = message,
                    Yes = yes,
                    No = no,
                    IsOk = isOk,
                    OnPresented = onPresented,
                    StillValid = stillValid,
                    Generation = _sessionGeneration,
                    Fingerprint = _mapFingerprint,
                    DecisionKey = decisionKey,
                };
                bool queued = routine ? _dialogQueue.EnqueueRoutine(decisionKey, dialog) : _dialogQueue.Enqueue(decisionKey, dialog);
                if (CanPresentDiplomacyUi)
                    PumpDiplomacyDialogs();
                return queued;
            }
        }

        private bool IsCurrentDialog(DiplomacyDialog dialog)
        {
            return !dialog.Cancelled && _mapActive && !_mapDisabled && dialog.Generation == _sessionGeneration && string.Equals(dialog.Fingerprint, _mapFingerprint, StringComparison.Ordinal) && (dialog.StillValid == null || dialog.StillValid());
        }

        private void CancelDiplomacyDialog(string decisionKey)
        {
            if (decisionKey == null)
                return;
            lock (_gate)
            {
                _dialogQueue.CancelPending(decisionKey);
                DiplomacyDialog active = _dialogQueue.Active;
                if (active != null && string.Equals(active.DecisionKey, decisionKey, StringComparison.Ordinal))
                    active.Cancelled = true;
                if (CanPresentDiplomacyUi)
                    PumpDiplomacyDialogs();
            }
        }

        private bool TryGetOwnedNativeConfirmation(DiplomacyDialog dialog, out MainViewModel view)
        {
            view = null;
            if (!CanPresentDiplomacyUi || dialog == null || dialog.IsOk)
                return false;
            try
            {
                view = MainViewModel.Instance;
                if (view == null || !view.Show_HUD_Confirmation || view.Show_HUD_ConfirmationMP || view.Show_HUD_ConfirmationSands || view.Show_HUD_ConfirmationSM || !DiplomacyPopupOwnership.Matches(dialog.PresentedPopup, view.HUDConfirmationPopup, dialog.PresentedYes, dialog.PresentedNo))
                    return false;
                return true;
            }
            catch
            {
                view = null;
                return false;
            }
        }

        private bool IsDiplomacyDialogPresented(string decisionKey)
        {
            lock (_gate)
            {
                DiplomacyDialog dialog = _dialogQueue.Active;
                MainViewModel view;
                return decisionKey != null && dialog != null && !dialog.Cancelled && dialog.Generation == _sessionGeneration && string.Equals(dialog.Fingerprint, _mapFingerprint, StringComparison.Ordinal) && string.Equals(dialog.DecisionKey, decisionKey, StringComparison.Ordinal) && TryGetOwnedNativeConfirmation(dialog, out view);
            }
        }

        private bool TryDismissOwnedNativeConfirmation(DiplomacyDialog dialog)
        {
            MainViewModel view;
            if (!TryGetOwnedNativeConfirmation(dialog, out view))
                return false;
            try
            {
                // Never call ConfirmationClicked: it hides every confirmation
                // mode and may execute a Yes/No callback. Close only our SP view.
                view.Show_HUD_Confirmation = false;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private void PumpDiplomacyDialogs()
        {
            // All queue and ExtendedState state access is on the frame/UI path under gate.
            lock (_gate)
            {
                if (!CanPresentDiplomacyUi)
                    return;
                FlushDeferredDiplomacyUi();
                if (!_mapActive || _mapDisabled || _dialogCallbackInProgress || _featureTicks < _dialogRetryNotBeforeTick || MainViewModel.Instance == null)
                    return;
                DiplomacyDialog active = _dialogQueue.Active;
                if (active != null)
                {
                    bool valid;
                    try
                    {
                        valid = IsCurrentDialog(active);
                    }
                    catch
                    {
                        return;
                    }

                    if (!valid)
                    {
                        if (!MainViewModel.Instance.Show_HUD_Confirmation || TryDismissOwnedNativeConfirmation(active))
                            _dialogQueue.Complete(active);
                        else
                            return; // A foreign popup or unknown binding owns presentation.
                    }
                }

                // A native/other-mod confirmation owns the shared popup too.
                // Never overwrite it merely because our queue has work.
                if (MainViewModel.Instance.Show_HUD_Confirmation || MainViewModel.Instance.Show_HUD_ConfirmationMP || MainViewModel.Instance.Show_HUD_ConfirmationSands || MainViewModel.Instance.Show_HUD_ConfirmationSM)
                    return;
                if (_dialogQueue.Active != null)
                {
                    // Dismissal without our callback (e.g. UI reconstruction)
                    // does not silently turn an unanswered question into No.
                    _dialogQueue.DeferActive();
                }

                DiplomacyDialog dialog;
                while ((dialog = _dialogQueue.BeginNext()) != null)
                {
                    try
                    {
                        if (!IsCurrentDialog(dialog))
                        {
                            _dialogQueue.Complete(dialog);
                            continue;
                        }

                        Action yes = delegate
                        {
                            CompleteDiplomacyDialog(dialog, true);
                        };
                        Action no = delegate
                        {
                            CompleteDiplomacyDialog(dialog, false);
                        };
                        if (dialog.IsOk)
                            ShowOkMessageNative(dialog.Title, yes, dialog.Message);
                        else
                            ShowConfirmationNative(dialog.Title, yes, no, dialog.Message);
                        dialog.PresentedPopup = MainViewModel.Instance.HUDConfirmationPopup;
                        dialog.PresentedYes = yes;
                        dialog.PresentedNo = no;
                        if (dialog.OnPresented != null)
                            dialog.OnPresented();
                        return;
                    }
                    catch (Exception exception)
                    {
                        // An unavailable popup is retried, never interpreted as
                        // rejection. Keep the same decision and pause its clock
                        // by not invoking OnPresented before a successful show.
                        _dialogQueue.DeferActive();
                        _dialogRetryNotBeforeTick = _featureTicks + 60;
                        Log("dialog", "status=deferred|reason=" + Safe(exception.GetType().Name));
                        return;
                    }
                }
            }
        }

        private void CompleteDiplomacyDialog(DiplomacyDialog dialog, bool accepted)
        {
            NoteLane(LaneUiDialog);
            lock (_gate)
            {
                if (!ReferenceEquals(_dialogQueue.Active, dialog))
                    return;
                _dialogQueue.Complete(dialog);
                _dialogCallbackInProgress = true;
                try
                {
                    if (IsCurrentDialog(dialog))
                    {
                        Action action = accepted ? dialog.Yes : dialog.No;
                        if (action != null)
                            action();
                    }
                }
                catch (Exception exception)
                {
                    DisableMap("ui_dialog", exception);
                }
                finally
                {
                    _dialogCallbackInProgress = false;
                    PublishCombatProtectionSnapshot();
                }

                PumpDiplomacyDialogs();
            }
        }

        private static void ShowConfirmationNative(string title, Action yes, Action no, string message)
        {
            Type popup = typeof(MainViewModel).Assembly.GetType("CrusaderDE.HUD_ConfirmationPopup", true);
            MethodInfo method = popup.GetMethod("ShowConfirmationMessage", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string), typeof(Action), typeof(Action), typeof(string), typeof(bool), typeof(bool), }, null);
            if (method == null)
            {
                throw new MissingMethodException(popup.FullName, "ShowConfirmationMessage");
            }

            method.Invoke(null, new object[] { title, yes ?? delegate
            {
            }, no ?? delegate
            {
            }, message, false, false, });
        }

        private static void ShowOkMessage(string title, Action ok, string message)
        {
            DiplomacyFeature owner;
            lock (OwnerGate)
            {
                owner = _activeOwner;
            }

            if (owner == null)
            {
                ShowOkMessageNative(title, ok, message);
                return;
            }

            owner.EnqueueDiplomacyDialog(title, ok, null, message, true, null, null, null);
        }

        private static void ShowOkMessageNative(string title, Action ok, string message)
        {
            Type popup = typeof(MainViewModel).Assembly.GetType("CrusaderDE.HUD_ConfirmationPopup", true);
            MethodInfo method = popup.GetMethod("ShowConfirmationOKMessage", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string), typeof(Action), typeof(string), typeof(bool), }, null);
            if (method == null)
            {
                throw new MissingMethodException(popup.FullName, "ShowConfirmationOKMessage");
            }

            // Klammer um das Meldungsfenster (): steht im
            // Protokoll `popup_open` ohne `popup_done`, stirbt das Spiel im
            // Aufruf selbst; stehen beide da, liegt es am Rendern danach.
            RareStepStatic("popup_open");
            method.Invoke(null, new object[] { title, ok ?? delegate
            {
            }, message, false, });
            RareStepStatic("popup_done");
        }

        //  live-Noesis UI fix: optional slot-6 help/order markers are
        // absent on 2-6 slot skirmishes or during XAML tree reconstruction.
        // They are decorative only, so never make the entire diplomacy
        // refresh fail for their absence. Required targets still use the
        // original strict SetProperty method below.
        private static void SetPropertyIfPresent(object target, string propertyName, object value)
        {
            if (target == null)
                return;
            SetProperty(target, propertyName, value);
        }

        private static void SetProperty(object target, string propertyName, object value)
        {
            if (target == null)
            {
                throw new ArgumentNullException("target");
            }

            PropertyInfo property = target.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
            if (property == null || !property.CanWrite)
            {
                throw new MissingMemberException(target.GetType().FullName, propertyName);
            }

            property.SetValue(target, value, null);
        }

        private static void SetVisibility(object target, bool visible)
        {
            PropertyInfo property = target.GetType().GetProperty("Visibility", BindingFlags.Public | BindingFlags.Instance);
            if (property == null || !property.CanWrite || !property.PropertyType.IsEnum)
            {
                throw new MissingMemberException(target.GetType().FullName, "Visibility");
            }

            object value = Enum.Parse(property.PropertyType, visible ? "Visible" : "Collapsed", false);
            property.SetValue(target, value, null);
        }

        private static void SetVisibilityIfPresent(object target, bool visible)
        {
            if (target != null)
            {
                SetVisibility(target, visible);
            }
        }

        private static void SetNumericProperty(object target, string propertyName, double value)
        {
            if (target == null)
            {
                return;
            }

            PropertyInfo property = target.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
            if (property == null || !property.CanWrite)
            {
                throw new MissingMemberException(target.GetType().FullName, propertyName);
            }

            Type propertyType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            property.SetValue(target, Convert.ChangeType(value, propertyType, CultureInfo.InvariantCulture), null);
        }

        private static void SetThicknessPropertyIfPresent(object target, float left, float top)
        {
            if (target != null)
            {
                SetThicknessProperty(target, "Margin", left, top, 0f, 0f);
            }
        }

        private static void SetThicknessProperty(object target, string propertyName, float left, float top, float right, float bottom)
        {
            if (target == null)
            {
                throw new ArgumentNullException("target");
            }

            PropertyInfo property = target.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
            if (property == null || !property.CanWrite)
            {
                throw new MissingMemberException(target.GetType().FullName, propertyName);
            }

            ConstructorInfo constructor = property.PropertyType.GetConstructor(new[] { typeof(float), typeof(float), typeof(float), typeof(float), });
            if (constructor == null)
            {
                throw new MissingMethodException(property.PropertyType.FullName, ".ctor(float,float,float,float)");
            }

            property.SetValue(target, constructor.Invoke(new object[] { left, top, right, bottom, }), null);
        }

        private static bool IsVisible(object target)
        {
            PropertyInfo property = target.GetType().GetProperty("Visibility", BindingFlags.Public | BindingFlags.Instance);
            object value = property == null ? null : property.GetValue(target, null);
            return value != null && string.Equals(value.ToString(), "Visible", StringComparison.Ordinal);
        }

        private void Log(string eventName, string fields)
        {
            try
            {
                _info(RuntimeLogPrefix + "|event=" + eventName + "|" + fields + "|seq=" + Interlocked.Increment(ref _logSequence).ToString(CultureInfo.InvariantCulture));
            }
            catch
            {
            // Logging is observational, never a transaction dependency.
            }
        }

        private static string PartitionText(IReadOnlyDictionary<int, int> partition)
        {
            return string.Join(",", partition.OrderBy(value => value.Key).Select(value => value.Key.ToString(CultureInfo.InvariantCulture) + ":" + value.Value.ToString(CultureInfo.InvariantCulture)));
        }

        private static string Safe(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "unspecified";
            }

            string safe = value.Replace('|', ' ').Replace('\r', ' ').Replace('\n', ' ').Trim();
            return safe.Length <= 256 ? safe : safe.Substring(0, 256);
        }

        private static string BoolText(bool value)
        {
            return value ? "true" : "false";
        }

        private static string NullableBoolText(bool? value)
        {
            return value.HasValue ? BoolText(value.Value) : "none";
        }

        private static long SaturatingAdd(long left, long right)
        {
            if (right > 0 && left > long.MaxValue - right)
            {
                return long.MaxValue;
            }

            return left + right;
        }

        private static long SaturatingMultiply(int left, int right)
        {
            if (left <= 0 || right <= 0)
            {
                return 0L;
            }

            return (long)left * right;
        }

        private static long ScalePercentSaturating(long value, int percent)
        {
            if (value <= 0 || percent <= 0)
            {
                return 0L;
            }

            long whole = (value / 100L) * percent;
            long remainder = (value % 100L) * percent / 100L;
            return SaturatingAdd(whole, remainder);
        }

        private static void Dispose(ref IDisposable subscription)
        {
            IDisposable owned = subscription;
            subscription = null;
            if (owned != null)
            {
                owned.Dispose();
            }
        }
    }
}
