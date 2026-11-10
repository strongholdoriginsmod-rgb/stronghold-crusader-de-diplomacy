#if SERP_DEVDIAG
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using APIShared;
using APIShared.Automation;
using APIShared.Commands;
using BepInEx;
using BepInEx.Logging;
using CrusaderDE;
using SHCDESE.API;
using SHCDESE.Interop;
using SHCDESE.Interop.Enums;
using Shared;
using R3;
using SHCDESE.EventAPI;
using SHCDESE.EventAPI.Player;

namespace StrongholdEuropeDE.Runtime
{
    internal sealed partial class DiplomacyFeature
    {
        private const int DefendAllyOrder = (int)AIRequestOrderType.DefendMe;
        private const int DefendObservationBatchTicks = 64;
        private const int DefendObservationLimitTicks = 4096;
        private const int DefendQueueObservationTicks = 128;
        private const int SaveProbeAmount = 723611;
        private const ulong SaveProbeId = 1;
        private bool _diagnosticMutateReplay;
        private HumanAllyMilitaryRequest _diagnosticObservedRequest;
        private int _diagnosticDefendPre, _diagnosticDefendPost, _diagnosticDefendAccepted, _diagnosticDefendSkipped;
        private uint _diagnosticDefendProfile, _diagnosticDefendBlockFlag;
        private int _diagnosticDefendArmy;

        internal void StartDiagnosticAcceptance(ManualLogSource logger)
        {
            DevDiag.Attach(logger);
            string request = Path.Combine(Paths.ConfigPath, "SerpAutomation", CompatibilityContracts.PluginId + ".run");
            if (!File.Exists(request))
                return;

            string[] lines = File.ReadAllLines(request);
            File.Delete(request);
            if (lines.Length != 2 || !Guid.TryParseExact(lines[0], "N", out var runId) || runId == Guid.Empty)
                throw new InvalidDataException("Invalid diplomacy acceptance request.");

            var api = APIShared.ApiShared.ForMod(CompatibilityContracts.PluginId);
            var fixture = Path.Combine(Paths.BepInExRootPath, "SerpAutomationRollback", "fixture.sav");
            var steps = new List<AutomationStep>
            {
                new AutomationStep("load-fixture", TimeSpan.FromSeconds(120), run => run.LoadSave(fixture)),
            };

            if (lines[1] == "diplomacy-ally-orders")
            {
                if (!GameActionEvents.TryRegister(CompatibilityContracts.PluginId, "z-diplomacy-devdiag-late-pre",
                        MutateDiagnosticReplay, null, out var reason, int.MaxValue))
                    throw new InvalidOperationException(reason);
                PlayerR3EventHooks.OnPlayerAIEvaluateDefendOrder.Observable.Subscribe(ObserveDiagnosticDefend);
                steps.Add(new AutomationStep("native-order", TimeSpan.FromSeconds(100),
                    CheckDiagnosticAllyOrder, missionBound: true));
            }
            else if (lines[1] == "diplomacy-target-rules")
            {
                steps.Add(new AutomationStep("shared-target-rules", TimeSpan.FromSeconds(40),
                    CheckDiagnosticTargetExclusions, missionBound: true));
            }
            else if (lines[1] == "diplomacy-save")
            {
                steps.Add(new AutomationStep("write-managed-diplomacy-save", TimeSpan.FromSeconds(30),
                    run => CheckDiagnosticSave(run, runId), missionBound: true));
            }
            else if (lines[1] == "diplomacy-cold-load")
            {
                steps.Add(new AutomationStep("cold-save-owner", TimeSpan.FromSeconds(25),
                    CheckColdDiplomacySave, missionBound: true));
                steps.Add(new AutomationStep("replace-map", TimeSpan.FromSeconds(50),
                    CheckDiagnosticMapReplacement));
            }
            else
            {
                throw new InvalidDataException("Unknown diplomacy acceptance scenario.");
            }

            api.Automation.Start(runId, steps, logger, quitWhenDone: true);
        }

        private void MutateDiagnosticReplay(GameActionPreEventArgs call)
        {
            var replay = _dispatchingAllyOrder;
            if (_diagnosticMutateReplay && replay != null && call.Command == Enums.GameActionCommand.Ally_Orders)
                call.ActionState = replay.Requester;
        }

