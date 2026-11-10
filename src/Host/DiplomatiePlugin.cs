using System;
using System.Globalization;
using System.Linq;
using System.Text;
#if !DIPLOMACY_PRIVATE_TESTS
using APIShared.Presentation;
#endif
using BepInEx;
using CrusaderDE;
using HarmonyLib;
using SHCDESE.API;
using StrongholdEuropeDE.Runtime;
using UnityEngine;
using UnityObject = UnityEngine.Object;

namespace StrongholdEuropeDE.Diplomatie
{
    /// <summary>
    /// Die Diplomatie als eigenstaendiges Plugin.
    /// </summary>
    /// <remarks>Coordinates capability checks, runtime registration and update dispatch for the diplomacy plugin.</remarks>
    [BepInPlugin(PluginId, "Stronghold Crusader Diplomatie", "2.3.11")]
    [BepInIncompatibility("strongholdeuropede.diplomatie.private")]
    // Activation requires the checked runtime identities and member surface.
    // Native operations also require their original engine and session contracts.
    // The assembly references the official SHCDESE 2.14.3 API.
    [BepInDependency("000shcdese", "2.14.3")]
#if DIPLOMACY_PRIVATE_TESTS
    [BepInDependency(PrivateProfileBootstrap.PluginId)]
    [BepInDependency("APIShared_Serp", "0.25.0")]
#else
    [BepInDependency("APIShared_Serp", GeneratedPublicBuildDependencies.ApiSharedMinimum)]
#endif
#if DIPLOMACY_PRIVATE_TESTS
    [BepInDependency("BugfixesAndQoL_Serp", "1.0.192")]
#endif
    public sealed class DiplomatiePlugin : BaseUnityPlugin
    {
        internal const string PluginId = StrongholdEuropeDE.Runtime.CompatibilityContracts.PluginId;
        private const string HarmonyId = StrongholdEuropeDE.Runtime.CompatibilityContracts.HarmonyId;
        private const string FeatureId = StrongholdEuropeDE.Runtime.CompatibilityContracts.FeatureId;
        private const string RuntimeLogPrefix = "DIPLOMACY_RUNTIME|lane=diplomacy";
        private Harmony _harmony;
        private ExtensionFeatureHost _extensionHost;
        private DiplomacyFeature _diplomacy;
#if DIPLOMACY_PRIVATE_TESTS
        private GameObject _hostObject;
        private DiplomatiePersistentHost _host;
#else
        private IUiFrameWorkRegistration _frameWork;
#endif
        private bool _terminalLogged;
        private bool _featureFailureLogged;
        private bool _frameEvidenceLogged;
        private bool _shuttingDown;
        private bool _applicationQuitting;
        private bool _rosterDiagnoseGeschrieben;
        private bool _rosterBereitGemeldet;
        private int _rosterTakte;
        private void Awake()
        {
            try
            {
#if DIPLOMACY_PRIVATE_TESTS
                UnityObject.DontDestroyOnLoad(gameObject);
                if (!PrivateProfileBootstrap.Ready ||
                    !PrivateProfileRouting.Install(message => Logger.LogInfo(message)))
                {
                    Logger.LogError(" disposable profile routing was not established; this test plugin remains disabled.");
                    enabled = false;
                    return;
                }

#endif
                // Unknown engines/extenders stay passive. The generated binary
                // identities are attested before any feature or hook activation;
                // member-name/arity checks cannot establish native ABI safety.
                CompatibilityReport report = DiplomatieCompatibilityGuard.Probe();
                Logger.LogInfo(RuntimeLogPrefix + "|event=compatibility" + "|status=" + (report.Compatible ? "ok" : "incompatible") + "|bindings=" + report.BindingsPresent.ToString(CultureInfo.InvariantCulture) + "/" + report.BindingsChecked.ToString(CultureInfo.InvariantCulture) + "|attested=" + report.PrerequisitesAttested.ToString(CultureInfo.InvariantCulture) + "/" + report.PrerequisitesTotal.ToString(CultureInfo.InvariantCulture) + "|engine=" + (report.EngineAttested ? "attested" : "unverified:" + report.EngineDigestPrefix) + "|contract=" + GeneratedCompatibilityContract.Sha256.Substring(0, 12));
                foreach (string id in report.Unattested)
                {
                    Logger.LogWarning(RuntimeLogPrefix + "|event=compatibility" + "|status=unattested_prerequisite|id=" + id);
                }

                if (!report.EngineAttested)
                {
                    Logger.LogWarning(RuntimeLogPrefix + "|event=compatibility" + "|status=unverified_engine|reason=" + (report.EngineFailure ?? "unknown"));
                }

                // Report each incompatible artifact before refusing activation.
                foreach (string drift in report.GameSideDrift)
                {
                    Logger.LogWarning(RuntimeLogPrefix + "|event=compatibility" + "|status=unattested_game_file|id=" + drift);
                }

                if (report.GameSideDrift.Count > 0)
                {
                    Logger.LogWarning(RuntimeLogPrefix + "|event=compatibility" + "|status=unverified_game_build|files=" + report.GameSideDrift.Count.ToString(CultureInfo.InvariantCulture) + "|note=activation_refused_until_runtime_verified");
                }

                if (!report.Compatible)
                {
                    int shown = 0;
                    foreach (string missing in report.MissingAssemblies)
                    {
                        if (shown++ < 20)
                        {
                            Logger.LogError(RuntimeLogPrefix + "|event=compatibility|status=missing_assembly|name=" + missing);
                        }
                    }

                    foreach (string missing in report.MissingTypes)
                    {
                        if (shown++ < 20)
                        {
                            Logger.LogError(RuntimeLogPrefix + "|event=compatibility|status=missing_type|name=" + missing);
                        }
                    }

                    foreach (string missing in report.MissingMembers)
                    {
                        if (shown++ < 20)
                        {
                            Logger.LogError(RuntimeLogPrefix + "|event=compatibility|status=missing_member|name=" + missing);
                        }
                    }

                    LogTerminal("failed", "incompatible:" + report.FirstProblem);
                    enabled = false;
                    return;
                }

                _harmony = new Harmony(HarmonyId);
                _extensionHost = new ExtensionFeatureHost();
                DiplomacyFeature candidate = new DiplomacyFeature(_harmony, message => Logger.LogInfo(message), message => Logger.LogWarning(message), message => Logger.LogError(message));
                string registrationFailure;
                if (!_extensionHost.TryRegister(candidate, out registrationFailure))
                {
                    LogTerminal("failed", "claim_registration:" + registrationFailure);
                    ShutdownAndRollback("claim registration failed");
                    enabled = false;
                    return;
                }

#if !DIPLOMACY_PRIVATE_TESTS
                // The APIShared publisher survives Unity's startup cleanup of the BepInEx object.
                if (!APIShared.ApiShared.ForMod(PluginId).Ui.TryRegisterBeforeRender(
                        "diplomacy-frame", DiplomatieTick, out _frameWork, out string frameFailure))
                    throw new InvalidOperationException("Diplomacy frame work unavailable: " + frameFailure);
#endif
                _extensionHost.ActivateAll();
                ExtensionFeatureSnapshot snapshot = FindSnapshot(FeatureId);
                if (snapshot == null || snapshot.State != ExtensionFeatureState.Active)
                {
                    LogTerminal("failed", snapshot == null ? "missing_snapshot" : snapshot.Failure);
                    ShutdownAndRollback("activation failed");
                    enabled = false;
                    return;
                }

                _diplomacy = candidate;
#if DIPLOMACY_PRIVATE_TESTS
                CreatePersistentHost();
#else
                _frameWork.SetContinuous(true);
#endif
                LogTerminal("active", null);
#if SERP_DEVDIAG
                _diplomacy.StartDiagnosticAcceptance(Logger);
#endif
            }
            catch (Exception exception)
            {
                LogTerminal("failed", "startup_failure");
                Logger.LogError("Diplomatie failed closed; no unsupported patch remains active: " + exception);
                ShutdownAndRollback("initialization failure");
                enabled = false;
            }
        }

