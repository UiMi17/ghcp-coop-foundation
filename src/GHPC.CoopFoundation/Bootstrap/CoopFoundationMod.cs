using GHPC.CoopFoundation.Diagnostics;
using GHPC.CoopFoundation.Networking;
using GHPC.CoopFoundation.Networking.NwhPuppet;
using GHPC.CoopFoundation.Patches;
using MelonLoader;
using UnityEngine;

[assembly: MelonInfo(
    typeof(GHPC.CoopFoundation.Bootstrap.CoopFoundationMod),
    "GHPC Coop Foundation",
    GHPC.CoopFoundation.Bootstrap.CoopModMetadata.Version,
    "GHPC Coop")]

namespace GHPC.CoopFoundation.Bootstrap;

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

    /// <summary>Verbose per-datagram GHP recv logs — very expensive with MelonLoader console + disk I/O; default off.</summary>
    private static readonly MelonPreferences_Entry<bool> LogNetworkReceive =
        PrefCategory.CreateEntry("LogNetworkReceive", false);

    private static readonly MelonPreferences_Entry<bool> LogMissionMismatch =
        PrefCategory.CreateEntry("LogMissionMismatch", true);

    /// <summary>Capsule/cube proxy for remote player (redundant once host drives real <see cref="GHPC.Unit" /> from snapshots).</summary>
    private static readonly MelonPreferences_Entry<bool> ShowRemoteGhost =
        PrefCategory.CreateEntry("ShowRemoteGhost", false);

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

    /// <summary>Host→client COO snapshot of <see cref="GHPC.World.WorldEnvironmentManager" /> (temperature, night, etc.).</summary>
    private static readonly MelonPreferences_Entry<bool> WorldEnvironmentReplicationEnabled =
        PrefCategory.CreateEntry("WorldEnvironmentReplicationEnabled", true);

    private static readonly MelonPreferences_Entry<bool> LogWorldEnvironmentSync =
        PrefCategory.CreateEntry("LogWorldEnvironmentSync", false);

    /// <summary>Host periodic COO refresh for world atmosphere/weather (Hz, 0 disables periodic refresh).</summary>
    private static readonly MelonPreferences_Entry<float> WorldEnvironmentReplicationHz =
        PrefCategory.CreateEntry("WorldEnvironmentReplicationHz", 1f);

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

    /// <summary>Phase 5: replicate authoritative HitResolved events (can be very noisy).</summary>
    private static readonly MelonPreferences_Entry<bool> HitResolvedReplicationEnabled =
        PrefCategory.CreateEntry("HitResolvedReplicationEnabled", true);

    /// <summary>Host: max HitResolved sends per second (0 = unlimited).</summary>
    private static readonly MelonPreferences_Entry<int> HitResolvedMaxPerSecond =
        PrefCategory.CreateEntry("HitResolvedMaxPerSecond", 60);

    /// <summary>Host: max HitResolved datagrams sent per LateUpdate after per-victim coalesce (0 = unlimited).</summary>
    private static readonly MelonPreferences_Entry<int> HitResolvedHostMaxPerFrame =
        PrefCategory.CreateEntry("HitResolvedHostMaxPerFrame", 8);

    /// <summary>Client: max low-priority HitResolved applies per frame.</summary>
    private static readonly MelonPreferences_Entry<int> HitResolvedApplyMaxPerFrame =
        PrefCategory.CreateEntry("HitResolvedApplyMaxPerFrame", 8);

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

    /// <summary>Phase 6: skip local LiveRound/SimpleRound impact SFX+particles for other shooters (host sends GHC cosmetics).</summary>
    private static readonly MelonPreferences_Entry<bool> ClientSuppressRemoteShooterCosmetics =
        PrefCategory.CreateEntry("ClientSuppressRemoteShooterCosmetics", true);

    /// <summary>Phase 6: host sends <c>ParticleImpact</c> GHC (requires combat replication).</summary>
    private static readonly MelonPreferences_Entry<bool> ParticleImpactReplicationEnabled =
        PrefCategory.CreateEntry("ParticleImpactReplicationEnabled", true);

    /// <summary>Phase 6: host sends <c>Explosion</c> GHC.</summary>
    private static readonly MelonPreferences_Entry<bool> ExplosionReplicationEnabled =
        PrefCategory.CreateEntry("ExplosionReplicationEnabled", true);

    /// <summary>Phase 6: client replays muzzle VFX from <c>WeaponFired</c> for remote shooters.</summary>
    private static readonly MelonPreferences_Entry<bool> MuzzleCosmeticReplayEnabled =
        PrefCategory.CreateEntry("MuzzleCosmeticReplayEnabled", true);

    /// <summary>Host: drop cosmetic UDP sends beyond this distance (m) from main camera or remote ghost (0 = unlimited).</summary>
    private static readonly MelonPreferences_Entry<float> CosmeticInterestMaxDistanceMeters =
        PrefCategory.CreateEntry("CosmeticInterestMaxDistanceMeters", 0f);

    /// <summary>Client: min TNT equivalent (kg) from replicated explosion to trigger camera shake/blur.</summary>
    private static readonly MelonPreferences_Entry<float> ExplosionCameraMinTntKg =
        PrefCategory.CreateEntry("ExplosionCameraMinTntKg", 0.01f);

    /// <summary>Host: GHC frag Explosion TNT from <see cref="GHPC.Weaponry.AntiPersonnelGrenade" /> serialized radius (linear vs ~6 m → 0.22 kg).</summary>
    private static readonly MelonPreferences_Entry<bool> FragGrenadeCosmeticTntUseApRadius =
        PrefCategory.CreateEntry("FragGrenadeCosmeticTntUseApRadius", true);

    /// <summary>Host: fixed TNT kg for frag GHC when radius mapping is off or unreadable.</summary>
    private static readonly MelonPreferences_Entry<float> FragGrenadeCosmeticTntFallbackKg =
        PrefCategory.CreateEntry("FragGrenadeCosmeticTntFallbackKg", 0.22f);

    /// <summary>Host→client: GHC cosmetic ballistic ghost for AT grenade jets (no local LiveRound on peer).</summary>
    private static readonly MelonPreferences_Entry<bool> AtGrenadeJetVisualReplicationEnabled =
        PrefCategory.CreateEntry("AtGrenadeJetVisualReplicationEnabled", true);

    private static readonly MelonPreferences_Entry<float> ExplosionCameraMaxDistanceMeters =
        PrefCategory.CreateEntry("ExplosionCameraMaxDistanceMeters", 400f);

    /// <summary>With <c>LogCombatReplication</c>: log cosmetic drop counters (throttled).</summary>
    private static readonly MelonPreferences_Entry<bool> LogCosmeticHealth =
        PrefCategory.CreateEntry("LogCosmeticHealth", false);

    /// <summary>Host: move real client <see cref="GHPC.Unit" /> from network snapshots; disable AI/driver fighting transforms.</summary>
    private static readonly MelonPreferences_Entry<bool> HostPeerBodySyncEnabled =
        PrefCategory.CreateEntry("HostPeerBodySyncEnabled", true);

    private static readonly MelonPreferences_Entry<bool> HostPeerBodySyncLog =
        PrefCategory.CreateEntry("HostPeerBodySyncLog", false);

    private static readonly MelonPreferences_Entry<bool> ClientPeerBodySyncEnabled =
        PrefCategory.CreateEntry("ClientPeerBodySyncEnabled", true);

    private static readonly MelonPreferences_Entry<bool> ClientPeerBodySyncLog =
        PrefCategory.CreateEntry("ClientPeerBodySyncLog", false);

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

    /// <summary>Client puppet: keep NWH <c>WheelController</c> running with injected wire velocity; skip chassis forces.</summary>
    private static readonly MelonPreferences_Entry<bool> NetworkNwhPuppetWheelControllerEnabled =
        PrefCategory.CreateEntry("NetworkNwhPuppetWheelControllerEnabled", true);

    /// <summary>Client puppet: leave NWH rigging enabled (requires live suspension).</summary>
    private static readonly MelonPreferences_Entry<bool> NetworkNwhPuppetRiggingEnabled =
        PrefCategory.CreateEntry("NetworkNwhPuppetRiggingEnabled", false);

    /// <summary>Host GHW: append linear acceleration (v5 wire); improves client extrapolation / NWH read-path.</summary>
    private static readonly MelonPreferences_Entry<bool> WorldReplicationGhwV5Acceleration =
        PrefCategory.CreateEntry("WorldReplicationGhwV5Acceleration", true);

    /// <summary>Host GHW v6: replicate NWH <c>input.Vertical</c> (throttle/brake intent); requires v5 acceleration on wire.</summary>
    private static readonly MelonPreferences_Entry<bool> WorldReplicationGhwV6MotorInput =
        PrefCategory.CreateEntry("WorldReplicationGhwV6MotorInput", true);

    /// <summary>Host: temporarily raise GHW send rate while braking / hard decel (same packet layout).</summary>
    private static readonly MelonPreferences_Entry<bool> WorldReplicationBrakeBoostEnabled =
        PrefCategory.CreateEntry("WorldReplicationBrakeBoostEnabled", true);

    /// <summary>Use <see cref="double"/> entries so TOML serializes as <c>2.1</c> (never <c>2.1f</c>), which Tomlet rejects.</summary>
    private static readonly MelonPreferences_Entry<double> WorldReplicationBrakeBoostHzMultiplier =
        PrefCategory.CreateEntry("WorldReplicationBrakeBoostHzMultiplier", 2.1);

    private static readonly MelonPreferences_Entry<double> WorldReplicationBrakeBoostSustainSeconds =
        PrefCategory.CreateEntry("WorldReplicationBrakeBoostSustainSeconds", 0.22);

    /// <summary>If true, disable phase 5 correction on first runtime exception.</summary>
    private static readonly MelonPreferences_Entry<bool> ClientSimulationSafeMode =
        PrefCategory.CreateEntry("ClientSimulationSafeMode", true);

    /// <summary>Trace post-apply turret/gun overwrites by LateFollow/constraints (diagnostics).</summary>
    private static readonly MelonPreferences_Entry<bool> ClientSimulationAimTrace =
        PrefCategory.CreateEntry("ClientSimulationAimTrace", false);

    /// <summary>Client governor: distance LOD + round-robin for aim/hull on far units (reduces sustained LateUpdate CPU).</summary>
    private static readonly MelonPreferences_Entry<bool> ClientGovernorLodEnabled =
        PrefCategory.CreateEntry("ClientGovernorLodEnabled", true);

    private static readonly MelonPreferences_Entry<double> ClientGovernorLodNearMeters =
        PrefCategory.CreateEntry("ClientGovernorLodNearMeters", 220.0);

    private static readonly MelonPreferences_Entry<double> ClientGovernorLodMidMeters =
        PrefCategory.CreateEntry("ClientGovernorLodMidMeters", 550.0);

    /// <summary>Tier 1 (mid distance): run aim every N frames (1 = every frame).</summary>
    private static readonly MelonPreferences_Entry<int> ClientGovernorLodMidAimEveryFrames =
        PrefCategory.CreateEntry("ClientGovernorLodMidAimEveryFrames", 2);

    /// <summary>Tier 2 (far): run hull correction every N frames (1 = every frame).</summary>
    private static readonly MelonPreferences_Entry<int> ClientGovernorLodFarHullEveryFrames =
        PrefCategory.CreateEntry("ClientGovernorLodFarHullEveryFrames", 2);

    /// <summary>Tier 2 (far): aim every N frames; 0 = no aim in far tier.</summary>
    private static readonly MelonPreferences_Entry<int> ClientGovernorLodFarAimEveryFrames =
        PrefCategory.CreateEntry("ClientGovernorLodFarAimEveryFrames", 6);

    /// <summary>Max units receiving hull correction per frame (soft cap inside governor budget formula).</summary>
    private static readonly MelonPreferences_Entry<int> ClientGovernorCorrectionBudgetMax =
        PrefCategory.CreateEntry("ClientGovernorCorrectionBudgetMax", 72);

    /// <summary>
    ///     Beyond this distance (m) from camera/local unit, NWH puppet wheels use last GHW wire velocity only (skips Hermite resample). 0 = off.
    /// </summary>
    private static readonly MelonPreferences_Entry<double> NetworkNwhWheelWireOnlyBeyondMeters =
        PrefCategory.CreateEntry("NetworkNwhWheelWireOnlyBeyondMeters", 0.0);

    /// <summary>
    ///     Verbose replication tracing (wire map, GHW/GHP accept/reject, missing Unit for netId). Enable while debugging peer desync / invisible vehicles.
    /// </summary>
    private static readonly MelonPreferences_Entry<bool> LogReplicationDiagnostics =
        PrefCategory.CreateEntry("LogReplicationDiagnostics", false);

    /// <summary>Throttled host+client player vs peer comparison (RB/VC/AI, GHP remote, GHW buffer + governor).</summary>
    private static readonly MelonPreferences_Entry<bool> LogPlayerReplicationScenario =
        PrefCategory.CreateEntry("LogPlayerReplicationScenario", false);

    private static readonly MelonPreferences_Entry<double> LogPlayerReplicationScenarioIntervalSec =
        PrefCategory.CreateEntry("LogPlayerReplicationScenarioIntervalSec", 2.0);

    /// <summary>Steam build: aggregate Stopwatch timings for major mod sections; log avg/max µs once per interval (no Unity Profiler).</summary>
    private static readonly MelonPreferences_Entry<bool> CpuSectionDiagnosticsEnabled =
        PrefCategory.CreateEntry("CpuSectionDiagnosticsEnabled", false);

    private static readonly MelonPreferences_Entry<double> CpuSectionDiagnosticsIntervalSec =
        PrefCategory.CreateEntry("CpuSectionDiagnosticsIntervalSec", 1.0);

    public override void OnInitializeMelon()
    {
        HookDiagnostics.Init(LogGameHooks);
        PrefCategory.SaveToFile(false);

        _harmony = new HarmonyLib.Harmony(HarmonyId);
        _harmony.PatchAll(typeof(CoopFoundationMod).Assembly);

        LoggerInstance.Msg(
            $"GHPC Coop Foundation {CoopModMetadata.Version} — Harmony patches applied ({HarmonyId}).");
        LoggerInstance.Msg(
            "Network: UDP GHP v6 + GHW v4/v5/v6 world (v6 motor + brake-boost send) + GHC combat (+ ImpactFx + ParticleImpact + Explosion + muzzle replay + DamageState); COO (+ WorldEnv); phase5 governor; NWH puppet wheel visuals; phase6 cosmetic channel.");

        // Phase 6 M2: keep network menu-authoritative in main menu flow.
        // We still apply transport prefs continuously in OnUpdate.
        CoopUdpTransport.ConfigureAndStart(
            enabled: false,
            roleName: "Off",
            bindPort: NetworkBindPort.Value,
            remoteHost: NetworkRemoteHost.Value,
            remotePort: NetworkRemotePort.Value,
            logReceive: LogNetworkReceive.Value,
            logMissionMismatch: LogMissionMismatch.Value,
            enforceVehicleOwnership: EnforceVehicleOwnership.Value,
            logVehicleOwnershipBlocks: LogVehicleOwnershipBlocks.Value);
    }

    public override void OnUpdate()
    {
        CoopCpuDiag.Configure(
            CpuSectionDiagnosticsEnabled.Value,
            (float)CpuSectionDiagnosticsIntervalSec.Value);

        PatchSimpleRoundCoopCosmetic.TryApply(_harmony);
        PatchBuildMenuControllerMultiplayerButton.TickLobbyControllers();
        using (CoopCpuDiag.Start("Cpu.Update_PrefsAndTransport"))
        {
            CoopNwhPuppetSettings.WheelControllerVisualsEnabled = NetworkNwhPuppetWheelControllerEnabled.Value;
            CoopNwhPuppetSettings.RiggingEnabledOnPuppets = NetworkNwhPuppetRiggingEnabled.Value;
            CoopNwhPuppetSettings.WheelWireOnlyBeyondMeters = (float)NetworkNwhWheelWireOnlyBeyondMeters.Value;
            CoopUdpTransport.SetWorldReplicationPrefs(
            WorldReplicationEnabled.Value,
            WorldReplicationHz.Value,
            LogWorldReplication.Value,
            LogWorldReplication.Value,
            WorldReplicationGhwV5Acceleration.Value,
            WorldReplicationGhwV6MotorInput.Value,
            WorldReplicationBrakeBoostEnabled.Value,
            (float)WorldReplicationBrakeBoostHzMultiplier.Value,
            (float)WorldReplicationBrakeBoostSustainSeconds.Value);
        CoopUdpTransport.SetWorldEnvironmentReplicationPrefs(
            WorldEnvironmentReplicationEnabled.Value,
            WorldEnvironmentReplicationHz.Value,
            LogWorldEnvironmentSync.Value);
        CoopUdpTransport.SetCombatReplicationPrefs(
            CombatReplicationEnabled.Value,
            LogCombatReplication.Value,
            LogCombatStruckPerHit.Value,
            CombatApplyMaxPerFrame.Value,
            CombatApplyMaxMsPerFrame.Value,
            HitResolvedReplicationEnabled.Value,
            HitResolvedMaxPerSecond.Value,
            HitResolvedHostMaxPerFrame.Value,
            HitResolvedApplyMaxPerFrame.Value);
        CoopUdpTransport.SetImpactFxReplicationPrefs(ImpactFxReplicationEnabled.Value, LogImpactFx.Value);
        CoopUdpTransport.SetDamageStateReplicationPrefs(DamageStateReplicationEnabled.Value, LogDamageState.Value);
        CoopUdpTransport.SetCosmeticReplicationPrefs(
            ClientSuppressRemoteShooterCosmetics.Value,
            ParticleImpactReplicationEnabled.Value,
            ExplosionReplicationEnabled.Value,
            MuzzleCosmeticReplayEnabled.Value,
            CosmeticInterestMaxDistanceMeters.Value,
            ExplosionCameraMinTntKg.Value,
            ExplosionCameraMaxDistanceMeters.Value,
            LogCosmeticHealth.Value,
            FragGrenadeCosmeticTntUseApRadius.Value,
            FragGrenadeCosmeticTntFallbackKg.Value,
            AtGrenadeJetVisualReplicationEnabled.Value);
        CoopCosmeticHealthCounters.TickLogIfDue();
        ClientWorldProxyService.ConfigureCapture(ShowWorldProxies.Value);
        ClientSimulationGovernor.Configure(
            ClientSimulationSuppressionEnabled.Value,
            ClientSimulationCorrectionStrength.Value,
            ClientSimulationSoftSuppressEnabled.Value,
            ClientSimulationLog.Value,
            ClientSimulationSafeMode.Value,
            ClientGovernorLodEnabled.Value,
            (float)ClientGovernorLodNearMeters.Value,
            (float)ClientGovernorLodMidMeters.Value,
            ClientGovernorLodMidAimEveryFrames.Value,
            ClientGovernorLodFarHullEveryFrames.Value,
            ClientGovernorLodFarAimEveryFrames.Value,
            ClientGovernorCorrectionBudgetMax.Value);
        AimOverwriteProbe.Configure(ClientSimulationAimTrace.Value);
        CoopReplicationDiagnostics.Configure(LogReplicationDiagnostics.Value);
        CoopPlayerScenarioDiagnostics.Configure(
            LogPlayerReplicationScenario.Value,
            (float)LogPlayerReplicationScenarioIntervalSec.Value);
            HostPeerUnitPuppet.Enabled = HostPeerBodySyncEnabled.Value;
            HostPeerUnitPuppet.Log = HostPeerBodySyncLog.Value;
            ClientPeerUnitPuppet.Enabled = ClientPeerBodySyncEnabled.Value;
            ClientPeerUnitPuppet.Log = ClientPeerBodySyncLog.Value;
        }

        using (CoopCpuDiag.Start("Cpu.Update_ProcessInbound"))
            CoopUdpTransport.ProcessInbound();
        if (CoopUdpTransport.IsClient)
        {
            using (CoopCpuDiag.Start("Cpu.Update_WorldEnvFlush"))
                CoopWorldEnvironmentReplication.TryFlushPendingIfPossible(LogWorldEnvironmentSync.Value);
        }

        using (CoopCpuDiag.Start("Cpu.Update_DrainCombat"))
            CoopUdpTransport.DrainClientCombatApply();
        using (CoopCpuDiag.Start("Cpu.Update_GovernorTickUpdate"))
            ClientSimulationGovernor.TickUpdate(Time.deltaTime);
        using (CoopCpuDiag.Start("Cpu.Update_NetworkSessionTick"))
            CoopUdpTransport.NetworkSessionTick();
        using (CoopCpuDiag.Start("Cpu.Update_HostWorldReplication"))
            CoopUdpTransport.HostTickWorldReplication(Time.deltaTime);
        using (CoopCpuDiag.Start("Cpu.Update_LocalPlayerSampler"))
            LocalPlayerSampler.Tick(
                Time.time,
                Time.deltaTime,
                LogLocalSnapshot.Value,
                SnapshotLogIntervalSeconds.Value);
    }

    public override void OnGUI()
    {
        if (!CoopClientPlanningGate.IsWaitingForHostPlanning)
            return;

        const int boxW = 540;
        const int boxH = 88;
        float x = (Screen.width - boxW) * 0.5f;
        float y = Screen.height * 0.32f;
        GUI.Box(new Rect(x - 14f, y - 14f, boxW + 28f, boxH + 28f), GUIContent.none);
        var style = new GUIStyle(GUI.skin.label)
        {
            alignment = TextAnchor.MiddleCenter,
            fontSize = 17,
            wordWrap = true
        };
        GUI.Label(
            new Rect(x, y, boxW, boxH),
            "Co-op: waiting for host.\nThe host is completing tactical planning on the map.",
            style);
    }

    public override void OnLateUpdate()
    {
        if (CoopUdpTransport.IsHost)
        {
            using (CoopCpuDiag.Start("Cpu.Late_HitResolvedFlush"))
                HostCombatBroadcast.FlushPendingHitResolved(CoopUdpTransport.CombatReplicationLogDamageState);
        }

        using (CoopCpuDiag.Start("Cpu.Late_GovernorTickLateUpdate"))
            ClientSimulationGovernor.TickLateUpdate(Time.deltaTime);
        using (CoopCpuDiag.Start("Cpu.Late_PuppetVisualOrchestrator"))
            CoopRemotePuppetVisualLateOrchestrator.Tick(Time.deltaTime);
        using (CoopCpuDiag.Start("Cpu.Late_HostPeer"))
            HostPeerUnitPuppet.TickLateUpdate();
        using (CoopCpuDiag.Start("Cpu.Late_ClientPeer"))
            ClientPeerUnitPuppet.TickLateUpdate();
        using (CoopCpuDiag.Start("Cpu.Late_AtJetVisualReplay"))
            CoopAtJetVisualReplay.Tick(Time.deltaTime);
        using (CoopCpuDiag.Start("Cpu.Late_RemoteGhost"))
            RemoteGhostService.TickLateUpdate(ShowRemoteGhost.Value, RemoteGhostSmoothing.Value, RemoteGhostYOffset.Value);
        using (CoopCpuDiag.Start("Cpu.Late_WorldProxy"))
            ClientWorldProxyService.TickLateUpdate(
                ShowWorldProxies.Value,
                WorldProxySmoothing.Value,
                WorldProxyYOffset.Value);
        using (CoopCpuDiag.Start("Cpu.Late_PlayerScenarioDiag"))
            CoopPlayerScenarioDiagnostics.TickLateUpdate();

        CoopCpuDiag.FlushIfDue(Time.unscaledTime);
    }

    public override void OnFixedUpdate()
    {
        using (CoopCpuDiag.Start("Cpu.Fixed_HostPeer"))
            HostPeerUnitPuppet.TickFixedUpdate();
        using (CoopCpuDiag.Start("Cpu.Fixed_ClientPeer"))
            ClientPeerUnitPuppet.TickFixedUpdate();
        using (CoopCpuDiag.Start("Cpu.Fixed_Governor"))
            ClientSimulationGovernor.TickFixedUpdate();
    }

    public override void OnApplicationQuit()
    {
        HostPeerUnitPuppet.Reset();
        ClientPeerUnitPuppet.Reset();
        RemoteGhostService.Destroy();
        ClientWorldProxyService.ClearAll();
        CoopUdpTransport.Shutdown();
        _harmony?.UnpatchSelf();
    }

    public override void OnSceneWasLoaded(int buildIndex, string sceneName)
    {
        PatchSimpleRoundCoopCosmetic.TryApply(_harmony);
        CoopSessionState.NotifySceneLoaded(sceneName);
        if (!LogSceneLoads.Value)
            return;
        LoggerInstance.Msg($"[CoopDiag] Scene loaded: index={buildIndex} name={sceneName}");
    }
}
