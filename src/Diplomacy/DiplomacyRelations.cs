using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using CrusaderDE;
using SHCDESE.API;
using SHCDESE.API.Components.SaveData;
using SHCDESE.EventAPI;
using SHCDESE.EventAPI.AI;
using SHCDESE.EventAPI.Tribes;
using SHCDESE.Interop;
using SHCDESE.Interop.Enums;

namespace StrongholdEuropeDE.Runtime
{
    internal sealed partial class DiplomacyFeature
    {
        private sealed class DemandPackageLine
        {
            internal int GoodId;
            internal int Amount;
            internal int SenderBefore;
            internal int RecipientBefore;
            internal ulong RequestId, ModGiftReceiptId;
        }

        private sealed class WorldForceUnit
        {
            internal int X;
            internal int Y;
            internal long MilitaryValue;
        }

        /// <summary>Kernzone eines Fuersten (Burgtor, Weltkoordinaten) und
        /// die Vertragsseite, der er angehoert.</summary>
        private sealed class TreatyZone
        {
            internal int Owner;
            internal int X;
            internal int Y;
            internal bool LocalBloc;
            internal DiplomacyCastleBoundary Boundary;
        }

        private sealed class TribeRecall
        {
            internal int Owner;
            internal uint GlobalId;
            internal TreatyZone Home;
        }

        private readonly HashSet<string> _pendingAgreementSafety = new HashSet<string>(StringComparer.Ordinal);
        private sealed class CounterpartHit
        {
            internal int Owner;
            internal long Distance;
            internal bool Treaty;
        }

        // A full unit catalogue traversal is deliberately throttled. At the
        // native simulation cadence 32 ticks still reacts within roughly one
        // second, while avoiding several map-wide scans per rendered frame.
        private const int IncursionScanIntervalTicks = 32;
        // Zonenradius und Staerkeschwellen: DiplomacyCombatRules (rein,
        // im Pruefstand gegen Zahlen geprueft - ).
        private bool _legacySaveHandlerRegistered;
        private byte[] _pendingLegacySavePayload;
        private long _pendingLegacySaveEpoch = -1;
        private long _pendingLegacySaveTick = -1;
        private long _pendingLegacyLoadSerial;
        private long _localPowerTick = long.MinValue;
        private long _localEconomy;
        private long _localMilitary;
        private long _lastIncursionScanTick = DiplomacyCombatRules.NeverScannedTick;
        private long _lastHostileAttributionTick = DiplomacyCombatRules.NeverScannedTick;
        // Abstandsgebot der Waffenruhe (): Kernzonen beider
        // Vertragsseiten, Rueckrufzeiten je Stamm, Protokollbudget.
        private readonly List<TreatyZone> _treatyZones = new List<TreatyZone>();
        private readonly Dictionary<int, long> _tribeRecallTicks = new Dictionary<int, long>();
        private bool _treatyStandoffActive;
        private bool _treatyStandoffDegraded;
        private long _standoffRetryTick;
        private int _standoffFaultsPending;
        private int _incursionLogLines;
        private int _standoffMovesBlocked;
        private int _standoffLogLines;
        private bool _uiPanelWarningLogged;
        private void InitializeRelation(RelationState relation, CoalitionState coalition, int gameDay)
        {
            DiplomacyPersonalitySignal signal = SelectCoalitionPersonalitySignal(coalition);
            DiplomacyPersonality profile = DiplomacyPersonalityCatalog.SelectForLord(signal);
            DiplomacyAgreementRules.Initialize(relation, gameDay, coalition.Target, profile.Kind, signal.LordId, coalition.StartingAlly);
        }

        private DiplomacyPersonalitySignal SelectCoalitionPersonalitySignal(CoalitionState coalition)
        {
            int primaryLord = -1;
            int margin = -1;
            int selectedPlayer = int.MaxValue;
            InternalAIC selectedAic = default(InternalAIC);
            int aicCount = ScriptExtenderNativeBridge.AicCount();
            foreach (int player in coalition.MemberPlayerIds.OrderBy(value => value))
            {
                int lord;
                if (!_capturedLordIds.TryGetValue(player, out lord) || lord < 0 || lord >= aicCount)
                {
                    continue;
                }

                InternalAIC aic = ScriptExtenderNativeBridge.Aic(lord);
                int candidateMargin = DiplomacyBalance.ClampMargin(aic.send_goods_margin);
                if (candidateMargin > margin || candidateMargin == margin && player < selectedPlayer)
                {
                    margin = candidateMargin;
                    primaryLord = lord;
                    selectedPlayer = player;
                    selectedAic = aic;
                }
            }

            if (primaryLord < 0)
            {
                throw new InvalidOperationException("diplomacy ExtendedState coalition has no personality signal");
            }

            int commerce = PositiveAverage(selectedAic.buy_apples_at, selectedAic.buy_cheese_at, selectedAic.buy_bread_at, selectedAic.buy_wheat_at, selectedAic.buy_hops_at, selectedAic.trade_wood_at, selectedAic.trade_stone_at, selectedAic.trade_resources_at, selectedAic.trade_flour_at, selectedAic.trade_ale_at, selectedAic.trade_pitch_at);
            int defensiveChance = AverageIncludingZero(selectedAic.chance_of_defensive1, selectedAic.chance_of_defensive2, selectedAic.chance_of_defensive3);
            int harassChance = AverageIncludingZero(selectedAic.chance_of_harrasment1, selectedAic.chance_of_harrasment2, selectedAic.chance_of_harrasment3);
            int siegeChance = AverageIncludingZero(selectedAic.chance_of_seiging1, selectedAic.chance_of_seiging2, selectedAic.chance_of_seiging3);
            int offensivePeak = new[]
            {
                selectedAic.chance_of_harrasment1,
                selectedAic.chance_of_harrasment2,
                selectedAic.chance_of_harrasment3,
                selectedAic.chance_of_seiging1,
                selectedAic.chance_of_seiging2,
                selectedAic.chance_of_seiging3,
            }.Max();
            int weaponSignal = PositiveAverage(selectedAic.buy_weapons, selectedAic.trade_weapons_at);
            return new DiplomacyPersonalitySignal
            {
                LordId = primaryLord,
                SendGoodsMargin = margin,
                CommerceOrientation = ClampSignal(28 + commerce / 2 + (selectedAic.base_gold_reserves > 0 ? 12 : 0)),
                MilitaryOrientation = ClampSignal(25 + weaponSignal / 3 + Math.Max(defensiveChance, Math.Max(harassChance, siegeChance)) / 2),
                Aggression = ClampSignal(15 + Math.Max(0, offensivePeak) * 3 / 4 + harassChance / 4),
                Caution = ClampSignal(20 + defensiveChance * 3 / 4 + margin / 5),
            };
        }

        private static int AverageIncludingZero(params int[] values)
        {
            return values == null || values.Length == 0 ? 0 : values.Select(value => Math.Max(0, Math.Min(100, value))).Sum() / values.Length;
        }

        private static int PositiveAverage(params int[] values)
        {
            int[] usable = (values ?? new int[0]).Where(value => value > 0).Select(value => Math.Min(100, value)).ToArray();
            return usable.Length == 0 ? 0 : usable.Sum() / usable.Length;
        }

        private static int ClampSignal(int value)
        {
            return Math.Max(0, Math.Min(100, value));
        }

        private void RefreshStrategicContext(CoalitionState coalition, int gameDay)
        {
            RelationState relation;
            if (coalition == null || !_relations.TryGetValue(coalition.CoalitionId, out relation) || relation.ExtendedState == null)
            {
                return;
            }

            CaptureLocalPower();
            int commonEnemies = 0;
            int hostileCoalitions = 0;
            // The selected coalition is excluded from the candidate loop. It
            // still belongs in the displayed and evaluated total when it is
            // already allied; otherwise a sole alliance was reported as zero
            // and wary rulers only noticed the third alliance onward.
            int playerAllianceCount = relation.Allied ? 1 : 0;
            long localBlocEconomy = Math.Max(0L, _localEconomy);
            long localBlocMilitary = Math.Max(0L, _localMilitary);
            long hostileEconomy = 0L;
            long hostileMilitary = 0L;
            foreach (CoalitionState candidate in _coalitions.Values)
            {
                RelationState candidateRelation;
                if (string.Equals(candidate.CoalitionId, coalition.CoalitionId, StringComparison.Ordinal) || !candidate.MemberPlayerIds.Any(IsAlivePlayer))
                {
                    continue;
                }

                // During initialisation the coalition catalogue is complete
                // before every relation has been inserted. A not-yet-created
                // relation is necessarily still hostile; indexing the
                // dictionary here used to abort maps with several AI groups.
                bool candidateAllied = _relations.TryGetValue(candidate.CoalitionId, out candidateRelation) && candidateRelation.Allied;
                if (candidateAllied)
                {
                    playerAllianceCount++;
                    localBlocEconomy = SaturatingAdd(localBlocEconomy, Math.Max(0L, candidate.Economy));
                    localBlocMilitary = SaturatingAdd(localBlocMilitary, Math.Max(0L, candidate.Military));
                }
                else
                {
                    commonEnemies++;
                    hostileCoalitions++;
                    hostileEconomy = SaturatingAdd(hostileEconomy, Math.Max(0L, candidate.Economy));
                    hostileMilitary = SaturatingAdd(hostileMilitary, Math.Max(0L, candidate.Military));
                }
            }

            // A hostile ruler counts the complete player bloc among the
            // forces arrayed against them. An allied ruler instead treats that
            // bloc as support and only the remaining coalitions as enemies.
            if (!relation.Allied)
            {
                hostileEconomy = SaturatingAdd(hostileEconomy, localBlocEconomy);
                hostileMilitary = SaturatingAdd(hostileMilitary, localBlocMilitary);
            }

            bool underPressure = (decimal)Math.Max(0L, coalition.Military) * 3m < (decimal)Math.Max(1L, hostileMilitary) * 2m;
            DiplomacyAgreementRules.UpdateStrategicContext(relation, gameDay, coalition.Target, localBlocEconomy, localBlocMilitary, coalition.Economy, coalition.Military, commonEnemies, hostileCoalitions, playerAllianceCount, hostileEconomy, hostileMilitary, underPressure);
        }

        private void RefreshInteractionContext()
        {
            int day = AbsoluteGameDay();
            CoalitionState[] living = _coalitions.Values.Where(value => value.MemberPlayerIds.Any(IsAlivePlayer)).OrderBy(value => value.CoalitionId, StringComparer.Ordinal).ToArray();
            foreach (CoalitionState coalition in living)
            {
                RefreshCoalition(coalition);
            }

            foreach (CoalitionState coalition in living)
            {
                RefreshStrategicContext(coalition, day);
            }
        }

        private void CaptureLocalPower()
        {
            if (_localPowerTick == ReadSimulationTick())
            {
                return;
            }

            GamePlayerManagerAPI players = GamePlayerManagerAPI.Instance;
            GameUnitManagerAPI units = GameUnitManagerAPI.Instance;
            long economy = Math.Max(0, players.GetPlayerGold(_localPlayer));
            foreach (int goodId in SendableGoods)
            {
                if (goodId == 15)
                {
                    continue;
                }

                int amount = Math.Max(0, players.GetGoodAmount(_localPlayer, (eGoods)goodId));
                economy = SaturatingAdd(economy, SaturatingMultiply(amount, _frozenPrices[goodId]));
            }

            economy = SaturatingAdd(economy, ProductionEconomy(new HashSet<int> { _localPlayer }));
            long military = 0L;
            foreach (int unitId in units.GetAllAliveUnits())
            {
                if (!units.IsValidId(unitId) || units.GetOwner(unitId) != _localPlayer)
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

                int maximum = Math.Max(1, units.GetMaxHealth(unitId));
                int current = Math.Max(0, Math.Min(maximum, units.GetCurrentHealth(unitId)));
                military = SaturatingAdd(military, decimal.ToInt64(decimal.Ceiling((decimal)cost * current / maximum)));
            }

            _localEconomy = economy;
            _localMilitary = military;
            _localPowerTick = ReadSimulationTick();
        }

        private unsafe void EvaluateMilitaryIncursions()
        {
            // Unity samples the simulation clock: exact modulo ticks can all
            // be missed at high speed or low FPS (audit F02, ).
            if (!DiplomacyCombatRules.ScanDue(_lastIncursionScanTick, ReadSimulationTick(), IncursionScanIntervalTicks))
            {
                return;
            }

            if (!_relations.Values.Any(relation => relation.ExtendedState != null && !relation.Allied && DiplomacyCombatRules.IsTreaty(relation.ExtendedState.Agreement)))
            {
                ClearTreatyStandoff();
                return;
            }

            int day = AbsoluteGameDay();
            var localForce = new List<WorldForceUnit>();
            IDisposable scanLease;
            if (!TryEnterNativeInventorySection(out scanLease))
                return;
            using (scanLease)
            {
                _lastIncursionScanTick = ReadSimulationTick();
                if (_treatyStandoffDegraded && unchecked(ReadSimulationTick() - _standoffRetryTick) >= 0)
                {
                    _treatyStandoffDegraded = false;
                }

                if (_standoffFaultsPending > 0)
                {
                    if (_standoffLogLines < 40)
                    {
                        _standoffLogLines++;
                        Log("treaty_standoff", "status=retry_scheduled|faults=" + _standoffFaultsPending.ToString(CultureInfo.InvariantCulture));
                    }

                    _standoffFaultsPending = 0;
                }

                RefreshTreatyZones(day);
                GameUnitManagerAPI units = GameUnitManagerAPI.Instance;
                GameTribeManagerAPI tribes = GameTribeManagerAPI.Instance;
                // Abstandsgebot (): Truppen einer Vertragsseite, die in
                // der Kernzone der anderen Seite stehen, werden je Stamm nach
                // Hause geschickt. Gemessen im Lauf bis 19:00: die KI belagerte
                // den Waffenruhe-Partner weiter - ohne Pfeile und Schaden, aber
                // mit dem ganzen Heer vor den Mauern.
                var recall = new Dictionary<int, TribeRecall>();
                foreach (int unitId in units.GetAllAliveUnits().OrderBy(id => id))
                {
                    if (!units.IsValidId(unitId))
                    {
                        continue;
                    }

                    int owner = units.GetOwner(unitId);
                    eChimps type = units.GetType(unitId);
                    if (type == eChimps.CHIMP_TYPE_LORD || !IsCombatUnit(type))
                    {
                        continue;
                    }

                    UnmanagedVector2<ushort> position = units.GetCurrentWorldTilePosition(unitId);
                    if (owner == _localPlayer)
                    {
                        localForce.Add(new WorldForceUnit { X = position.X, Y = position.Y, MilitaryValue = DiplomacyMilitaryValue(units, unitId, type), });
                        continue;
                    }

                    TreatyZone home;
                    if (!StandoffEnforceable() || !TryGetStandoffRecallHome(owner, position.X, position.Y, out home))
                    {
                        continue;
                    }

                    int tribeId = units.GetTribe(unitId);
                    if (tribeId > 0 && !recall.ContainsKey(tribeId))
                    {
                        GameTribe* tribe;
                        if (tribes.TryGetTribeById(tribeId, out tribe) && tribe != null && tribe->r_PlayerIdOwner == owner)
                            recall[tribeId] = new TribeRecall
                            {
                                Owner = owner,
                                GlobalId = tribe->r_GlobalId,
                                Home = home
                            };
                    }
                }

                EnforceTreatyStandoff(recall);
            }

            if (localForce.Count < DiplomacyCombatRules.HeavyForceMinimumUnits)
            {
                return;
            }

            foreach (CoalitionState coalition in _coalitions.Values.OrderBy(value => value.CoalitionId, StringComparer.Ordinal))
            {
                RelationState relation = _relations[coalition.CoalitionId];
                if (relation.ExtendedState == null || !DiplomacyCombatRules.TreatyProtects(relation.Allied, relation.ExtendedState.Agreement, relation.ExtendedState.AgreementUntilDay, day))
                {
                    continue;
                }

                var centers = new List<TreatyZone>();
                foreach (TreatyZone zone in _treatyZones)
                {
                    if (!zone.LocalBloc && Array.IndexOf(coalition.MemberPlayerIds, zone.Owner) >= 0)
                    {
                        centers.Add(zone);
                    }
                }

                if (centers.Count == 0)
                {
                    continue;
                }

                int count = 0;
                long strength = 0L;
                // Entfernungsbaender um das Burgtor, in lokalen Kacheln.
                // Warum (): der Benutzer marschierte mit ueber 200
                // Einheiten ein, gezaehlt wurden 7 - weil die Zone ein Kreis
                // von 18 Kacheln um das Burgtor ist, also der Burghof und
                // nicht das Gebiet. Wie weit die Streitmacht wirklich stand,
                // weiss niemand. Diese vier Zahlen sagen es, ohne einen
                // einzigen zusaetzlichen nativen Zugriff.
                int[] band = new int[IncursionBands.Length];
                foreach (WorldForceUnit unit in localForce)
                {
                    for (int index = 0; index < IncursionBands.Length; index++)
                    {
                        if (centers.Any(center => DiplomacyCombatRules.WithinBandTiles(unit.X, unit.Y, center.X, center.Y, IncursionBands[index])))
                        {
                            band[index]++;
                        }
                    }

                    bool inside = centers.Any(center => InsideTreatyZone(center, unit.X, unit.Y));
                    if (!inside)
                    {
                        continue;
                    }

                    count++;
                    strength = SaturatingAdd(strength, unit.MilitaryValue);
                }

                // Melden, was gezaehlt wurde - sonst ist "es wird nicht
                // registriert" nicht von "die Schwelle wird nie erreicht" zu
                // unterscheiden (Meldung des Benutzers ). Nur bei
                // Aenderung und nur, solange eigene Truppen ueberhaupt in der
                // Kernzone stehen.
                if (count > 0 || band[IncursionBands.Length - 1] > 0)
                {
                    int vorher;
                    int schluessel = (count * 100000) + band[IncursionBands.Length - 1];
                    if (_incursionLogLines < 60 && (!_incursionCountSeen.TryGetValue(coalition.CoalitionId, out vorher) || vorher != schluessel))
                    {
                        _incursionLogLines++;
                        // Nach dem aeussersten Band schluesseln, sonst
                        // meldet sich nichts, solange die Kernzone leer bleibt.
                        _incursionCountSeen[coalition.CoalitionId] = (count * 100000) + band[IncursionBands.Length - 1];
                        Log("incursion", "coalition=" + coalition.CoalitionId + "|status=counted|units=" + count.ToString(CultureInfo.InvariantCulture) + "|strength=" + strength.ToString(CultureInfo.InvariantCulture) + "|zones=" + centers.Count.ToString(CultureInfo.InvariantCulture) + "|need_units=" + DiplomacyCombatRules.SubstantialForceMinimumUnits.ToString(CultureInfo.InvariantCulture) + "|need_value=" + DiplomacyCombatRules.SubstantialForceMinimumValue.ToString(CultureInfo.InvariantCulture) + "|force=" + localForce.Count.ToString(CultureInfo.InvariantCulture) + "|within_18=" + band[0].ToString(CultureInfo.InvariantCulture) + "|within_36=" + band[1].ToString(CultureInfo.InvariantCulture) + "|within_72=" + band[2].ToString(CultureInfo.InvariantCulture) + "|within_144=" + band[3].ToString(CultureInfo.InvariantCulture));
                    }
                }

                bool substantial = DiplomacyCombatRules.IsSubstantialForce(count, strength);
                if (!substantial || !DiplomacyAgreementRules.RecordMilitaryIncursion(relation, day, coalition.Target, _rules))
                {
                    continue;
                }

                ShowMessage(Text("Eure Truppen sind in die Kernzone des Gegners eingerückt. Das Abkommen und alle Gespräche sind beendet.", "Your troops entered the enemy's inner territory. The agreement and all talks have ended."));
                Log("agreement", "coalition=" + coalition.CoalitionId + "|status=broken|actor=player_incursion|units=" + count.ToString(CultureInfo.InvariantCulture) + "|military=" + strength.ToString(CultureInfo.InvariantCulture) + "|fallback_radius_local=" + DiplomacyCombatRules.IntimateZoneRadiusLocalTiles.ToString(CultureInfo.InvariantCulture) + "|boundary=connected_castle_envelope|center=keep_door");
            }
        }