        internal void DiplomatieTick()
        {
            if (_shuttingDown || _extensionHost == null)
            {
                return;
            }

            RosterDiagnose();
            try
            {
                _extensionHost.TickAll();
                if (!_frameEvidenceLogged && _diplomacy != null && _diplomacy.HasActiveMissionTick)
                {
                    _frameEvidenceLogged = true;
                    Logger.LogInfo(RuntimeLogPrefix + "|event=runtime_frame|status=active_mission_ticked|time=" +
                        DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture));
                }
                ExtensionFeatureSnapshot snapshot = FindSnapshot(FeatureId);
                // Die eigentliche Feature-Referenz muss bis zum sicheren
                // Shutdown erhalten bleiben. RequiresFailClosedHooks kann nur
                // darüber feststellen, ob unbewiesene Team-/Transferzustände
                // weiterhin durch die nativen Schutz-Hooks eingefroren werden
                // müssen. Ein eigener Marker verhindert die Log-Flut.
                if (!_featureFailureLogged && _diplomacy != null && snapshot != null && snapshot.State == ExtensionFeatureState.Failed)
                {
                    Logger.LogError(RuntimeLogPrefix + "|event=feature" + "|status=failed_closed|reason=" + SafeReason(snapshot.Failure));
                    _featureFailureLogged = true;
                }
            }
            catch (Exception exception)
            {
                Logger.LogError("Diplomatie tick encountered an error: " + exception);
            }
        }

