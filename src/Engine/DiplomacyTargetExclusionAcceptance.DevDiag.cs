#if SERP_DEVDIAG && !DIPLOMACY_PRIVATE_TESTS
using System;
using System.Collections.Generic;
using APIShared;
using APIShared.Automation;
using APIShared.Units;
using Shared;
using SHCDESE.API;
using SHCDESE.Interop;

namespace StrongholdEuropeDE.Runtime
{
    internal sealed partial class DiplomacyFeature
    {
        private IEnumerable<AutomationWait> CheckDiagnosticTargetExclusions(AutomationRun run)
        {
            var mission = run.CurrentMission;
            yield return run.WaitForPlayState(mission);
            yield return AutomationWait.Until("active diplomacy frame", () => HasActiveMissionTick);
            yield return AutomationWait.Until("native target exclusion hook", () => AttackTargetRules.IsAvailable);
            int local = mission.LocalPlayerId ?? throw new InvalidOperationException("Fixture has no local player.");
            foreach (var wait in run.SetPause(mission, false))
                yield return wait;
            run.InSimulation(mission, () => QueueDiagnosticSelection(local, new List<int>()));
            yield return run.WaitForTicks(mission, 3);
            yield return AutomationWait.Until("empty local selection", () =>
                LocalSelectionAPI.TryCapture(local, out var selection) && selection.Count == 0);
            foreach (var wait in run.SetPause(mission, true))
                yield return wait;

            RelationState relation = null;
            DiplomacyRelationState original = null, treaty = null;
            int foreign = 0, localTeam = 0, foreignTeam = 0, unit = 0;
            try
            {
                lock (_gate)
                {
                    if (!TryEnterNativeInventorySection(out var lease))
                        throw new InvalidOperationException("Simulation is busy after pausing the fixture.");
                    using (lease)
                    {
                        if (_missionSessionId != mission.SessionId || _localPlayer != local ||
                            !CombatGuardOperational() || _pendingTeamTransition != null ||
                            _transitionGuardPartition != null || _transitionVerificationPartition != null ||
                            _rollbackGuardActive)
                            throw new InvalidOperationException("Fixture diplomacy is not in a stable mission.");

                        localTeam = _teamPort.GetTeam(local);
                        if (localTeam <= 0 || !IsAlivePlayer(local))
                            throw new InvalidOperationException("Fixture has no active local player with a positive native team.");

                        (foreign, foreignTeam, relation) = FindDiagnosticTreatyPartner(local, localTeam);
                        unit = FindDiagnosticSelectionUnit(local);
                        PublishCombatProtectionSnapshot();
                        RequirePublishedDiagnosticPair(mission.SessionId, local, foreign, expected: false);
                        original = relation.ExtendedState;
                        treaty = original.Clone();
                        treaty.Agreement = DiplomacyAgreementKind.Truce;
                        treaty.AgreementUntilDay = checked(Math.Max(0, _lastProcessedDay) + 2);
                        relation.ExtendedState = treaty;
                        if (!TreatyProtectsPlayers(local, foreign))
                            throw new InvalidOperationException("Diagnostic treaty does not qualify for protection.");

                        PublishCombatProtectionSnapshot();
                        RequirePublishedDiagnosticPair(mission.SessionId, local, foreign, expected: true);
                        QueueDiagnosticSelection(local, new List<int> { unit });
                    }
                }

                foreach (var wait in run.SetPause(mission, false))
                    yield return wait;
                yield return run.WaitForTicks(mission, 3);
                yield return AutomationWait.Until("Extender local unit selection", () =>
                    LocalSelectionAPI.TryCapture(local, out var selection) &&
                    selection.Count == 1 && selection[0].UnitId == unit);
                foreach (var wait in run.SetPause(mission, true))
                    yield return wait;

                lock (_gate)
                {
                    if (!ReferenceEquals(relation.ExtendedState, treaty) || !TreatyProtectsPlayers(local, foreign))
                        throw new InvalidOperationException("The treaty changed after an ordinary unit selection.");
                    RequirePublishedDiagnosticPair(mission.SessionId, local, foreign, expected: true);
                }
            }
            finally
            {
                lock (_gate)
                {
                    if (_missionSessionId == mission.SessionId && treaty != null &&
                        ReferenceEquals(relation.ExtendedState, treaty))
                    {
                        relation.ExtendedState = original;
                        PublishCombatProtectionSnapshot();
                    }
                }
            }

            lock (_gate)
            {
                if (!TryEnterNativeInventorySection(out var lease))
                    throw new InvalidOperationException("Simulation is busy during treaty rollback readback.");
                using (lease)
                {
                    RequirePublishedDiagnosticPair(mission.SessionId, local, foreign, expected: false);
                    if (_teamPort.GetTeam(local) != localTeam || _teamPort.GetTeam(foreign) != foreignTeam)
                        throw new InvalidOperationException("Fixture native teams changed during the treaty probe.");
                }
            }

            DevDiag.Log(() => "diplomacy target rule accepted and removed players=" + local + "," + foreign +
                " native_teams=" + localTeam + "," + foreignTeam + " native_hook_ready=true" +
                " extender_selection=" + unit + " treaty_retained_after_selection=true");
        }