        private IEnumerable<AutomationWait> CheckDiagnosticAllyOrder(AutomationRun run)
        {
            var mission = run.CurrentMission;
            yield return run.WaitForPlayState(mission);
            yield return AutomationWait.Until("active diplomacy frame", () => HasActiveMissionTick);
            foreach (var wait in run.SetPause(mission, false))
                yield return wait;
            yield return run.WaitForTicks(mission, 2);

            var rejectedRequest = run.InSimulation(mission, () => SelectDiagnosticAlly(false)) ??
                throw new InvalidOperationException("No valid allied route for the rejected-ticket checks.");
            Volatile.Write(ref _diagnosticObservedRequest, rejectedRequest);
            if (!Volatile.Read(ref _sharedAllyOrderActive))
                throw new InvalidOperationException("Expected verified production ally forwarding.");
            try
            {
                _diagnosticMutateReplay = true;
                var rejected = QueueDiagnosticAllyOrder(rejectedRequest);
                yield return AutomationWait.Until("late Pre change vetoed", () =>
                    rejected.SharedManagedPostObserved && rejected.SharedManagedWasSkipped);
                _diagnosticMutateReplay = false;
                var stale = new HumanAllyOrderAttempt
                {
                    Generation = _sessionGeneration - 1, Epoch = _mapEpoch,
                    Requester = rejectedRequest.Requester, Consulted = rejectedRequest.Consulted,
                    Target = rejectedRequest.Target, Opcode = rejectedRequest.Opcode, NativeRequest = rejectedRequest
                };
                bool staleVeto = run.InSimulation(mission, () =>
                {
                    _dispatchingAllyOrder = stale;
                    try
                    {
                        EngineInterface.GameAction(Enums.GameActionCommand.Ally_Orders,
                            rejectedRequest.Opcode, rejectedRequest.Consulted, rejectedRequest.Target);
                        return stale.SharedManagedPostObserved && stale.SharedManagedWasSkipped;
                    }
                    finally { _dispatchingAllyOrder = null; }
                });
                yield return run.WaitForTicks(mission, DefendQueueObservationTicks);
                if (!staleVeto || Volatile.Read(ref _diagnosticDefendPre) != 0 ||
                    Volatile.Read(ref _diagnosticDefendPost) != 0)
                    throw new InvalidOperationException("A rejected ticket reached the native evaluator.");
                DevDiag.Log(() => "diplomacy late-pre and stale replay vetoed; native evaluations=0");

                var samples = new Dictionary<int, (uint Profile, uint BlockFlag, int Army, int Threshold)>();
                HumanAllyMilitaryRequest request;
                for (int observedTicks = 0; ; observedTicks += DefendObservationBatchTicks)
                {
                    samples.Clear();
                    request = run.InSimulation(mission, () => SelectDiagnosticAlly(true, samples));
                    if (request != null || observedTicks >= DefendObservationLimitTicks)
                        break;
                    yield return run.WaitForTicks(mission, DefendObservationBatchTicks);
                }
                DevDiag.Log(() => "diplomacy natural Defend readiness " + string.Join("; ", samples.OrderBy(p => p.Key)
                    .Select(p => $"slot={p.Key},aic={p.Value.Profile},block={p.Value.BlockFlag},army={p.Value.Army},threshold={p.Value.Threshold}")));
                if (request == null)
                    throw new InvalidOperationException("No naturally eligible AI ally in the observation window.");
                Volatile.Write(ref _diagnosticObservedRequest, request);
                var valid = QueueDiagnosticAllyOrder(request);
                yield return AutomationWait.Until("fresh matching native order tuple", () =>
                {
                    var status = Volatile.Read(ref _allyOrderStatus[request.Consulted]);
                    if (Volatile.Read(ref _diagnosticDefendPost) != 0 &&
                        Volatile.Read(ref _diagnosticDefendAccepted) == 0)
                        throw new InvalidOperationException("Vanilla declined the selected ally order: " +
                            DescribeDiagnosticDefendEvaluation());
                    return valid.SharedManagedPostObserved && !valid.SharedManagedWasSkipped &&
                        status != null && ReferenceEquals(status.HumanSource, valid) &&
                        status.NativeActivity == AllyOrderActivityRelation.MatchingLaterSnapshot;
                });
                yield return run.WaitForTicks(mission, DefendQueueObservationTicks);
                if (!valid.DurableForwarded || valid.DurableRequestId == 0 ||
                    Volatile.Read(ref _diagnosticDefendPre) != 1 ||
                    Volatile.Read(ref _diagnosticDefendPost) != 1 ||
                    Volatile.Read(ref _diagnosticDefendAccepted) != 1 ||
                    Volatile.Read(ref _diagnosticDefendSkipped) != 0)
                    throw new InvalidOperationException("The Defend replay lacks a single unforced native evaluation and durable submission: " +
                        DescribeDiagnosticDefendEvaluation());
                DevDiag.Log(() => "diplomacy native order observed requester=" + request.Requester +
                    " consulted=" + request.Consulted + " durable=" + valid.DurableRequestId + " " +
                    DescribeDiagnosticDefendEvaluation());
            }
            finally
            {
                _diagnosticMutateReplay = false;
                Volatile.Write(ref _diagnosticObservedRequest, null);
            }

        }

