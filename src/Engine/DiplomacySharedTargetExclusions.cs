#if !DIPLOMACY_PRIVATE_TESTS
using System.Collections.Generic;
using APIShared.Units;

namespace StrongholdEuropeDE.Runtime
{
    internal sealed partial class DiplomacyFeature
    {
        private AttackTargetRuleRegistration _targetExclusionRules;
        private long _targetExclusionRevision;
        private long _publishedTargetExclusionMission;
        private CombatProtectionSnapshot _publishedTargetExclusions;
        private string _targetExclusionPublicationFailure;

        private void RegisterSharedTargetExclusions()
        {
            if (_targetExclusionRules != null)
                return;

            if (!AttackTargetRules.TryRegister(CompatibilityContracts.PluginId, "protected-player-pairs",
                    out _targetExclusionRules, out var reason))
            {
                Log("visible_truce", "status=unavailable|reason=" + Safe(reason));
                return;
            }

            Log("visible_truce", "status=shared_rules_registered");
        }

        // Called from the existing snapshot publisher under _gate. Registering
        // the service does not authorize a world or broaden the SP mode gate.
        private void PublishSharedTargetExclusions(CombatProtectionSnapshot snapshot)
        {
            if (_targetExclusionRules == null)
                return;

            var mission = _missionLifecycle.Current;
            if (mission == null || mission.SessionId != _missionSessionId ||
                _missionPlayState.Current?.SessionId != mission.SessionId ||
                !mission.Mode.IsSingleplayerSkirmish || mission.Mode.IsRealMultiplayer)
            {
                // The API retires mission-owned rules at the world boundary.
                _publishedTargetExclusionMission = 0;
                _publishedTargetExclusions = null;
                _targetExclusionPublicationFailure = null;
                return;
            }

            if (_publishedTargetExclusionMission == mission.SessionId &&
                snapshot.HasSamePairs(_publishedTargetExclusions))
                return;

            var rules = new List<AttackTargetRule>();
            for (int first = 1; first <= 8; first++)
                for (int second = first + 1; second <= 8; second++)
                    if (snapshot.Protects(first, second))
                        rules.Add(new AttackTargetRule(new AttackTargetSelector(playerId: first),
                            new AttackTargetSelector(playerId: second), bidirectional: true));

            if (!_targetExclusionRules.TryReplaceRules(mission, ++_targetExclusionRevision, rules, out var reason))
            {
                if (_targetExclusionPublicationFailure != reason)
                {
                    _targetExclusionPublicationFailure = reason;
                    Log("visible_truce", "status=shared_rules_rejected|reason=" + Safe(reason));
                }
                return;
            }

            _publishedTargetExclusionMission = mission.SessionId;
            _publishedTargetExclusions = snapshot;
            _targetExclusionPublicationFailure = null;
        }
    }
}
#endif