        /// <summary>
        /// Eine Zeile je Karte: was die Aufstellungspruefung der Diplomatie
        /// tatsaechlich vorfindet.
        /// </summary>
        /// <remarks>Coordinates capability checks, runtime registration and update dispatch for the diplomacy plugin.</remarks>
        private void RosterDiagnose()
        {
            try
            {
                GameData daten = GameData.Instance;
                object zustand = daten == null ? null : daten.lastGameState;
                if (daten == null || zustand == null)
                {
                    // Keine Karte geladen. Die Sperren fallen, damit die
                    // naechste Karte wieder ihre Zeilen schreibt.
                    _rosterDiagnoseGeschrieben = false;
                    _rosterBereitGemeldet = false;
                    _rosterTakte = 0;
                    return;
                }

                _rosterTakte++;
                GamePlayerManagerAPI spieler = GamePlayerManagerAPI.Instance;
                int lokal = spieler == null ? -1 : spieler.GetLocalPlayerId();
                int[] lebend = spieler == null ? new int[0] : (spieler.GetAlivePlayerIds() ?? new int[0]);
                // Zweite Zeile, genau einmal: der Augenblick, in dem die
                // Lebendliste zum ersten Mal steht. Ohne sie waere ein
                // wartender Bootstrap nicht von einem nie startenden zu
                // unterscheiden - beide schweigen.
                if (!_rosterBereitGemeldet && lebend.Length > 0)
                {
                    _rosterBereitGemeldet = true;
                    Logger.LogInfo(RuntimeLogPrefix + "|event=roster_bereit" + "|nach_takten=" + _rosterTakte.ToString(CultureInfo.InvariantCulture) + "|alive=[" + string.Join(",", lebend.Select(v => v.ToString(CultureInfo.InvariantCulture)).ToArray()) + "]");
                }

                if (_rosterDiagnoseGeschrieben)
                {
                    return;
                }

                _rosterDiagnoseGeschrieben = true;
                var gueltig = new StringBuilder();
                var rechner = new StringBuilder();
                var mannschaften = new StringBuilder();
                for (int id = 1; id <= 8; id++)
                {
                    gueltig.Append(daten.lastGameState.is_valid_player(id) ? 'x' : '-');
                    rechner.Append(daten.lastGameState.is_skirmish_player(id) ? 'x' : '-');
                    // Die Teamnummer entscheidet, wen die Diplomatie fuer einen
                    // Startverbuendeten haelt - und damit, welche Sendung ueber
                    // die native Strecke geht. Der Spieler sieht davon nichts.
                    if (mannschaften.Length > 0)
                    {
                        mannschaften.Append(',');
                    }

                    mannschaften.Append(spieler == null ? "?" : spieler.GetPlayerTeam(id).ToString(CultureInfo.InvariantCulture));
                }

                int lokalesTeam = spieler == null || lokal < 1 || lokal > 8 ? -1 : spieler.GetPlayerTeam(lokal);
                Logger.LogInfo(RuntimeLogPrefix + "|event=roster_diagnose" + "|local=" + lokal.ToString(CultureInfo.InvariantCulture) + "|game_player_id=" + daten.playerID.ToString(CultureInfo.InvariantCulture) + "|alive=[" + string.Join(",", lebend.Select(v => v.ToString(CultureInfo.InvariantCulture)).ToArray()) + "]|alive_contains_local=" + (lokal >= 1 && lokal <= 8 && lebend.Contains(lokal) ? "true" : "false") + "|valid_1_8=" + gueltig + "|skirmish_1_8=" + rechner + "|local_is_skirmish=" + (lokal >= 1 && lokal <= 8 && daten.lastGameState.is_skirmish_player(lokal) ? "true" : "false") + "|local_team=" + lokalesTeam.ToString(CultureInfo.InvariantCulture) + "|teams_1_8=" + mannschaften + "|startverbuendete=" + Startverbuendete(spieler, lokalesTeam, lokal));
            }
            catch (Exception exception)
            {
                // Beobachtbarkeit darf keine Verfuegbarkeitsabhaengigkeit
                // werden: eine misslungene Diagnose bleibt eine Zeile.
                try
                {
                    Logger.LogWarning(RuntimeLogPrefix + "|event=roster_diagnose" + "|status=unavailable|reason=" + exception.GetType().Name);
                }
                catch
                {
                }
            }
        }