        private IEnumerable<AutomationWait> CheckDiagnosticSave(AutomationRun run, Guid runId)
        {
            var mission = run.CurrentMission;
            yield return run.WaitForPlayState(mission);
            yield return AutomationWait.Until("active diplomacy frame", () => HasActiveMissionTick);
            lock (_gate)
            {
                int counterparty = _roster.First(player => player != _localPlayer);
                if (_sharedGoodsRequestActive || _allyOrders.Any(order => order != null) ||
                    _allyInFlight.Any(order => order != null) ||
                    _missionSessionId != mission.SessionId ||
                    !TryCaptureDiplomaticRequest(DiplomaticRequestKind.Goods, DiplomaticRequestOrigin.ModAuthored,
                        _localPlayer, counterparty, 0, (int)eGoods.STORED_GOLD, SaveProbeAmount, 0, out var id) ||
                    id != SaveProbeId ||
                    !TryDecideDiplomaticRequest(id, false, DiplomaticRequestDecisionEvidence.ModPolicy))
                    throw new InvalidOperationException("Could not record the expected inert save probe.");
            }

            string path = Path.Combine(ConfigSettings.GetSavesPath(), "SerpAcceptance-" + runId.ToString("N") + ".sav");
            string outerVetoPath = path + ".outer-veto.sav";
            string innerVetoPath = path + ".inner-veto.sav";
            if (File.Exists(path) || File.Exists(outerVetoPath) || File.Exists(innerVetoPath))
                throw new InvalidOperationException("An acceptance save path already exists.");
            int saveBegins = 0, saveCompletions = 0;
            int outerVetoCompletions = 0, innerVetoCompletions = 0;
            bool matchingSave = false;
            if (!APIShared.Savegames.ManagedSaveEvents.TryRegister(CompatibilityContracts.PluginId,
                    "diag-original-save",
                    call =>
                    {
                        if (call.MapSave)
                            return null;
                        if (call.Path == path)
                            saveBegins++;
                        return call.Path == path || call.Path == outerVetoPath || call.Path == innerVetoPath
                            ? call : null;
                    },
                    (completion, state) =>
                    {
                        if (!(state is APIShared.Savegames.ManagedSaveCall started))
                            return;
                        bool matching = ReferenceEquals(completion.Call, started) &&
                            _missionSessionId == mission.SessionId &&
                            completion.Path == started.Path && completion.Exception == null;
                        if (started.Path == path)
                        {
                            saveCompletions++;
                            matchingSave = matching && !completion.WasSkipped && completion.Result == true;
                        }
                        else if (matching && completion.WasSkipped && !completion.Result.HasValue)
                        {
                            if (started.Path == outerVetoPath)
                                outerVetoCompletions++;
                            else if (started.Path == innerVetoPath)
                                innerVetoCompletions++;
                        }
                    }, out string saveObserverFailure))
                throw new InvalidOperationException(saveObserverFailure);
            var location = CameraControls2D.instance.getScreenXYForSaveCentring(
                (int)((float)(UnityEngine.Screen.width / 64) / PerfectPixelWithZoom.instance.getZoom()),
                (int)((float)(UnityEngine.Screen.height / 16) / PerfectPixelWithZoom.instance.getZoom()));
            int centreX = -1, centreY = -1;
            GameMap.instance.getRotationCentre(ref centreX, ref centreY);
            if (!EngineInterface.SaveSaveGame(path, location.x, location.y, centreX, centreY, false, false, false) ||
                !File.Exists(path) || new FileInfo(path).Length == 0)
                throw new InvalidOperationException("Original acceptance save did not complete.");
            if (saveBegins != 1 || saveCompletions != 1 || !matchingSave)
                throw new InvalidOperationException("The shared save observer did not pair the actual save.");
            if (!HasPassiveOriginalSaveReadback())
                throw new InvalidOperationException("Diplomacy did not verify its original SaveData bytes from the shared save completion.");
            DevDiag.Log(() => "diplomacy shared managed save and archive readback verified once");
            DevDiag.Log(() => "diplomacy managed save recorded id=" + SaveProbeId);

            if (_saveTarget == null)
                throw new InvalidOperationException("The original save wrapper is unavailable.");
            var flags = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic;
            _harmony.Patch(_saveTarget, prefix: new HarmonyLib.HarmonyMethod(
                typeof(DiplomacyFeature).GetMethod(nameof(VetoDiagnosticSaveAttempt), flags))
                { priority = HarmonyLib.Priority.Last });
            var veto = new DiagnosticSaveVeto(outerVetoPath, innerVetoPath);
            bool outerResult;
            _diagnosticSaveVeto = veto;
            try
            {
                outerResult = EngineInterface.SaveSaveGame(outerVetoPath, 0, 0, 0, 0, false, false, false);
            }
            finally { _diagnosticSaveVeto = null; }

            if (outerResult || veto.InnerResult != false || outerVetoCompletions != 1 ||
                innerVetoCompletions != 1 || File.Exists(outerVetoPath) || File.Exists(innerVetoPath) ||
                HasPassiveOriginalSaveReadback())
                throw new InvalidOperationException("Nested vetoes did not clear the earlier save evidence safely.");
            DevDiag.Log(() => "diplomacy nested save veto cleared shared archive evidence");
        }

