using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using CrusaderDE;
using Noesis;
using SHCDESE.API;

namespace StrongholdEuropeDE.Runtime
{
    // Display-only messages, never orders, a Steam packet, a native reply
    // or evidence of goods/military success. The original shared lower-left
    // chat panel is used only in an independently verified solo session.
    internal static class LordChatAddress
    {
        internal static string Normalize(string message)
        {
            if (String.IsNullOrWhiteSpace(message))
                return String.Empty;
            StringBuilder safe = new StringBuilder(180);
            foreach (char c in message)
            {
                if (safe.Length == 180)
                    break;
                if (!Char.IsControl(c))
                    safe.Append(c);
            }
            return safe.ToString().Trim();
        }

        // Reserved group address. Does not select a lord by name or slot.
        internal static bool TryParseTeam(string raw, out string body)
        {
            body = String.Empty;
            string message = Normalize(raw);
            if (!message.StartsWith("@team ", StringComparison.OrdinalIgnoreCase))
                return false;
            body = Normalize(message.Substring(6));
            return body.Length != 0;
        }

        // liveAi was already restricted to current, living original AIs by
        // the guarded solo roster. Require BOTH the captured original team
        // and the current original PlayState snapshot to agree positively.
        // A diplomatic alliance is deliberately insufficient for @team.
        internal static List<int> TeamRecipients(int localPlayer,
            IDictionary<int, string> liveAi, IDictionary<int, int> originalTeams,
            short[] liveTeams)
        {
            var result = new List<int>();
            if (liveAi == null || originalTeams == null || liveTeams == null ||
                localPlayer < 1 || localPlayer > 8 ||
                localPlayer >= liveTeams.Length)
                return result;
            int originalTeam;
            if (!originalTeams.TryGetValue(localPlayer, out originalTeam) ||
                originalTeam <= 0 ||
                liveTeams[localPlayer] != originalTeam)
                return result;
            foreach (int id in liveAi.Keys)
            {
                int memberTeam;
                if (id < 1 || id > 8 || id == localPlayer ||
                    id >= liveTeams.Length ||
                    !originalTeams.TryGetValue(id, out memberTeam) ||
                    memberTeam != originalTeam ||
                    liveTeams[id] != originalTeam)
                    continue;
                result.Add(id);
            }
            result.Sort();
            return result;
        }

        // An exact native slot, or a uniquely displayed living-AI name.
        // Longest complete name first makes multiword ruler names routable.
        // A duplicate name is never silently bound to the first player.
        internal static bool TryParse(string raw,
            IDictionary<int, string> liveAi, out int target, out string body)
        {
            target = 0;
            body = String.Empty;
            string message = Normalize(raw);
            if (message.Length < 4 || message[0] != '@' || liveAi == null)
                return false;
            string addressAndText = message.Substring(1);
            int separator = addressAndText.IndexOf(' ');
            if (separator < 1)
                return false;
            int slot;
            string leading = addressAndText.Substring(0, separator);
            if (Int32.TryParse(leading, NumberStyles.None,
                    CultureInfo.InvariantCulture, out slot))
            {
                string proposed = Normalize(addressAndText.Substring(separator + 1));
                if (slot < 1 || slot > 8 || !liveAi.ContainsKey(slot) ||
                    proposed.Length == 0)
                    return false;
                target = slot;
                body = proposed;
                return true;
            }

            int best = -1;
            int matches = 0;
            foreach (KeyValuePair<int, string> entry in liveAi)
            {
                if (entry.Key < 1 || entry.Key > 8 ||
                    String.IsNullOrWhiteSpace(entry.Value))
                    continue;
                string name = entry.Value.Trim();
                if (addressAndText.Length <= name.Length ||
                    !addressAndText.StartsWith(name, StringComparison.OrdinalIgnoreCase) ||
                    addressAndText[name.Length] != ' ')
                    continue;
                if (name.Length > best)
                {
                    best = name.Length;
                    matches = 1;
                    slot = entry.Key;
                    target = slot;
                }
                else if (name.Length == best)
                    matches++;
            }
            if (matches != 1 || best < 1)
            {
                target = 0;
                return false;
            }
            body = Normalize(addressAndText.Substring(best + 1));
            if (body.Length == 0)
            {
                target = 0;
                return false;
            }
            return true;
        }
    }

