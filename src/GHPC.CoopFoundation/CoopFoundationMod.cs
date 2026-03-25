using GHPC.CoopFoundation.Net;
using MelonLoader;
using UnityEngine;

[assembly: MelonInfo(
    typeof(GHPC.CoopFoundation.CoopFoundationMod),
    "GHPC Coop Foundation",
    GHPC.CoopFoundation.CoopModMetadata.Version,
    "GHPC Coop")]

namespace GHPC.CoopFoundation;

public sealed class CoopFoundationMod : MelonMod
{
    private const string HarmonyId = "com.ghpc.coop.foundation";

    private HarmonyLib.Harmony? _harmony;

    private static readonly MelonPreferences_Category PrefCategory =
        MelonPreferences.CreateCategory("GHPC_Coop_Foundation");

    private static readonly MelonPreferences_Entry<bool> LogSceneLoads =
        PrefCategory.CreateEntry("LogSceneLoads", true);

    private static readonly MelonPreferences_Entry<bool> LogGameHooks =
        PrefCategory.CreateEntry("LogGameHooks", true);

    private static readonly MelonPreferences_Entry<bool> LogLocalSnapshot =
        PrefCategory.CreateEntry("LogLocalSnapshot", true);

    private static readonly MelonPreferences_Entry<float> SnapshotLogIntervalSeconds =
        PrefCategory.CreateEntry("SnapshotLogIntervalSeconds", 2f);

    private static readonly MelonPreferences_Entry<bool> NetworkEnabled =
        PrefCategory.CreateEntry("NetworkEnabled", false);

    private static readonly MelonPreferences_Entry<string> NetworkRole =
        PrefCategory.CreateEntry("NetworkRole", "Off");

    private static readonly MelonPreferences_Entry<int> NetworkBindPort =
        PrefCategory.CreateEntry("NetworkBindPort", 27015);

    private static readonly MelonPreferences_Entry<string> NetworkRemoteHost =
        PrefCategory.CreateEntry("NetworkRemoteHost", "127.0.0.1");

    private static readonly MelonPreferences_Entry<int> NetworkRemotePort =
        PrefCategory.CreateEntry("NetworkRemotePort", 27015);

    private static readonly MelonPreferences_Entry<bool> LogNetworkReceive =
        PrefCategory.CreateEntry("LogNetworkReceive", true);

    private static readonly MelonPreferences_Entry<bool> LogMissionMismatch =
        PrefCategory.CreateEntry("LogMissionMismatch", true);

    private static readonly MelonPreferences_Entry<bool> ShowRemoteGhost =
        PrefCategory.CreateEntry("ShowRemoteGhost", true);

    private static readonly MelonPreferences_Entry<float> RemoteGhostSmoothing =
        PrefCategory.CreateEntry("RemoteGhostSmoothing", 12f);

    private static readonly MelonPreferences_Entry<float> RemoteGhostYOffset =
        PrefCategory.CreateEntry("RemoteGhostYOffset", 1.1f);

    private static readonly MelonPreferences_Entry<bool> EnforceVehicleOwnership =
        PrefCategory.CreateEntry("EnforceVehicleOwnership", true);

    private static readonly MelonPreferences_Entry<bool> LogVehicleOwnershipBlocks =
        PrefCategory.CreateEntry("LogVehicleOwnershipBlocks", true);

    private static readonly MelonPreferences_Entry<bool> WorldReplicationEnabled =
        PrefCategory.CreateEntry("WorldReplicationEnabled", true);

    private static readonly MelonPreferences_Entry<float> WorldReplicationHz =
        PrefCategory.CreateEntry("WorldReplicationHz", 5f);

    private static readonly MelonPreferences_Entry<bool> LogWorldReplication =
        PrefCategory.CreateEntry("LogWorldReplication", false);

    private static readonly MelonPreferences_Entry<bool> ShowWorldProxies =
        PrefCategory.CreateEntry("ShowWorldProxies", true);

