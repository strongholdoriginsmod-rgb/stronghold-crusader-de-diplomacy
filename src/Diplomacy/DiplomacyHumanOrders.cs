using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using CrusaderDE;
using HarmonyLib;
using SHCDESE.API;
using SHCDESE.EventAPI.Tribes;
using SHCDESE.Interop;
using SHCDESE.Interop.Enums;
#if !DIPLOMACY_PRIVATE_TESTS
using APIShared.Units;
#endif

namespace StrongholdEuropeDE.Runtime
{
    internal sealed partial class DiplomacyFeature
    {
        private sealed class TreatyIntentTarget
        {
            internal readonly string CoalitionId;
            internal readonly DiplomacyAgreementKind Agreement;
            internal readonly int UntilDay, BrokenAgreements, ManualTreatyEnds;
            internal readonly long Generation, Epoch;
            internal readonly int Local;
            internal TreatyIntentTarget(RelationState relation, long generation, long epoch, int local)
            {
                CoalitionId = relation.CoalitionId;
                Agreement = relation.ExtendedState.Agreement;
                UntilDay = relation.ExtendedState.AgreementUntilDay;
                BrokenAgreements = relation.ExtendedState.BrokenAgreements;
                ManualTreatyEnds = relation.ExtendedState.ManualTreatyEnds;
                Generation = generation;
                Epoch = epoch;
                Local = local;
            }

            internal bool Matches(RelationState relation)
            {
                // A failed goods ledger restores deep relation copies. Object
                // identity is therefore not the identity of this agreement.
                return relation != null && relation.CoalitionId == CoalitionId && relation.ExtendedState != null && relation.ExtendedState.Agreement == Agreement && relation.ExtendedState.AgreementUntilDay == UntilDay && relation.ExtendedState.BrokenAgreements == BrokenAgreements && relation.ExtendedState.ManualTreatyEnds == ManualTreatyEnds;
            }
        }

        private sealed class HumanWarIntent
        {
            internal TreatyIntentTarget Treaty;
            internal int TargetPlayer, UnitId, BuildingId, TileId, X, Y;
            internal uint TargetGlobalId;
            internal readonly Dictionary<int, uint> SelectedTribes = new Dictionary<int, uint>();
        }

        private sealed class HumanCommandFrame
        {
#if DIPLOMACY_PRIVATE_TESTS
            internal int[] UnderCursor;
#endif
            internal HumanWarIntent Intent;
            internal readonly HashSet<int> AllowedTribes = new HashSet<int>();
        }

        private TreatyIntentTarget[] _treatyIntentTargets = new TreatyIntentTarget[9];
        private readonly HumanWarIntent[] _humanWarIntents = new HumanWarIntent[9];
        [ThreadStatic]
        private static HumanCommandFrame _humanCommandFrame;
#if DIPLOMACY_PRIVATE_TESTS
        private MethodInfo _troopSelectionTarget, _preDllActionsTarget, _diplomacyProbeTarget;

        private bool PrepareHumanOrders(out string failure)
        {
            _troopSelectionTarget = typeof(EngineInterface).GetMethod("TroopSelection", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(int), typeof(bool), typeof(bool), typeof(int[]), typeof(bool), typeof(bool), typeof(int[]), typeof(int), typeof(int), typeof(bool), typeof(int[]) }, null);
            _preDllActionsTarget = typeof(EditorDirector).GetMethod("preDLLCallActions", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(int).MakeByRefType(), typeof(int).MakeByRefType() }, null);
            bool ready = _troopSelectionTarget != null && _troopSelectionTarget.ReturnType == typeof(void) &&
                _preDllActionsTarget != null && _preDllActionsTarget.ReturnType == typeof(void);
            _diplomacyProbeTarget = typeof(EngineInterface).GetMethod("run", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(bool) }, null);
            ready = ready && _diplomacyProbeTarget != null && _diplomacyProbeTarget.ReturnType == typeof(int);
            failure = ready ? null : "pinned managed human-order signatures are unavailable";
            return failure == null;
        }

#else
        private static bool _sharedHumanRunRegistered, _sharedTroopInputRegistered;