        /// <summary>
        /// Die Kernzonen beider Vertragsseiten fuer das Abstandsgebot: der
        /// Spielerblock (Spieler und verbuendete Verbuende) und jeder Verbund
        /// mit laufendem Pakt. Ohne Burgtor (SHCDESE-Vorgabe 0,0) keine Zone.
        /// </summary>
        private void RefreshTreatyZones(int day)
        {
            _treatyZones.Clear();
            AddTreatyZone(_localPlayer, true);
            foreach (CoalitionState coalition in _coalitions.Values.OrderBy(value => value.CoalitionId, StringComparer.Ordinal))
            {
                RelationState relation;
                if (!_relations.TryGetValue(coalition.CoalitionId, out relation) || relation.ExtendedState == null)
                {
                    continue;
                }

                bool localBloc = relation.Allied;
                if (!localBloc && !DiplomacyCombatRules.TreatyProtects(relation.Allied, relation.ExtendedState.Agreement, relation.ExtendedState.AgreementUntilDay, day))
                {
                    continue;
                }

                foreach (int member in coalition.MemberPlayerIds)
                {
                    AddTreatyZone(member, localBloc);
                }
            }

            _treatyStandoffActive = _treatyZones.Any(zone => !zone.LocalBloc);
        }

        private void AddTreatyZone(int player, bool localBloc)
        {
            WorldForceUnit center;
            if (player <= 0 || !IsAlivePlayer(player) || !TryGetFixedKeepCenter(player, out center))
            {
                return;
            }

            _treatyZones.Add(new TreatyZone { Owner = player, X = center.X, Y = center.Y, LocalBloc = localBloc, Boundary = Volatile.Read(ref _castleBoundaries)[player], });
        }

        private void ClearTreatyStandoff()
        {
            // Endet der Pakt, endet auch der Messversuch - und zwar
            // zurueckgenommen, nicht abgebrochen.
            RestoreProbeSubject("standoff_cleared");
            _treatyZones.Clear();
            _treatyStandoffActive = false;
            _tribeRecallTicks.Clear();
            _pendingAgreementSafety.Clear();
        }

        private bool StandoffEnforceable()
        {
            return _treatyStandoffActive && !_treatyStandoffDegraded;
        }

        /// <summary>
        /// Welcher Vertragsseite eine KI angehoert. Der Mensch ist nie
        /// Gegenstand des Abstandsgebots - fuer ihn gilt die Einmarschregel.
        /// </summary>
        private bool TryGetTreatySide(int player, out bool localBloc)
        {
            localBloc = false;
            if (player <= 0 || player == _localPlayer || !_roster.Contains(player))
            {
                return false;
            }

            string coalitionId;
            RelationState relation;
            if (!_playerCoalition.TryGetValue(player, out coalitionId) || !_relations.TryGetValue(coalitionId, out relation) || relation.ExtendedState == null)
            {
                return false;
            }

            if (relation.Allied)
            {
                localBloc = true;
                return true;
            }

            return DiplomacyCombatRules.TreatyProtects(relation.Allied, relation.ExtendedState.Agreement, relation.ExtendedState.AgreementUntilDay, Math.Max(0, _lastProcessedDay));
        }

        private bool IsOpposingTreatyPlayer(bool localBloc, int player)
        {
            bool playerLocalBloc;
            if (player == _localPlayer)
            {
                return !localBloc;
            }

            return TryGetTreatySide(player, out playerLocalBloc) && playerLocalBloc != localBloc;
        }

        private TreatyZone FindOpposingTreatyZone(bool localBloc, int worldX, int worldY)
        {
            foreach (TreatyZone zone in _treatyZones)
            {
                if (zone.LocalBloc != localBloc && InsideTreatyZone(zone, worldX, worldY))
                {
                    return zone;
                }
            }

            return null;
        }

        private bool TryGetStandoffRecallHome(int owner, int worldX, int worldY, out TreatyZone home)
        {
            home = null;
            bool localBloc;
            if (!TryGetTreatySide(owner, out localBloc) || FindOpposingTreatyZone(localBloc, worldX, worldY) == null)
            {
                return false;
            }

            foreach (TreatyZone zone in _treatyZones)
            {
                if (zone.Owner == owner)
                {
                    home = zone;
                    break;
                }
            }

            // Ohne eigenes Burgtor, oder wenn die Heimat selbst in der fremden
            // Kernzone liegt (Burgen dichter als der Radius), gibt es keinen
            // Rueckweg, den das Gebot nicht selbst sperren wuerde.
            return home != null && FindOpposingTreatyZone(localBloc, home.X, home.Y) == null;
        }

        // SHCDESE 2.14.1 renamed the native AI siege target at 0x2BD8.
        // GamePlayerResources has the same 0x583C-byte layout on the pinned
        // engine in 2.11.0 and 2.14.1. Use the runtime assembly metadata:
        // compiling a direct FieldRef against either name breaks the other.
        // No unverified offsets or incompatible field types are accepted.
        private static class SiegeTargetLayout
        {
            internal static readonly int Offset = Resolve();
            private static int Resolve()
            {
                Type layout = typeof(GamePlayerResources);
                if (Marshal.SizeOf(layout) != 0x583C)
                    throw new InvalidOperationException("Unsupported player resources layout");
                FieldInfo oldName = layout.GetField("r_AISiegePlayerIdTarget", BindingFlags.Instance | BindingFlags.Public);
                FieldInfo newName = layout.GetField("r_SiegeAttackTargetPlayerId", BindingFlags.Instance | BindingFlags.Public);
                FieldInfo field = newName ?? oldName;
                if (field == null || field.FieldType != typeof(uint) || (int)Marshal.OffsetOf(layout, field.Name) != 0x2BD8)
                    throw new InvalidOperationException("AI siege target native field contract changed");
                return 0x2BD8;
            }
        }

        private static unsafe uint ReadNativeSiegeTarget(GamePlayerResources* player)
        {
            if (player == null)
                throw new InvalidOperationException("AI siege target player pointer is null");
            return *(uint*)((byte*)player + SiegeTargetLayout.Offset);
        }

        private static unsafe void ClearNativeSiegeTarget(GamePlayerResources* player)
        {
            if (player == null)
                throw new InvalidOperationException("AI siege target player pointer is null");
            uint* target = (uint*)((byte*)player + SiegeTargetLayout.Offset);
            *target = 0;
            if (*target != 0)
                throw new InvalidOperationException("AI siege target clear was rejected");
        }

        private unsafe int ClearTreatyAiSiegeTargets()
        {
            RequireNativeDiplomacySection();
            int cleared = 0;
            GamePlayerManagerAPI players = GamePlayerManagerAPI.Instance;
            foreach (int player in _roster)
            {
                bool localBloc;
                if (player == _localPlayer || !TryGetTreatySide(player, out localBloc) || !GameData.Instance.lastGameState.is_skirmish_player(player))
                {
                    continue;
                }

                GamePlayerResources* resources;
                if (!players.TryGetPlayerResourcesById(player, out resources) || resources == null)
                {
                    continue;
                }

                uint rawTarget = ReadNativeSiegeTarget(resources);
                if (rawTarget < 1 || rawTarget > 8 || !IsOpposingTreatyPlayer(localBloc, (int)rawTarget))
                {
                    continue;
                }

                ClearNativeSiegeTarget(resources);
                cleared++;
            }

            return cleared;
        }

        /// <summary>
        /// Abstandsgebot durchsetzen: Belagerungsziele in die andere
        /// Vertragsseite loeschen, Staemme in fremder Kernzone nach Hause
        /// schicken. Komfort ueber dem Waechter - ein Fehler hier schaltet
        /// nur das Gebot ab, nie die Karte; Angriff, Geschoss, Feuer und
        /// Schaden bleiben gesperrt.
        /// </summary>
        private unsafe void EnforceTreatyStandoff(Dictionary<int, TribeRecall> recall)
        {
            RequireNativeDiplomacySection();
            if (!StandoffEnforceable())
            {
                return;
            }

            try
            {
                int targetsCleared = ClearTreatyAiSiegeTargets();
                int recalled = 0;
                GameTribeManagerAPI tribes = GameTribeManagerAPI.Instance;
                foreach (KeyValuePair<int, TribeRecall> entry in recall.OrderBy(pair => pair.Key))
                {
                    GameTribe* tribe;
                    if (tribes == null || !tribes.TryGetTribeById(entry.Key, out tribe) || tribe == null || !NativeDiplomacyIdentityMatches(entry.Value.Owner, entry.Value.GlobalId, tribe->r_PlayerIdOwner, tribe->r_GlobalId))
                        continue;
                    long lastRecall;
                    if (_tribeRecallTicks.TryGetValue(entry.Key, out lastRecall) && !DiplomacyCombatRules.ScanDue(lastRecall, ReadSimulationTick(), DiplomacyCombatRules.TreatyRecallIntervalTicks))
                    {
                        continue;
                    }

                    if (tribes.IssueMoveHereCommand(entry.Key, entry.Value.Home.X / DiplomacyCombatRules.WorldPositionsPerLocalTile, entry.Value.Home.Y / DiplomacyCombatRules.WorldPositionsPerLocalTile))
                    {
                        _tribeRecallTicks[entry.Key] = ReadSimulationTick();
                        recalled++;
                    }
                }

                int movesBlocked = _standoffMovesBlocked;
                _standoffMovesBlocked = 0;
                if ((targetsCleared > 0 || recalled > 0 || movesBlocked > 0) && _standoffLogLines < 40)
                {
                    _standoffLogLines++;
                    Log("treaty_standoff", "status=enforced|ai_targets_cleared=" + targetsCleared.ToString(CultureInfo.InvariantCulture) + "|tribes_recalled=" + recalled.ToString(CultureInfo.InvariantCulture) + "|moves_blocked=" + movesBlocked.ToString(CultureInfo.InvariantCulture) + "|zones=" + _treatyZones.Count.ToString(CultureInfo.InvariantCulture) + "|tick=" + ReadSimulationTick().ToString(CultureInfo.InvariantCulture));
                }
            }
            catch (Exception)
            {
                // Exception is intentionally not inspected from a Native callback.
                DeferTreatyStandoffRetry();
            }
        }

        /// <summary>
        /// Marschbefehle einer Vertragsseite in die Kernzone der anderen
        /// werden abgewiesen (Rueckgabe 0), solange der Pakt laeuft. Nur
        /// KI-Staemme mit Kampfeinheiten; der Mensch bleibt frei - fuer ihn
        /// gilt die Einmarschregel.
        /// </summary>
        private void OnTribeMoveOrder(TribeIssueOrderMoveHereEventArgs e)
        {
            NoteLane(LaneNative);
            if (e == null || e.Phase != EventHookPhase.Pre)
            {
                return;
            }

            // Nicht blockierend sperren (, nachgezogen): dieser
            // Behandler liegt auf derselben nativen Spur wie die acht
            // Kampfwaechter, die am 12:08 wegen genau dieses `lock` das Spiel
            // aufgehaengt haben. Er wurde damals uebersehen, weil die Liste
            // der nativen Waechter von Hand gepflegt war. Bekommt er das Tor
            // nicht, faellt ein Marschbefehl durch - der Abstandsschutz laesst
            // dann einen Schritt durch, statt den Spielkern anzuhalten.
            if (!TryEnterCombatGate())
            {
                return;
            }

            try
            {
                if (!_mapActive || _mapDisabled || !StandoffEnforceable())
                {
                    return;
                }

                try
                {
                    if (!GameTileManagerAPI.Instance.IsTileInsideMapBounds(e.TileX, e.TileY))
                    {
                        return;
                    }

                    int owner = TribeOwner(e.TribeId);
                    bool localBloc;
                    if (owner <= 0 || !TryGetTreatySide(owner, out localBloc))
                    {
                        return;
                    }

                    GameTribeManagerAPI tribes = GameTribeManagerAPI.Instance;
                    GameUnitManagerAPI units = GameUnitManagerAPI.Instance;
                    int leader = tribes.GetLeaderUnitId(e.TribeId);
                    if (leader <= 0 || !units.IsValidId(leader) || !IsCombatUnit(units.GetType(leader)))
                    {
                        return;
                    }

                    TreatyZone zone = FindOpposingTreatyZone(localBloc, DiplomacyCombatRules.TileCenterWorld(e.TileX), DiplomacyCombatRules.TileCenterWorld(e.TileY));
                    if (zone == null)
                    {
                        return;
                    }

                    // Phase 0: waehrend des Umlenkversuchs bekommt der Stamm
                    // ein erreichbares Ersatzziel statt einer Absage. Die
                    // Frage ist, ob die KI das annimmt - heute weisen wir ab,
                    // und sie versucht es immer wieder.
                    if (ProbeRedirectMoveOrder(e, zone, localBloc))
                    {
                        return;
                    }

                    e.SkipOriginalFunction = true;
                    e.ReturnValue = 0;
                    _standoffMovesBlocked++;
                }
                catch (Exception)
                {
                    // Native callbacks only record a bounded counter. Reporting
                    // and retry scheduling are observed on the next frame scan.
                    DeferTreatyStandoffRetry();
                }
            }
            finally
            {
                Monitor.Exit(_gate);
            }
        }

        private static bool TryGetFixedKeepCenter(int player, out WorldForceUnit center)
        {
            center = null;
            UnmanagedVector2<int> keepDoor = GamePlayerManagerAPI.Instance.GetPlayerKeepDoorPosition(player);
            // SHCDESE returns default(0, 0) when no player resources exist.
            // A missing fixed center is not permission to move the zone with
            // the lord and accidentally declare a breach on the battlefield.
            int worldX;
            int worldY;
            if (!GameTileManagerAPI.Instance.IsTileInsideMapBounds(keepDoor.X, keepDoor.Y) || !DiplomacyCombatRules.TryKeepCenter(keepDoor.X, keepDoor.Y, GameTileManagerAPI.MAX_WIDTH, out worldX, out worldY))
            {
                return false;
            }

            center = new WorldForceUnit
            {
                X = worldX,
                Y = worldY,
                MilitaryValue = 0L,
            };
            return true;
        }

        private long DiplomacyMilitaryValue(GameUnitManagerAPI units, int unitId, eChimps type)
        {
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

            int maximum = Math.Max(1, units.GetMaxHealth(unitId));
            int current = Math.Max(0, Math.Min(maximum, units.GetCurrentHealth(unitId)));
            return decimal.ToInt64(decimal.Ceiling((decimal)cost * current / maximum));
        }

        private void HandleDemandResolution(CoalitionState coalition, RelationState relation, DiplomacyDemandResolution resolution)
        {
            switch (resolution)
            {
                case DiplomacyDemandResolution.TruceFormed:
                    ActivateAgreementSafety(coalition);
                    PlayCoalitionSpeech(coalition, "SK_THANK_GOODS", "thanks");
                    ShowMessage(Text("Waffenruhe vereinbart. Nach der Wartezeit könnt Ihr Bedingungen für ein Handelsabkommen anfragen.", "Truce agreed. Once the waiting period ends, you can request terms for a trade pact."));
                    Log("agreement", "coalition=" + coalition.CoalitionId + "|kind=truce|status=formed");
                    break;
                case DiplomacyDemandResolution.TradePactFormed:
                    ActivateAgreementSafety(coalition);
                    PlayCoalitionSpeech(coalition, "SK_THANK_GOODS", "thanks");
                    ShowMessage(string.Format(CultureInfo.InvariantCulture, Text("Handelsabkommen geschlossen: {0} Tage Schutz, höhere diplomatische Wertung Eurer Geschenke und Zugang zu Bündnisbedingungen.", "Trade pact concluded: {0} days of protection, greater diplomatic value for your gifts, and access to alliance terms."), Math.Max(0, relation.ExtendedState.AgreementUntilDay - AbsoluteGameDay())));
                    Log("agreement", "coalition=" + coalition.CoalitionId + "|kind=trade_pact|status=formed");
                    break;
                case DiplomacyDemandResolution.AllianceReady:
                    if (CanJoinAlliance(coalition.CoalitionId))
                    {
                        FormAlliance(coalition, "_terms_fulfilled");
                    }
                    else
                    {
                        PlayCoalitionSpeech(coalition, "SK_THANK_GOODS", "thanks");
                        ShowMessage(Text("Die Bündnisbedingungen sind erfüllt. Ein feindlicher KI-Verbund muss jedoch verbleiben; Waffenruhe und Handel gelten weiter.", "The alliance terms are fulfilled, but one hostile AI coalition must remain; truce and trade stay in force."));
                    }

                    break;
                case DiplomacyDemandResolution.AllianceTermsAccepted:
                    PlayCoalitionSpeech(coalition, "SK_THANK_GOODS", "thanks");
                    ShowMessage(DiplomacyAgreementRules.PermanentHistoryPreventsAlliance(relation) ? Text("Die Bündnisbedingungen sind erfüllt. Frühere Vertragsbrüche schließen ein Bündnis mit diesem Herrscher jedoch dauerhaft aus.", "The alliance terms are fulfilled, but earlier treaty breaches permanently rule out an alliance with this ruler.") : Text("Die Bündnisbedingungen sind erfüllt. Vertrauen und Ansehen reichen für ein Bündnis noch nicht aus.", "The alliance terms are fulfilled. Trust and standing are not yet sufficient for an alliance."));
                    Log("agreement", "coalition=" + coalition.CoalitionId + "|kind=alliance_terms|status=accepted_not_ready");
                    break;
                case DiplomacyDemandResolution.SupportFulfilled:
                    PlayCoalitionSpeech(coalition, "SK_THANK_GOODS", "thanks");
                    Log("support", "coalition=" + coalition.CoalitionId + "|status=fulfilled");
                    break;
                case DiplomacyDemandResolution.UltimatumFulfilled:
                    PlayCoalitionSpeech(coalition, "SK_THANK_GOODS", "thanks");
                    ShowMessage(Text("Die letzte Forderung wurde erfüllt. Das Bündnis bleibt bestehen.", "The final demand was fulfilled. The alliance remains in force."));
                    Log("ultimatum", "coalition=" + coalition.CoalitionId + "|status=fulfilled");
                    break;
                case DiplomacyDemandResolution.RestitutionAccepted:
                    PlayCoalitionSpeech(coalition, "SK_THANK_GOODS", "thanks");
                    ShowMessage(Text("Die Wiedergutmachung wurde angenommen. Gespräche sind wieder möglich, doch der Vertragsbruch bleibt in Erinnerung.", "Restitution was accepted. Talks may resume, but the breach will still be remembered."));
                    Log("restitution", "coalition=" + coalition.CoalitionId + "|status=accepted");
                    break;
            }
        }