    private static readonly MelonPreferences_Entry<float> WorldProxySmoothing =
        PrefCategory.CreateEntry("WorldProxySmoothing", 10f);

    private static readonly MelonPreferences_Entry<float> WorldProxyYOffset =
        PrefCategory.CreateEntry("WorldProxyYOffset", 1.1f);

    private static readonly MelonPreferences_Entry<bool> CombatReplicationEnabled =
        PrefCategory.CreateEntry("CombatReplicationEnabled", true);

    private static readonly MelonPreferences_Entry<bool> LogCombatReplication =
        PrefCategory.CreateEntry("LogCombatReplication", false);

    /// <summary>When <see cref="LogCombatReplication" /> is true, log each GHC Struck send/recv (very noisy).</summary>
    private static readonly MelonPreferences_Entry<bool> LogCombatStruckPerHit =
        PrefCategory.CreateEntry("LogCombatStruckPerHit", false);

    /// <summary>Client: max GHC events to apply per frame (0 = unlimited; default spreads load).</summary>
    private static readonly MelonPreferences_Entry<int> CombatApplyMaxPerFrame =
        PrefCategory.CreateEntry("CombatApplyMaxPerFrame", 64);

    /// <summary>Client: max wall milliseconds per frame spent applying GHC (0 = unlimited).</summary>
    private static readonly MelonPreferences_Entry<float> CombatApplyMaxMsPerFrame =
        PrefCategory.CreateEntry("CombatApplyMaxMsPerFrame", 16f);

    /// <summary>Phase 4: host sends GHC ImpactFx for terrain/ricochet/armor/penetration SFX (throttled). Requires combat replication.</summary>
    private static readonly MelonPreferences_Entry<bool> ImpactFxReplicationEnabled =
        PrefCategory.CreateEntry("ImpactFxReplicationEnabled", true);

    /// <summary>When <see cref="LogCombatReplication" /> is true, log each ImpactFx send/recv.</summary>
    private static readonly MelonPreferences_Entry<bool> LogImpactFx =
        PrefCategory.CreateEntry("LogImpactFx", false);

    /// <summary>Phase 4B: host sends compact damage state corrections (spall-safe alternative to per-hit replication).</summary>
    private static readonly MelonPreferences_Entry<bool> DamageStateReplicationEnabled =
        PrefCategory.CreateEntry("DamageStateReplicationEnabled", true);

    /// <summary>When <see cref="LogCombatReplication" /> is true, log each damage-state send/recv.</summary>
    private static readonly MelonPreferences_Entry<bool> LogDamageState =
        PrefCategory.CreateEntry("LogDamageState", false);

    /// <summary>Phase 5: correction-first governor for non-local client units.</summary>
    private static readonly MelonPreferences_Entry<bool> ClientSimulationSuppressionEnabled =
        PrefCategory.CreateEntry("ClientSimulationSuppressionEnabled", true);

    /// <summary>Phase 5 correction strength multiplier (0 = off, 1 = default).</summary>
    private static readonly MelonPreferences_Entry<float> ClientSimulationCorrectionStrength =
        PrefCategory.CreateEntry("ClientSimulationCorrectionStrength", 1f);

    /// <summary>Phase 5 M3: soft suppress non-local crew AI where API is safe.</summary>
    private static readonly MelonPreferences_Entry<bool> ClientSimulationSoftSuppressEnabled =
        PrefCategory.CreateEntry("ClientSimulationSoftSuppressEnabled", true);

    /// <summary>Throttled phase 5 diagnostics.</summary>
    private static readonly MelonPreferences_Entry<bool> ClientSimulationLog =
        PrefCategory.CreateEntry("ClientSimulationLog", false);

    /// <summary>If true, disable phase 5 correction on first runtime exception.</summary>
    private static readonly MelonPreferences_Entry<bool> ClientSimulationSafeMode =
        PrefCategory.CreateEntry("ClientSimulationSafeMode", true);