#endif

        private void ApplyHumanOrders()
        {
#if DIPLOMACY_PRIVATE_TESTS
            BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Static;
            _harmony.Patch(_troopSelectionTarget, prefix: new HarmonyMethod(typeof(DiplomacyFeature).GetMethod("TroopSelectionPrefix", flags)));
            _harmony.Patch(_preDllActionsTarget, postfix: new HarmonyMethod(typeof(DiplomacyFeature).GetMethod("PreDllActionsPostfix", flags)));
#if HOST_DIRECTED_AI_TRIBE_CANARY && ORIGINAL_POSTTICK_INSTALL_EXPLICITLY_AUTHORIZED
            // The earlier ordered engine bridge already co-registered both
            // human-order callbacks on this target when it admitted pre-worker.
            // In that case a second Patch would rewrite a live native entry.
            if (!OriginalPreWorkerRegistration.MustRetainRunPatch)
#endif
                _harmony.Patch(_diplomacyProbeTarget, prefix: new HarmonyMethod(typeof(DiplomacyFeature).GetMethod("RunBegin", flags)), finalizer: new HarmonyMethod(typeof(DiplomacyFeature).GetMethod("RunEnd", flags)));
#else
            if (!_sharedHumanRunRegistered)
            {
                if (!APIShared.Simulation.EngineRunEvents.TryRegister(CompatibilityContracts.PluginId,
                    "diplomacy-human-command-frame", ClearHumanCommandFrame, ClearHumanCommandFrame, out string reason))
                {
                    Log("human_orders", "status=shared_run_unavailable|reason=" + Safe(reason));
                    return;
                }
                _sharedHumanRunRegistered = true;
                Log("human_orders", "status=shared_run_registered");
            }

            if (!_sharedTroopInputRegistered)
            {
                if (!TroopCommandInputEvents.TryRegister(CompatibilityContracts.PluginId,
                    "diplomacy-troop-command-input", OnSharedTroopCommandInput, out string reason))
                {
                    Log("human_orders", "status=shared_input_unavailable|reason=" + Safe(reason));
                    return;
                }
                _sharedTroopInputRegistered = true;
                Log("human_orders", "status=shared_input_registered");
            }
#endif
        }

        private void RollbackHumanOrders()
        {
#if DIPLOMACY_PRIVATE_TESTS
            // Unpatch by our owner even if a later patch failed during Apply.
            if (_troopSelectionTarget != null)
                _harmony.Unpatch(_troopSelectionTarget, HarmonyPatchType.Prefix, _harmony.Id);
            if (_preDllActionsTarget != null)
                _harmony.Unpatch(_preDllActionsTarget, HarmonyPatchType.Postfix, _harmony.Id);
            if (_diplomacyProbeTarget != null)
            {
#if DIPLOMACY_PRIVATE_TESTS && HOST_DIRECTED_AI_TRIBE_CANARY && ORIGINAL_POSTTICK_INSTALL_EXPLICITLY_AUTHORIZED
                if (OriginalPreWorkerRegistration.MustRetainRunPatch)
                    OriginalPreWorkerRegistration.DisableAndDetach(this, _gate);
                else
#endif
                {
                    _harmony.Unpatch(_diplomacyProbeTarget, HarmonyPatchType.Prefix, _harmony.Id);
                    _harmony.Unpatch(_diplomacyProbeTarget, HarmonyPatchType.Finalizer, _harmony.Id);
                }
            }

#endif
            ClearHumanOrders();
        }

        private void ClearHumanOrders()
        {
            Volatile.Write(ref _treatyIntentTargets, new TreatyIntentTarget[9]);
            for (int player = 1; player <= 8; player++)
                Interlocked.Exchange(ref _humanWarIntents[player], null);
            _humanCommandFrame = null;
        }

        private void PublishTreatyIntentSnapshot()
        {
            var targets = new TreatyIntentTarget[9];
            if (_mapActive && !_mapDisabled)
                foreach (KeyValuePair<int, string> pair in _playerCoalition)
                {
                    RelationState relation;
                    if (pair.Key >= 1 && pair.Key <= 8 && _relations.TryGetValue(pair.Value, out relation) && relation.ExtendedState != null && DiplomacyCombatRules.TreatyProtects(relation.Allied, relation.ExtendedState.Agreement, relation.ExtendedState.AgreementUntilDay, Math.Max(0, _lastProcessedDay)))
                        targets[pair.Key] = new TreatyIntentTarget(relation, _sessionGeneration, _mapEpoch, _localPlayer);
                }

            Volatile.Write(ref _treatyIntentTargets, targets);
        }