        /// <summary>
        /// Rechnet einen faelligen Tributzeitraum ab.
        /// </summary>
        /// <remarks>
        /// Laeuft im Tageslauf, also im Unity-Bild - dort sind
        /// Meldungsfenster erlaubt und kein Kontexthaken liegt auf dem
        /// Stapel.
        ///
        /// Haertung, bewusst so gebaut ():
        /// <list type="bullet">
        /// <item>Der ganze Ablauf liegt in einem eigenen `try`. Ein Fehler
        /// schaltet **nur diese Tributpflicht** ab, nie die Karte - ein
        /// normales Ereignis darf niemals dauerhaft abschalten.</item>
        /// <item>Hoechstens ein Zeitraum je Tag. Auch wenn mehrere
        /// verstrichen sind, wird einer abgerechnet; ein Nachholstoss waere
        /// weder spielbar noch bezahlbar.</item>
        /// <item>Keine Teilzahlung - die Uebertragung bleibt atomar.</item>
        /// <item>Sparsam: ohne bestehende und faellige Pflicht kostet der
        /// Zweig einen Feldvergleich.</item>
        /// </list>
        /// </remarks>
         // Militaerwert des oertlichen Spielers, hoechstens einmal je Tag
        // berechnet. -1 heisst "fuer diesen Tag noch nicht".
        // Feldstaerke je Spieler, einmal je Spieltag. Ein Durchlauf statt
        // eines je Vergleich ().
        private Dictionary<int, long> _militaryByPlayer;
        private int _militaryByPlayerDay = -1;
        // Aufeinanderfolgende Fehler der Lehnsspur. Erst danach gibt sie auf -
        // ein einzelner Fehler darf keinen Vertrag kosten.
        private readonly Dictionary<string, int> _vassalageFaultsByCoalition = new Dictionary<string, int>(StringComparer.Ordinal);
        private const int VassalageFaultLimit = 3;
        /// <summary>
        /// Der Militaerwert des oertlichen Spielers, nach derselben Formel wie
        /// bei den Koalitionen (Kosten mal Gesundheitsanteil).
        /// </summary>
        /// <remarks>
        /// Sparsam: ein Durchlauf ueber die Einheiten je Tag, und nur, wenn
        /// ueberhaupt ein Lehnsverhaeltnis besteht. Ein geschlagenes Heer
        /// zaehlt weniger als ein frisches - das ist gewollt, denn genau
        /// darum geht es beim Uebergewicht.
        /// </remarks>
        /// <summary>
        /// Feldstaerke JE SPIELER, einmal je Spieltag gemessen.
        /// </summary>
        /// <remarks>
        /// Ersetzt seit dem den Einzeldurchlauf fuer den oertlichen
        /// Spieler. Grund ist nicht Eleganz, sondern die Sperrzeit: der
        /// Tagesschritt haelt `_gate` bereits 13,9 ms und liegt damit ueber
        /// der Wartezeit des Kampftors (5 ms); im Lauf vom sind
        /// dadurch 278 Kampfwaechter uebersprungen worden. Ein Vergleich
        /// "Vasall gegen Lehnsherr" braucht Werte je SPIELER - als eigener
        /// Durchlauf je Vasall waere das ein Vielfaches gewesen. So ist es
        /// EIN Durchlauf fuer alle, und fuer den oertlichen Spieler sogar
        /// einer weniger als vorher.
        ///
        /// Der Fuerst zaehlt bewusst nicht mit: er steht in jeder Burg genau
        /// einmal und verschoebe den Vergleich zugunsten des Schwaecheren.
        /// </remarks>
        private Dictionary<int, long> MilitaryByPlayer(int day, bool currentTick = false)
        {
            if (_militaryByPlayerDay == day && _militaryByPlayer != null && _militaryGeneration == _sessionGeneration && _militaryEpoch == _mapEpoch && (!currentTick || _militaryByPlayerTick == ReadSimulationTick()))
            {
                return _militaryByPlayer;
            }

            RequireNativeDiplomacySection();
            Dictionary<int, long> tabelle = new Dictionary<int, long>();
            var positions = new List<MilitaryPosition>();
            var teams = new Dictionary<int, int>();
            foreach (int player in _roster)
                if (player >= 1 && player <= 8)
                    teams[player] = _teamPort.GetTeam(player);
            GameUnitManagerAPI units = GameUnitManagerAPI.Instance;
            foreach (int unitId in units.GetAllAliveUnits())
            {
                if (!units.IsValidId(unitId))
                {
                    continue;
                }

                int owner = units.GetOwner(unitId);
                if (owner <= 0 || owner > 8)
                {
                    // Spieler 0 ist die bekannte Falle - er steht fuer
                    // "niemand", nicht fuer einen Mitspieler.
                    continue;
                }

                eChimps type = units.GetType(unitId);
                if (type == eChimps.CHIMP_TYPE_LORD || !IsCombatUnit(type))
                {
                    continue;
                }

                long vorher;
                tabelle.TryGetValue(owner, out vorher);
                long value = DiplomacyMilitaryValue(units, unitId, type);
                tabelle[owner] = SaturatingAdd(vorher, value);
                if (value > 0)
                {
                    UnmanagedVector2<ushort> position = units.GetCurrentWorldTilePosition(unitId);
                    positions.Add(new MilitaryPosition { Owner = owner, X = position.X, Y = position.Y, Value = value });
                }
            }

            // Publish only a complete observation under the same engine lease.
            _militaryPositions = positions.ToArray();
            _militaryTeams = teams;
            _militaryByPlayerTick = ReadSimulationTick();
            _militaryGeneration = _sessionGeneration;
            _militaryEpoch = _mapEpoch;
            _militaryByPlayerDay = day;
            _militaryByPlayer = tabelle;
            return tabelle;
        }

        /// <summary>Feldstaerke eines Spielers, 0 wenn er keine hat.</summary>
        private long MilitaryOf(int player, int day)
        {
            long wert;
            return player > 0 && MilitaryByPlayer(day).TryGetValue(player, out wert) ? wert : 0L;
        }

        /// <summary>Ordinary alliances remain equal; discard obsolete automatic offers.</summary>
        private void EvaluateVassalageOnAlliance(CoalitionState coalition, RelationState relation, int day)
        {
            // An ordinary alliance supplies no hostile physical pressure.
            // Preserve historical active roles; never generate a new fealty
            // candidate from the allies' global strength difference.
            WithdrawLegacyAllianceSubjugation(relation);
        }

        /// <summary>
        /// Begruendet das Verhaeltnis und richtet den Tribut ein.
        /// </summary>
        /// <remarks>
        /// Beides zusammen oder gar nichts: ein Lehnsverhaeltnis ohne Tribut
        /// waere eine Rolle ohne Inhalt, ein Tribut ohne Verhaeltnis eine
        /// Pflicht ohne Grund (und der Tagesschritt loeschte ihn beim
        /// naechsten Durchlauf wieder).
        /// </remarks>
        private void EstablishVassalage(CoalitionState coalition, RelationState relation, int vassalPlayer, int overlordPlayer, int day, string reason, int quotedAmount = 0)
        {
            DiplomacyRelationState state = relation.ExtendedState;
            int amount = quotedAmount > 0 ? quotedAmount : TributeQuoteForPlayer(vassalPlayer);
            // Protokollzeile VOR dem Schreiben: bricht etwas dazwischen ab,
            // steht wenigstens die Absicht im Protokoll (Hausregel).
            Log("vassalage", "coalition=" + coalition.CoalitionId + "|status=about_to_establish|vassal=" + vassalPlayer.ToString(CultureInfo.InvariantCulture) + "|overlord=" + overlordPlayer.ToString(CultureInfo.InvariantCulture) + "|tribute_gold=" + amount.ToString(CultureInfo.InvariantCulture) + "|interval=" + DiplomacyTribute.DefaultIntervalDays.ToString(CultureInfo.InvariantCulture) + "|reason=" + Safe(reason));
            if (!DiplomacyVassalage.Establish(relation, vassalPlayer, overlordPlayer, _localPlayer, day))
            {
                Log("vassalage", "coalition=" + coalition.CoalitionId + "|status=refused|reason=preconditions");
                return;
            }

            if (!DiplomacyTribute.Arrange(state, DiplomacyTribute.GoldGoodId, amount, DiplomacyTribute.DefaultIntervalDays, day))
            {
                // Ohne Tribut kein Lehnsverhaeltnis - sonst bliebe eine
                // Rolle ohne Inhalt stehen.
                DiplomacyVassalage.ClearWithoutRelease(state);
                Log("vassalage", "coalition=" + coalition.CoalitionId + "|status=refused|reason=tribute_rejected");
                return;
            }

            Log("vassalage", "coalition=" + coalition.CoalitionId + "|status=established|role=" + state.VassalRole.ToString());
            PublishCombatProtectionSnapshot();
            if (vassalPlayer == _localPlayer)
            {
                ShowMessage(Text("Ihr habt Lehnstreue geschworen. Bis Ihr Euch befreit, entrichtet Ihr regelmäßig Tribut.", "You have sworn fealty. Until you free yourself you will pay regular tribute."));
                return;
            }

            ShowMessage(Text("Euer neuer Verbündeter hat Euch Lehnstreue geschworen und entrichtet Euch fortan Tribut.", "Your new ally has sworn fealty to you and will pay you tribute from now on."));
        }

        /// <summary>Retires an obsolete automatic allied offer restored from an old save.</summary>
        private void AskSubjugation(CoalitionState coalition, RelationState relation, int overlordPlayer, int day)
        {
            // Retained as the old-save restore entry point. This automatic
            // allied offer has no valid siege producer under the current rule.
            WithdrawLegacyAllianceSubjugation(relation);
        }

        /// <summary>Der Spieler hat auf die Lehnsfrage geantwortet.</summary>
        /// <remarks>
        /// Der Rueckruf kommt spaeter und moeglicherweise in einer anderen
        /// Partie - dieselben vier Pruefungen wie bei den uebrigen Dialogen.
        /// </remarks>
        private void AnswerSubjugation(string coalitionId, long generation, string fingerprint, int overlordPlayer, int quotedAmount, int offerDay, int releases, bool accepted)
        {
            StageVassalAction(coalitionId, generation, fingerprint, accepted ? DiplomacyVassalActionKind.AcceptSubjugation : DiplomacyVassalActionKind.RefuseSubjugation, _localPlayer, overlordPlayer, -1, offerDay, quotedAmount, releases);
        }

        // ------------------------------------------------------------------
        // Vasallen-Befehlshebel ()
        // ------------------------------------------------------------------
        /// <summary>
        /// Grund eines Teamuebergangs, den eine Auflehnung ausgeloest hat.
        /// </summary>
        /// <remarks>
        /// Traegt die Bruchart durch den Uebergang, weil der im
        /// legacy-Spielstandformat gespeichert wird und ein eigenes Feld eine
        /// Formataenderung waere ().
        /// </remarks>
        internal const string VassalRebellionReason = "vassal_rebellion";
        /// <summary>
        /// Grund eines Teamuebergangs, den ein Tributverzug ausgeloest hat.
        /// </summary>
        internal const string TributeDefaultReason = "tribute_default";
        /// <summary>Hoechstens so viele Protokollzeilen je Karte.</summary>
        private const int VassalOrderLogBudget = 40;
        private int _vassalOrdersSeen;
        private int _vassalOrdersForced;
        private int _vassalOrdersMismatched;
        private int _vassalOrderLogLines;
        private int _vassalOrderFaults;
        private const int VassalOrderFaultLogLimit = 3;
        /// <summary>
        /// Entscheidet, ob eine KI-Bewertung zugunsten des Lehnsherrn
        /// ausfaellt.
        /// </summary>
        /// <param name = "hook">"attack" oder "defend" - nur fuers Protokoll.</param>
        /// <param name = "consulted">
        /// Der Fuerst, dessen Bereitschaft bewertet wird (`TargetPlayerId`).
        /// </param>
        /// <param name = "requester">Der Bittsteller (`SourcePlayerId`).</param>
        /// <param name = "intendedTarget">
        /// Angriffsziel (`ContextPlayerId`), nur fuer Diagnose, nie Bittsteller.
        /// </param>
        /// <param name = "original">Was die native KI selbst entschieden hat.</param>
        /// <returns>Der Wert, der zurueckgegeben werden soll.</returns>
        /// <remarks>
        /// MUSS unter dem Kampftor gerufen werden - liest `_relations`.
        /// Wirft nicht: der Aufrufer laeuft in einem nativen Rahmen.
        /// </remarks>
        private bool VassalOrderBias(string hook, int consulted, int requester, int intendedTarget, bool original)
        {
            if (original)
            {
                // Schon ein Ja - nichts zu beugen. Der haeufigste Fall zuerst,
                // damit die Suche unten meistens gar nicht laeuft.
                return true;
            }

            if (!_mapActive || _mapDisabled || consulted <= 0 || requester <= 0)
            {
                return false;
            }

            bool passtVerdreht = false;
            foreach (KeyValuePair<string, RelationState> eintrag in _relations)
            {
                DiplomacyRelationState state = eintrag.Value == null ? null : eintrag.Value.ExtendedState;
                if (!DiplomacyVassalage.IsActive(state))
                {
                    continue;
                }

                int vasall = DiplomacyVassalage.VassalOf(state);
                int lehnsherr = DiplomacyVassalage.OverlordOf(state);
                // nativer Caller 0xD771E und SE-1.45-Detour
                // belegen Source=Bittsteller, Target=KI, Context=Angriffsziel.
                bool herrFragt = lehnsherr == requester;
                if (vasall == consulted && herrFragt)
                {
                    _vassalOrdersSeen++;
                    if (!DiplomacyVassalage.ObeysOrders(state))
                    {
                        // Er ist Vasall, aber gerade nicht willig: offener
                        // Antrag auf Gleichstellung oder versaeumter Tribut.
                        LogVassalOrder(hook, "withheld", eintrag.Key, consulted, requester, intendedTarget);
                        return false;
                    }

                    _vassalOrdersForced++;
                    LogVassalOrder(hook, "obeyed", eintrag.Key, consulted, requester, intendedTarget);
                    return true;
                }

                // Ein Vasall kann seinem Lehnsherrn keinen Befehl aufzwingen.
                if (vasall == requester && lehnsherr == consulted)
                {
                    passtVerdreht = true;
                }
            }

            if (passtVerdreht)
            {
                _vassalOrdersMismatched++;
                LogVassalOrder(hook, "role_mismatch", "-", consulted, requester, intendedTarget);
            }

            return false;
        }

        /// <summary>
        /// Ein Fehler im Befehlshebel - gezaehlt, nicht bestraft.
        /// </summary>
        /// <remarks>
        /// Kein `CaptureCombatSafetyFault`: das vermerkte Versagen wuerde
        /// spaeter auf der Bildschleife geworfen und die Karte abschalten.
        /// Der Hebel ist eine Bequemlichkeit, kein Sicherheitswaechter - sein
        /// Ausfall heisst, dass die KI selbst entscheidet, und das ist genau
        /// das Verhalten ohne diesen Mod.
        /// </remarks>
        private void NoteVassalOrderFault(string hook, Exception exception)
        {
            DiplomacyRuntimeSafety.SaturatingIncrement(ref _vassalOrderFaults);
            if (_vassalOrderFaults > VassalOrderFaultLogLimit)
            {
                return;
            }

            try
            {
                Log("vassal_order", "hook=" + Safe(hook) + "|status=fault|count=" + _vassalOrderFaults.ToString(CultureInfo.InvariantCulture) + "|reason=" + Safe(exception.GetType().Name));
            }
            catch
            {
            } // Optional order diagnostics must not escape a native callback.
        }

        /// <summary>Gedeckelte Protokollzeile des Befehlshebels.</summary>
        private void LogVassalOrder(string hook, string status, string coalitionId, int consulted, int requesterA, int requesterB)
        {
            if (_vassalOrderLogLines >= VassalOrderLogBudget)
            {
                return;
            }

            _vassalOrderLogLines++;
            Log("vassal_order", "hook=" + Safe(hook) + "|status=" + Safe(status) + "|coalition=" + Safe(coalitionId) + "|consulted=" + consulted.ToString(CultureInfo.InvariantCulture) + "|source=" + requesterA.ToString(CultureInfo.InvariantCulture) + "|context=" + requesterB.ToString(CultureInfo.InvariantCulture) + "|local=" + _localPlayer.ToString(CultureInfo.InvariantCulture) + "|seen=" + _vassalOrdersSeen.ToString(CultureInfo.InvariantCulture) + "|forced=" + _vassalOrdersForced.ToString(CultureInfo.InvariantCulture) + "|mismatched=" + _vassalOrdersMismatched.ToString(CultureInfo.InvariantCulture));
        }