    internal sealed partial class DiplomacyFeature
    {
        private readonly Dictionary<int, string> _lordChatEventStamps =
            new Dictionary<int, string>();
        private long _lordChatGeneration = -1;
        private long _lordChatEpoch = -1;
        private string _lordChatMap, _lordChatRoster;
        private long _lordChatNextSoloCheck, _lordChatNextBindTry, _lordChatNextAmbient, _lordChatNextUpdate;
        private int _lordChatPairCursor;
        private bool _lordChatSoloConfirmed, _lordChatIntroduced, _lordChatBound;
        private bool _lordChatChangedVisibility, _lordChatKeyboardOwned;
        private HUD_MPChatMessages _lordChatPanel, _lordChatVisibilityPanel;
        private MainViewModel _lordChatVisibilityView;
        private Noesis.Grid _lordChatComposer, _lordChatLayout;
        private float _lordChatPriorMinHeight;
        private Noesis.TextBox _lordChatInput;
        private Noesis.Button _lordChatSend;

        private bool LordChatIsOriginalSoloBase()
        {
            if (!CanPresentDiplomacyUi || !_mapActive || _mapDisabled ||
                _mapBootstrapPending || _localPlayer < 1 || _localPlayer > 8 ||
                _originalSavedWorld.IsOriginalLoadInProgress() ||
                _roster.Count < 2 || String.IsNullOrEmpty(_mapFingerprint) ||
                String.IsNullOrEmpty(_rosterFingerprint))
                return false;
            GameData game = GameData.Instance;
            if (game == null || game.lastGameState == null || game.playerID != _localPlayer)
                return false;
#if DIPLOMACY_PRIVATE_TESTS
            return game.SkirmishGameType == 0 && game.coopTrailID == 0 &&
                !GameNetworkAPI.IsLobbyEnvironment();
#else
            // The periodic solo proof still verifies the managed and native rosters.
            return IsCurrentPublicSoloMission(_missionLifecycle?.Current);
#endif
        }

        private void ResetLordChatSession()
        {
            _lordChatSoloConfirmed = false;
            _lordChatGeneration = -1;
            _lordChatEpoch = -1;
            _lordChatMap = null;
            _lordChatRoster = null;
            _lordChatEventStamps.Clear();
            _lordChatIntroduced = false;
            _lordChatPairCursor = 0;
            _lordChatNextAmbient = 0;
            _lordChatNextSoloCheck = 0;
            _lordChatNextBindTry = 0;
            _lordChatNextUpdate = 0;
            // Map callbacks can originate outside the Unity presentation
            // lane. No Noesis mutation or unsubscription from that lane.
            if (CanPresentDiplomacyUi)
                ReleaseLordChatUi();
        }

        // Called before Tick can return early on save/transition/disable.
        private void MaintainLordChatPresentation()
        {
            if (!CanPresentDiplomacyUi ||
                (_lordChatPanel == null && !_lordChatChangedVisibility))
                return;
            bool valid = false;
            try
            {
                valid = LordChatIsOriginalSoloBase() &&
                    _lordChatSoloConfirmed &&
                    _lordChatGeneration == _sessionGeneration &&
                    _lordChatEpoch == _mapEpoch &&
                    String.Equals(_lordChatMap, _mapFingerprint, StringComparison.Ordinal) &&
                    String.Equals(_lordChatRoster, _rosterFingerprint, StringComparison.Ordinal) &&
                    ReferenceEquals(_lordChatPanel,
                        MainViewModel.Instance == null ? null :
                        MainViewModel.Instance.HUDMPChatMessages);
            }
            catch { valid = false; }
            if (!valid)
                ReleaseLordChatUi();
        }