#if DIPLOMACY_PRIVATE_TESTS
        private static void RunBegin()
        {
            _humanCommandFrame = null;
        }

        private static Exception RunEnd(Exception __exception)
        {
            _humanCommandFrame = null;
            return __exception;
        }

        private static void TroopSelectionPrefix(int __0, bool __2, bool __4, bool __5, int[] __6)
        {
            _humanCommandFrame = null;
            // COPY native 8F596..8F5AE: SH1 mode consumes left-up (3),
            // DE mode consumes right-up. A selection rectangle is not attack.
            if ((ConfigSettings.Settings_SH1RTSControls ? __0 != 3 : !__2) || __4 || __5)
                return;
            DiplomacyFeature owner = Volatile.Read(ref _activeOwner);
            if (owner == null || !Volatile.Read(ref owner._mapActive) || Volatile.Read(ref owner._mapDisabled))
                return;
            owner.NoteLane(LaneInput);
            _humanCommandFrame = new HumanCommandFrame
            {
                UnderCursor = __6 == null ? new int[0] : (int[])__6.Clone()
            };
        }

        private static void PreDllActionsPostfix(ref int __0, ref int __1)
        {
            HumanCommandFrame frame = _humanCommandFrame;
            if (frame == null)
                return;
            DiplomacyFeature owner = Volatile.Read(ref _activeOwner);
            if (owner == null || !IsNativeDiplomacySectionHeld)
                return;
            owner.NoteLane(LaneInput);
            try
            {
                owner.CaptureHumanWarIntent(frame, __0, __1, frame.UnderCursor);
            }
            catch
            {
                _humanCommandFrame = null;
            } // Never throw through the engine frame.
        }

#else
        private static void ClearHumanCommandFrame() => _humanCommandFrame = null;

        private static void OnSharedTroopCommandInput(TroopCommandInput input)
        {
            _humanCommandFrame = null;
            DiplomacyFeature owner = Volatile.Read(ref _activeOwner);
            if (owner == null || !Volatile.Read(ref owner._mapActive) || Volatile.Read(ref owner._mapDisabled) ||
                !IsNativeDiplomacySectionHeld)
                return;

            owner.NoteLane(LaneInput);
            var frame = new HumanCommandFrame();
            _humanCommandFrame = frame;
            try
            {
                owner.CaptureHumanWarIntent(frame, input.X, input.Y, input.UnitsUnderCursor);
            }
            catch
            {
                _humanCommandFrame = null;
            }
        }