        private sealed class DiagnosticSaveVeto
        {
            internal DiagnosticSaveVeto(string outerPath, string innerPath)
            {
                OuterPath = outerPath;
                InnerPath = innerPath;
            }

            internal readonly string OuterPath, InnerPath;
            internal bool? InnerResult;
        }

        [ThreadStatic] private static DiagnosticSaveVeto _diagnosticSaveVeto;

        private static bool VetoDiagnosticSaveAttempt(string __0, ref bool __result)
        {
            var veto = _diagnosticSaveVeto;
            if (veto == null || (__0 != veto.OuterPath && __0 != veto.InnerPath))
                return true;

            // Nest after the shared begin observer. Both originals are vetoed.
            if (__0 == veto.OuterPath)
                veto.InnerResult = EngineInterface.SaveSaveGame(veto.InnerPath, 0, 0, 0, 0, false, false, false);
            __result = false;
            return false;
        }

        private unsafe void ObserveDiagnosticDefend(PlayerAIEvaluateDefendOrder call)
        {
            var request = Volatile.Read(ref _diagnosticObservedRequest);
            if (request == null || call == null || call.SourcePlayerId != request.Requester ||
                call.TargetPlayerId != request.Consulted)
                return;
            if (call.Phase == EventHookPhase.Pre)
            {
                if (call.SkipOriginalFunction)
                    Interlocked.Increment(ref _diagnosticDefendSkipped);
                // Only copy primitives on the evaluator thread. The selected AIC
                // threshold was read under the original monitor during selection.
                if (GamePlayerManagerAPI.Instance.TryGetPlayerResourcesById(request.Consulted, out GamePlayerResources* resources) &&
                    resources != null)
                {
                    _diagnosticDefendProfile = resources->r_AILordMinusOne;
                    _diagnosticDefendBlockFlag = resources->N000040FB;
                    _diagnosticDefendArmy = unchecked((int)resources->N00004015);
                }
                Interlocked.Increment(ref _diagnosticDefendPre);
            }
            else if (call.Phase == EventHookPhase.Post)
            {
                if (call.ReturnValue)
                    Interlocked.Increment(ref _diagnosticDefendAccepted);
                Interlocked.Increment(ref _diagnosticDefendPost);
            }
        }