        private void ProcessVassalage(CoalitionState coalition, RelationState relation, int day)
        {
            DiplomacyRelationState state = relation.ExtendedState;
            if (!DiplomacyVassalage.IsActive(state))
            {
                return;
            }

            try
            {
                if (!coalition.MemberPlayerIds.Any(IsAlivePlayer) || !IsAlivePlayer(DiplomacyVassalage.VassalOf(state)) || !IsAlivePlayer(DiplomacyVassalage.OverlordOf(state)))
                {
                    // Ein besiegter Lehnsherr oder Vasall - das Verhaeltnis
                    // endet lautlos. Weiterzurechnen hiesse, den Militaerwert
                    // einer Koalition zu vergleichen, den niemand mehr
                    // auffrischt ().
                    DiplomacyTribute.Clear(state);
                    DiplomacyVassalage.Release(state);
                    _equalityPresentedDays.Remove(coalition.CoalitionId);
                    CancelDiplomacyDialog("equality:" + coalition.CoalitionId);
                    Log("vassalage", "coalition=" + coalition.CoalitionId + "|status=void|reason=coalition_defeated");
                    return;
                }

                // Read the named parties every day, including while a request
                // is open. A lost strength advantage invalidates the claim
                // before silence or a stale answer can cause a rebellion.
                long vasall = MilitaryOf(DiplomacyVassalage.VassalOf(state), day);
                long lehnsherr = MilitaryOf(DiplomacyVassalage.OverlordOf(state), day);
                bool canRequest = DiplomacyVassalage.NoteStrengthComparison(state, vasall, lehnsherr, day);
                // Observe once per genuine day, with exact named military parties.
                ObserveVassalRisk(relation, day, vasall, lehnsherr);
                _vassalageFaultsByCoalition.Remove(coalition.CoalitionId);
                if (DiplomacyVassalage.CancelUnjustifiedEqualityRequest(state, day))
                {
                    _equalityPresentedDays.Remove(coalition.CoalitionId);
                    CancelDiplomacyDialog("equality:" + coalition.CoalitionId);
                    Log("vassalage", "coalition=" + coalition.CoalitionId + "|status=equality_withdrawn|reason=strength_changed");
                    return;
                }

                int presentedDay;
                bool presented = _equalityPresentedDays.TryGetValue(coalition.CoalitionId, out presentedDay) && day > presentedDay && IsDiplomacyDialogPresented("equality:" + coalition.CoalitionId);
                if (DiplomacyVassalage.ElapseEqualityResponseDay(state, day, presented))
                {
                    // Schweigen ist auch eine Antwort.
                    ResolveEqualityRequest(coalition, relation, day, false);
                    return;
                }

                if (DiplomacyVassalage.EqualityRequestOpen(state))
                {
                    AskEqualityRequest(coalition, relation);
                    return;
                }

                if (!canRequest)
                {
                    return;
                }

                // Human vassals use the same strength/tenure rule, but choose
                // when to ask via the primary button. They are never forced
                // into an automatic rebellion or a hidden request.
                if (!DiplomacyVassalage.CoalitionIsVassal(state))
                {
                    return;
                }

                // ERST zeigen, DANN vermerken. Andersherum liefe die
                // Bedenkzeit fuer einen Antrag ab, den der Spieler nie zu
                // sehen bekam - und das Schweigen gaelte als Ablehnung
                // ().
                if (!AskEqualityRequest(coalition, relation))
                {
                    Log("vassalage", "coalition=" + coalition.CoalitionId + "|status=request_deferred|reason=dialog_unavailable");
                    return;
                }

                Log("vassalage", "coalition=" + coalition.CoalitionId + "|status=equality_queued|vassal_military=" + vasall.ToString(CultureInfo.InvariantCulture) + "|overlord_military=" + lehnsherr.ToString(CultureInfo.InvariantCulture) + "|edge_days=" + state.VassalStrengthEdgeDays.ToString(CultureInfo.InvariantCulture));
            }
            catch (Exception exception)
            {
                // Zu scharf waere, das Verhaeltnis gleich aufzuloesen: ein
                // einmaliger Fehler beim Lesen eines Militaerwerts ist kein
                // Grund, einen Vertrag zu beenden. Der Tagesschritt wird
                // uebersprungen und gezaehlt; erst nach mehreren Fehlern in
                // Folge gibt die Spur auf ().
                int faults;
                _vassalageFaultsByCoalition.TryGetValue(coalition.CoalitionId, out faults);
                faults = Math.Min(VassalageFaultLimit + 1, faults + 1);
                _vassalageFaultsByCoalition[coalition.CoalitionId] = faults;
                if (faults <= VassalageFaultLimit)
                {
                    Log("vassalage", "coalition=" + coalition.CoalitionId + "|status=deferred|faults=" + faults.ToString(CultureInfo.InvariantCulture) + "|reason=" + Safe(exception.GetType().Name));
                }
            }
        }

        /// <summary>
        /// Legt dem Spieler den Antrag vor - ueber den Bestaetigungsdialog des
        /// Spiels, damit es aussieht wie alles andere.
        /// </summary>
        /// <remarks>
        /// Die Rueckrufe pruefen, ob die Welt noch dieselbe ist: Sitzung und
        /// Kartenkennung werden mitgenommen. Sonst wirkte eine Antwort in eine
        /// Partie hinein, zu der sie nicht gehoert - derselbe Schutz wie beim
        /// vorhandenen Aufkuendigungsdialog.
        /// </remarks>
        private bool AskEqualityRequest(CoalitionState coalition, RelationState relation)
        {
            if (DiplomacyVassalActions.HasPending(relation.ExtendedState))
                return false;
            int vassal = relation.ExtendedState.VassalPlayerId, overlord = relation.ExtendedState.VassalOverlordPlayerId;
            int sinceDay = relation.ExtendedState.VassalSinceDay, releases = relation.ExtendedState.VassalReleases;
            int requestDay = relation.ExtendedState.VassalEqualityRequestedDay;
            long generation = _sessionGeneration;
            string fingerprint = _mapFingerprint;
            string coalitionId = coalition.CoalitionId;
            return ShowConfirmation(PlayerName(vassal) + " · " + Text("Antrag auf Gleichberechtigung", "Request for equal standing"), delegate
            {
                AnswerEqualityRequest(coalitionId, generation, fingerprint, vassal, overlord, sinceDay, releases, requestDay, true);
            }, delegate
            {
                AnswerEqualityRequest(coalitionId, generation, fingerprint, vassal, overlord, sinceDay, releases, requestDay, false);
            }, Text("Euer Vasall ist stärker geworden als Ihr und verlangt, Euch gleichgestellt zu werden. Gewährt Ihr es, enden die Tributzahlungen und das Bündnis bleibt bestehen. Weist Ihr ihn ab, könnte er sich lossagen.", "Your vassal has grown stronger than you and demands equal standing. Granting it ends the tribute payments and keeps the alliance. Refusing may make him break away.") + " " + string.Format(CultureInfo.InvariantCulture, Text("Antwortfrist: {0} Spieltage, während diese Entscheidung sichtbar ist.", "Response period: {0} game days while this decision is visible."), DiplomacyVassalage.EqualityRequestOpen(relation.ExtendedState) ? relation.ExtendedState.VassalEqualityResponseDaysRemaining : DiplomacyVassalage.EqualityRequestGraceDays), delegate
            {
                lock (_gate)
                {
                    RelationState current;
                    if (!VassalDecisionCurrent(coalitionId, generation, fingerprint, out current) || !VassalContractMatches(current.ExtendedState, vassal, overlord, sinceDay, releases) || DiplomacyVassalActions.HasPending(current.ExtendedState))
                        return;
                    if (!DiplomacyVassalage.EqualityRequestOpen(current.ExtendedState))
                        DiplomacyVassalage.RequestEquality(current.ExtendedState, Math.Max(0, _lastProcessedDay));
                    if (DiplomacyVassalage.EqualityRequestOpen(current.ExtendedState))
                    {
                        requestDay = current.ExtendedState.VassalEqualityRequestedDay;
                        if (!_equalityPresentedDays.ContainsKey(coalitionId))
                            _equalityPresentedDays[coalitionId] = Math.Max(0, _lastProcessedDay);
                    }
                }
            }, "equality:" + coalitionId, delegate
            {
                lock (_gate)
                {
                    RelationState current;
                    return VassalDecisionCurrent(coalitionId, generation, fingerprint, out current) && !DiplomacyVassalActions.HasPending(current.ExtendedState) && VassalContractMatches(current.ExtendedState, vassal, overlord, sinceDay, releases) && (requestDay < 0 || current.ExtendedState.VassalEqualityRequestedDay == requestDay) && DiplomacyVassalage.CoalitionIsVassal(current.ExtendedState) && (DiplomacyVassalage.EqualityRequestOpen(current.ExtendedState) || DiplomacyVassalage.CanRequestEquality(current.ExtendedState, Math.Max(0, _lastProcessedDay)));
                }
            });
        }

        private void AnswerEqualityRequest(string coalitionId, long generation, string fingerprint, int vassal, int overlord, int sinceDay, int releases, int requestDay, bool granted)
        {
            StageVassalAction(coalitionId, generation, fingerprint, granted ? DiplomacyVassalActionKind.GrantEquality : DiplomacyVassalActionKind.RefuseEquality, vassal, overlord, sinceDay, requestDay, 0, releases);
        }

        /// <summary>Setzt die Entscheidung um.</summary>
        private void ResolveEqualityRequest(CoalitionState coalition, RelationState relation, int day, bool granted)
        {
            // A timeout is a typed refusal intent too. An inventory journal
            // must settle before any role or request is resolved.
            DiplomacyVassalActions.Stage(relation, _localPlayer, granted ? DiplomacyVassalActionKind.GrantEquality : DiplomacyVassalActionKind.RefuseEquality);
        }

        private void ExecuteEqualityDecision(CoalitionState coalition, RelationState relation, int day, bool granted)
        {
            DiplomacyRelationState state = relation.ExtendedState;
            if (!DiplomacyVassalage.EqualityRequestOpen(state))
            {
                return;
            }

            if (DiplomacyVassalage.CancelUnjustifiedEqualityRequest(state, day))
            {
                _equalityPresentedDays.Remove(coalition.CoalitionId);
                CancelDiplomacyDialog("equality:" + coalition.CoalitionId);
                return;
            }

            // A timeout consumes only our own visible decision. Explicit
            // answers already completed it; cancellation is then a no-op.
            _equalityPresentedDays.Remove(coalition.CoalitionId);
            CancelDiplomacyDialog("equality:" + coalition.CoalitionId);
            if (granted)
            {
                // Wer freiwillig loslaesst, gewinnt einen verlaesslichen
                // Partner - und die Tributpflicht endet mit dem Verhaeltnis.
                DiplomacyTribute.Clear(state);
                DiplomacyVassalage.Release(state);
                DiplomacyAgreementRules.RecordVassalReleased(state);
                DiplomacyAgreementRules.ResumeAllianceMaintenance(state, day);
                PublishCombatProtectionSnapshot();
                Log("vassalage", "coalition=" + coalition.CoalitionId + "|status=equality_granted");
                ShowMessage(Text("Ihr habt Euren Vasallen freigelassen. Die Tributzahlungen enden, das Bündnis bleibt.", "You have freed your vassal. The tribute payments end and the alliance remains."));
                return;
            }

            int sensitivity = DiplomacyAgreementRules.BreachSensitivityOf(state);
            bool bricht = DiplomacyVassalage.RejectionBreaksRelation(state, sensitivity) && RebellionPermitted(relation, day);
            DiplomacyVassalage.CloseEqualityRequest(state, day);
            Log("vassalage", "coalition=" + coalition.CoalitionId + "|status=equality_refused|resolve=" + DiplomacyVassalage.RebellionResolve(state, sensitivity).ToString(CultureInfo.InvariantCulture) + "|threshold=" + DiplomacyVassalage.RebellionBreakThreshold.ToString(CultureInfo.InvariantCulture) + "|breaks=" + BoolText(bricht));
            if (!bricht)
            {
                ShowMessage(string.Format(CultureInfo.InvariantCulture, Text("Ihr habt den Antrag abgewiesen. Die Lehnspflicht bleibt bestehen; ein neuer Gleichstellungsantrag ist frühestens in {0} Tagen möglich.", "You refused the request. Fealty continues; another request for equal standing is possible in {0} days at the earliest."), DiplomacyVassalage.EqualityRequestCooldownDays));
                return;
            }

            // Ueber den Uebergangsweg, nicht an ihm vorbei (,
            // zweite Fassung). Die erste Fassung rief die Regel direkt: das
            // setzte `relation.Allied = false`, liess aber `coalition.Allied`
            // und die nativen Mannschaften stehen - und genau diese
            // Ungleichheit laesst `DiplomacySaveCodec.Validate` werfen. Ab der
            // Auflehnung haette jedes Speichern geworfen.
            // `BreakCoalition` reiht ein; `CommitTransitionState` setzt dann
            // Beziehung, Koalitionsflag und Mannschaften ZUSAMMEN. Die
            // Bruchart reist im Grund mit, weil der Uebergang im
            // legacy-Spielstandformat gespeichert wird und ein neues Feld eine
            // Formataenderung waere.
            // Meldung und Protokollzeile stehen deshalb im Commit: bevor der
            // Uebergang durch ist, hat sich niemand losgesagt, und ein
            // abgebrochener Uebergang haette den Spieler belogen.
            BreakCoalition(coalition, day, VassalRebellionReason);
        }

        // A single resource line settles the complete gold invoice. Choosing
        // the smallest share of a stockpile avoids stripping a scarce good;
        // the journal still records only the actual transfer.
        private bool TrySelectInKindPayment(int payer, int recipient, DiplomacyRelationState state, out int goodId, out int amount, out string failure)
        {
            RequireNativeDiplomacySection();
            goodId = 0;
            amount = 0;
            failure = null;
            int selectedStock = 0;
            long selectedOverpay = long.MaxValue;
            foreach (int candidate in SendableGoods)
            {
                int price;
                int needed;
                if (!_frozenPrices.TryGetValue(candidate, out price))
                {
                    failure = "frozen_price_unavailable";
                    return false;
                }

                if (!DiplomacyTribute.TryEquivalentGoodsAmount(state, candidate, price, out needed))
                    continue;
                int payerStock;
                if (!TryReadTransferAmount(payer, candidate, out payerStock))
                {
                    failure = "resource_stock_unavailable";
                    continue;
                }

                if (payerStock < needed)
                    continue;
                int recipientStock;
                if (!TryReadTransferAmount(recipient, candidate, out recipientStock))
                {
                    failure = "recipient_stock_unavailable";
                    continue;
                }

                if (recipientStock > int.MaxValue - needed)
                {
                    failure = "recipient_capacity_unavailable";
                    continue;
                }

                long overpay = (long)needed * price - state.TributeAmount;
                if (goodId == 0 || (long)needed * selectedStock < (long)amount * payerStock || (long)needed * selectedStock == (long)amount * payerStock && (overpay < selectedOverpay || overpay == selectedOverpay && candidate < goodId))
                {
                    goodId = candidate;
                    amount = needed;
                    selectedStock = payerStock;
                    selectedOverpay = overpay;
                }
            }

            if (goodId != 0)
                failure = null;
            return failure == null;
        }

        private void ProcessTribute(CoalitionState coalition, RelationState relation, int day)
        {
            DiplomacyRelationState state = relation.ExtendedState;
            if (state == null || !DiplomacyTribute.IsActive(state) || _nativeTransferProbe != null || _pendingTeamTransition != null || _pendingPackage != null || _inventoryRestorePending || _goodsTransfersUnavailable)
            {
                return;
            }

            // Survival and the payment/default decision belong to one native
            // snapshot. A defeated payer must not be charged after an earlier
            // unprotected alive check; nested inventory operations are reentrant.
            IDisposable lease;
            if (!TryEnterNativeInventorySection(out lease))
            {
                NoteTributeTechnicalFailure(coalition.CoalitionId, "native_inventory_busy");
                return;
            }

            using (lease)
                ProcessTributeUnderLease(coalition, relation, day);
        }