        private void PumpOriginalLordChat()
        {
            try
            {
                if (_featureTicks < _lordChatNextUpdate || !LordChatIsOriginalSoloBase())
                    return;
                _lordChatNextUpdate = _featureTicks + 15;
                GameData game = GameData.Instance;
                EngineInterface.PlayState state = game.lastGameState;
                MainViewModel view = MainViewModel.Instance;
                if (state == null || view == null || state.lord_alive == null ||
                    state.teams == null || state.teams.Length < 9)
                    return;

                if (_lordChatGeneration != _sessionGeneration ||
                    _lordChatEpoch != _mapEpoch ||
                    !String.Equals(_lordChatMap, _mapFingerprint, StringComparison.Ordinal) ||
                    !String.Equals(_lordChatRoster, _rosterFingerprint, StringComparison.Ordinal))
                {
                    ResetLordChatSession();
                    _lordChatGeneration = _sessionGeneration;
                    _lordChatEpoch = _mapEpoch;
                    _lordChatMap = _mapFingerprint;
                    _lordChatRoster = _rosterFingerprint;
                    _lordChatNextAmbient = _featureTicks + 180;
                }
                // The preexisting native-lease/roster proof is required on
                // this actual Unity frame, and periodically rechecked.
                if (_featureTicks >= _lordChatNextSoloCheck)
                {
                    _lordChatNextSoloCheck = _featureTicks + 60;
                    string reason;
                    if (!IsSoloEngineSession(out reason))
                    {
                        if (reason == "session_engine_monitor_busy")
                        {
                            _lordChatNextSoloCheck = _featureTicks + 15;
                            return;
                        }
                        _lordChatSoloConfirmed = false;
                        ReleaseLordChatUi();
                        return;
                    }
                    _lordChatSoloConfirmed = true;
                }
                if (!_lordChatSoloConfirmed)
                    return;

                List<int> ai = GetLivingLordChatAis(state);
                if (ai.Count == 0)
                {
                    ReleaseLordChatUi();
                    return;
                }
                HUD_MPChatMessages panel = view.HUDMPChatMessages;
                if (panel == null)
                {
                    ReleaseLordChatUi();
                    return; // late registration: retry on a later frame
                }
                // If XAML controls arrive later, retry at a bounded rate.
                // A replaced panel is detached before any message is sent.
                if (_lordChatPanel != null &&
                    !ReferenceEquals(panel, _lordChatPanel))
                    ReleaseLordChatUi();
                if (!_lordChatBound && _featureTicks >= _lordChatNextBindTry)
                    BindLordChatPanel(panel, view);
                if (!_lordChatBound)
                    return;
                if (!_lordChatIntroduced)
                {
                    _lordChatIntroduced = true;
                    foreach (int id in ai)
                        RememberLordEvent(id);
                    SendLordChatMessage(panel, ai[0],
                        Text("Der Fürstenrat ist versammelt. Schreibt @",
                             "The council of lords is assembled. Type @") +
                        ai[0].ToString(CultureInfo.InvariantCulture) +
                        Text(" und Eure Nachricht; @team erreicht nur lebende eigene KI-Teampartner.",
                             " and your message; @team reaches only living AI teammates."));
                    return;
                }

                // Only newly observed, real extended-state events. Saved
                // event snapshots are seeded, never replayed as fresh news.
                foreach (int id in ai)
                {
                    string cid;
                    RelationState relation;
                    if (!_playerCoalition.TryGetValue(id, out cid) ||
                        !_relations.TryGetValue(cid, out relation) ||
                        relation.ExtendedState == null)
                        continue;
                    string signature = ((int)relation.ExtendedState.LastEvent)
                        .ToString(CultureInfo.InvariantCulture) + ":" +
                        relation.ExtendedState.LastEventDay.ToString(
                            CultureInfo.InvariantCulture);
                    string previous;
                    if (!_lordChatEventStamps.TryGetValue(id, out previous))
                    {
                        _lordChatEventStamps[id] = signature;
                        continue;
                    }
                    if (previous == signature)
                        continue;
                    _lordChatEventStamps[id] = signature;
                    // Several AI rulers may share one coalition and one
                    // relation. Announce the recorded event just once.
                    foreach (int related in ai)
                    {
                        string relatedCoalition;
                        if (related != id &&
                            _playerCoalition.TryGetValue(related, out relatedCoalition) &&
                            String.Equals(relatedCoalition, cid, StringComparison.Ordinal))
                            _lordChatEventStamps[related] = signature;
                    }
                    string text = LordEventText(relation.ExtendedState.LastEvent);
                    if (!String.IsNullOrEmpty(text) &&
                        relation.ExtendedState.LastEventDay >= 0)
                    {
                        // Only the local player's recorded diplomatic relation
                        // is described; never expose an enemy-team private chat.
                        SendLordChatMessage(panel, id,
                            Text("An Euch: ", "To you: ") + text);
                        return;
                    }
                }

                if (_featureTicks < _lordChatNextAmbient)
                    return;
                _lordChatNextAmbient = _featureTicks + 960;
                // Ambient speech is addressed to the actual local player,
                // never a purported private exchange between two AI rulers.
                int from = ai[_lordChatPairCursor % ai.Count];
                _lordChatPairCursor++;
                string ambientCoalition;
                RelationState ambientRelation;
                if (!_playerCoalition.TryGetValue(from, out ambientCoalition) ||
                    !_relations.TryGetValue(ambientCoalition, out ambientRelation) ||
                    ambientRelation.ExtendedState == null)
                    return;
                bool ownTeammate = LordChatAddress.TeamRecipients(
                    _localPlayer, ai.ToDictionary(id => id, PlayerName),
                    _originalTeamPartition, state.teams).Contains(from);
                SendLordChatMessage(panel, from,
                    Text("An Euch: ", "To you: ") +
                    LordChatIdleLine(ambientRelation, ownTeammate));
            }
            catch (Exception exception)
            {
                // Presentation is optional. An absent HUD never disables
                // diplomacy or changes a native/serialized state.
                ReleaseLordChatUi();
                Log("lord_chat", "status=frame_unavailable|reason=" +
                    Safe(exception.GetType().Name));
            }
        }

