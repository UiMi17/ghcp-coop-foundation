# GHPC mod workspace (agent notes)

## Game (this machine)

- **Root:** `GHPCGameDir` in `Directory.Build.props` (default `E:\SteamLibrary\steamapps\common\Gunner HEAT PC\Bin`).
- **Executable:** `GHPC.exe`. **Unity:** 2022.3.62f2 (Mono, not IL2CPP). Managed assemblies: `GHPC_Data\Managed\`.
- **Steam App ID:** `1705180` (`steam_appid.txt` in Bin).
- **Pinned game build (update when Steam updates GHPC):** see `GAME_BUILD.txt`.

## This repo

- **Mod project:** `src/GHPC.CoopFoundation/` → output `GHPC.CoopFoundation.dll`.
- **MelonLoader compile refs:** `lib/MelonLoader/*.dll` (not committed). Populate with `pwsh tools/fetch-melonloader-libs.ps1` (downloads official `MelonLoader.x64.zip`, extracts `net35` API DLLs).
- **Deploy:** After `dotnet build`, the DLL is copied to each configured game `Bin\Mods` where `GHPC.exe` exists: `GHPCGameDir` and optional `GHPCGameDirSecondary` (`DeployMod` in csproj, paths in `Directory.Build.props`).
- **Decompiled game source (search only):** `artifacts/decompiled/` — generate with `tools/decompile-game.ps1` after `dotnet tool install -g ilspycmd`. Do not commit; do not treat as authoritative signatures across game updates.
- **Coop replication design (decompile map, events, GHW/GHC):** [docs/CoopReplication.md](docs/CoopReplication.md). **Roadmap:** фази 1–4 закриті в плані; поточний фокус розробки — **фаза 5** (локальна AI + **combat truth snapshots** на GHC). Чеклист перевірок: [docs/Phase5CombatTruthTestMatrix.md](docs/Phase5CombatTruthTestMatrix.md).

## Custom paths

- Copy `Directory.Build.user.props.example` → `Directory.Build.user.props` and set `GHPCGameDir`, or set env `GHPC_GAME_DIR` for `decompile-game.ps1` only.

## Workflow

1. `pwsh tools/fetch-melonloader-libs.ps1` (once per clone / after MelonLoader upgrade).
2. `dotnet build GHPC.sln -c Release`
3. Install **MelonLoader** on the game (installer) if not already — required to **run** mods, not to compile.

## Harmony anchors (from `Assembly-CSharp` decompile, game `0.1.0-alpha+20260210.1`)

Diagnostics-only patches live in `src/GHPC.CoopFoundation/Patches/` (toggle `GHPC_Coop_Foundation.LogGameHooks`):

| Patch | Target | Why |
|--------|--------|-----|
| `PatchMissionInitializer` | `GHPC.Mission.MissionInitializer.Awake` | Місія: старт компонента, у лог потрапляє `MissionSceneName` (якщо вже задано). |
| `PatchMissionStateController` | `GHPC.State.MissionStateController.SetState(MissionState)` | Переходи `Planning` / `Playing` / `Finished` — зручно синхронізувати «фазу» коопу пізніше. |
| `PatchWorldScriptPlayerVehicle` | `WorldScript.SetPlayerVehicle(VehicleInfo)` | Додатковий шлях призначення `VehicleInfo` (у сесії часто не викликається при swap у загоні). |
| `PatchPlayerInputSetPlayerUnit` | `GHPC.Player.PlayerInput.SetPlayerUnit(Unit)` | **Основний** swap техніки в місії. |
| `PatchPlayerInputSetDefaultUnit` | `GHPC.Player.PlayerInput.SetDefaultUnit(IUnit)` | Початковий юніт при старті місії / мета. |
| `PatchWeaponSystemFire` | `GHPC.Weapons.WeaponSystem.Fire(IUnit)` | (v0.5.0+) Хост: **GHC** `WeaponFired` (muzzle, dir, shooter/target net id, ammo key). |
| `PatchUnitNotifyStruck` | `GHPC.Unit.NotifyStruck(IUnit, AmmoType, Vector3, bool)` | (v0.5.0+) Хост: **GHC** `UnitStruck`; на клієнті під час застосування реплікованого удару broadcast вимкнено (`SuppressStruckBroadcast`). **(Фаза 5+)** після удару додатково емітяться truth snapshots (`UnitState`, `CrewState`, `CompartmentState`) та `HitResolved` де доречно. |
| `PatchUnitNotifyDestroyed` | `GHPC.Unit.NotifyDestroyed` | **(Фаза 5+)** Хост: примусова відправка `UnitState` / `CrewState` / `CompartmentState` + вже існуючий `DamageState` flow (`destroyed`). |
| `PatchUnitStateTransitions` | `Unit.NotifyIncapacitated`, `NotifyAbandoned`, `NotifyCannotMove`, `NotifyCannotShoot` | **(Фаза 5+)** Хост: емісія truth snapshots при зміні цих прапорів. |
| `PatchImpactSfxReplication` | `GHPC.Audio.ImpactSFXManager.Play*ImpactSFX` / `PlayRicochetSFX` | (v0.6.1+) Хост: **GHC** `ImpactFx` для terrain/ricochet/armor/pen; клієнт викликає відповідні `ImpactSFXManager` API; per-kind throttle на хості. |
| `PatchAimOverwriteWriters` | `LateFollow.Sync`, `RotationConstraint.ProcessConstraints` | **(діагностика)** Коли `ClientSimulationAimTrace=true`, логує можливі перезаписи обертання після governor (`AimOverwriteProbe`). |

4. Regenerate `artifacts/decompiled` after Steam updates (`tools/decompile-game.ps1`) and re-verify patch targets if the game breaks mods.

## Local player snapshot (v0.1.4+)

- `CoopSessionState` — `LastMissionState`, `ControlledUnit`, `IsPlaying`, last sampled hull `Transform` + turret/gun world rotations from `Unit.AimablePlatforms` (`CoopAimableSampler`), `CoopUnitNetId`, and `GetInstanceID()` (10 Hz while Playing).
- `LocalPlayerSampler` — driven from `OnUpdate`; prefs `LogLocalSnapshot`, `SnapshotLogIntervalSeconds`.
- Menu scenes (`MainMenu`, `LOADER_MENU`, `LOADER_INITIAL`) clear session state via `NotifySceneLoaded`.

## UDP snapshot exchange (v0.1.5+)

Prefs (category `GHPC_Coop_Foundation`):

| Key | Role |
|-----|------|
| `NetworkEnabled` | `true` to open UDP (read at game start only — restart after changing). |
| `NetworkRole` | `Host` \| `Client` \| `Off` (default `Off`). In raw `MelonPreferences.cfg` (TOML) use quotes: `NetworkRole = "Host"`. |
| `NetworkBindPort` | Host listens on this UDP port (default `27015`). |
| `NetworkRemoteHost` | Client only: IPv4 string, e.g. `127.0.0.1`. |
| `NetworkRemotePort` | Client target port (must match host bind port). |
| `LogNetworkReceive` | Log each parsed inbound packet as `[CoopNet] recv ...`. |
| `LogMissionMismatch` | Throttled `[CoopNet] Dropped remote snapshot (mission mismatch)` when mission token/phase disagrees (v2+ snapshots). |
| `RemoteGhostYOffset` | (v0.1.8+) World-space Y added to remote position for the capsule (default `1.1`). |
| `EnforceVehicleOwnership` | (v0.1.9+) Block `SetPlayerUnit` / `SetDefaultUnit` into units held by another peer (host-authoritative table). |
| `LogVehicleOwnershipBlocks` | Throttled warnings when a seat swap is blocked. |
| `WorldReplicationEnabled` | (v0.4.0+) Host sends **GHW** world snapshots (`GHW\x01`) to the peer. |
| `WorldReplicationHz` | How often the host emits a full world tick (default `5`). |
| `LogWorldReplication` | Logs `[CoopNet] GHW send …` / `GHW recv …` (host + client). |
| `ShowWorldProxies` | Client: green capsule proxies for replicated units (excluding remote player `netId`). |
| `WorldProxySmoothing` | Client LateUpdate interpolation factor (default `10`). |
| `WorldProxyYOffset` | World Y offset for world proxies (default `1.1`, same idea as remote ghost). |
| `CombatReplicationEnabled` | (v0.5.0+) Host emits and client applies **GHC** combat datagrams (`true` by default). |
| `LogCombatReplication` | When true: `[CoopNet] GHC send/recv Fired …` and (with `LogCombatStruckPerHit`) per-hit Struck lines. |
| `LogCombatStruckPerHit` | (v0.5.2+) Requires `LogCombatReplication`: log each **Struck** send/recv (default off; very noisy). |
| `CombatApplyMaxPerFrame` | (v0.5.1+) Client: max **GHC** applies per frame after dequeue (`0` = unlimited; default `64` since v0.5.2). |
| `CombatApplyMaxMsPerFrame` | (v0.5.1+) Client: wall-time budget (ms) per frame for GHC apply (`0` = unlimited; default `16` since v0.5.2). |
| `ImpactFxReplicationEnabled` | (v0.6.0+) Host emits **GHC** `ImpactFx` for terrain impacts (`true` by default; requires `CombatReplicationEnabled`). |
| `LogImpactFx` | (v0.6.0+) With `LogCombatReplication`: log each **ImpactFx** send/recv (default off). |
| `DamageStateReplicationEnabled` | (v0.6.2+) Host emits **GHC** `DamageState` correction snapshots for chassis/components (`true` by default; requires `CombatReplicationEnabled`). |
| `LogDamageState` | (v0.6.2+) Log each **DamageState** send/recv when true (does not require `LogCombatReplication`). |
| `HitResolvedReplicationEnabled` | **(Фаза 5+)** Host emits **GHC** `HitResolved` telemetry (`true` by default; requires `CombatReplicationEnabled`). |
| `HitResolvedMaxPerSecond` | **(Фаза 5+)** Host: max `HitResolved` sends per second (`0` = unlimited; default `60`). |
| `HitResolvedHostMaxPerFrame` | **(Фаза 5+)** Host: max `HitResolved` UDP sends per `LateUpdate` after coalescing latest per victim (`0` = unlimited; default `8`). |
| `HitResolvedApplyMaxPerFrame` | **(Фаза 5+)** Client: max low-priority `HitResolved` applies per frame after `PendingHigh` drain (default `8`). |
| `ClientSimulationSuppressionEnabled` | **(Фаза 5+)** Client: correction-first governor for non-local units (default on). |
| `ClientSimulationCorrectionStrength` | **(Фаза 5+)** Governor correction multiplier (`0` = off). |
| `ClientSimulationSoftSuppressEnabled` | **(Фаза 5+)** Soft-disable non-local crew AI where safe. |
| `ClientSimulationLog` | Throttled Phase 5 governor diagnostics. |
| `ClientSimulationSafeMode` | Disable governor after first apply exception. |
| `ClientSimulationAimTrace` | Diagnostics: `AimOverwriteProbe` after turret/gun apply. |

**GHC combat replication (v0.5.0+):** `Net/CoopCombatPacket.cs` — magic `GHC`, wire v1, `hostCombatSeq` + mission token + phase. **WeaponFired** (~52 B): shooter/target net ids, FNV ammo key (`AmmoType.Name`), muzzle position, fire direction. **UnitStruck** (~44 B): victim/shooter net ids, ammo key, impact, spall flag on the wire (v0.5.2+: host **does not** emit spall strikes — only non-spall `NotifyStruck`; spall remains local sim detail). **ImpactFx** (v0.6.0+, event type `3`, ~56 B): `effectKind` (e.g. terrain), world position, normal (reserved), ammo key, `victimNetId` (0 for terrain), flags (tree/spall hint) — клієнт для `CoopImpactFxKind.Terrain` викликає `ImpactSFXManager.PlayTerrainImpactSFX`. **DamageState** (v0.6.2+, `4`): chassis HP% + `UnitDestroyed`. **Фаза 5+ truth:** **UnitState** (`5`), **CrewState** (`6`), **HitResolved** (`7`, throttled on host, low-priority apply + coalesce on client), **CompartmentState** (`8`). Той самий **`hostCombatSeq`** інкрементується для всіх типів на дроті; health-метрики клієнта **не трактують прогалини в seq лише через `HitResolved`** як чистий packet loss. Host sends to learned `_hostPeer` (same UDP socket as GHP/GHW/COO). Client resolves `AmmoType` via `CoopAmmoResolver` (`Resources.FindObjectsOfTypeAll<AmmoCodexScriptable>` + `LiveRound.SpallAmmoType`) and calls `Unit.NotifyStruck` (with re-entrancy guard). **WeaponFired** on the client is logged only for now (no full `LiveRound`). Authoritative snapshots should remain monotonic in apply order where gated by `AcceptSeq`; strict UDP reorder can still drop individual datagrams. Struck-related **warnings** (missing victim, ammo resolve failure, apply exception) always log when they occur.

**ImpactFx / локальна симуляція (v0.6.0+):** клієнт може **дублювати** terrain impact SFX, якщо локальний `LiveRound` теж б’є ґрунт; повне прибирання — **фаза 5** (приглушення локальної балістики). Вимкніть `ImpactFxReplicationEnabled`, якщо потрібен лише `UnitStruck` без додаткового SFX.

**DamageState correction / spall parity (v0.6.2+):** host emits compact **GHC** `DamageState` (`eventType=4`) with change-detect + throttle instead of raw per-hit spall stream. Client applies authoritative component health correction (engine/transmission/radiator/tracks + destroyed flag). **Фаза 5+** додає окремі snapshots для прапорів юніта, екіпажу та відсіків (`UnitState` / `CrewState` / `CompartmentState`) — застосування на клієнті idempotent, latest-wins coalescing по `unitNetId` у черзі перед дреном.

**GHC Fired payload (v0.5.3+):** Harmony postfix runs *after* `WeaponSystem.Fire` returns; by then `AmmoFeed` has usually cleared `AmmoTypeInBreech` via the weapon `Fired` event. The mod resolves ammo from **`WeaponSystem._lastRound.Info`** (then `CurrentAmmoType`, then breech). **`target` net id:** explicit `Fire(IUnit)` argument, else **`Unit.InfoBroker.CurrentTarget.Owner`** when the gunner has a locked target (player often calls `Fire(null)`).

**Paired `GHC send Fired` seq in one frame:** only one Harmony postfix exists on `WeaponSystem.Fire`; two consecutive seq values mean **`Fire` returned true twice** in the same frame (e.g. multiple `WeaponSystem` instances or game firing twice), not duplicate patching.

**GHC client apply budget (v0.5.1+):** `ProcessInbound` only **enqueues** GHC on the client; `DrainClientCombatApply` (after inbound, each `OnUpdate`) спочатку дренить **high-priority** чергу (усі типи крім `HitResolved`) в межах `CombatApplyMaxPerFrame` / `CombatApplyMaxMsPerFrame`, потім обмежено застосовує **low-priority** `HitResolved` (`HitResolvedApplyMaxPerFrame`). Черги не скидаються; навантаження розмивається по кадрах. Підсумок сесії: `[CoopNet][Summary]` та throttled `[CoopNet][Health]` (queue-pressure, budget-hit, seq-gap для authoritative типів).

**GHW world replication (v0.4.0+):** `Net/CoopWorldPacket.cs` — magic `GHW`, v1 header + up to **16** entities per UDP datagram (`netId`, position, hull + turret/gun world quaternions). Host enumerates `Unit` via `FindObjectsOfType`, resolves FNV `netId` with synthetic ids on collision, **excludes** the peer’s vehicle (`CoopRemoteState.RemoteUnitNetId` when the host has received a GHP snapshot). Client **reassembles** multi-part frames (same `hostSeq`, `partIndex`/`partCount`), then spawns/updates/destroys proxies in `ClientWorldProxyService` (mission token + `Playing` phase must match, same as GHP).

**Vehicle ownership + session (v0.3.0+):** `Net/CoopControlPacket.cs` — `COO\x01` control on the **same UDP socket** (16 B fixed for Switch/Hello/Welcome/Heartbeat). **Hello** (client→host, nonce) → **Welcome** (host→client, assigned `peerId` + nonce echo). Client uses `peerId==0` until Welcome (then resyncs vehicle claim). **Heartbeat** (~1.25 Hz while Playing) so the host can warn if the client goes quiet (~30 s). **Switch** + **OwnerSync** stay on the same wire byte for compatibility with 0.2.x; host may override `peerId` on Switch if it disagrees with the endpoint table. **Legacy:** if no Welcome within ~6 s, client assumes an older host (no Hello handler) and uses `peerId=2`. Parsers still accept `COO\x02` if it ever appears.

**Two-instance loopback test:** start **Host** instance first (`NetworkEnabled=true`, `NetworkRole=Host`, `NetworkBindPort=27015`). Second instance: `NetworkRole=Client`, `NetworkRemoteHost=127.0.0.1`, `NetworkRemotePort=27015`. Both need the mod DLL and **the same `MissionSceneName`** (from `MissionInitializer`) while **`Playing`** — live traffic uses **v3**; packets carry an FNV-1a token of that string; mismatch drops snapshots and clears the ghost.

**Packet:** `Net/CoopNetPacket.cs` — **v3 = 84 bytes** (`GHP\x03`): same header fields as v2 through `missionPhase` + 3-byte pad, then **world** `Quaternion` turret, **world** `Quaternion` gun, **`unitNetId`** (FNV-1a of `Unit.UniqueName`, fallback `gameObject.name` — same as ownership). **v2 = 48 bytes** (`GHP\x02`) and **v1 = 40 bytes** (`GHP\x01`) still accepted; for v1/v2 the mod sets `turret = gun = hull` and `unitNetId = 0` when applying. Applied snapshots land in `CoopRemoteState`.

**One PC, no second game / Steam:** run GHPC as **Host** (in `Playing` on e.g. `GT03_Native_Narrative`), then `python tools/coop_udp_fake_client.py` (defaults match that mission token) — see `[CoopNet] recv` if `LogNetworkReceive` is true.

## Phase 5 client simulation (`ClientSimulationGovernor`)

- Клієнт: **корекція позиції hull** і **синхронізація башти/ствола** для юнітів, якими не керує локальний гравець, через `FireControlSystem` / `AimablePlatform` (не «сирі» `Transform` там, де гра очікує API платформи).
- Плавність: буферизована інтерполяція цілей з **GHW**; AI екіпажу для чужих машин приглушується, коли це безпечно (`ClientSimulationSoftSuppressEnabled`).
- Prefs: див. таблицю вище (`ClientSimulation*`).

## Remote ghost (v0.1.7+)

- While **`Playing`** and **`CoopRemoteState.HasData`**, a proxy follows the last accepted snapshot (no colliders); prefs `ShowRemoteGhost`, `RemoteGhostSmoothing`, `RemoteGhostYOffset`.
- **v0.2.0+:** hierarchy **hull** (cyan capsule) + **turret** pivot + **barrel** (orange cube); locals are `Inverse(hull)*turretWorld` and `Inverse(turretWorld)*gunWorld` so the mesh aim matches decompiled `AimablePlatform` world rotations.
- First packet after a gap **snaps**; then **Lerp/Slerp** on position and on hull/turret/gun world rotations each `LateUpdate`.