        private string DescribeDiagnosticDefendEvaluation()
        {
            int pre = Volatile.Read(ref _diagnosticDefendPre);
            return $"pre={pre},post={Volatile.Read(ref _diagnosticDefendPost)},accepted={Volatile.Read(ref _diagnosticDefendAccepted)}," +
                $"skipped={Volatile.Read(ref _diagnosticDefendSkipped)},aicAtPre={_diagnosticDefendProfile}," +
                $"blockAtPre={_diagnosticDefendBlockFlag},armyAtPre={_diagnosticDefendArmy}";
        }

        private unsafe HumanAllyMilitaryRequest SelectDiagnosticAlly(bool requireNaturalAcceptance,
            Dictionary<int, (uint Profile, uint BlockFlag, int Army, int Threshold)> samples = null)
        {
            if (!IsSoloEngineSession(out string reason))
                throw new InvalidOperationException("Diagnostic requires a live solo skirmish: " + reason);
            var players = GamePlayerManagerAPI.Instance;
            int profileCount = ScriptExtenderNativeBridge.AicCount();
            HumanAllyMilitaryRequest selected = null;
            long bestMargin = -1;
            for (int player = 1; player <= GamePlayerManagerAPI.MAX_PLAYERS; ++player)
            {
                if (!IsCoalitionAllied(player) ||
                    !HumanAllyMilitaryRequest.TryCreate(_localPlayer, player, DefendAllyOrder, 0, out var request, out _) ||
                    !OriginalHumanAllyOrderBridge.CanDispatchUnderOriginalMonitor(request) ||
                    !OriginalHumanAllyOrderBridge.TryReadActiveUnderOriginalMonitor(player, out var before, out _) ||
                    OriginalHumanAllyOrderRules.Matches(request, before))
                    continue;
                if (!requireNaturalAcceptance)
                    return request;
                if (!players.TryGetPlayerResourcesById(player, out GamePlayerResources* resources) || resources == null)
                    continue;
                uint identity = resources->r_AILordMinusOne;
                if (identity == 0 || identity - 1 >= profileCount)
                    continue;
                int threshold = ScriptExtenderNativeBridge.Aic((int)(identity - 1)).siege_troops_before_will_come_to_rescue;
                int army = unchecked((int)resources->N00004015);
                uint blockFlag = resources->N000040FB;
                if (samples != null)
                    samples[player] = (identity, blockFlag, army, threshold);
                // Signed comparison used by the hash-verified Vanilla Defend evaluator.
                long margin = (long)army - threshold;
                if (blockFlag != 0 || margin <= bestMargin)
                    continue;
                bestMargin = margin;
                selected = request;
            }
            return selected;
        }

        private HumanAllyOrderAttempt QueueDiagnosticAllyOrder(HumanAllyMilitaryRequest request)
        {
            lock (_gate)
            {
                if (_sharedGoodsRequestActive || !HasActiveMissionTick ||
                    _missionLifecycle.Current?.SessionId != _missionSessionId)
                    throw new InvalidOperationException("Diagnostic mission/goods gate is not safe.");
                int unused = 0;
                if (AllyGameActionPrefix(Enums.GameActionCommand.Ally_Orders, request.Opcode,
                        request.Consulted, request.Target, ref unused))
                    throw new InvalidOperationException("Diagnostic command could not enter the existing ticket pipeline.");
                return Volatile.Read(ref _allyOrders[request.Consulted]) ??
                    throw new InvalidOperationException("Diagnostic ticket was not queued.");
            }
        }