        private List<int> GetLivingLordChatAis(EngineInterface.PlayState state)
        {
            var result = new List<int>();
            if (state == null || state.lord_alive == null)
                return result;
            foreach (int id in _roster)
                if (id > 0 && id <= 8 && id != _localPlayer &&
                    id < state.lord_alive.Length && state.lord_alive[id] != 0 &&
                    state.is_skirmish_player(id) && IsAlivePlayer(id))
                    result.Add(id);
            result.Sort();
            return result;
        }

        private void RememberLordEvent(int id)
        {
            string cid;
            RelationState r;
            if (_playerCoalition.TryGetValue(id, out cid) &&
                _relations.TryGetValue(cid, out r) && r.ExtendedState != null)
                _lordChatEventStamps[id] = ((int)r.ExtendedState.LastEvent)
                    .ToString(CultureInfo.InvariantCulture) + ":" +
                    r.ExtendedState.LastEventDay.ToString(CultureInfo.InvariantCulture);
        }

        private static string LordEventText(DiplomacyEventKind e)
        {
            switch (e)
            {
                case DiplomacyEventKind.GiftReceived:
                    return Text("Ich habe Eure Lieferung erhalten.",
                                "I have received your shipment.");
                case DiplomacyEventKind.DemandIssued:
                    return Text("Meine Gesandten haben eine Forderung überbracht.",
                                "My envoys have delivered a demand.");
                case DiplomacyEventKind.DemandFulfilled:
                    return Text("Eure Verpflichtung wurde erfüllt.",
                                "Your obligation was fulfilled.");
                case DiplomacyEventKind.DemandMissed:
                    return Text("Eine vereinbarte Lieferung blieb aus.",
                                "An agreed delivery did not arrive.");
                case DiplomacyEventKind.TruceFormed:
                    return Text("Die vereinbarte Waffenruhe gilt.",
                                "The agreed truce is in effect.");
                case DiplomacyEventKind.TradePactFormed:
                    return Text("Unser Handelspakt wurde geschlossen.",
                                "Our trade pact has been concluded.");
                case DiplomacyEventKind.AllianceFormed:
                    return Text("Unser Bündnis wurde geschlossen.",
                                "Our alliance has been formed.");
                case DiplomacyEventKind.AllianceBroken:
                    return Text("Unser Bündnis ist zerbrochen.",
                                "Our alliance has ended.");
                case DiplomacyEventKind.HostileAct:
                    return Text("Eure Feindseligkeiten bleiben nicht unbeachtet.",
                                "Your hostilities have not gone unnoticed.");
                case DiplomacyEventKind.AgreementExpired:
                    return Text("Unser Vertrag ist ausgelaufen.",
                                "Our treaty has expired.");
                default:
                    return null;
            }
        }

        private void SendLordChatMessage(HUD_MPChatMessages panel, int sender, string message)
        {
            if (panel == null || sender < 1 || sender > 8 ||
                String.IsNullOrEmpty(message) || !LordChatIsOriginalSoloBase() ||
                !_lordChatSoloConfirmed || !ReferenceEquals(panel, _lordChatPanel))
                return;
            panel.recieveIngameChat(PlayerName(sender), sender, message, 45);
        }