    /// <summary>Trace post-apply turret/gun overwrites by LateFollow/constraints (diagnostics).</summary>
    private static readonly MelonPreferences_Entry<bool> ClientSimulationAimTrace =
        PrefCategory.CreateEntry("ClientSimulationAimTrace", false);

    public override void OnInitializeMelon()
    {
        HookDiagnostics.Init(LogGameHooks);
        PrefCategory.SaveToFile(false);

        _harmony = new HarmonyLib.Harmony(HarmonyId);
        _harmony.PatchAll(typeof(CoopFoundationMod).Assembly);

        LoggerInstance.Msg(
            $"GHPC Coop Foundation {CoopModMetadata.Version} — Harmony patches applied ({HarmonyId}).");
        LoggerInstance.Msg(
            "Network: UDP GHP v3 + GHW world + GHC combat (+ ImpactFx + DamageState correction); COO session; vehicle ownership; remote ghost + world proxies; phase5 correction governor.");

        CoopUdpTransport.ConfigureAndStart(
            NetworkEnabled.Value,
            NetworkRole.Value,
            NetworkBindPort.Value,
            NetworkRemoteHost.Value,
            NetworkRemotePort.Value,
            LogNetworkReceive.Value,
            LogMissionMismatch.Value,
            EnforceVehicleOwnership.Value,
            LogVehicleOwnershipBlocks.Value);
    }

    public override void OnUpdate()
    {
        CoopUdpTransport.SetWorldReplicationPrefs(
            WorldReplicationEnabled.Value,
            WorldReplicationHz.Value,
            LogWorldReplication.Value,
            LogWorldReplication.Value);
        CoopUdpTransport.SetCombatReplicationPrefs(
            CombatReplicationEnabled.Value,
            LogCombatReplication.Value,
            LogCombatStruckPerHit.Value,
            CombatApplyMaxPerFrame.Value,
            CombatApplyMaxMsPerFrame.Value);
        CoopUdpTransport.SetImpactFxReplicationPrefs(ImpactFxReplicationEnabled.Value, LogImpactFx.Value);
        CoopUdpTransport.SetDamageStateReplicationPrefs(DamageStateReplicationEnabled.Value, LogDamageState.Value);
        ClientSimulationGovernor.Configure(
            ClientSimulationSuppressionEnabled.Value,
            ClientSimulationCorrectionStrength.Value,
            ClientSimulationSoftSuppressEnabled.Value,
            ClientSimulationLog.Value,
            ClientSimulationSafeMode.Value);
        AimOverwriteProbe.Configure(ClientSimulationAimTrace.Value);
        CoopUdpTransport.ProcessInbound();
        CoopUdpTransport.DrainClientCombatApply();
        ClientSimulationGovernor.TickUpdate(Time.deltaTime);
        CoopUdpTransport.NetworkSessionTick();
        CoopUdpTransport.HostTickWorldReplication(Time.deltaTime);
        LocalPlayerSampler.Tick(
            Time.time,
            Time.deltaTime,
            LogLocalSnapshot.Value,
            SnapshotLogIntervalSeconds.Value);
    }

    public override void OnLateUpdate()
    {
        ClientSimulationGovernor.TickLateUpdate(Time.deltaTime);
        RemoteGhostService.TickLateUpdate(ShowRemoteGhost.Value, RemoteGhostSmoothing.Value, RemoteGhostYOffset.Value);
        ClientWorldProxyService.TickLateUpdate(
            ShowWorldProxies.Value,
            WorldProxySmoothing.Value,
            WorldProxyYOffset.Value);
    }

    public override void OnApplicationQuit()
    {
        RemoteGhostService.Destroy();
        ClientWorldProxyService.ClearAll();
        CoopUdpTransport.Shutdown();
        _harmony?.UnpatchSelf();
    }

    public override void OnSceneWasLoaded(int buildIndex, string sceneName)
    {
        CoopSessionState.NotifySceneLoaded(sceneName);
        if (!LogSceneLoads.Value)
            return;
        LoggerInstance.Msg($"[CoopDiag] Scene loaded: index={buildIndex} name={sceneName}");
    }
}