        internal void DiplomatieApplicationQuit()
        {
            _applicationQuitting = true;
            ShutdownAndRollback("application quit");
        }

#if DIPLOMACY_PRIVATE_TESTS
        internal void DiplomatieHostDestroyed(bool applicationQuitting)
        {
            _applicationQuitting = _applicationQuitting || applicationQuitting;
            ShutdownAndRollback(applicationQuitting ? "application quit" : "persistent diplomacy host destroyed");
        }

        private void OnApplicationQuit()
        {
            DiplomatieApplicationQuit();
        }

        private void OnDestroy()
        {
            if (_applicationQuitting || IsUnityNull(_hostObject))
            {
                ShutdownAndRollback(_applicationQuitting ? "application quit" : "plugin component destroyed without a persistent host");
            }
        }

        private void CreatePersistentHost()
        {
            GameObject hostObject = null;
            try
            {
                hostObject = new GameObject("StrongholdEuropeDE.DiplomatieHost");
                UnityObject.DontDestroyOnLoad(hostObject);
                hostObject.hideFlags = HideFlags.HideAndDontSave;
                DiplomatiePersistentHost host = hostObject.AddComponent<DiplomatiePersistentHost>();
                host.Bind(this);
                _hostObject = hostObject;
                _host = host;
                hostObject = null;
            }
            finally
            {
                if (!IsUnityNull(hostObject))
                {
                    UnityObject.Destroy(hostObject);
                }
            }
        }

#endif