        private IEnumerable<AutomationWait> CheckColdDiplomacySave(AutomationRun run)
        {
            var mission = run.CurrentMission;
            yield return run.WaitForPlayState(mission);
            yield return AutomationWait.Until("restored diplomacy frame", () => HasActiveMissionTick);
            lock (_gate)
            {
                if (_sharedGoodsRequestActive || _allyOrders.Any(order => order != null) ||
                    _allyInFlight.Any(order => order != null) ||
                    _missionSessionId != mission.SessionId || _pendingSavePayload != null || _pendingLegacySavePayload != null)
                    throw new InvalidOperationException("Cold load left forwarding or staged payload active.");
                var rows = _requestHistory?.Records.Where(record =>
                    record.Kind == DiplomaticRequestKind.Goods &&
                    record.Origin == DiplomaticRequestOrigin.ModAuthored &&
                    record.GoodId == (int)eGoods.STORED_GOLD && record.Quantity == SaveProbeAmount &&
                    record.Requester == _localPlayer).ToArray();
                if (rows == null || rows.Length != 1 || rows[0].Id != SaveProbeId ||
                    rows[0].Status != DiplomaticRequestStatus.Refused)
                    throw new InvalidOperationException("Cold load did not restore exactly one inert save probe.");
                DevDiag.Log(() => "diplomacy managed save restored id=" + rows[0].Id);
            }
        }
        private IEnumerable<AutomationWait> CheckDiagnosticMapReplacement(AutomationRun run)
        {
            var previous = run.CurrentMission;
            string previousMap = _mapFingerprint;
            lock (_gate)
            {
                int player = _roster.First(id => id != _localPlayer);
                if (!Volatile.Read(ref _sharedAllyOrderActive) || _allyInFlight[player] != null)
                    throw new InvalidOperationException("Cannot stage an inert reply for mission retirement.");
                Volatile.Write(ref _allyInFlight[player], new HumanAllyOrderAttempt
                {
                    Generation = _sessionGeneration,
                    Epoch = _mapEpoch
                });
            }
            var view = MainViewModel.Instance;
            view.HUDIngameMenu.restartSkirmishMapInfo = null;
            view.HUDmain.InGameOptions(null, null);
            view.ButtonIngameMenuCommand.Execute("6");
            yield return AutomationWait.Until("leave mission confirmation", () => view.Show_HUD_Confirmation);
            view.ButtonConfirmationCommand.Execute("1");
            yield return AutomationWait.Until("retired diplomacy mission", () =>
                run.CurrentMission == null && !HasActiveMissionTick);

            foreach (var wait in run.StartSkirmish("4spielerkarte.map", new[] { AILords.SK_RAT }))
                yield return wait;
            var mission = run.CurrentMission;
            yield return AutomationWait.Until("new skirmish briefing", () => view.Show_HUD_Briefing);
            view.ButtonBriefingResumeCommand.Execute(null);
            yield return run.WaitForPlayState(mission);
            yield return AutomationWait.Until("new diplomacy mission tick", () => HasActiveMissionTick);
            yield return AutomationWait.Until("lord chat bound on the new local map", () => _lordChatBound);
            lock (_gate)
            {
                if (!Volatile.Read(ref _sharedAllyOrderActive))
                    throw new InvalidOperationException("Map replacement disabled verified HUD forwarding.");
                if (mission.SessionId == previous.SessionId || _missionSessionId != mission.SessionId ||
                    _mapFingerprint == previousMap || _pendingSavePayload != null || _pendingLegacySavePayload != null ||
                    _pendingTeamTransition != null || HasPendingGoodsCommand ||
                    _allyOrders.Any(order => order != null) ||
                    _allyInFlight.Any(order => order != null) || _allyReplies.Any(order => order != null) ||
                    (_requestHistory?.Records.Any(record => record.Origin == DiplomaticRequestOrigin.ModAuthored &&
                        record.GoodId == (int)eGoods.STORED_GOLD && record.Quantity == SaveProbeAmount) ?? false))
                    throw new InvalidOperationException("Map replacement retained prior diplomacy state.");
                DevDiag.Log(() => "diplomacy map replaced old=" + previous.SessionId + " new=" + mission.SessionId +
                    " with verified HUD forwarding and without old tickets");
            }
        }
    }
}
#endif