        private void ProcessTributeUnderLease(CoalitionState coalition, RelationState relation, int day)
        {
            RequireNativeDiplomacySection();
            DiplomacyRelationState state = relation.ExtendedState;
            try
            {
                // Beide Seiten sind benannt: der Tribut geht vom
                // Vasallen an SEINEN Lehnsherrn, und beide koennen
                // Bundesgenossen sein, an denen der oertliche Spieler nicht
                // beteiligt ist (Vorgabe des Benutzers, ).
                int payer = DiplomacyVassalage.VassalOf(state);
                int recipient = DiplomacyVassalage.OverlordOf(state);
                if (payer <= 0 || recipient <= 0 || !IsAlivePlayer(payer) || !IsAlivePlayer(recipient))
                {
                    // Eine Seite fehlt oder ist besiegt. Die Pflicht endet
                    // lautlos - ein besiegter Empfaenger ist kein
                    // Versaeumnis des Zahlers, und ein Tribut ohne benannte
                    // Seiten ist ein Datenfehler, keine Schuld.
                    DiplomacyTribute.Clear(state);
                    DiplomacyVassalage.ClearWithoutRelease(state);
                    Log("tribute", "coalition=" + coalition.CoalitionId + "|status=void|reason=party_gone|payer=" + payer.ToString(CultureInfo.InvariantCulture) + "|payee=" + recipient.ToString(CultureInfo.InvariantCulture));
                    return;
                }

                int goodId = state.TributeGoodId;
                int amount = state.TributeAmount;
                if (!DiplomacyTribute.IsDue(state, day))
                    return;
                int stock;
                if (!TryReadTransferAmount(payer, goodId, out stock))
                {
                    NoteTributeTechnicalFailure(coalition.CoalitionId, "stock_unavailable");
                    return;
                }

                int paymentGoodId = goodId;
                int paymentAmount = amount;
                bool zahlbar = stock >= amount;
                if (!zahlbar && goodId == DiplomacyTribute.GoldGoodId)
                {
                    string lookupFailure;
                    if (!TrySelectInKindPayment(payer, recipient, state, out paymentGoodId, out paymentAmount, out lookupFailure))
                    {
                        NoteTributeTechnicalFailure(coalition.CoalitionId, lookupFailure);
                        return;
                    }

                    zahlbar = paymentGoodId != 0;
                }

                string failure = null;
                if (zahlbar)
                {
                    zahlbar = TryAtomicTransferFrom(payer, recipient, paymentGoodId, paymentAmount, out failure, DiplomacyTransferPurpose.Tribute, day);
                    if (!zahlbar)
                    {
                        // A transport failure is not a player's missed payment.
                        // Keep the due date and retry on a later simulation day.
                        NoteTributeTechnicalFailure(coalition.CoalitionId, failure ?? "transfer_unavailable");
                        return;
                    }
                }

                if (zahlbar)
                {
                    // Das Journal schliessen, BEVOR irgendetwas anderes
                    // geschieht (): `TryAtomicTransferFrom` laesst
                    // `_nativeTransferProbe` absichtlich stehen, damit der
                    // Aufrufer eine Wiederherstellung haette. Bleibt es offen,
                    // nimmt `VerifyNativeTransferProbe` die Zahlung auf der
                    // Bildschleife wieder zurueck oder sperrt alle
                    // Guetersendungen der Partie - waehrend der
                    // Beziehungszustand "bezahlt" sagt.
                    // Hier ist nichts nachzupruefen: die Belastung des Zahlers
                    // ist in `TryAtomicTransferFrom` exakt zurueckgelesen
                    // worden, sonst waere `zahlbar` falsch. Der Empfaenger wird
                    // seit NICHT mehr abgeglichen - sein Lager
                    // gehoert einer laufenden Wirtschaft.
                    CompleteVerifiedNativeTransfer(_nativeTransferProbe, "tribute");
                    // Transfer purpose dispatch commits payment history and
                    // resources together, including save/load journal replay.
                    _tributeTechnicalFaults.Remove(coalition.CoalitionId);
                    Log("tribute", "coalition=" + coalition.CoalitionId + "|status=paid|payer=" + payer.ToString(CultureInfo.InvariantCulture) + "|payee=" + recipient.ToString(CultureInfo.InvariantCulture) + "|good=" + paymentGoodId.ToString(CultureInfo.InvariantCulture) + "|amount=" + paymentAmount.ToString(CultureInfo.InvariantCulture) + "|cycles=" + state.TributePaidCycles.ToString(CultureInfo.InvariantCulture) + "|next_due=" + state.TributeNextDueDay.ToString(CultureInfo.InvariantCulture));
                    OnDiplomacyPresentationCommitted(DiplomacyPresentationEvent.Create(DiplomacyPresentationEventKind.TributePaid, _sessionGeneration, day, payer == _localPlayer ? recipient : payer, null, _localPlayer, coalition.CoalitionId, "tribute_cycle", new DiplomacyPresentationReference("tribute_cycle", sequence: state.TributePaidCycles, goodId: paymentGoodId, amount: paymentAmount, intervalDays: state.TributeIntervalDays, dueDay: state.TributeNextDueDay, payerPlayerId: payer, recipientPlayerId: recipient)));
                    return;
                }

                // The original invoice stays due until its verified payment.
                // A continued shortage does not generate a second default.
                if (!DiplomacyTribute.CanRegisterMiss(state, day))
                {
                    return;
                }

                DiplomacyTribute.RegisterMiss(state, day);
                Log("tribute", "coalition=" + coalition.CoalitionId + "|status=arrears|good=" + goodId.ToString(CultureInfo.InvariantCulture) + "|amount=" + amount.ToString(CultureInfo.InvariantCulture) + "|stock=" + stock.ToString(CultureInfo.InvariantCulture) + "|missed=" + state.TributeMissedCycles.ToString(CultureInfo.InvariantCulture) + "|reason=" + Safe(failure ?? "insufficient_stock"));
            }
            catch (Exception exception)
            {
                // Technical failure never forgives a debt or breaks a treaty.
                NoteTributeTechnicalFailure(coalition.CoalitionId, exception.GetType().Name);
            }
        }

        private void FulfillDemandPackage(int targetPlayer, PendingGoodsCommand command)
        {
            if (RejectUnavailableGoodsTransfer())
            {
                return;
            }

            string coalitionId;
            RelationState relation;
            if (!_playerCoalition.TryGetValue(targetPlayer, out coalitionId) || !_relations.TryGetValue(coalitionId, out relation) || relation.Demand == null || relation.Demand.Complete || !IsAlivePlayer(targetPlayer) || targetPlayer == _localPlayer)
            {
                ShowMessage(Text("Keine erfüllbare Forderung ausgewählt.", "No payable demand is selected."));
                return;
            }

            if (_nativeTransferProbe != null || _pendingPackage != null || _pendingTeamTransition != null)
            {
                ShowMessage(Text("Eine andere Übertragung wird noch abgeschlossen.", "Another transfer is still being completed."));
                return;
            }

            // The command was constructed by an explicit Mod-owned demand
            // response UI click. Identifiers come from that immutable command,
            // never from a later tuple search in request history.
            if (command == null || !ReferenceEquals(command.Demand, relation.Demand) ||
                !command.RecordedResponsePrepared || command.RecordedRequestIds == null ||
                command.DemandQuote == null || command.DemandQuote.Items == null ||
                command.RecordedRequestIds.Length != relation.Demand.Items.Length ||
                !SameGoodsDemandQuote(relation.Demand, command.DemandQuote))
                return;

            DemandPackageLine[] lines = relation.Demand.Items
                .Select((item, index) => new { Item = item, Index = index })
                .Where(value => value.Item != null && value.Item.RemainingAmount > 0)
                .Select(value => new DemandPackageLine
                {
                    GoodId = value.Item.GoodId,
                    Amount = value.Item.RemainingAmount,
                    RequestId = command.RecordedRequestIds[value.Index],
                }).ToArray();
            if (lines.Length == 0 || lines.Any(line => line.RequestId == 0))
                return;

            List<string> missing;
            bool boundsValid;
            if (!TryReadPackageAmounts(targetPlayer, lines, out missing, out boundsValid))
                return;
            if (!boundsValid)
            {
                ShowMessage(Text("Das Güterpaket kann nicht sicher geprüft werden.", "The goods package cannot be verified safely."));
                Log("demand_package", "coalition=" + coalitionId + "|status=rejected|reason=inventory_bounds");
                return;
            }

            if (missing.Count > 0)
            {
                ShowMessage(Text("Es fehlen: ", "Missing: ") + string.Join(", ", missing));
                Log("demand_package", "coalition=" + coalitionId + "|status=rejected|reason=insufficient_stock");
                return;
            }

            if (!TryForwardRecordedDemandResponse(command))
                return;

            // The single-gift path already owns an authenticated two-cell
            // journal and request receipt. Do NOT also invoke Ally_SendGoods.
            if (lines.Length == 1)
            {
                command.TransferAttempted = true;
                string failure;
                if (!TryExecuteRequestBoundModGift(lines[0].RequestId,
                        command.Generation, command.Fingerprint,
                        command.RosterFingerprint, targetPlayer,
                        lines[0].GoodId, lines[0].Amount, out failure))
                {
                    Log("demand_package", "status=single_response_not_completed|reason=" +
                        Safe(failure ?? "unconfirmed"));
                    ShowMessage(Text("Die Sendung konnte nicht abgeschlossen werden.",
                        "The transfer could not be completed."));
                }
                return;
            }

            command.TransferAttempted = true;
            ExecuteDemandPackage(targetPlayer, relation, lines);
        }

        private void HandleDayOutcome(CoalitionState coalition, RelationState relation, int gameDay, bool expired)
        {
            if (expired)
            {
                BreakCoalition(coalition, gameDay, "_ultimatum_expired");
                return;
            }

            if (relation.ExtendedState != null && relation.ExtendedState.LastEvent == DiplomacyEventKind.DemandMissed && relation.ExtendedState.LastEventDay == gameDay)
            {
                foreach (int player in coalition.MemberPlayerIds)
                    RecordGoodsOutcome(player, "demand_expired", false);
            }

            if (relation.ExtendedState != null && relation.ExtendedState.LastEvent == DiplomacyEventKind.AgreementExpired && relation.ExtendedState.LastEventDay == gameDay)
            {
                ShowMessage(Text("Das Abkommen ist ausgelaufen.", "The agreement has expired."));
                Log("agreement", "coalition=" + coalition.CoalitionId + "|status=expired");
            }

            if (relation.ExtendedState != null && relation.ExtendedState.LastEvent == DiplomacyEventKind.ProactiveSupportRecognized && relation.ExtendedState.LastEventDay == gameDay)
            {
                Log("support", "coalition=" + coalition.CoalitionId + "|status=proactive_credit_accepted");
            }

            GoodsDemand demand = relation.Demand;
            if (demand == null || demand.IssuedDay != gameDay)
            {
                return;
            }

            foreach (int player in coalition.MemberPlayerIds)
                ClearGoodsOutcome(player);
            if (demand.DemandPurposeDescription == DiplomacyDemandPurpose.Support)
            {
                PlayCoalitionSpeech(coalition, "SK_REQUEST_GOODS", "req");
                ShowMessage(DiplomacyDemandText(demand));
                Log("support", "coalition=" + coalition.CoalitionId + "|status=requested|expires=" + demand.ExpiresDay.ToString(CultureInfo.InvariantCulture));
            }
            else if (demand.DemandPurposeDescription == DiplomacyDemandPurpose.Ultimatum)
            {
                PlayCoalitionSpeech(coalition, "SK_REQUEST_GOODS", "req");
                ShowMessage(Text("Euer Verbündeter stellt eine letzte Forderung. ", "Your ally has issued a final demand. ") + DiplomacyDemandText(demand));
                Log("ultimatum", "coalition=" + coalition.CoalitionId + "|status=issued|expires=" + demand.ExpiresDay.ToString(CultureInfo.InvariantCulture));
            }
        }

        private void ActivateAgreementSafety(CoalitionState coalition)
        {
            IDisposable lease;
            if (!TryEnterNativeInventorySection(out lease))
            {
                _pendingAgreementSafety.Add(coalition.CoalitionId);
                PublishCombatProtectionSnapshot();
                return;
            }

            using (lease)
            {
                // A treaty is concluded with the player's complete diplomatic
                // bloc, not only with the human player object. Use a synthetic
                // positive partition because Vanilla represents unaffiliated
                // players with team zero, which must never be mistaken for a
                // shared alliance while stale combat is being purged.
                var partition = _roster.ToDictionary(player => player, player => player);
                foreach (CoalitionState ally in _coalitions.Values)
                {
                    RelationState allyRelation = _relations[ally.CoalitionId];
                    if (!allyRelation.Allied)
                    {
                        continue;
                    }

                    foreach (int member in ally.MemberPlayerIds)
                    {
                        partition[member] = _localPlayer;
                    }
                }

                foreach (int member in coalition.MemberPlayerIds)
                {
                    partition[member] = _localPlayer;
                }

                int tribes = QuarantineAlliedTribes(partition);
                int projectiles = QuarantineAlliedProjectiles(partition);
                int fires = QuarantineAlliedBuildingFires(partition);
                int targets = QuarantineAlliedAiTargets(partition);
                Log("agreement_safety", "coalition=" + coalition.CoalitionId + "|tribes_stopped=" + tribes.ToString(CultureInfo.InvariantCulture) + "|projectiles_deleted=" + projectiles.ToString(CultureInfo.InvariantCulture) + "|building_fires_cleared=" + fires.ToString(CultureInfo.InvariantCulture) + "|ai_targets_cleared=" + targets.ToString(CultureInfo.InvariantCulture));
                _pendingAgreementSafety.Remove(coalition.CoalitionId);
            }
        }

        private void RetryAgreementSafety()
        {
            foreach (string id in _pendingAgreementSafety.ToArray())
            {
                CoalitionState coalition;
                RelationState relation;
                if (!_coalitions.TryGetValue(id, out coalition) || !_relations.TryGetValue(id, out relation) || relation.ExtendedState == null || !DiplomacyCombatRules.TreatyProtects(relation.Allied, relation.ExtendedState.Agreement, relation.ExtendedState.AgreementUntilDay, AbsoluteGameDay()))
                {
                    _pendingAgreementSafety.Remove(id);
                    continue;
                }

                ActivateAgreementSafety(coalition);
            }
        }

        private bool TreatyProtectsPlayers(int first, int second)
        {
            if (first == second)
            {
                return false;
            }

            bool firstInLocalBloc = IsLocalDiplomaticBlocPlayer(first);
            bool secondInLocalBloc = IsLocalDiplomaticBlocPlayer(second);
            if (firstInLocalBloc == secondInLocalBloc)
            {
                return false;
            }

            int foreign = firstInLocalBloc ? second : first;
            string coalitionId;
            RelationState relation;
            if (!_playerCoalition.TryGetValue(foreign, out coalitionId) || !_relations.TryGetValue(coalitionId, out relation) || relation.Allied || relation.ExtendedState == null)
            {
                return false;
            }

            int day = Math.Max(0, _lastProcessedDay);
            return DiplomacyCombatRules.TreatyProtects(relation.Allied, relation.ExtendedState.Agreement, relation.ExtendedState.AgreementUntilDay, day);
        }

        private bool IsLocalDiplomaticBlocPlayer(int player)
        {
            if (player == _localPlayer)
            {
                return true;
            }

            string coalitionId;
            RelationState relation;
            return player > 0 && _playerCoalition.TryGetValue(player, out coalitionId) && _relations.TryGetValue(coalitionId, out relation) && relation.Allied;
        }

        // Zuletzt gemeldete Einheitenzahl in der Kernzone je Koalition. Nur
        // fuer die Meldung - eine Zeile je Aenderung, nicht je Zaehlung
        // (; der Auslassungszaehler hat vorgemacht, wie ein Melder
        // ein Protokoll zudeckt).
        private readonly Dictionary<string, int> _incursionCountSeen = new Dictionary<string, int>(StringComparer.Ordinal);
        // Entfernungsbaender um das Burgtor, in lokalen Kacheln. 18 ist die
        // heutige Kernzone; die drei weiteren sagen, wie weit eine
        // Streitmacht tatsaechlich steht ().
        private static readonly int[] IncursionBands =
        {
            18,
            36,
            72,
            144
        };
        private bool TryResolveProtectedGroundAttackTarget(int source, TribeAICommand command, int targetValue1, int targetValue2, out int target)
        {
            target = 0;
            if (!_roster.Contains(source) || (command != TribeAICommand.AttackTilePosition && command != TribeAICommand.ThrowLava))
            {
                return false;
            }

            int worldX = DiplomacyCombatRules.TileCenterWorld(targetValue1);
            int worldY = DiplomacyCombatRules.TileCenterWorld(targetValue2);
            return TryFindDiplomaticCounterpartNearWorldTarget(source, worldX, worldY, GroundFireProjectileRiskRadiusWorld, true, out target);
        }

        private void RecordUnprotectedHostileAct(int source, int target)
        {
            // This state models the foreign ruler's opinion of the human.
            // Enemy attacks must therefore never be booked as treachery by
            // the player; on an active treaty they are blocked earlier by the
            // combat guard, and outside a treaty they remain ordinary enemy
            // warfare. Only player-originated hostility damages diplomacy.
            if (source != _localPlayer || target <= 0 || source == target || TreatyProtectsPlayers(source, target))
            {
                return;
            }

            int foreign = target;
            string coalitionId;
            RelationState relation;
            CoalitionState coalition;
            if (!_playerCoalition.TryGetValue(foreign, out coalitionId) || !_relations.TryGetValue(coalitionId, out relation) || !_coalitions.TryGetValue(coalitionId, out coalition) || relation.Allied || relation.ExtendedState == null)
            {
                return;
            }

            DiplomacyAgreementRules.RecordHostileAct(relation, Math.Max(0, _lastProcessedDay), coalition.Target);
        }

        private bool TryFindDiplomaticCounterpartNearWorldTarget(int sourcePlayer, int worldTileX, int worldTileY, int riskRadius, bool requireActiveTreaty, out int counterpartOwner)
        {
            counterpartOwner = 0;
            // Schutzsuche: ohne aktiven Pakt kann sie nichts finden - die
            // Abkuerzung aendert das Ergebnis nicht, spart aber je
            // Feuerereignis den Vollscan.
            if (requireActiveTreaty && !AnyActiveTreaty())
            {
                return false;
            }

            var hits = new List<CounterpartHit>();
            if (!ScanDiplomaticCounterparts(sourcePlayer, worldTileX, worldTileY, riskRadius, requireActiveTreaty, hits))
            {
                return false;
            }

            CounterpartHit best = null;
            foreach (CounterpartHit hit in hits)
            {
                if (best == null || hit.Treaty && !best.Treaty || hit.Treaty == best.Treaty && hit.Distance < best.Distance)
                {
                    best = hit;
                }
            }

            if (best == null)
            {
                return false;
            }

            counterpartOwner = best.Owner;
            return true;
        }

        /// <summary>
        /// Feindakt-Zuordnung fuer Weltziele ohne Besitzer (Boden, Feuer).
        /// Erst pruefen, dann drosseln (Pruefbefund P1-A, ): der
        /// alte Sentinel int.MinValue lief bei der Differenz ueber, die
        /// Zuordnung blieb ab Takt 0 fuer immer gedrosselt. Jeder Treffer im
        /// Umkreis wird gebucht, nicht nur der naechste - RecordHostileAct
        /// ist je Verbund und Tag idempotent, darum genuegt ein Vollscan je
        /// 32 Takte.
        /// </summary>
        private void AttributeHostileActNearWorldTarget(int sourcePlayer, int worldTileX, int worldTileY, int riskRadius)
        {
            if (sourcePlayer != _localPlayer || GameUnitManagerAPI.Instance == null)
            {
                return;
            }

            int worldLimit = checked(GameTileManagerAPI.MAX_WIDTH * WorldPositionsPerLocalTile);
            if (!_roster.Contains(sourcePlayer) || worldTileX < 0 || worldTileX >= worldLimit || worldTileY < 0 || worldTileY >= worldLimit)
            {
                return;
            }

            if (!DiplomacyCombatRules.ScanDue(_lastHostileAttributionTick, ReadSimulationTick(), DiplomacyCombatRules.HostileAttributionIntervalTicks))
            {
                return;
            }

            _lastHostileAttributionTick = ReadSimulationTick();
            var hits = new List<CounterpartHit>();
            ScanDiplomaticCounterparts(sourcePlayer, worldTileX, worldTileY, riskRadius, false, hits);
            var booked = new HashSet<int>();
            foreach (CounterpartHit hit in hits)
            {
                if (booked.Add(hit.Owner))
                {
                    RecordUnprotectedHostileAct(sourcePlayer, hit.Owner);
                }
            }
        }