        private void BindLordChatPanel(HUD_MPChatMessages panel, MainViewModel view)
        {
            ReleaseLordChatUi();
            _lordChatNextBindTry = _featureTicks + 90;
            try
            {
                Noesis.FrameworkElement root = panel;
                Noesis.Grid layout = root.FindName("LayoutRoot") as Noesis.Grid;
                Noesis.Grid composer = root.FindName("LordChatComposer") as Noesis.Grid;
                Noesis.TextBox input = root.FindName("LordChatInput") as Noesis.TextBox;
                Noesis.Button send = root.FindName("LordChatSend") as Noesis.Button;
                Noesis.TextBlock caption = root.FindName("LordChatCaption") as Noesis.TextBlock;
                if (layout == null || composer == null || input == null || send == null || caption == null)
                    return; // actual HUD patch may be registered later
                _lordChatPanel = panel;
                _lordChatComposer = composer;
                _lordChatLayout = layout;
                _lordChatPriorMinHeight = layout.MinHeight;
                _lordChatInput = input;
                _lordChatSend = send;
                input.IsKeyboardFocusedChanged += OnLordChatFocus;
                input.KeyDown += OnLordChatKeyDown;
                send.Click += OnLordChatSend;
                caption.Text = Text("Fürstenchat", "Lord chat");
                send.Content = Text("Senden", "Send");
                layout.MinHeight = Math.Max(181f, _lordChatPriorMinHeight);
                composer.Visibility = Noesis.Visibility.Visible;
                _lordChatBound = true;
                // Save only the original visibility value WE change.
                if (!view.Show_HUD_MPChatMessages)
                {
                    _lordChatVisibilityView = view;
                    _lordChatVisibilityPanel = panel;
                    view.Show_HUD_MPChatMessages = true;
                    _lordChatChangedVisibility = true;
                }
                Log("lord_chat", "status=bound|solo_roster_verified=true|original_hud=true");
            }
            catch (Exception exception)
            {
                ReleaseLordChatUi();
                Log("lord_chat", "status=bind_failed|reason=" +
                    Safe(exception.GetType().Name));
            }
        }

        private void OnLordChatFocus(object sender,
            Noesis.DependencyPropertyChangedEventArgs e)
        {
            if (!ReferenceEquals(sender, _lordChatInput) || e == null ||
                !(e.NewValue is bool) || !CanPresentDiplomacyUi)
                return;
            MainViewModel view = MainViewModel.Instance;
            if (view == null || !_lordChatBound || !_lordChatSoloConfirmed ||
                !LordChatIsOriginalSoloBase() ||
                _lordChatGeneration != _sessionGeneration ||
                !ReferenceEquals(view.HUDMPChatMessages, _lordChatPanel))
                return;
            bool focused = (bool)e.NewValue;
            view.SetNoesisKeyboardState(focused);
            _lordChatKeyboardOwned = focused;
        }

        private void OnLordChatKeyDown(object sender, Noesis.KeyEventArgs e)
        {
            if (!ReferenceEquals(sender, _lordChatInput) || e == null ||
                (e.Key != Noesis.Key.Return && e.Key != Noesis.Key.Enter))
                return;
            // Do not consume the stock multiplayer chat's Enter key if
            // this saved-world/session certificate has become invalid.
            if (!_lordChatSoloConfirmed || !LordChatIsOriginalSoloBase() ||
                _lordChatGeneration != _sessionGeneration ||
                _lordChatEpoch != _mapEpoch || !_lordChatBound)
                return;
            e.Handled = true;
            SendTypedLordChat();
        }

        private void OnLordChatSend(object sender, Noesis.RoutedEventArgs args)
        {
            if (ReferenceEquals(sender, _lordChatSend))
                SendTypedLordChat();
        }

