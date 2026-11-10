//  SERPS/APIShared/rawra SHCDESE compatibility contract, compiled against
// published official APIShared 0.16.3 + SHCDESE 2.14.3 + BepInEx 5.4.23.5.
// This is a hard FAIL-CLOSED release gate, not an installation or hash bypass.
// Acquire shared capabilities via APIShared.ApiShared.ForMod(OwnGuid);
// do not re-hook native market/Chore/HUD capabilities already shared by APIShared.
using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Bootstrap;
using APIShared;
using APIShared.Economy;
using SHCDESE.API;
using SHCDESE.Interop;

namespace StrongholdEuropeDE.TradeNegotiation
{
    internal static class SerpsRawraCompat
    {
        internal const string OwnerGuid = StrongholdEuropeDE.Runtime.CompatibilityContracts.PluginId;
        internal const string RawraGuid = "000shcdese";
        internal const string ApiSharedGuid = "APIShared_Serp";
        internal const string SerpsGuid = "BugfixesAndQoL_Serp";
        internal const string VerifiedNativeGameSha256 = "fbcb93195fc7efca9bdac5204852efdd76f9818f59a6711750d77c9cef2831e2";
        // Previous D: game copy, never an attestation of a loaded game process.
        // Preserve only as an explicit NEGATIVE regression reference.
        internal const string HistoricalUnattestedGameSha256 = "1e6d4c2e10cc35a7b8082a7e2bcd8bb20680ebeda803d9b943257b948145cb2b";
        internal const bool CanMutateGameGoods = false;
        internal const bool CanClaimRealHostQuorum = false;
        internal const bool CanInstallGamePatch = false;
        internal static readonly Version MinRawra = new Version(2, 14, 0);
        internal static readonly Version MinApiShared = new Version(0, 16, 3);
        internal static readonly Version MinSerps = new Version(1, 0, 192);
        internal static bool ManagedApiVersionsCompatible(IDictionary<string, Version> discovered, out string diagnostic)
        {
            diagnostic = "loaded plugin metadata unavailable";
            if (discovered == null)
                return false;
            Version rawra, shared, serps;
            if (!discovered.TryGetValue(RawraGuid, out rawra) || rawra == null)
            {
                diagnostic = "missing loaded Script Extender (" + RawraGuid + ")";
                return false;
            }
            if (!discovered.TryGetValue(ApiSharedGuid, out shared) || shared == null)
            {
                diagnostic = "missing loaded APIShared (" + ApiSharedGuid + ")";
                return false;
            }
            if (!discovered.TryGetValue(SerpsGuid, out serps) || serps == null)
            {
                diagnostic = "missing loaded BugfixesAndQoL (" + SerpsGuid + ")";
                return false;
            }
            if (rawra < MinRawra)
            {
                diagnostic = "rawra ScriptExtender <2.14.0";
                return false;
            }

            if (shared < MinApiShared)
            {
                diagnostic = "official APIShared <0.16.3";
                return false;
            }

            if (serps < MinSerps)
            {
                diagnostic = "SERPS Bugfixes & QoL <1.0.192";
                return false;
            }

            diagnostic = "managed version floors met; native game pairing NOT attested";
            return true;
        }

        internal static bool AttestedNativeCompatibility(string sha256, out string diagnostic)
        {
            diagnostic = "official native PE provenance absent or mismatched";
            if (string.IsNullOrEmpty(sha256) || sha256.Length != 64 || !string.Equals(sha256, VerifiedNativeGameSha256, StringComparison.OrdinalIgnoreCase))
                return false;
            diagnostic = "known SERPS-compatible PE hash; still requires live native ABI and save/lease checks";
            return true;
        }