        /// <summary>
        /// Alle diplomatischen Gegenparteien (Einheiten und Kacheln) im
        /// Umkreis eines Weltziels, mit Abstand und Paktschutz. False nur,
        /// wenn Quelle oder Koordinaten ungueltig sind.
        /// </summary>
        private bool ScanDiplomaticCounterparts(int sourcePlayer, int worldTileX, int worldTileY, int riskRadius, bool requireActiveTreaty, List<CounterpartHit> hits)
        {
            int worldLimit = checked(GameTileManagerAPI.MAX_WIDTH * WorldPositionsPerLocalTile);
            if (!_roster.Contains(sourcePlayer) || worldTileX < 0 || worldTileX >= worldLimit || worldTileY < 0 || worldTileY >= worldLimit)
            {
                return false;
            }

            int boundedRadius = Math.Max(0, Math.Min(riskRadius, worldLimit));
            long squaredRadius = (long)boundedRadius * boundedRadius;
            var unitIds = new List<int>();
            GameUnitManagerAPI units = GameUnitManagerAPI.Instance;
            if (units == null)
            {
                return false;
            }

            units.GetAllUnits(unitIds, AliveState.IsAlive);
            unitIds.Sort();
            bool sourceInLocalBloc = IsLocalDiplomaticBlocPlayer(sourcePlayer);
            foreach (int unitId in unitIds)
            {
                int owner = UnitOwner(unitId);
                bool ownerInLocalBloc = IsLocalDiplomaticBlocPlayer(owner);
                if (owner <= 0 || owner == sourcePlayer || sourceInLocalBloc == ownerInLocalBloc)
                {
                    continue;
                }

                int foreign = sourceInLocalBloc ? owner : sourcePlayer;
                string coalitionId;
                RelationState relation;
                if (!_playerCoalition.TryGetValue(foreign, out coalitionId) || !_relations.TryGetValue(coalitionId, out relation) || relation.Allied || relation.ExtendedState == null)
                {
                    continue;
                }

                bool protectedByTreaty = TreatyProtectsPlayers(sourcePlayer, owner);
                if (requireActiveTreaty && !protectedByTreaty)
                {
                    continue;
                }

                UnmanagedVector2<ushort> position = units.GetCurrentWorldTilePosition(unitId);
                long deltaX = (long)position.X - worldTileX;
                long deltaY = (long)position.Y - worldTileY;
                long distance = deltaX * deltaX + deltaY * deltaY;
                if (distance > squaredRadius)
                {
                    continue;
                }

                hits.Add(new CounterpartHit { Owner = owner, Distance = distance, Treaty = protectedByTreaty, });
            }

            int minimumTileX = Math.Max(0, worldTileX - boundedRadius) / WorldPositionsPerLocalTile;
            int maximumTileX = Math.Min(worldLimit - 1, worldTileX + boundedRadius) / WorldPositionsPerLocalTile;
            int minimumTileY = Math.Max(0, worldTileY - boundedRadius) / WorldPositionsPerLocalTile;
            int maximumTileY = Math.Min(worldLimit - 1, worldTileY + boundedRadius) / WorldPositionsPerLocalTile;
            for (int tileY = minimumTileY; tileY <= maximumTileY; tileY++)
            {
                for (int tileX = minimumTileX; tileX <= maximumTileX; tileX++)
                {
                    int owner = TileOwner(tileX, tileY);
                    bool ownerInLocalBloc = IsLocalDiplomaticBlocPlayer(owner);
                    if (owner <= 0 || owner == sourcePlayer || sourceInLocalBloc == ownerInLocalBloc)
                    {
                        continue;
                    }

                    int foreign = sourceInLocalBloc ? owner : sourcePlayer;
                    string coalitionId;
                    RelationState relation;
                    if (!_playerCoalition.TryGetValue(foreign, out coalitionId) || !_relations.TryGetValue(coalitionId, out relation) || relation.Allied || relation.ExtendedState == null)
                    {
                        continue;
                    }

                    bool protectedByTreaty = TreatyProtectsPlayers(sourcePlayer, owner);
                    if (requireActiveTreaty && !protectedByTreaty)
                    {
                        continue;
                    }

                    long tileMinimumWorldX = (long)tileX * WorldPositionsPerLocalTile;
                    long tileMaximumWorldX = tileMinimumWorldX + WorldPositionsPerLocalTile - 1;
                    long tileMinimumWorldY = (long)tileY * WorldPositionsPerLocalTile;
                    long tileMaximumWorldY = tileMinimumWorldY + WorldPositionsPerLocalTile - 1;
                    long deltaX = worldTileX < tileMinimumWorldX ? tileMinimumWorldX - worldTileX : worldTileX > tileMaximumWorldX ? worldTileX - tileMaximumWorldX : 0L;
                    long deltaY = worldTileY < tileMinimumWorldY ? tileMinimumWorldY - worldTileY : worldTileY > tileMaximumWorldY ? worldTileY - tileMaximumWorldY : 0L;
                    long distance = deltaX * deltaX + deltaY * deltaY;
                    if (distance > squaredRadius)
                    {
                        continue;
                    }

                    hits.Add(new CounterpartHit { Owner = owner, Distance = distance, Treaty = protectedByTreaty, });
                }
            }

            return true;
        }

        private bool AnyActiveTreaty()
        {
            foreach (RelationState relation in _relations.Values)
            {
                if (HasBreakableAgreement(relation))
                {
                    return true;
                }
            }

            return false;
        }

        private bool HasBreakableAgreement(RelationState relation)
        {
            return relation != null && relation.ExtendedState != null && DiplomacyCombatRules.TreatyProtects(relation.Allied, relation.ExtendedState.Agreement, relation.ExtendedState.AgreementUntilDay, _lastProcessedDay);
        }

        private void ConfirmBreakAgreement(int selectedPlayer)
        {
            string id;
            if (!_playerCoalition.TryGetValue(selectedPlayer, out id))
                return;
            RelationshipDecisionQuote quote = CaptureRelationshipDecision(id, selectedPlayer, DiplomacyRelationshipDecisionKind.EndAgreement);
            if (quote == null)
                return;
            ShowConfirmation(quote.Names + " · " + Text("Abkommen aufkündigen", "End agreement"), delegate
            {
                StageRelationshipDecision(quote);
            }, delegate
            {
            }, quote.Names + " · " + Text("Der Gegner wird den Vertragsbruch lange in Erinnerung behalten.", "The opponent will remember this breach for a long time."), null, quote.Key, delegate
            {
                lock (_gate)
                    return IsCurrentRelationshipDecision(quote);
            });
        }

        private string NegotiationStatusText(RelationState relation, CoalitionState coalition, int player = 0)
        {
            DiplomacyRelationState state = relation.ExtendedState;
            if (state == null)
            {
                return Text("unbekannt", "unknown");
            }

            int day = AbsoluteGameDay();
            if (relation.Allied)
            {
                if (IsFealtyCounterpart(state, player))
                {
                    return VassalRoleText(state);
                }

                if (relation.Demand != null && relation.Demand.DemandPurposeDescription == DiplomacyDemandPurpose.Ultimatum)
                {
                    return Text("Bündnis unter Vorbehalt — ", "Alliance at risk — ") + DurationText(relation.Demand.ExpiresDay - day);
                }

                if (relation.Demand != null)
                {
                    return Text("Bündnis · Unterstützung erbeten — ", "Alliance · support requested — ") + DurationText(relation.Demand.ExpiresDay - day);
                }

                // The shared relation suspends maintenance while its named
                // fealty is active, also for ordinary peers in legacy teams.
                if (DiplomacyVassalage.IsActive(state))
                    return Text("verbündet", "allied");
                return Text("Bündnis — nächste Prüfung ", "Alliance — next review ") + DurationText(state.MaintenanceDueDay - day);
            }

            if (state.Agreement == DiplomacyAgreementKind.Truce && state.AgreementUntilDay > day)
            {
                return Text("Waffenruhe — ", "Truce — ") + DurationText(state.AgreementUntilDay - day);
            }

            if (state.Agreement == DiplomacyAgreementKind.TradePact && state.AgreementUntilDay > day)
            {
                return Text("Handelsabkommen — ", "Trade pact — ") + DurationText(state.AgreementUntilDay - day);
            }

            if (day < relation.RelationshipBlockedUntilDay)
            {
                return Text("Gespräche ausgesetzt — ", "Talks suspended — ") + DurationText(relation.RelationshipBlockedUntilDay - day);
            }

            switch (DiplomacyAgreementRules.DisplayStage(relation, day))
            {
                case DiplomacyNegotiationStage.Hostile:
                    return Text("feindselig", "hostile");
                case DiplomacyNegotiationStage.Contact:
                    return Text("gesprächsbereit", "willing to talk");
                case DiplomacyNegotiationStage.Negotiating:
                    return state.Agreement == DiplomacyAgreementKind.Truce ? Text("Waffenruhe", "truce") : Text("in Verhandlung", "negotiating");
                case DiplomacyNegotiationStage.Trusted:
                    return state.Agreement == DiplomacyAgreementKind.TradePact ? Text("Handelspartner", "trade partner") : Text("vertrauensvoll", "trusted");
                case DiplomacyNegotiationStage.AllianceReady:
                    return CanJoinAlliance(coalition.CoalitionId) ? Text("bündnisbereit", "ready to ally") : Text("Bündnis nicht möglich", "alliance unavailable");
                case DiplomacyNegotiationStage.Allied:
                    return Text("verbündet", "allied");
                case DiplomacyNegotiationStage.Ultimatum:
                    return Text("warnt Euch", "warning you");
                default:
                    return Text("misstrauisch", "distrustful");
            }
        }

        private string RelationshipSummary(RelationState relation)
        {
            DiplomacyRelationState state = relation.ExtendedState;
            if (state == null)
            {
                return Text("Beziehung unbekannt", "Relationship unknown");
            }

            return Text("Herrscher: ", "Ruler: ") + PersonalityName(state.Personality) + Text(" · Vertrauen ", " · trust ") + Level(state.Trust) + Text(" · Respekt ", " · respect ") + Level(state.Respect) + Text(" · Groll ", " · grudge ") + Level(state.Grudge);
        }

        private string ActionHint(RelationState relation, CoalitionState coalition)
        {
            if (HasPendingGoodsCommand)
                return PendingGoodsCommandStatusText();
            if (relation.Demand != null)
            {
                return DiplomacyDemandText(relation.Demand);
            }

            if (DiplomacySubmission.HasOffer(relation.ExtendedState))
                return relation.ExtendedState.SubmissionAcceptancePending || relation.ExtendedState.SubmissionOfferAccepted ? Text("Unterwerfung bestätigt", "Submission accepted") : Text("Entscheidung offen", "Decision pending");
            if (relation.PendingOffer)
            {
                return Text("Entscheidung offen", "Decision pending");
            }

            if (relation.Allied)
            {
                return RelationshipSummary(relation);
            }

            int waitDays = DiplomacyAgreementRules.NegotiationWaitDays(relation, AbsoluteGameDay());
            if (!relation.Allied && waitDays > 0)
            {
                return waitDays == 1 ? Text("Gespräche in 1 Tag", "Talks in 1 day") : string.Format(CultureInfo.InvariantCulture, Text("Gespräche in {0} Tagen", "Talks in {0} days"), waitDays);
            }

            if (relation.ExtendedState.RestitutionOutstandingValue > 0m)
            {
                return Text("Nächster Schritt: Bedingungen zur Wiedergutmachung anfragen.", "Next step: request terms for restitution.");
            }

            switch (relation.ExtendedState.Agreement)
            {
                case DiplomacyAgreementKind.None:
                    return relation.ExtendedState.Stage == DiplomacyNegotiationStage.Hostile ? Text("Nächster Schritt: Benötigte Güter senden, um Gesprächsbereitschaft zu schaffen.", "Next step: send needed goods to open talks.") : Text("Nächster Schritt: Bedingungen für eine Waffenruhe anfragen.", "Next step: request terms for a truce.");
                case DiplomacyAgreementKind.Truce:
                    return Text("Nächster Schritt: Bedingungen für ein Handelsabkommen anfragen.", "Next step: request terms for a trade pact.");
                case DiplomacyAgreementKind.TradePact:
                    if (DiplomacyAgreementRules.CanFormAlliance(relation, coalition.Target))
                    {
                        return CanJoinAlliance(coalition.CoalitionId) ? Text("Der Verbund ist zu einem Bündnis bereit.", "The coalition is ready for an alliance.") : Text("Mindestens ein feindlicher KI-Verbund muss verbleiben.", "At least one hostile AI coalition must remain.");
                    }

                    if (relation.ExtendedState.AllianceTermsFulfilled)
                    {
                        return DiplomacyAgreementRules.PermanentHistoryPreventsAlliance(relation) ? Text("Die Bedingungen sind erfüllt; frühere Vertragsbrüche verhindern dieses Bündnis dauerhaft.", "Terms are fulfilled; earlier treaty breaches permanently prevent this alliance.") : Text("Die Bedingungen sind erfüllt. Benötigte Güter senden, um Vertrauen und Ansehen weiter zu festigen.", "Terms are fulfilled. Send needed goods to strengthen trust and standing.");
                    }

                    return Text("Nächster Schritt: Bündnisbedingungen anfragen und Vertrauen festigen.", "Next step: request alliance terms and strengthen trust.");
                default:
                    return RelationshipSummary(relation);
            }
        }

        private string DiplomacyDemandText(GoodsDemand demand)
        {
            string prefix;
            switch (demand.DemandPurposeDescription)
            {
                case DiplomacyDemandPurpose.Courtship:
                    prefix = Text("Bedingung zur Waffenruhe: ", "Truce terms: ");
                    break;
                case DiplomacyDemandPurpose.TradeProof:
                    prefix = Text("Handelsbedingung: ", "Trade terms: ");
                    break;
                case DiplomacyDemandPurpose.AllianceTerms:
                    prefix = Text("Bündnisbedingung: ", "Alliance terms: ");
                    break;
                case DiplomacyDemandPurpose.Support:
                    prefix = Text("Erbetene Unterstützung: ", "Requested support: ");
                    break;
                case DiplomacyDemandPurpose.Ultimatum:
                    prefix = Text("Letzte Forderung: ", "Final demand: ");
                    break;
                case DiplomacyDemandPurpose.Restitution:
                    prefix = Text("Wiedergutmachung: ", "Restitution: ");
                    break;
                default:
                    prefix = demand.IsUltimatum ? Text("Ultimatum: ", "Ultimatum: ") : Text("Forderung: ", "Demand: ");
                    break;
            }

            string goods = string.Join(" · ", demand.Items.Where(item => item != null && item.RemainingAmount > 0).Select(item => item.RemainingAmount.ToString(CultureInfo.InvariantCulture) + " " + GoodName(item.GoodId, item.RemainingAmount)));
            int remaining = Math.Max(0, demand.ExpiresDay - AbsoluteGameDay());
            return prefix + goods + " — " + DurationText(remaining);
        }

        private string DemandProgressText(GoodsDemand demand)
        {
            return Text("Lieferung verbucht. Offen: ", "Delivery recorded. Due: ") + DiplomacyDemandText(demand);
        }

        private string DemandPackageHint()
        {
            return Text("Bezahlt die offenen Positionen dieser Forderung.", "Pays the outstanding items in this demand.");
        }

        private string GraceText(int days)
        {
            int bounded = Math.Max(0, days);
            return bounded == 1 ? Text("Schonfrist: 1 Tag", "Grace: 1 day") : string.Format(CultureInfo.InvariantCulture, Text("Schonfrist: {0} Tage", "Grace: {0} days"), bounded);
        }

        private string DurationText(int days)
        {
            int bounded = Math.Max(0, days);
            // These are binding game-day deadlines, not approximate durations.
            return bounded == 1 ? Text("Noch 1 Tag", "1 day remaining") : string.Format(CultureInfo.InvariantCulture, Text("Noch {0} Tage", "{0} days remaining"), bounded);
        }

        private string RequestRolloverText(RelationState relation, int player = 0)
        {
            if (relation != null && relation.Demand != null)
            {
                return Text("Forderung vollständig erfüllen", "Fulfil complete demand");
            }

            if (relation == null || relation.ExtendedState == null)
            {
                return Text("Bedingungen anfragen", "Request terms");
            }

            if (DiplomacySubmission.HasOffer(relation.ExtendedState))
                return relation.ExtendedState.SubmissionAcceptancePending || relation.ExtendedState.SubmissionOfferAccepted ? Text("Unterwerfung bestätigt", "Submission accepted") : Text("Entscheidung offen", "Decision pending");
            if (relation.PendingOffer)
            {
                return Text("Eine Bündnisentscheidung ist bereits offen.", "An alliance decision is already pending.");
            }

            if (relation.Allied)
            {
                if (!CanRequestGoodsFrom(player))
                    return Text("Als Vasall könnt Ihr keine Güter fordern.", "As a vassal, you cannot request goods.");
                return Text("Güter vom Verbündeten erbitten", "Request goods from ally");
            }

            int waitDays = DiplomacyAgreementRules.NegotiationWaitDays(relation, AbsoluteGameDay());
            if (!relation.Allied && waitDays > 0)
            {
                return Text("Wartezeit: ", "Waiting period: ") + DurationText(waitDays);
            }

            if (relation.ExtendedState.RestitutionOutstandingValue > 0m)
            {
                return Text("Wiedergutmachung anfragen", "Request restitution terms");
            }

            switch (relation.ExtendedState.Agreement)
            {
                case DiplomacyAgreementKind.None:
                    return Text("Waffenruhe anfragen", "Request truce terms");
                case DiplomacyAgreementKind.Truce:
                    return Text("Handelsabkommen anfragen", "Request trade terms");
                case DiplomacyAgreementKind.TradePact:
                    if (relation.ExtendedState.AllianceTermsFulfilled)
                    {
                        return Text("Bündnisbereitschaft prüfen", "Review alliance readiness");
                    }

                    return Text("Bündnisbedingungen anfragen", "Request alliance terms");
                default:
                    return Text("Unterstützung leisten", "Provide support");
            }
        }