#endif

        // SE 2.0-2.11 expose GameUnit+0x92 as byte, but Vanilla's
        // troop-command owner consumer MOVSX-reads the *signed WORD*.
        // SE 2.14.1 exposes ushort at the same offset. Reading only the
        // legacy low byte could falsely authorize a 0x0101 owner as player 1.
        // The full native WORD is required regardless of managed projection;
        // unexpectedly high/non-player values will fail the local-player test.
        // The native 0x490-byte record and all offsets are ABI-checked below.
        private static class HumanNativeFields
        {
            internal static readonly int OwnerOffset;
            internal static readonly int SelectionOffset;
            static HumanNativeFields()
            {
                Type layout = typeof(GameUnit);
                if (Marshal.SizeOf(layout) != 0x490)
                    throw new InvalidOperationException("Unsupported native GameUnit size");
                FieldInfo owner = layout.GetField("r_ControllableForPlayerId", BindingFlags.Public | BindingFlags.Instance);
                if (owner == null || (int)Marshal.OffsetOf(layout, owner.Name) != 0x92 || (owner.FieldType != typeof(byte) && owner.FieldType != typeof(ushort)))
                    throw new InvalidOperationException("Unsupported native GameUnit owner field");
                OwnerOffset = 0x92;
                FieldInfo selection = layout.GetField("r_UnitSelected", BindingFlags.Public | BindingFlags.Instance);
                if (selection == null || selection.FieldType != typeof(ushort) || (int)Marshal.OffsetOf(layout, selection.Name) != 0xAE)
                    selection = layout.GetField("r_UnitSelected2", BindingFlags.Public | BindingFlags.Instance);
                if (selection == null || selection.FieldType != typeof(ushort) || (int)Marshal.OffsetOf(layout, selection.Name) != 0xAE)
                    throw new InvalidOperationException("Unsupported native GameUnit selection field");
                SelectionOffset = 0xAE;
            }
        }

        private static unsafe int NativeControllingPlayer(GameUnit* unit)
        {
            // Full signed 16-bit native owner contract. A byte-only managed
            // SE declaration describes one projection, not the native width.
            return *(short*)((byte*)unit + HumanNativeFields.OwnerOffset);
        }

        private static unsafe int NativeSelectedPlayer(GameUnit* unit)
        {
            return *(ushort*)((byte*)unit + HumanNativeFields.SelectionOffset);
        }

        private unsafe void CaptureHumanWarIntent(HumanCommandFrame frame, int x, int y, IReadOnlyList<int> underCursor)
        {
            GameTileManagerAPI tiles = GameTileManagerAPI.Instance;
            if (!tiles.IsTileInsideMapBounds(x, y))
                return;
            TreatyIntentTarget[] targets = Volatile.Read(ref _treatyIntentTargets);
            GameUnitManagerAPI units = GameUnitManagerAPI.Instance;
            int owner = 0, unitId = 0, buildingId = 0;
            uint globalId = 0;
            // Vanilla sorted these exact IDs by cursor depth (GameMap4048..4136).
            foreach (int candidate in underCursor)
            {
                GameUnit* unit;
                if (!units.IsValidId(candidate) || !units.TryGetUnitById(candidate, out unit) || unit == null || unit->r_AliveState != AliveState.IsAlive)
                    continue;
                owner = units.GetOwner(candidate);
                unitId = candidate;
                globalId = unit->r_GlobalId;
                break;
            }

            int tileId = tiles.GetTileId(x, y);
            if (!tiles.IsValidTileId(tileId))
                return;
            if (unitId == 0)
            {
                buildingId = tiles.GetTileBuildingId(tileId);
                GameBuildingManagerAPI buildings = GameBuildingManagerAPI.Instance;
                GameBuilding* building;
                if (buildingId > 0 && buildings.IsValidId(buildingId) && buildings.TryGetBuildingById(buildingId, out building) && building != null && building->r_AliveState == AliveState.IsAlive)
                {
                    owner = building->r_PlayerIdOwner;
                    globalId = building->r_GlobalId;
                }
                else if ((tiles.GetTilePropertyFlag(tileId) & TilePropertyFlag.IsWall) != 0)
                {
                    buildingId = 0;
                    owner = tiles.GetTilePlayerOwnerId(tileId);
                }
            }

            if (owner < 1 || owner > 8 || targets[owner] == null)
                return;
            TreatyIntentTarget treaty = targets[owner];
            var intent = new HumanWarIntent
            {
                Treaty = treaty,
                TargetPlayer = owner,
                UnitId = unitId,
                BuildingId = buildingId,
                TargetGlobalId = globalId,
                TileId = tileId,
                X = x,
                Y = y
            };
            // SE 2.9.0 renamed the UInt16 selection-owner field at 0xAE.
            bool selected = false;
            GameTribeManagerAPI tribes = GameTribeManagerAPI.Instance;
            foreach (int id in units.GetAllAliveUnits())
            {
                GameUnit* unit;
                if (!units.IsValidId(id) || !units.TryGetUnitById(id, out unit) || unit == null || unit->r_AliveState != AliveState.IsAlive || units.GetOwner(id) != treaty.Local || NativeControllingPlayer(unit) != treaty.Local || NativeSelectedPlayer(unit) != treaty.Local || !IsCombatUnit(units.GetType(id)))
                    continue;
                selected = true;
                int tribeId = unit->r_TribeId;
                GameTribe* tribe;
                if (tribeId > 0 && tribes.IsValidId(tribeId) && tribes.TryGetTribeById(tribeId, out tribe) && tribe != null && tribe->r_PlayerIdOwner == treaty.Local)
                    intent.SelectedTribes[tribeId] = tribe->r_GlobalId;
            }

            if (!selected)
                return;
            frame.Intent = intent;
            Interlocked.Exchange(ref _humanWarIntents[owner], intent);
            // Most commands commit before RunTick. Under contention only this
            // captured native command is allowed; the managed event is drained
            // by Frame or Save before taking the persistent model snapshot.
            if (!Monitor.TryEnter(_gate))
                return;
            try
            {
                PumpHumanWarIntents();
            }
            finally
            {
                Monitor.Exit(_gate);
            }
        }

        private void PumpHumanWarIntents(bool publishProtection = true)
        {
            bool changed = false;
            for (int player = 1; player <= 8; player++)
            {
                HumanWarIntent intent = Interlocked.Exchange(ref _humanWarIntents[player], null);
                if (intent == null)
                    continue;
                TreatyIntentTarget treaty = intent.Treaty;
                string coalitionId;
                RelationState relation;
                if (!_mapActive || _mapDisabled || treaty.Generation != _sessionGeneration || treaty.Epoch != _mapEpoch || treaty.Local != _localPlayer || !_playerCoalition.TryGetValue(player, out coalitionId) || coalitionId != treaty.CoalitionId || !_relations.TryGetValue(coalitionId, out relation) || !treaty.Matches(relation) || relation.ExtendedState == null || !DiplomacyCombatRules.TreatyProtects(relation.Allied, relation.ExtendedState.Agreement, relation.ExtendedState.AgreementUntilDay, Math.Max(0, _lastProcessedDay)))
                    continue;
                DiplomacyAgreementRules.BreakAgreementByPlayer(relation, Math.Max(0, _lastProcessedDay), _coalitions[coalitionId].Target, DiplomacyBreachKind.TreacherousAttack, _rules);
                _pendingAgreementSafety.Remove(coalitionId);
                QueueBreachNotice(coalitionId, player, "command=human_release|target_player=" + player);
                changed = true;
            }

            if (changed && publishProtection)
                PublishCombatProtectionSnapshot();
        }

        private unsafe bool AllowCapturedHumanTribeOrder(TribeIssueOrderWithTargetEventArgs e)
        {
            HumanCommandFrame frame = _humanCommandFrame;
            HumanWarIntent intent = frame == null ? null : frame.Intent;
            if (intent == null || intent.Treaty.Generation != Volatile.Read(ref _sessionGeneration) || intent.Treaty.Epoch != Volatile.Read(ref _mapEpoch) || Volatile.Read(ref _mapDisabled) || !Volatile.Read(ref _mapActive) || intent.Treaty.Local != Volatile.Read(ref _localPlayer) || !IsNativeDiplomacySectionHeld || frame.AllowedTribes.Contains(e.TribeId))
                return false;
            uint expected;
            GameTribe* tribe;
            if (!intent.SelectedTribes.TryGetValue(e.TribeId, out expected) || !GameTribeManagerAPI.Instance.TryGetTribeById(e.TribeId, out tribe) || tribe == null || tribe->r_GlobalId != expected || tribe->r_PlayerIdOwner != intent.Treaty.Local)
                return false;
            bool matches = intent.UnitId > 0 ? e.AICommand == TribeAICommand.AttackUnit && e.TargetValue1 == intent.UnitId && unchecked((uint)e.TargetValue2) == intent.TargetGlobalId : intent.BuildingId > 0 ? (e.AICommand == TribeAICommand.AttackBuilding || e.AICommand == TribeAICommand.ForceAttackBuilding || e.AICommand == TribeAICommand.BuildTunnel) && e.TargetValue1 == intent.BuildingId && unchecked((uint)e.TargetValue2) == intent.TargetGlobalId : (e.AICommand == TribeAICommand.AttackWallTileId || e.AICommand == TribeAICommand.AttachLadderToWall) && e.TargetValue1 == intent.TileId;
            matches |= (e.AICommand == TribeAICommand.AttackTilePosition || e.AICommand == TribeAICommand.ThrowLava) && e.TargetValue1 == intent.X && e.TargetValue2 == intent.Y;
            if (!matches)
                return false;
            frame.AllowedTribes.Add(e.TribeId);
            return true;
        }
    }
}