        private (int Player, int Team, RelationState Relation) FindDiagnosticTreatyPartner(int local, int localTeam)
        {
            foreach (int foreign in _roster)
            {
                if (foreign == local || IsLocalDiplomaticBlocPlayer(foreign) || !IsAlivePlayer(foreign) ||
                    !_playerCoalition.TryGetValue(foreign, out var coalitionId) ||
                    !_relations.TryGetValue(coalitionId, out var relation) ||
                    relation.ExtendedState == null || relation.ExtendedState.Agreement != DiplomacyAgreementKind.None)
                    continue;
                int foreignTeam = _teamPort.GetTeam(foreign);
                if (foreignTeam > 0 && foreignTeam != localTeam)
                    return (foreign, foreignTeam, relation);
            }

            throw new InvalidOperationException("Fixture has no eligible pair on distinct positive native teams.");
        }

        private static bool QueueDiagnosticSelection(int local, List<int> unitIds)
        {
            if (PlayerPerspectiveAPI.GetControlledPlayerId() != local ||
                PlayerPerspectiveAPI.GetViewedPlayerId() != local ||
                !GamePlayerManagerAPI.Instance.SetSelectedChimps(unitIds))
                throw new InvalidOperationException("Extender selection was refused for the local player.");
            return true;
        }

        private static unsafe int FindDiagnosticSelectionUnit(int local)
        {
            foreach (int id in UnitAccess.GetAllReallyAliveUnits())
                if (UnitAccess.TryGetById(id, out var unit, out _) && unit->r_ControllableForPlayerId == local &&
                    unit->r_GlobalId != 0 && (unit->r_UnitChimp == eChimps.CHIMP_TYPE_ARCHER ||
                    unit->r_UnitChimp == eChimps.CHIMP_TYPE_SPEARMAN || unit->r_UnitChimp == eChimps.CHIMP_TYPE_SWORDSMAN))
                    return id;

            int lordId = GamePlayerManagerAPI.Instance.GetLordUnitId(local);
            if (UnitAccess.TryGetById(lordId, out var lord, out _) && UnitAccess.IsReallyAlive(lord) &&
                lord->r_ControllableForPlayerId == local && lord->r_GlobalId != 0 &&
                lord->r_UnitChimp == eChimps.CHIMP_TYPE_LORD)
                return lordId;
            throw new InvalidOperationException("Fixture has no selectable local troop or lord.");
        }

        private void RequirePublishedDiagnosticPair(long session, int first, int second, bool expected)
        {
            if (_publishedTargetExclusionMission != session || _publishedTargetExclusions == null ||
                _publishedTargetExclusions.Protects(first, second) != expected ||
                _publishedTargetExclusions.Protects(second, first) != expected ||
                _targetExclusionPublicationFailure != null)
                throw new InvalidOperationException("Unexpected published Diplomacy target rule for the diagnostic pair.");
        }
    }
}
#endif