        private void HideDetailPanel(object panel)
        {
            CloseTradeWindow(panel);
            _overviewVisible = false;
            if (panel == null)
            {
                return;
            }

            SetVisibilityIfPresent(FindElement(panel, "AllianceRootPanel"), false);
            SetVisibilityIfPresent(FindElement(panel, "CrusaderDiplomacyNativeDetail"), true);
        }

        /// <summary>
        /// Ist die Buendnisuebersicht aufgeklappt?
        /// </summary>
        /// <remarks>
        /// Bewusst nur Anzeige: das Umschalten aendert keinen Spielzustand,
        /// darum ueberlebt es auch keinen Kartenwechsel ().
        /// </remarks>
        private bool _overviewVisible;
        /// <summary>
        /// Zeigt alle laufenden Abkommen auf einmal.
        /// </summary>
        /// <remarks>
        /// Sparsam: ist die Uebersicht zugeklappt, wird nur die Sichtbarkeit
        /// gesetzt und sonst nichts gerechnet - dieser Weg laeuft je Bild.
        ///
        /// Fehlt ein Textfeld, bleibt die Zeile leer. Ein fehlendes Element
        /// ist kein Grund, die Oberflaeche oder gar die Karte abzuschalten.
        /// </remarks>
        private void RefreshOverviewPanel(object panel)
        {
            UiStep("_overview");
            object button = FindElement(panel, "AllianceOverviewButton");
            object label = FindElement(panel, "AllianceOverviewLabel");
            object details = FindElement(panel, "AllianceDetailsButton");
            object grid = FindElement(panel, "AllianceOverviewPanel");
            object detailPanel = FindElement(panel, "AllianceDetailsPanel");
            SetVisibilityIfPresent(button, true);
            SetVisibilityIfPresent(label, true);
            if (button != null)
                SetProperty(button, "IsChecked", _overviewVisible);
            if (details != null)
                SetProperty(details, "IsChecked", !_overviewVisible && !_tradeTab);
            SetTextIfPresent(panel, "AllianceDetailsLabel", Text("Details", "Details"));
            if (label != null)
            {
                SetProperty(label, "Text", Text("Bündnis", "Alliance"));
            }

            bool showOverview = grid != null && _overviewVisible;
            SetVisibilityIfPresent(detailPanel, !showOverview && !_tradeTab);
            if (grid == null)
            {
                // Overview is optional; if absent, retain usable diplomacy
                // details and trade instead of closing their entire UI.
                _overviewVisible = false;
                SetVisibilityIfPresent(detailPanel, !_tradeTab);
                return;
            }

            SetVisibility(grid, showOverview);
            if (!showOverview)
            {
                // Zugeklappt kostet die Uebersicht nichts weiter.
                return;
            }

            RefreshAllianceManagement(panel);
        }

        /// <summary>Sieben Zeilen - so viele Koalitionen gibt es hoechstens.</summary>
        private const int OverviewRowCount = 7;
        /// <summary>
        /// Setzt einen Text, wenn es das Feld gibt. Fehlt es, passiert nichts.
        /// </summary>
        private void SetTextIfPresent(object panel, string name, string text)
        {
            object element = FindElement(panel, name);
            if (element != null)
            {
                SetProperty(element, "Text", text);
            }
        }

        private void RefreshDetailPanel(object panel, RelationState relation, CoalitionState coalition, int selectedPlayer)
        {
            // Overview has its own selected member and stock snapshot. Do not
            // rebuild hidden detail strings or read its demand a second time.
            if (_overviewVisible)
            {
                object overviewGrid = FindElement(panel, "AllianceOverviewPanel");
                object outerPanel = FindElement(panel, "AllianceRootPanel");
                object nativePanel = FindElement(panel, "CrusaderDiplomacyNativeDetail");
                if (overviewGrid != null && outerPanel != null && nativePanel != null)
                {
                    SetVisibility(nativePanel, false);
                    SetVisibility(outerPanel, true);
                    // Reset stale Noesis trade tab before overview's early
                    // return, rather than leaving its overlay visible.
                    _tradeTab = false;
                    RefreshTradePanel(panel, selectedPlayer);
                    RefreshOverviewPanel(panel);
                    return;
                }
            }

            // One nonblocking native snapshot, consumed after releasing the
            // monitor. Text, buttons and stocks describe the same observation.
            Dictionary<int, int> demandStock = DemandStockSnapshot(relation == null ? null : relation.Demand);
            UiStep("_find");
            object advanced = FindElement(panel, "AllianceRootPanel");
            object native = FindElement(panel, "CrusaderDiplomacyNativeDetail");
            object ruler = FindElement(panel, "AllianceSelectedRuler");
            object agreement = FindElement(panel, "AllianceAgreement");
            object attitudeTitle = FindElement(panel, "AllianceAttitudeTitle");
            object attitudeLine1 = FindElement(panel, "AllianceAttitudeLine1");
            object attitudeLine2 = FindElement(panel, "AllianceAttitudeLine2");
            object gratitude = FindElement(panel, "AllianceGratitude");
            object respect = FindElement(panel, "AllianceRespect");
            object reliability = FindElement(panel, "AllianceReliability");
            object trustCeiling = FindElement(panel, "AllianceTrustCeiling");
            object strategicTitle = FindElement(panel, "AllianceStrategicTitle");
            object strategicLine1 = FindElement(panel, "AllianceStrategicLine1");
            object strategicLine2 = FindElement(panel, "AllianceStrategicLine2");
            object threat = FindElement(panel, "AllianceThreat");
            object dependency = FindElement(panel, "AllianceDependency");
            object commonEnemies = FindElement(panel, "AllianceCommonEnemies");
            object allianceCount = FindElement(panel, "AllianceCount");
            object demandTitle = FindElement(panel, "AllianceDemandTitle");
            object demandLine1 = FindElement(panel, "AllianceDemandLine1");
            object demandLine2 = FindElement(panel, "AllianceDemandLine2");
            object actionHint = FindElement(panel, "AllianceActionHint");
            object primary = FindElement(panel, "AlliancePrimary");
            object primaryLabel = FindElement(panel, "AlliancePrimaryLabel");
            object send = FindElement(panel, "AllianceDetailsSend");
            object sendLabel = FindElement(panel, "AllianceDetailsSendLabel");
            object orders = FindElement(panel, "AllianceDetailsOrders");
            object ordersLabel = FindElement(panel, "AllianceDetailsOrdersLabel");
            object breakButton = FindElement(panel, "AllianceDetailsBreak");
            object declineDemand = FindElement(panel, "AllianceDeclineDemand");
            object declineDemandLabel = FindElement(panel, "AllianceDeclineDemandLabel");
            object breakLabel = FindElement(panel, "AllianceDetailsBreakLabel");
            // the game can temporarily omit decorative Noesis
            // labels/portrait aliases during the first AlliesPanel layout.
            // Those do not make the entire trade/diplomacy view unavailable.
            // Only a missing OUTER ExtendedState grid or absent valid relationship scope
            // prevents displaying meaningful diplomacy. The other targets
            // are written opportunistically through null-safe helpers below.
            if (relation == null || relation.ExtendedState == null || coalition == null || selectedPlayer <= 0 || advanced == null)
            {
                SetVisibilityIfPresent(advanced, false);
                SetVisibilityIfPresent(native, true);
                if (advanced == null)
                {
                    if (!_uiPanelWarningLogged)
                        Log("ui_integration", "status=missing__root|native_detail=" + (native != null ? "present" : "missing"));
                    NotifyUiFallback();
                }

                return;
            }

            UiStep("_texts");
            DiplomacyRelationState state = relation.ExtendedState;
            SetDetailText(ruler, PlayerName(selectedPlayer));
            SetPropertyIfPresent(ruler, "ToolTip", PlayerName(selectedPlayer));
            SetDetailText(agreement, NegotiationStatusText(relation, coalition, selectedPlayer));
            SetDetailText(attitudeTitle, Text("Haltung", "Attitude"));
            SetDetailText(attitudeLine1, Metric(Text("Vertrauen", "Trust"), state.Trust));
            SetDetailText(gratitude, Metric(Text("Dankbarkeit", "Gratitude"), state.Gratitude));
            SetDetailText(respect, Metric(Text("Respekt", "Respect"), state.Respect));
            SetDetailText(attitudeLine2, Metric(Text("Groll", "Grudge"), state.Grudge));
            SetDetailText(reliability, Metric(Text("Verlässlichkeit", "Reliability"), state.Reliability));
            SetDetailText(trustCeiling, Text("Vertrauensgrenze ", "Trust ceiling ") + Number(state.TrustCeiling));
            SetDetailText(strategicTitle, Text("Strategische Lage", "Strategic situation"));
            SetDetailText(strategicLine1, Text("Machtbilanz ", "Power balance ") + SignedNumber(state.StrategicLeverage) + " (" + LeverageLevel(state.StrategicLeverage) + ")");
            // The native screenshot clipped the explanation after "Ihr seid".
            // Keep the number visible and the full explanation in its tooltip.
            SetPropertyIfPresent(strategicLine1, "Text", Text("Machtbilanz ", "Power balance ") + SignedNumber(state.StrategicLeverage));
            SetDetailText(threat, Text("Bedrohung ", "Threat ") + Number(state.StrategicThreat));
            SetDetailText(dependency, Text("Abhängigkeit ", "Dependency ") + Number(state.StrategicDependency));
            SetDetailText(strategicLine2, Text("Koalitionswert ", "Coalition value ") + Number(state.CoalitionValue));
            SetDetailText(commonEnemies, Text("Gemeinsame Gegner ", "Common enemies ") + state.CommonEnemyCoalitions.ToString(CultureInfo.InvariantCulture) + "/" + state.HostileCoalitions.ToString(CultureInfo.InvariantCulture));
            SetDetailText(allianceCount, Text("Eure Bündnisse ", "Your alliances ") + state.PlayerAllianceCount.ToString(CultureInfo.InvariantCulture));
            UiStep("_demand");
            string[] demand = PanelDemandLines(relation, coalition, selectedPlayer, demandStock);
            SetDetailText(demandTitle, demand[0]);
            SetDetailText(demandLine1, demand[1]);
            SetDetailText(demandLine2, demand[2]);
            string actionHintText = HasPendingGoodsCommand ? PendingGoodsCommandStatusText() : demand[3];
            MainViewModel visibleView = MainViewModel.Instance;
            bool detailVisible = !_overviewVisible && !HasPendingGoodsCommand && visibleView != null && visibleView.AlliesPanelVisible && visibleView.Allies_MainViewVis;
            string goodsOutcome = HasPendingGoodsCommand ? string.Empty : ShownGoodsOutcome(selectedPlayer, detailVisible);
            if (detailVisible && string.IsNullOrEmpty(goodsOutcome))
                NoteAllyOrderStatusShown(selectedPlayer);
            string orderStatus = AllyOrderStatusText(selectedPlayer);
            if (!string.IsNullOrEmpty(goodsOutcome))
                actionHintText = goodsOutcome + " · " + actionHintText;
            else if (!HasPendingGoodsCommand && !string.IsNullOrEmpty(orderStatus))
                actionHintText = orderStatus + " · " + actionHintText;
            SetDetailText(actionHint, actionHintText);
            SetPropertyIfPresent(actionHint, "ToolTip", actionHintText);
            bool allied = relation.Allied;
            bool breakable = allied || HasBreakableAgreement(relation);
            int waitDays = DiplomacyAgreementRules.NegotiationWaitDays(relation, AbsoluteGameDay());
            bool primaryEnabled = relation.Demand != null ? CanFulfilDemand(relation.Demand, demandStock) : allied ? CanRequestGoodsFrom(selectedPlayer) : !relation.PendingOffer && waitDays == 0;
            UiStep("_buttons");
            RefreshExtraFealtyAction(panel, relation);
            if (DiplomacySubmission.HasOffer(state))
                primaryEnabled = false;
            SetPropertyIfPresent(primary, "IsEnabled", primaryEnabled);
            // Opacity ist in Noesis ein Single. SetProperty schreibt den Wert
            // ohne Umwandlung; ein Double warf dort am auf 2.80
            // "Object of type 'System.Double' cannot be converted to type
            // 'System.Single'" und riss ueber ui_refresh die ganze Diplomatie
            // mit. SetNumericProperty wandelt in den Zieltyp um.
            if (primaryLabel != null)
                SetNumericProperty(primaryLabel, "Opacity", primaryEnabled ? 1.0 : 0.48);
            UiStep("_tooltip");
            SetPropertyIfPresent(primary, "ToolTip", RequestRolloverText(relation, selectedPlayer));
            // der ausgegraute Erfuellen-Knopf sagte nicht, warum.
            // Er liefert immer das GANZE Paket; fehlt eine Position, ist er aus.
            // Teillieferungen laufen ueber "Gueter senden" und werden auf die
            // Forderung angerechnet (GoodsDemand.ApplyDelivery).
            if (relation.Demand != null && !primaryEnabled && !DiplomacySubmission.HasOffer(state))
            {
                SetPropertyIfPresent(primary, "ToolTip", demandStock == null ? Text("Bestand wird aktualisiert", "Updating stores") : Text("Für die vollständige Erfüllung fehlen noch Vorräte.", "More stores are needed to fulfil the complete package.") + " " + Text("Fehlende Güter senden oder zunächst einlagern.", "Send available goods or acquire the missing stores."));
            }

            // "Forderung ablehnen" erscheint nur, solange eine Forderung offen
            // ist - und auch bei Fuersten ohne Buendnis.
            bool declinable = relation.Demand != null && !DiplomacySubmission.HasOffer(state);
            SetVisibilityIfPresent(declineDemand, declinable);
            SetVisibilityIfPresent(declineDemandLabel, declinable);
            if (declineDemand != null)
            {
                SetProperty(declineDemand, "IsEnabled", declinable && !HasPendingGoodsCommand);
                SetProperty(declineDemand, "ToolTip", Text("Die Forderung zurückweisen. Das kostet Ansehen, aber nie das Bündnis.", "Reject the demand. It costs standing, never the alliance."));
            }

            SetDetailText(declineDemandLabel, Text("Forderung ablehnen", "Decline demand"));
            SetDetailText(primaryLabel, PrimaryActionLabel(relation, selectedPlayer));
            SetDetailText(sendLabel, Text("Güter senden", "Send goods"));
            SetDetailText(ordersLabel, Text("Befehle", "Orders"));
            SetDetailText(breakLabel, allied ? Text("Bündnis lösen", "End alliance") : Text("Abkommen lösen", "End agreement"));
            UiStep("_visibility");
            SetVisibilityIfPresent(orders, allied);
            SetVisibilityIfPresent(ordersLabel, allied);
            SetVisibilityIfPresent(breakButton, breakable);
            SetVisibilityIfPresent(breakLabel, breakable);
            SetVisibilityIfPresent(FindElement(panel, "EuropeDiplomacyBreak"), false);
            SetVisibilityIfPresent(native, false);
            SetVisibilityIfPresent(advanced, true);
            // Die Uebersicht liegt ueber der Detailtafel; ist sie offen,
            // bleibt die Detailtafel darunter unsichtbar.
            RefreshOverviewPanel(panel);
            RefreshTradePanel(panel, selectedPlayer);
        }

        private static void SetDetailText(object element, string text)
        {
            // A Noesis label is presentational, not a release gate.
            if (element == null)
                return;
            SetProperty(element, "Text", text);
            SetProperty(element, "ToolTip", text);
        }

        private void NotifyUiFallback()
        {
            _overviewVisible = false;
            if (_uiPanelWarningLogged)
                return;
            _uiPanelWarningLogged = true;
            Log("ui_integration", "status=_detail_degraded|reason=display_unavailable");
            ShowMessage(Text("Die Diplomatieansicht ist nicht verfügbar. Die Grundansicht bleibt geöffnet; Bündnis- und Lehnsverwaltung fehlen.", "The diplomacy view is unavailable. The basic view remains open; alliance and fealty management are unavailable."));
        }

        private string[] PanelDemandLines(RelationState relation, CoalitionState coalition, int player = 0, Dictionary<int, int> stock = null)
        {
            GoodsDemand demand = relation.Demand;
            if (DiplomacySubmission.HasOffer(relation.ExtendedState))
            {
                bool accepted = relation.ExtendedState.SubmissionAcceptancePending || relation.ExtendedState.SubmissionOfferAccepted;
                return new[]
                {
                    accepted ? Text("Unterwerfung bestätigt", "Submission accepted") : Text("Unterwerfungsangebot", "Offer of submission"),
                    FealtyTerms(relation.ExtendedState.SubmissionOfferTributeAmount),
                    accepted ? string.Empty : string.Format(CultureInfo.InvariantCulture, Text("Antwortfrist: {0} Spieltage.", "Response time: {0} game days."), relation.ExtendedState.SubmissionResponseDaysRemaining),
                    accepted ? string.Empty : Text("Angebot prüfen", "Review offer"),
                };
            }

            if (demand == null)
            {
                if (IsFealtyCounterpart(relation.ExtendedState, player))
                    return new[]
                    {
                        Text("Lehnsverhältnis", "Fealty"),
                        AllianceObligation(relation, AbsoluteGameDay(), player),
                        TributeLedgerText(relation.ExtendedState),
                        string.Empty,
                    };
                return new[]
                {
                    Text("Diplomatische Möglichkeiten", "Diplomatic options"),
                    ActionHint(relation, coalition),
                    PersistentHistoryText(relation.ExtendedState),
                    RequestRolloverText(relation, player) + Text(" · Gezielte Geschenke bleiben möglich.", " · Targeted gifts remain available."),
                };
            }

            var items = demand.Items.Where(item => item != null && item.RemainingAmount > 0).Select(item => DemandItemPanelText(item, stock)).ToList();
            while (items.Count < 3)
            {
                items.Add(string.Empty);
            }

            bool ready = CanFulfilDemand(demand, stock);
            if (string.IsNullOrEmpty(items[1]))
            {
                items[1] = stock == null ? Text("Bestand wird aktualisiert", "Updating stores") : ready ? Text("Das vollständige Paket liegt in Euren Lagern bereit.", "The complete package is available in your stores.") : Text("Für die vollständige Erfüllung fehlen noch Vorräte.", "More stores are needed to fulfil the complete package.");
            }

            string third = string.IsNullOrEmpty(items[2]) ? (stock == null ? Text("Bestand wird aktualisiert", "Updating stores") : ready ? Text("Mit einem Klick vollständig liefern.", "Deliver everything with one click.") : Text("Fehlende Güter senden oder zunächst einlagern.", "Send available goods or acquire the missing stores.")) : items[2];
            return new[]
            {
                DemandPurposeName(demand.DemandPurposeDescription) + " · " + DurationText(demand.ExpiresDay - AbsoluteGameDay()),
                items[0],
                items[1],
                third,
            };
        }