        private void SendTypedLordChat()
        {
            lock (_gate)
            {
                if (!_lordChatBound || !_lordChatSoloConfirmed ||
                    _lordChatInput == null || _lordChatPanel == null ||
                    _lordChatGeneration != _sessionGeneration ||
                    _lordChatEpoch != _mapEpoch ||
                    !String.Equals(_lordChatMap, _mapFingerprint,
                        StringComparison.Ordinal) ||
                    !String.Equals(_lordChatRoster, _rosterFingerprint,
                        StringComparison.Ordinal) ||
                    !LordChatIsOriginalSoloBase() || !IsSoloEngineSession(out _))
                    return;
                try
                {
                    EngineInterface.PlayState state = GameData.Instance.lastGameState;
                    MainViewModel view = MainViewModel.Instance;
                    if (state == null || view == null ||
                        !ReferenceEquals(view.HUDMPChatMessages, _lordChatPanel))
                        return;
                    var ai = new Dictionary<int, string>();
                    foreach (int id in GetLivingLordChatAis(state))
                        ai.Add(id, PlayerName(id));
                    string body;
                    List<int> teammates = LordChatAddress.TeamRecipients(
                        _localPlayer, ai, _originalTeamPartition, state.teams);
                    if (LordChatAddress.TryParseTeam(_lordChatInput.Text, out body))
                    {
                        var recipients = new List<KeyValuePair<int, RelationState>>();
                        foreach (int id in teammates)
                        {
                            string teamCoalition;
                            RelationState teamRelation;
                            if (_playerCoalition.TryGetValue(id, out teamCoalition) &&
                                _relations.TryGetValue(teamCoalition, out teamRelation) &&
                                teamRelation.ExtendedState != null)
                                recipients.Add(new KeyValuePair<int, RelationState>(
                                    id, teamRelation));
                        }
                        if (recipients.Count == 0)
                        {
                            _lordChatPanel.recieveIngameChat(
                                Text("Fürstenchat", "Lord chat"), _localPlayer,
                                Text("Kein lebender eigener KI-Teampartner ist erreichbar.",
                                     "No living AI teammate is available."), 20);
                            return;
                        }
                        _lordChatInput.Text = String.Empty;
                        _lordChatPanel.recieveIngameChat(
                            Text("Ihr", "You"), _localPlayer, "@team: " + body, 35);
                        foreach (KeyValuePair<int, RelationState> recipient in recipients)
                            SendLordChatMessage(_lordChatPanel, recipient.Key,
                                LordChatReply(body, recipient.Value, true));
                        return;
                    }

                    int target;
                    if (!LordChatAddress.TryParse(_lordChatInput.Text,
                            ai, out target, out body))
                    {
                        _lordChatPanel.recieveIngameChat(
                            Text("Fürstenchat", "Lord chat"), _localPlayer,
                            Text("Schreibt @2 Nachricht, @Fürstenname Nachricht oder @team Nachricht.",
                                 "Type @2 message, @lordname message or @team message."), 20);
                        return;
                    }
                    string cid;
                    RelationState relation;
                    if (!_playerCoalition.TryGetValue(target, out cid) ||
                        !_relations.TryGetValue(cid, out relation) ||
                        relation.ExtendedState == null)
                        return;
                    _lordChatInput.Text = String.Empty;
                    _lordChatPanel.recieveIngameChat(
                        Text("Ihr", "You"), _localPlayer,
                        "@" + ai[target] + ": " + body, 35);
                    SendLordChatMessage(_lordChatPanel, target,
                        LordChatReply(body, relation, teammates.Contains(target)));
                }
                catch (Exception exception)
                {
                    Log("lord_chat", "status=message_unavailable|reason=" +
                        Safe(exception.GetType().Name));
                }
            }
        }

        private enum LordChatMood { Friendly, Neutral, Hostile }

        private static LordChatMood LordChatMoodFor(bool originalTeammate,
            bool allied, decimal trust, decimal grudge)
        {
            if (grudge >= 65m ||
                (trust <= 15m && !originalTeammate && !allied))
                return LordChatMood.Hostile;
            if (((originalTeammate || allied) && grudge < 50m) ||
                (trust >= 58m && grudge < 35m))
                return LordChatMood.Friendly;
            return LordChatMood.Neutral;
        }

        private static string LordChatIdleLine(RelationState relation,
            bool originalTeammate)
        {
            DiplomacyRelationState current = relation.ExtendedState;
            LordChatMood mood = LordChatMoodFor(originalTeammate,
                relation.Allied, current.Trust, current.Grudge);
            if (mood == LordChatMood.Hostile)
                return Text("Unser Verhältnis ist angespannt. Ich werde Eure Schritte genau beobachten.",
                            "Relations are strained. I will watch your actions closely.");
            if (originalTeammate)
                return Text("Wir stehen im selben ursprünglichen Team. Für Befehle gelten die üblichen Wege.",
                            "We share the same original team. Orders still use the normal channels.");
            if (relation.Allied)
                return Text("Unser Bündnis besteht. Konkrete Absprachen gehören in die Diplomatie.",
                            "Our alliance stands. Specific agreements belong in diplomacy.");
            if (mood == LordChatMood.Friendly)
                return Text("Unsere Beziehungen lassen ein ruhiges Gespräch zu.",
                            "Our relations leave room for calm discussion.");
            return Text("Ich höre Eure Nachrichten. Verträge werden weiterhin förmlich geschlossen.",
                        "I can hear your messages. Treaties must still be concluded formally.");
        }