        // A correctly named DLL existing on disk is NOT enough to attest
        // which game DLL the process actually loaded. This is a pure
        // file-content check used only after process/module identity proves
        // the path is the genuine loaded engine. Never authorizes writes.
        internal static bool TryAttestNativeFile(string path, out string diagnostic)
        {
            diagnostic = "native game file missing, unreadable, or not attested";
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !string.Equals(Path.GetFileName(path), "CrusaderDE.dll", StringComparison.OrdinalIgnoreCase))
                    return false;
                string absolute = Path.GetFullPath(path);
                using (var input = File.OpenRead(absolute))
                using (var hasher = SHA256.Create())
                {
                    if (input.Length < 1000000L || input.Length > 16000000L)
                        return false;
                    string hash = BitConverter.ToString(hasher.ComputeHash(input)).Replace("-", "").ToLowerInvariant();
                    return AttestedNativeCompatibility(hash, out diagnostic);
                }
            }
            catch (Exception error)
            {
                diagnostic = "native file attestation failed: " + error.GetType().Name;
                return false;
            }
        }

        // The historical constant alone was not a runtime attestation:
        // even changing it to the current Steam hash would silently accept
        // a different engine after an update. Require the ACTUAL loaded
        // CrusaderDE.dll module at the canonical game-relative path and
        // hash its real original on-disk image. If this process isn't the
        // game (e.g. a research harness), fail CLOSED without Native reads.
        internal static bool AttestedLoadedNativeEngine(out string diagnostic)
        {
            diagnostic = "no authentic loaded Stronghold Crusader DE native engine";
            try
            {
                using (var process = Process.GetCurrentProcess())
                {
                    ProcessModule main = process.MainModule;
                    if (main == null || !string.Equals(Path.GetFileName(main.FileName), "Stronghold Crusader Definitive Edition.exe", StringComparison.OrdinalIgnoreCase))
                        return false;
                    string root = Path.GetDirectoryName(Path.GetFullPath(main.FileName));
                    string expected = Path.GetFullPath(Path.Combine(root, "Stronghold Crusader Definitive Edition_Data", "Plugins", "x86_64", "CrusaderDE.dll"));
                    int matchingModules = 0;
                    foreach (ProcessModule module in process.Modules)
                    {
                        if (!string.Equals(module.ModuleName, "CrusaderDE.dll", StringComparison.OrdinalIgnoreCase))
                            continue;
                        matchingModules++;
                        if (!string.Equals(Path.GetFullPath(module.FileName), expected, StringComparison.OrdinalIgnoreCase))
                            return false;
                    }

                    if (matchingModules != 1)
                        return false;
                    return TryAttestNativeFile(expected, out diagnostic);
                }
            }
            catch (Exception error)
            {
                diagnostic = "loaded native engine attestation unavailable: " + error.GetType().Name;
                return false;
            }
        }

        //  independent reviewer gate: a correct process name, loaded-module
        // path and matching DISK SHA prove provenance only. They do not
        // establish the memory-mapped module's runtime integrity, rawra
        // native pointer layout, simulation-thread ownership or a durable
        // atomic SaveData/warehouse protocol. Never equate them.
        // The separate AttestedLoadedNativeEngine() remains a diagnostic.
        internal static bool AttestedLoadedNativeCompatibility(out string diagnostic)
        {
            diagnostic = "loaded game provenance alone insufficient: exact native AI ABI, memory image and simulation lease not attested";
            return false; // No native pointer read until independent ABI review.
        }

        internal static bool CheckLoadedBepInExDependencies(out string diagnostic)
        {
            // Call only during verified plugin startup, not from offline harness.
            // A directory entry is NOT proof that BepInEx successfully loaded it.
            var discovered = new Dictionary<string, Version>(StringComparer.Ordinal);
            foreach (var entry in Chainloader.PluginInfos)
            {
                if (entry.Value == null || entry.Value.Metadata == null)
                    continue;
                discovered[entry.Key] = entry.Value.Metadata.Version;
            }

            return ManagedApiVersionsCompatible(discovered, out diagnostic);
        }

        internal static ModApiClient AcquireOwnerBoundManagedClient(Func<bool> verifiedUnityMainThread, Func<bool> lifecycleActive)
        {
            if (verifiedUnityMainThread == null || lifecycleActive == null || !verifiedUnityMainThread() || !lifecycleActive())
                throw new NotSupportedException("APIShared capability acquisition blocked outside verified startup");
            string diagnostic;
            if (!CheckLoadedBepInExDependencies(out diagnostic))
                throw new NotSupportedException("Required SERPS+rawra services absent: " + diagnostic);
            // Acquiring this owner-bound client reserves NO shared hook.
            return APIShared.ApiShared.ForMod(OwnerGuid);
        }

        internal static void AssertNoNativeGoodsWrite()
        {
            throw new NotSupportedException("Native four-cell Trade2 and durable save are not yet attested; no live goods mutation");
        }

        internal static int NativeMarketArithmeticForResearch(int value, int count)
        {
            // Calls the real APIShared public arithmetic helper only.
            // It does NOT install, intercept or modify native market hooks.
            return MarketPriceEvents.CalculateTradeTotal(value, count);
        }

        internal static Type RawraStockpileApiTypeForResearch()
        {
            // Pure type identity, no Instance/native pointer dereference.
            return typeof(GamePlayerManagerAPI);
        }
    }
}