        private void ShutdownAndRollback(string reason)
        {
            if (_shuttingDown)
            {
                return;
            }

            _shuttingDown = true;
#if !DIPLOMACY_PRIVATE_TESTS
            _frameWork?.SetContinuous(false);
#endif
#if DIPLOMACY_PRIVATE_TESTS && HOST_DIRECTED_AI_TRIBE_CANARY && ORIGINAL_POSTTICK_INSTALL_EXPLICITLY_AUTHORIZED
            // Atomically render the passive worker callback inert before any
            // feature rollback or process-exit fast path, never live-Unpatch.
            if (OriginalPreWorkerRegistration.MustRetainRunPatch)
                OriginalSimulationPostTickBoundary.DisableForProcess();
#endif
            try
            {
                Logger.LogInfo(RuntimeLogPrefix + "|event=shutdown|reason=" + SafeReason(reason));
            }
            catch
            {
            // Eine Protokollzeile darf nichts beenden koennen.
            }

            if (_applicationQuitting)
            {
                try
                {
                    Logger.LogInfo(RuntimeLogPrefix + "|event=shutdown" + "|status=process_exit|rollback=not_required");
                }
                catch
                {
                }

                // Unity is terminating the process. Restoring teams and
                // unpatching native callbacks at this point cannot protect a
                // future frame because no future frame exists; it only turns
                // a normal quit into a synthetic fail_closed rollback.
                return;
            }

            bool preserveFailClosedHooks = false;
            if (_extensionHost != null)
            {
                try
                {
                    _extensionHost.RollbackAll();
                }
                catch (Exception exception)
                {
                    Logger.LogError("Diplomatie rollback encountered an error: " + exception);
                }

                preserveFailClosedHooks = _diplomacy != null && _diplomacy.RequiresFailClosedHooks;
                if (preserveFailClosedHooks)
                {
                    Logger.LogError("Diplomacy native rollback remains unproven; " + "retaining runtime hooks and save recovery " + "while the game is paused.");
                }

                _extensionHost = null;
                _diplomacy = null;
            }

            if (_harmony != null && !preserveFailClosedHooks)
            {
#if DIPLOMACY_PRIVATE_TESTS && HOST_DIRECTED_AI_TRIBE_CANARY && ORIGINAL_POSTTICK_INSTALL_EXPLICITLY_AUTHORIZED
                if (OriginalPreWorkerRegistration.MustRetainRunPatch)
                {
                    // UnpatchSelf also rewrites EngineInterface.run and would
                    // be unsafe after the original worker may have started.
                    // Other normal hook rollback has already been attempted.
                    Logger.LogWarning(RuntimeLogPrefix +
                        "|event=private_run_patch_retained_until_process_exit");
                }
                else
#endif
                {
                    try
                    {
                        _harmony.UnpatchSelf();
                    }
                    catch (Exception exception)
                    {
                        Logger.LogError("Diplomatie unpatch encountered an error: " + exception);
                    }

                    _harmony = null;
                }
            }

#if DIPLOMACY_PRIVATE_TESTS
            if (!IsUnityNull(_hostObject))
            {
                try
                {
                    UnityObject.Destroy(_hostObject);
                }
                catch (Exception exception)
                {
                    Logger.LogWarning("Diplomatie host teardown errored: " + exception.GetType().Name);
                }
            }

            _hostObject = null;
            _host = null;
#endif
        }

        private ExtensionFeatureSnapshot FindSnapshot(string id)
        {
            if (_extensionHost == null)
            {
                return null;
            }

            foreach (ExtensionFeatureSnapshot snapshot in _extensionHost.Snapshot())
            {
                if (string.Equals(snapshot.Id, id, StringComparison.Ordinal))
                {
                    return snapshot;
                }
            }

            return null;
        }

        private void LogTerminal(string status, string reason)
        {
            if (_terminalLogged)
            {
                return;
            }

            _terminalLogged = true;
            try
            {
                string line = RuntimeLogPrefix + "|event=feature" + "|status=" + status;
                if (!string.IsNullOrEmpty(reason))
                {
                    line += "|reason=" + SafeReason(reason);
                }

                if (string.Equals(status, "failed", StringComparison.Ordinal))
                {
                    Logger.LogError(line);
                }
                else
                {
                    Logger.LogInfo(line);
                }
            }
            catch
            {
            // Beobachtbarkeit darf keine Verfuegbarkeitsabhaengigkeit werden.
            }
        }

        /// <summary>
        /// Wen die Diplomatie ab Kartenstart fuer verbuendet haelt.
        /// </summary>
        /// <remarks>Coordinates capability checks, runtime registration and update dispatch for the diplomacy plugin.</remarks>
        private static string Startverbuendete(GamePlayerManagerAPI spieler, int lokalesTeam, int lokal)
        {
            if (spieler == null || lokalesTeam <= 0)
            {
                return "keine";
            }

            var treffer = new StringBuilder();
            for (int id = 1; id <= 8; id++)
            {
                if (id == lokal || spieler.GetPlayerTeam(id) != lokalesTeam)
                {
                    continue;
                }

                if (treffer.Length > 0)
                {
                    treffer.Append(',');
                }

                treffer.Append(id.ToString(CultureInfo.InvariantCulture));
            }

            return treffer.Length == 0 ? "keine" : treffer.ToString();
        }

        private static string SafeReason(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "unknown";
            }

            return value.Replace('|', ' ').Replace('\r', ' ').Replace('\n', ' ');
        }

#if DIPLOMACY_PRIVATE_TESTS
        private static bool IsUnityNull(UnityObject value)
        {
            return value == null;
        }
#endif
    }
}