        private static string LordChatReply(string raw, RelationState relation,
            bool originalTeammate)
        {
            if (relation == null || relation.ExtendedState == null)
                return Text("Zurzeit liegen mir keine verlässlichen Angaben vor.",
                            "I have no reliable information at present.");
            DiplomacyRelationState current = relation.ExtendedState;
            string message = LordChatAddress.Normalize(raw).ToLowerInvariant();
            LordChatMood mood = LordChatMoodFor(originalTeammate, relation.Allied,
                current.Trust, current.Grudge);
            if (message.Contains("hallo") || message.Contains("hello") ||
                message.Contains("grüß") || message == "hi")
                return mood == LordChatMood.Friendly ?
                    Text("Seid gegrüßt. Ich höre Euch gern zu.",
                         "Greetings. I am glad to hear from you.") :
                    mood == LordChatMood.Hostile ?
                    Text("Sprecht offen, aber erwartet kein leichtes Einlenken.",
                         "Speak plainly, but do not expect easy concessions.") :
                    Text("Ich höre Euch. Worum geht es?",
                         "I hear you. What concerns you?");
            if (message.Contains("neuigkeit") || message.Contains("nachrichten") ||
                message.Contains("ereignis") || message.Contains("news") ||
                message.Contains("berichte") || message.Contains("status") ||
                message.Contains("lage"))
            {
                string last = current.LastEventDay >= 0 ?
                    LordEventText(current.LastEvent) : null;
                return String.IsNullOrEmpty(last) ?
                    Text("In unserer Diplomatiechronik ist kein entsprechendes Ereignis vermerkt.",
                         "Our diplomatic record contains no matching event.") :
                    Text("Die Chronik vermerkt: ", "The record notes: ") + last;
            }
            if (message.Contains("handel") || message.Contains("trade") ||
                message.Contains("waren") || message.Contains("goods"))
                return relation.Allied || originalTeammate ?
                    Text("Über Handel können wir sprechen; Güter werden nur über die bestehende Handelsoberfläche bewegt.",
                         "We can discuss trade; goods move only through the existing trade interface.") :
                    mood == LordChatMood.Hostile ?
                    Text("Unser Verhältnis erschwert den Handel. Hier wurden keine Waren übertragen.",
                         "Our relations make trade difficult. No goods were transferred here.") :
                    Text("Handelsabkommen sind förmlich zu regeln; diese Nachricht bucht keine Waren.",
                         "Trade agreements are formal; this message transfers no goods.");
            if (message.Contains("bündnis") || message.Contains("allian") ||
                message.Contains("verbünd"))
                return relation.Allied ?
                    Text("Unser Bündnis ist verzeichnet. Diese Nachricht ändert daran nichts.",
                         "Our alliance is recorded. This message does not change it.") :
                    originalTeammate ?
                    Text("Unser ursprüngliches Team bleibt erkennbar; ein Bündnisvertrag ist gesondert zu prüfen.",
                         "Our original team remains clear; a formal alliance needs separate review.") :
                    mood == LordChatMood.Hostile ?
                    Text("Für ein Bündnis fehlt derzeit Vertrauen. Hier wurde keines geschlossen.",
                         "Trust is too low for an alliance. None was formed here.") :
                    Text("Bündnisse entstehen nur durch das Diplomatiefenster.",
                         "Alliances are formed only through the diplomacy screen.");
            if (message.Contains("frieden") || message.Contains("peace") ||
                message.Contains("waffenruh") || message.Contains("truce"))
                return mood == LordChatMood.Hostile ?
                    Text("Unser Streit endet nicht durch Worte allein. Eine Waffenruhe braucht einen förmlichen Vertrag.",
                         "Words alone will not end our dispute. A truce needs a formal treaty.") :
                    Text("Waffenruhe und Frieden werden im Vertragsfenster vereinbart, nicht im Chat.",
                         "Truces and peace are agreed in the treaty screen, not in chat.");
            if (message.Contains("hilfe") || message.Contains("help") ||
                message.Contains("angriff") || message.Contains("attack"))
                return relation.Allied ?
                    Text("Für Verbündetenbefehle nutzt die vorhandene Befehlsoberfläche. Hier wurde kein Befehl erteilt.",
                         "Use the existing ally-orders interface. No order was issued here.") :
                    Text("Ohne aktives Bündnis gibt es keinen Verbündetenbefehl. Dieser Chat befiehlt nichts.",
                         "Without an active alliance there is no ally order. This chat issues none.");
            if (message.Contains("tribut") || message.Contains("vasall") ||
                message.Contains("fealty"))
                return Text("Tribut und Lehen verlangen einen förmlichen Vertrag. Diese Nachricht ändert nichts.",
                            "Tribute and fealty need a formal treaty. This message changes nothing.");
            if (message.Contains("vertrau") || message.Contains("trust") ||
                message.Contains("groll") || message.Contains("resent"))
                return Text("Vertrauen ", "Trust ") +
                    Math.Round(current.Trust, 0).ToString("0", CultureInfo.InvariantCulture) +
                    Text(", Groll ", ", resentment ") +
                    Math.Round(current.Grudge, 0).ToString("0", CultureInfo.InvariantCulture);
            if (mood == LordChatMood.Hostile)
                return Text("Ich habe Euch gehört. Unser Verhältnis bleibt angespannt.",
                            "I heard you. Our relations remain strained.");
            if (originalTeammate)
                return Text("Wir stehen auf derselben ursprünglichen Seite. Für Taten gelten die echten Befehle.",
                            "We began on the same side. Real actions still require actual commands.");
            if (relation.Allied)
                return Text("Wir sind verbündet. Was verbindlich sein soll, gehört in die Diplomatie.",
                            "We are allies. Binding matters belong in diplomacy.");
            return mood == LordChatMood.Friendly ?
                Text("Ich nehme Eure Worte freundlich auf. Verträge bleiben förmlich.",
                     "I receive your words warmly. Treaties remain formal.") :
                Text("Eure Nachricht ist angekommen. Taten und Verträge entscheiden.",
                     "Your message was received. Actions and treaties decide.");
        }