        // der Lagerstand wurde bei jeder Aktualisierung frisch
        // nativ gelesen, und ein misslungener Lesezugriff loeschte die ganze
        // Anzeige. Eine beschaeftigte Engine ist aber der Normalfall, nicht die
        // Ausnahme: die Bestaende sprangen sichtbar zwischen Zahl und "?", und
        // weil CanFulfilDemand ohne Momentaufnahme false liefert, verschwand
        // der Erfuellen-Knopf im selben Takt - er war nur zufaellig zu treffen.
        // Der zuletzt gelesene Stand bleibt deshalb stehen, bis ein neuer
        // Lesezugriff gelingt. Er kann wenige Takte alt sein; das ist harmlos,
        // weil die Erfuellung selbst nativ nachprueft und fehlende Ware sauber
        // meldet. Ueber eine Karten- oder Sitzungsgrenze wird nie getragen.
        private Dictionary<int, int> _demandStockLastKnown;
        private long _demandStockEpoch = -1;
        private long _demandStockGeneration = -1;
        private Dictionary<int, int> DemandStockFallback()
        {
            if (_demandStockLastKnown == null || _demandStockEpoch != Volatile.Read(ref _mapEpoch) || _demandStockGeneration != Volatile.Read(ref _sessionGeneration))
                return null;
            return _demandStockLastKnown;
        }

        private Dictionary<int, int> DemandStockSnapshot(GoodsDemand demand)
        {
            if (demand == null || demand.Items == null)
                return null;
            Dictionary<int, int> known = DemandStockFallback();
            IDisposable lease;
            if (!TryEnterNativeInventorySection(out lease))
                return known;
            using (lease)
            {
                var stock = new Dictionary<int, int>();
                foreach (DemandItem item in demand.Items)
                {
                    if (item == null || item.RemainingAmount <= 0 || stock.ContainsKey(item.GoodId))
                        continue;
                    int available;
                    // Ein echter Lesefehler bleibt ein Fehler: er koennte auf
                    // eine kaputte Lage deuten, und eine halb gelesene Aufnahme
                    // darf nie als vollstaendig durchgehen. Nur die BESETZTE
                    // Engine oben - der haeufige, harmlose Fall - haelt den
                    // letzten Stand.
                    if (!TryReadTransferAmount(_localPlayer, item.GoodId, out available))
                        return null;
                    stock.Add(item.GoodId, available);
                }

                _demandStockLastKnown = stock;
                _demandStockEpoch = Volatile.Read(ref _mapEpoch);
                _demandStockGeneration = Volatile.Read(ref _sessionGeneration);
                return stock;
            }
        }

        private string DemandItemPanelText(DemandItem item, Dictionary<int, int> stock)
        {
            int available = 0;
            bool known = stock != null && stock.TryGetValue(item.GoodId, out available);
            return GoodName(item.GoodId, item.RequiredAmount) + Text(" · geliefert ", " · delivered ") + item.DeliveredAmount.ToString(CultureInfo.InvariantCulture) + "/" + item.RequiredAmount.ToString(CultureInfo.InvariantCulture) + Text(" · offen ", " · due ") + item.RemainingAmount.ToString(CultureInfo.InvariantCulture) + Text(" · Lager ", " · stores ") + (known ? available.ToString(CultureInfo.InvariantCulture) : "?");
        }

        private bool CanFulfilDemand(GoodsDemand demand, Dictionary<int, int> stock)
        {
            if (stock == null || demand == null || demand.Items == null || demand.Items.Length == 0)
            {
                return false;
            }

            bool hasOutstandingItem = false;
            foreach (DemandItem item in demand.Items)
            {
                int available;
                if (item == null)
                {
                    return false;
                }

                if (item.RemainingAmount <= 0)
                {
                    continue;
                }

                hasOutstandingItem = true;
                if (!stock.TryGetValue(item.GoodId, out available) || available < item.RemainingAmount)
                {
                    return false;
                }
            }

            return hasOutstandingItem;
        }

        private string DemandPurposeName(DiplomacyDemandPurpose purpose)
        {
            switch (purpose)
            {
                case DiplomacyDemandPurpose.Courtship:
                    return Text("Bedingungen zur Waffenruhe", "Truce terms");
                case DiplomacyDemandPurpose.TradeProof:
                    return Text("Bedingungen zum Handel", "Trade terms");
                case DiplomacyDemandPurpose.AllianceTerms:
                    return Text("Bündnisbedingungen", "Alliance terms");
                case DiplomacyDemandPurpose.Support:
                    return Text("Unterstützungsersuchen", "Support request");
                case DiplomacyDemandPurpose.Ultimatum:
                    return Text("Ultimatum", "Ultimatum");
                case DiplomacyDemandPurpose.Restitution:
                    return Text("Wiedergutmachung", "Restitution");
                default:
                    return Text("Forderung", "Demand");
            }
        }

        private string PersistentHistoryText(DiplomacyRelationState state)
        {
            if (state.BrokenAgreements == 0 && state.TreacherousAttacks == 0 && state.MilitaryIncursions == 0 && state.ObservedAllianceAbandonments == 0)
            {
                return Text("Keine dauerhaften Vertragsbrüche vermerkt.", "No lasting treaty breaches are recorded.");
            }

            return Text("Vertragsgedächtnis · Brüche ", "Treaty memory · breaches ") + state.BrokenAgreements.ToString(CultureInfo.InvariantCulture) + Text(" · Vertragsbruchangriffe ", " · treaty-breaking attacks ") + state.TreacherousAttacks.ToString(CultureInfo.InvariantCulture) + Text(" · Grenzverletzungen ", " · incursions ") + state.MilitaryIncursions.ToString(CultureInfo.InvariantCulture) + Text(" · beobachteter Verrat ", " · observed betrayals ") + state.ObservedAllianceAbandonments.ToString(CultureInfo.InvariantCulture);
        }

        private string PrimaryActionLabel(RelationState relation, int player = 0)
        {
            if (DiplomacySubmission.HasOffer(relation.ExtendedState))
                return relation.ExtendedState.SubmissionAcceptancePending || relation.ExtendedState.SubmissionOfferAccepted ? Text("Unterwerfung bestätigt", "Submission accepted") : Text("Entscheidung offen", "Decision pending");
            if (relation.Demand != null)
            {
                return Text("Forderung erfüllen", "Fulfil demand");
            }

            if (relation.Allied)
            {
                if (IsFealtyCounterpart(relation.ExtendedState, player) && DiplomacyVassalage.OverlordOf(relation.ExtendedState) == _localPlayer)
                    return Text("Güter anfordern", "Request supplies");
                return Text("Güter erbitten", "Request goods");
            }

            if (relation.PendingOffer)
            {
                return Text("Entscheidung offen", "Decision pending");
            }

            if (DiplomacyAgreementRules.NegotiationWaitDays(relation, AbsoluteGameDay()) > 0)
            {
                return Text("Gesandte warten", "Envoys waiting");
            }

            if (relation.ExtendedState.RestitutionOutstandingValue > 0m)
            {
                return Text("Wiedergutmachung", "Restitution");
            }

            switch (relation.ExtendedState.Agreement)
            {
                case DiplomacyAgreementKind.Truce:
                    return Text("Handel anfragen", "Request trade");
                case DiplomacyAgreementKind.TradePact:
                    return relation.ExtendedState.AllianceTermsFulfilled ? Text("Bündnis prüfen", "Review alliance") : Text("Bündnis anfragen", "Request alliance");
                default:
                    return Text("Waffenruhe erbitten", "Request truce");
            }
        }

        private string Metric(string label, decimal value)
        {
            return label + " " + Number(value) + " (" + Level(value) + ")";
        }

        private static string Number(decimal value)
        {
            return decimal.Round(value, 0, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture);
        }

        private static string SignedNumber(decimal value)
        {
            decimal rounded = decimal.Round(value, 0, MidpointRounding.AwayFromZero);
            return (rounded > 0m ? "+" : string.Empty) + rounded.ToString("0", CultureInfo.InvariantCulture);
        }

        private string LeverageLevel(decimal value)
        {
            if (value <= -35m)
            {
                return Text("Gegner deutlich stärker", "opponent much stronger");
            }

            if (value <= -12m)
            {
                return Text("Gegner stärker", "opponent stronger");
            }

            if (value < 12m)
            {
                return Text("ausgeglichen", "balanced");
            }

            if (value < 35m)
            {
                return Text("Ihr seid stärker", "you are stronger");
            }

            return Text("Ihr seid deutlich stärker", "you are much stronger");
        }

        private string PersonalityName(DiplomacyPersonalityKind kind)
        {
            switch (kind)
            {
                case DiplomacyPersonalityKind.Honorable:
                    return Text("ehrenhaft", "honorable");
                case DiplomacyPersonalityKind.Mercantile:
                    return Text("händlerisch", "mercantile");
                case DiplomacyPersonalityKind.Wary:
                    return Text("vorsichtig", "wary");
                case DiplomacyPersonalityKind.Warlike:
                    return Text("kriegerisch", "warlike");
                case DiplomacyPersonalityKind.Opportunistic:
                    return Text("opportunistisch", "opportunistic");
                case DiplomacyPersonalityKind.Loyal:
                    return Text("treu", "loyal");
                default:
                    return Text("fordernd", "demanding");
            }
        }

        private string Level(decimal value)
        {
            string level = value < 20m ? Text("sehr gering", "very low") : value < 40m ? Text("gering", "low") : value < 60m ? Text("mittel", "moderate") : value < 80m ? Text("hoch", "high") : Text("sehr hoch", "very high");
            return level;
        }

        private void RegisterLegacySaveImport()
        {
            if (!ModSaveDataAPI.Instance.RegisterModDataHandler(DiplomacyContract.LegacySaveDataIdentifier, SaveLegacyData, LoadLegacyData))
            {
                throw new InvalidOperationException("legacy diplomacy save-data identifier is already registered");
            }

            _legacySaveHandlerRegistered = true;
        }

        private void UnregisterLegacySaveImport()
        {
            if (!_legacySaveHandlerRegistered)
            {
                return;
            }

            _legacySaveHandlerRegistered = false;
            ModSaveDataAPI.Instance.UnregisterModDataHandler(DiplomacyContract.LegacySaveDataIdentifier);
        }

        private byte[] SaveLegacyData(SaveContext context)
        {
            byte[] Frozen;
            if (TryFrozenSave(context, true, out Frozen))
                return Frozen;
            NoteLane(LaneSave);
            lock (_gate)
            {
                if (context == null || !context.IsSaveFile)
                {
                    return null;
                }

                if (_saveSuppressed)
                {
                    return DiplomacySaveCodec.SerializeInactive("primary_save_suppressed");
                }

                try
                {
                    bool recoveryOnly = _mapDisabled && (_nativeTransferProbe != null || _disabledTeamRecoveryPending) && _coalitions.Count > 0 && _relations.Count == _coalitions.Count && _roster.Count > 1;
                    if ((!_mapActive && !recoveryOnly) || (_mapDisabled && !recoveryOnly))
                    {
                        return DiplomacySaveCodec.SerializeInactive("map_inactive");
                    }

                    LegacyDiplomacySave state;
                    if (_stableSavePayload != null)
                    {
                        state = DiplomacyStateCodec.Deserialize(_stableSavePayload, _rules);
                    }
                    else
                    {
                        PumpHumanWarIntents(false);
                        state = recoveryOnly ? SnapshotRecoverySave() : SnapshotSave();
                    }

                    // legacy cannot express a foreign payer or tribute bookkeeping.
                    // Overwrite its archive entry explicitly instead of retaining
                    // an older journal or inventing a pre-transfer native state.
                    if (state.PendingTransfer != null && (state.PendingTransfer.Purpose == DiplomacyTransferPurpose.Tribute || state.PendingTransfer.DirectAtomicTransfer || state.PendingTransfer.SenderPlayer > 0 && state.PendingTransfer.SenderPlayer != state.LocalPlayerId))
                    {
                        return DiplomacySaveCodec.SerializeInactive("requires__transfer_recovery");
                    }

                    // An old legacy slot cannot represent consumed trade IDs.
                    // Never let legacy-fallback resurrect an already settled  quote.
                    if (state.TradeArchive != null && (state.TradeArchive.Records.Count > 0 || state.TradeArchive.PendingNativeId != 0))
                        return DiplomacySaveCodec.SerializeInactive("trade_archive_requires_modern_");
                    // Keep the legacy slot current as a genuine rollback path.
                    // The projection strips only concepts legacy cannot represent,
                    // normalises its shorter deadlines, and retains inventories,
                    // teams, transfer journals and final/support demand progress.
                    LegacyDiplomacySave projection = DiplomacyStateCodec.BuildCompatibilityProjection(state, _rules);
                    byte[] payload = DiplomacySaveCodec.Serialize(projection, _rules);
                    Log("saving", "status=_compat_saved|bytes=" + payload.Length.ToString(CultureInfo.InvariantCulture));
                    return payload;
                }
                catch (Exception exception)
                {
                    // This optional legacy projection cannot invalidate a valid ExtendedState save.
                    Log("saving", "status=_compat_inactive|reason=" + exception.GetType().Name);
                    return DiplomacySaveCodec.SerializeInactive("legacy_serialize_failed");
                }
            }
        }

        private void LoadLegacyData(byte[] payload, LoadContext context)
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
                _pendingLegacySavePayload = payload == null ? null : (byte[])payload.Clone();
                _pendingLegacySaveEpoch = payload == null ? -1 : _mapEpoch;
                _pendingLegacySaveTick = payload == null ? -1 : _featureTicks;
                _pendingLegacyLoadSerial = payload == null ? 0 : _originalSavedWorld.StagingSerial();
                Log("saving", "status=_import_staged|bytes=" + (payload == null ? "0" : payload.Length.ToString(CultureInfo.InvariantCulture)));
            }
        }

        private void ReconcileLegacySaveEpoch()
        {
            if (_pendingLegacySavePayload == null || _pendingLegacySaveEpoch == _mapEpoch)
            {
                return;
            }

            if (_originalSavedWorld.CanRebase(_pendingLegacyLoadSerial, _mapEpoch))
            {
                _pendingLegacySaveEpoch = _mapEpoch;
                return;
            }

            DiscardPendingLegacySave("save_load_epoch");
        }

        private void BeginLegacyMapEpoch(long previousEpoch)
        {
            if (_pendingLegacySavePayload != null && _pendingLegacySaveEpoch == previousEpoch &&
                !_originalSavedWorld.CanRetainAcrossEpoch(_pendingLegacyLoadSerial, previousEpoch) &&
                _pendingLegacySaveTick < _featureTicks)
            {
                DiscardPendingLegacySave("native_epoch");
            }
        }

        private byte[] StageLegacySaveForBootstrap()
        {
            return _pendingLegacySaveEpoch == _mapEpoch ? _pendingLegacySavePayload : null;
        }

        private void ConsumePendingLegacySave()
        {
            _pendingLegacySavePayload = null;
            _pendingLegacySaveEpoch = -1;
            _pendingLegacySaveTick = -1;
            _pendingLegacyLoadSerial = 0;
        }

        private void DiscardPendingLegacySave(string trigger)
        {
            _pendingLegacySavePayload = null;
            _pendingLegacySaveEpoch = -1;
            _pendingLegacySaveTick = -1;
            _pendingLegacyLoadSerial = 0;
            Log("saving", "status=_import_stale_discarded|trigger=" + Safe(trigger));
        }

        private void RestoreLegacySave(byte[] legacyPayload)
        {
            byte[] migrated = DiplomacyStateCodec.MigrateLegacyPayload(legacyPayload, _rules);
            RestoreSave(migrated);
            RebindMigratedPersonalities();
            Log("saving", "status=_import_migrated|source_bytes=" + legacyPayload.Length.ToString(CultureInfo.InvariantCulture) + "|_bytes=" + migrated.Length.ToString(CultureInfo.InvariantCulture));
        }

        private void RestorePrimaryOrLegacySave(byte[] primaryPayload, byte[] legacyPayload)
        {
            bool usedLegacy;
            string primaryFailureKind;
            byte[] selected = DiplomacyStateCodec.SelectPrimaryOrLegacyLoadPayload(primaryPayload, legacyPayload, _rules, out usedLegacy, out primaryFailureKind);
            RestoreSave(selected);
            if (!usedLegacy)
            {
                return;
            }

            RebindMigratedPersonalities();
            Log("saving", "status=_primary_recovered_from_" + "|primary_failure=" + Safe(primaryFailureKind) + "|source_bytes=" + legacyPayload.Length.ToString(CultureInfo.InvariantCulture) + "|_bytes=" + selected.Length.ToString(CultureInfo.InvariantCulture));
        }

        private void RebindMigratedPersonalities()
        {
            foreach (CoalitionState coalition in _coalitions.Values)
            {
                RelationState relation = _relations[coalition.CoalitionId];
                DiplomacyPersonalitySignal signal = SelectCoalitionPersonalitySignal(coalition);
                DiplomacyPersonality profile = DiplomacyPersonalityCatalog.SelectForLord(signal);
                DiplomacyAgreementRules.RebindPersonality(relation, profile.Kind, signal.LordId, coalition.Target);
            }
        }

        private void ResetRuntimeMapState()
        {
            ClearSubmissionPressure();
            ResetGoodsRequests();
            ResetGoodsOutcomes();
            _vassalageFaultsByCoalition.Clear();
            _equalityPresentedDays.Clear();
            ResetSubmissionPresentation();
            _tributeTechnicalFaults.Clear();
            _incursionLogLines = 0;
            _standoffRetryTick = 0;
            _standoffFaultsPending = 0;
            _localPowerTick = long.MinValue;
            _localEconomy = 0L;
            _localMilitary = 0L;
            ClearDeferredNotices();
            _lastIncursionScanTick = DiplomacyCombatRules.NeverScannedTick;
            _lastHostileAttributionTick = DiplomacyCombatRules.NeverScannedTick;
            ClearTreatyStandoff();
            _treatyStandoffDegraded = false;
            _standoffMovesBlocked = 0;
            _standoffLogLines = 0;
            ResetProbeState();
            _uiPanelWarningLogged = false;
        }
    }
}