        private void ReleaseLordChatUi()
        {
            // Detach only our exact subscriptions, including partial binds.
            if (_lordChatInput != null)
            {
                try { _lordChatInput.KeyDown -= OnLordChatKeyDown; } catch { }
                try { _lordChatInput.IsKeyboardFocusedChanged -= OnLordChatFocus; } catch { }
            }
            if (_lordChatSend != null)
                try { _lordChatSend.Click -= OnLordChatSend; } catch { }
            if (_lordChatComposer != null)
                try { _lordChatComposer.Visibility = Noesis.Visibility.Collapsed; } catch { }
            if (_lordChatLayout != null)
            {
                try
                {
                    if (_lordChatLayout.MinHeight == Math.Max(181f, _lordChatPriorMinHeight))
                        _lordChatLayout.MinHeight = _lordChatPriorMinHeight;
                }
                catch { }
            }

            // Only release our keyboard claim while this very input still
            // owns focus. Do not steal focus from the stock chat or other UI.
            if (_lordChatKeyboardOwned && _lordChatInput != null && CanPresentDiplomacyUi)
            {
                try
                {
                    MainViewModel view = MainViewModel.Instance;
                    if (_lordChatInput.IsKeyboardFocused && view != null &&
                        ReferenceEquals(view.HUDMPChatMessages, _lordChatPanel))
                        view.SetNoesisKeyboardState(false);
                }
                catch { }
            }
            _lordChatKeyboardOwned = false;

            // Restore only a visibility flag changed by this connector,
            // only if the original view and panel still own that state, and
            // never hide a subsequently activated stock multiplayer chat.
            if (_lordChatChangedVisibility && CanPresentDiplomacyUi)
            {
                try
                {
                    MainViewModel current = MainViewModel.Instance;
                    if (ReferenceEquals(current, _lordChatVisibilityView) &&
                        current != null &&
                        ReferenceEquals(current.HUDMPChatMessages,
                            _lordChatVisibilityPanel) &&
                        !GameNetworkAPI.IsLobbyEnvironment() &&
                        (!GameNetworkAPI.IsNetworkedEnvironment() || IsSoloEngineSession(out _)) &&
                        current.Show_HUD_MPChatMessages)
                        current.Show_HUD_MPChatMessages = false;
                }
                catch { }
            }
            _lordChatChangedVisibility = false;
            _lordChatVisibilityView = null;
            _lordChatVisibilityPanel = null;
            _lordChatPanel = null;
            _lordChatComposer = null;
            _lordChatLayout = null;
            _lordChatInput = null;
            _lordChatSend = null;
            _lordChatBound = false;
        }
    }
}
