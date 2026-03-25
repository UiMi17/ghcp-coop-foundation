# Coop replication bible (GHPC)

Документ фіксує **карту decompile**, кандидатів під **Harmony**, чернетку **подій/стану** для host-authoritative коопу та **розширення протоколу** (GHW / world) поверх поточного **GHP v3** (позиція/сесія). Орієнтир для імплементації фаз 2–7 roadmap.

**Пін версії гри:** див. [GAME_BUILD.txt](../GAME_BUILD.txt) (на момент написання: `0.1.0-alpha+20260210.1`, Unity 2022.3.62f2). Після оновлення Steam — перегенерувати decompile і перевірити зміни в перелічених типах.

**Джерело коду гри:** локальний ILSpy-вивід у `artifacts/decompiled/` (gitignored). Шляхи нижче — відносно цього каталогу.

---

## 1. Цілі коопу (нагадування)

- **Не lockstep:** окремі клієнти не зобов’язані крокувати один фізичний кадр; ціль — **один авторитетний світ на хості** + узгоджені **наслідки** (події/стан) на клієнтах.
- **Поточний мод (0.3.x):** UDP **GHP** — сесія (Hello/Welcome/Heartbeat), **84 B** пакет позиції (hull/turret/barrel), `unitNetId`, ownership; клієнт — **ghost** (`RemoteGhostService`), не повноцінний `Unit`.
- **Фаза 3 (закрита в моду v0.5.x):** канал **GHC** — `WeaponFired` / `UnitStruck`, хостові Harmony-патчі, клієнт `NotifyStruck` + ammo key; wire id через **`CoopUnitWireRegistry`** (узгоджено з GHW при колізіях FNV).
- **Фаза 5 (coop truth):** окрім придушення локальної AI на клієнті (`ClientSimulationGovernor`), канал **GHC** розширено **host-authoritative snapshots** (`UnitState`, `CrewState`, `CompartmentState`) + подія **`HitResolved`** (низький пріоритет / телеметрія порівняно з truth snapshots). Деталі wire і тест-матриця: [Phase5CombatTruthTestMatrix.md](Phase5CombatTruthTestMatrix.md), реалізація в `src/GHPC.CoopFoundation/Net/`.
- **Наступні кроки після truth-sync:** місія/campaign, довготривала надійність (фаза 7). **Фаза 4** закрита у v0.6.x (ImpactFx + damage-state correction).

---

## 2. Карта збірки (namespaces → роль)

| Зона | Збірка / namespace (decompile) | Роль для коопу |
|------|--------------------------------|----------------|
| Юніт, ушкодження | `GHPC` (`Unit.cs`, …) | Ідентичність, хітбокси, `NotifyStruck`, стан корпусу |
| Світ / списки юнітів | `GHPC.World` (`SceneUnitsManager`, …) | Реєстрація юнітів, фракції, «хто в сцені» |
| Зброя / постріли | `GHPC.Weapons` | `IWeaponSystem.Fired`, `LiveRound`, `ShotInfo`, балістика |
| AI | `GHPC.AI` | `AIManager`, `UnitAI`, Gunner/Driver/Commander — **локальна симуляція**, на клієнті підлягає придушенню/перезапису |
| Транспорт / пошкодження корпусу | `GHPC.Vehicle` | `ChassisDamageManager`, події руйнування |
| Місія | `GHPC.Mission` | `MissionInitializer`, `DynamicMissionLauncher`, метадані сцени |
| Стан гри / кампанія | `GHPC.State` | `MissionStateController`, `CampaignSaveState`, ES3 `.buh` |
| Дані кампанії | `GHPC.Campaign.Data` | `CampaignSave`, `CampaignTeamState`, армії |
| Збереження | `ES3` + `ES3Types` | Серіалізація кампанії; користувацькі типи для `CampaignSave` тощо |

---

## 3. Ключові типи та файли (якорі)

### 3.1 Юніт і взаємодія зі світом

- **`GHPC/Unit.cs`** — центральний тип: `UniqueName`, події/методи ушкодження (`NotifyStruck`, `DamageStatuses`), посилання на `Chassis`, екіпаж, зброю. Реплікація «реального» юніта на клієнті вимагає стабільного **wire net id**: у моду **`CoopUnitWireRegistry`** (поверх FNV `CoopUnitNetId`, синтетика при колізіях) узгоджує GHW/GHC/GHP; плюс `SceneUnitsManager`.
- **`GHPC.World/SceneUnitsManager.cs`** — `AddSingleUnit`, списки `AllLiveUnitsByFaction` / `AllUnitsByFaction`; місія після ініціалізації реєструє всіх `Unit` у сцені. Важливо для **snapshot** і **interest management**.

### 3.2 Зброя та постріл

- **`GHPC.Weapons/IWeaponSystem.cs`** — контракт: `bool Fire(IUnit target = null)`, події `Fired` (`AmmoType`, `LiveRound`), `TriggerDown` / `TriggerUp`, `ReadyToFire`. **Harmony:** пост-префікс на реалізації `Fire` або підписка на `Fired` на хості для **CombatEvent**.
- **`GHPC.Weapons/WeaponSystem.cs`** — реалізація `Fire` (перевірки `AbleToFire`, швидкості, набою, мультиствол тощо).
- **`GHPC.Weapons/LiveRound.cs`** — симуляція снаряда в польоті (`DoUpdate`), пошкодження по колайдерах, `Shooter`, `ShotStory`, `NpcRound`, спавн візуалу. **Реплікація:** повні траєкторії по UDP зазвичай не вигідні; практичніше **подія пострілу** (точка/напрямок/тип набою/shooter net id) + **подія влучання** з хоста.
- **`GHPC.Weapons/ShotInfo.cs`** — дані про постріл (тип набою, джерело, ціль, кінетика тощо) для обчислення ушкоджень.
- **`GHPC.Weapons/LiveRoundBatchHandler.cs`** — `Update()` оновлює всі `LiveRound`; singleton-пошук через `FindObjectOfType`. Точка для розуміння життєвого циклу снарядів (не обов’язково хук, якщо події збираються з `Fire`/колізій).

### 3.3 Ушкодження корпусу

- **`GHPC.Vehicle/ChassisDamageManager.cs`** — керує пошкодженнями корпусу, підсистемами, візуалом руйнування. **Реплікація:** узгоджений **стан пошкоджень** (або стислі **події** «компонент знищено») з хоста.

### 3.4 AI

- **`GHPC.AI/AIManager.cs`** — `SuspendAllAI`, черга `IUnitAI`, `MaxUpdatesPerFrame`, ручний `BehaviorManager.UpdateInterval`. Можливий **глобальний вимикач** на клієнті (лише візуал + застосування мережевого стану).
- **`GHPC.AI/UnitAI.cs`** — координація Gunner/Driver/Commander, цілі, дистанції пострілу. На клієнті в ідеалі **не приймати рішень**, що змінюють світ; лише відтворювати позицію/стан з хоста.

### 3.5 Місія

- **`GHPC.Mission/MissionInitializer.cs`** — завантаження місії, `InitFlexMission`, реєстрація `Unit` у `SceneUnitsManager`, waypoints. **Кооп:** узгодження імені сцени / фази завантаження (у моді вже є перевірки місії в сесії).
- **`GHPC.State/MissionStateController.cs`** — `MissionState` (Planning / Playing / Finished), `EndPlanningPhase`, UMC vs non-UMC. **Кооп:** перехід фаз має бути **на хості**, клієнт синхронізується подією.

### 3.6 Кампанія та збереження

- **`GHPC.State/CampaignSaveState.cs`** — `ActiveSaveData`, `SaveToFile` / `LoadFromFile`, шлях `IOUtility.SAVED_DATA_PATH`, файл `*.buh`, ключ ES3 `GHPC_CAMPAIGN_SAVE`.
- **`GHPC.Campaign.Data/CampaignSave.cs`** — театр, дати, `PlayerTeamState` / `EnemyTeamState`, статистика місій, переможець тощо.
- **`GHPC.Mission/DynamicMissionLauncher.cs`** — старт кампанії instant action, робота з `CampaignSaveState` (створення/завантаження сейву).
- **`ES3Types/ES3UserType_CampaignSave*.cs`** — поля серіалізації; корисно для **фази 6** (синхронізація або «лише хост зберігає»).

---

## 4. Таблиця: що реплікувати (чернетка)

Пріоритет: **P0** — мінімум узгодженого бою; **P1** — візуал і AI; **P2** — кампанія/мета.

| Підсистема | Джерело правди | Що саме (стан / подія) | Пріоритет |
|------------|----------------|-------------------------|-----------|
| Трансформи юніта (hull/turret/gun) | Хост | Поза + кватерніони (вже GHP) | P0 |
| Ідентичність юніта в сесії | Хост | Стабільний `unitNetId`, мапінг на `Unit.UniqueName` / індекс | P0 |
| Реєстрація юнітів у сцені | Хост | Snapshot появи/зникнення (спавн/знищення) | P0 |
| Постріл | Хост | Подія: shooter net id, тип зброї/набою, напрямок/точка виходу, optional target id | P0 |
| Влучання / ушкодження | Хост | Подія: victim net id, hit point/normal, тип ушкодження, компонент; або дельта `DamageStatuses` | P0 |
| Снаряд у польоті | Хост (імпліцитно) | За потреби — скорочений «візуальний» спавн на клієнті без повної балістики | P1 |
| Chassis / підсистеми | Хост | Події руйнування або періодичний стислий стан | P1 |
| AI рішення | Хост | Не слати повну BT; слати **наслідки** (рух, таргет якщо потрібно для анімації) | P1 |
| Планування місії (карта) | Хост | `MissionState`, розміщення підрозділів (коли з’явиться вимога) | P2 |
| Кампанія | Хост | Один save на хості або синхронізований бінарний payload `.buh` / підмножина полів | P2 |

---

## 5. Harmony: кандидати (за пріоритетом дослідження)

1. **`WeaponSystem.Fire`** (або всі `IWeaponSystem` реалізації) — надійне місце для фіксації **факту пострілу** та параметрів.
2. **`Unit.NotifyStruck`** (або нижчий рівень колізій `LiveRound`) — **влучання** та застосування шкоди.
3. **`ChassisDamageManager`** (методи застосування пошкоджень / знищення компонентів) — узгоджений вигляд техніки.
4. **`LiveRound.DoUpdate` / колізії** — лише якщо потрібні деталі, яких немає в `ShotInfo`; ризик шуму та продуктивності.
5. **`AIManager` / `UnitAI.UpdateAI`** — придушення або no-op на клієнті (фаза 5).
6. **`MissionStateController.SetState` / `EndPlanningPhase`** — синхронізація фази місії.
7. **`CampaignSaveState.SaveToFile` / `LoadFromFile`** — інструменти для «тільки хост» або експорт стану.

Завжди перевіряти **кілька збірок** після патчу гри: сигнатури та приватні поля змінюються.

---

## 6. Чернетка протоколу: GHW / world (поверх GHP)

**Розділення каналів (логічно):**

- **GHP (існуючий):** короткий пакет позиції + сесія; залишається для частого оновлення «свого» юніта та heartbeat.
- **GHW (пропозиція):** менш часті або більші повідомлення: snapshot сутностей, ефекти, місія.
- **GHC (v0.5.0+; v0.6.2+ DamageState; Фаза 5+ combat truth):** окремий magic `GHC`, wire v1 — події **пострілу / влучання / ефектів / корекції пошкоджень / truth snapshots** (`Net/CoopCombatPacket.cs`). **Wire net id юнітів:** `CoopUnitWireRegistry` (узгоджено з GHW; FNV + синтетика при колізіях). Ключ набою — FNV-1a від `AmmoType.Name`; резолв на клієнті через `AmmoCodexScriptable` + runtime `LiveRound.SpallAmmoType`. **v0.5.3+ (хост, `WeaponFired`):** після `Fire()` ложе часто вже порожнє (`AmmoFeed` підписаний на `Fired`), тому тип набою для GHC береться з **`WeaponSystem._lastRound.Info`**, далі fallback `CurrentAmmoType`; **`target` net id** — з аргументу `Fire(IUnit)` або з **`InfoBroker.CurrentTarget.Owner`** при `Fire(null)` і наявній цілі. **v0.6.2+:** замість сирого spall-потоку використовується **throttled host-authoritative `DamageState`** (корпус: двигун/КПП/радіатор/гусениці + `UnitDestroyed`). **Фаза 5 (truth channel):** додаткові типи подій у тому ж потоці `hostCombatSeq`:
  - **`EventUnitState` (5)** — bitflags: destroyed/incapacitated/abandoned/cannot move/shoot (`CoopUnitStateSnapshot`).
  - **`EventCrewState` (6)** — маски присутності/статусів по місцях екіпажу (`CoopCrewStateSnapshot`).
  - **`EventHitResolved` (7)** — компактний «результат пострілу» з хоста; **емісія:** черга в кадрі (остання подія на `victimNetId` до `LateUpdate`) + `FlushPendingHitResolved` з лімітами **`HitResolvedHostMaxPerFrame`** і **`HitResolvedMaxPerSecond`** (без мікро-бурстів на кожен `NotifyStruck`). На клієнті — **низькопріоритетна** черга + coalescing по `victimNetId`. Для health-метрик seq цього типу **не трактується як мережевий gap** (телеметрія vs strict authoritative sequence).
  - **`EventCompartmentState` (8)** — критичний стан відсіків/flammables (вогонь, температура) (`CoopCompartmentStateSnapshot`).
  Емісія з хоста: `HostCombatBroadcast` + Harmony після `NotifyStruck`, `NotifyDestroyed`, переходів `NotifyIncapacitated` / `NotifyAbandoned` / `NotifyCannotMove` / `NotifyCannotShoot`. Застосування на клієнті: `ClientCombatApplier` — **дві черги** (truth snapshots і критичні події спочатку, потім slice для `HitResolved`), coalescing **останнього snapshot на `unitNetId`** для DamageState/UnitState/CrewState та окремий dedup Struck по victim. **Мінімальна довжина датаграми GHC** на прийомі прив’язана до найменшого валідного пакета (щоб короткі state-пакети не відсіювались). Бюджет: `CombatApplyMaxPerFrame`, `CombatApplyMaxMsPerFrame`, `HitResolvedApplyMaxPerFrame`. Зведення сесії: `[CoopNet][Summary]` (recv/applied, `destroyParityMismatch`, coalesced, budget hits).

- **GHC ImpactFx (v0.6.0+, Фаза 4 / підтрек A):** `eventType=3` — terrain bullet SFX з хоста (`ImpactSFXManager.PlayTerrainImpactSFX`), throttle на емісії; спільний `hostCombatSeq`; prefs `ImpactFxReplicationEnabled`, `LogImpactFx` (див. AGENTS).
- **GHC DamageState (v0.6.2+, Фаза 4 / підтрек B):** `eventType=4` — compact damage correction snapshot (`engine/transmission/radiator/left/right track HP%` + `UnitDestroyed`), change-detect + throttle на хості; prefs `DamageStateReplicationEnabled`, `LogDamageState`. *Розширення на довільні `DestructibleComponent` поза шасі — окремий майбутній крок, не обов’язково в цьому snapshot.*

**Версіонування**

- Окремий **wire version** для GHW (наприклад `ghwVersion : uint8`), незалежний від semver мода.
- У Hello/Welcome вже можна оголосити підтримувані версії (розширення існуючого handshake).

**Упорядкування**

- **`tick` або `hostSeq`:** монотонний лічильник хоста для подій (клієнт відкидає застарілі або ставить у чергу).
- Для **UDP** — idempotency за `(tick, eventType, smallId)` де доречно.

**Приклад категорій повідомлень (не фінальний бінарний layout)**

| Код | Назва | Зміст (чернетка) |
|-----|--------|------------------|
| W01 | EntitySnapshot | список: netId, prefab/key, поза, мінімальний бойовий стан |
| W02 | EntityDespawn | netId, причина |
| C01 | WeaponFired | shooterId, weaponSlot, ammoKey, origin, direction, t tick |
| C02 | HitApplied | victimId, damage flags, component id, optional impulse |
| C03 | UnitDestroyed | netId |
| M01 | MissionPhase | enum Planning/Playing/Finished + optional payload |
| F01 | EffectSpawn | тип ефекту, позиція, netId прив’язки |

Точний розмір полів і компресія — на етапі імплементації (фази 2–4); цей роздільник фіксує **контракт на рівні ідей**.

---

## 7. Зв’язок з кодом мода

Реалізація в репозиторії (не decompile):

- Транспорт UDP, пакети: `src/GHPC.CoopFoundation/Net/`
- Сесія: `CoopNetSession`, `CoopControlPacket`
- Ghost: `RemoteGhostService`, `CoopAimableSampler`
- Net id: `CoopUnitNetId`
- **Фаза 5 combat truth:** `CoopCombatPacket` (типи подій 5–8), `CoopUnitStateSnapshot`, `CoopCrewStateSnapshot`, `CoopCompartmentStateSnapshot`, `HostCombatBroadcast`, `ClientCombatApplier`; корекція візуалу/AI для чужих юнітів на клієнті — `ClientSimulationGovernor`; перевірні сценарії — [Phase5CombatTruthTestMatrix.md](Phase5CombatTruthTestMatrix.md).

Після додавання GHW варто явно розділити **парсери** та **черги** за типом пакета, щоб GHP залишався O(1) у гарячому шляху.

---

## 8. Відкриті питання (для наступних фаз)

- **Spall / вторинні ушкодження:** паритет корпусу та фінальних truth-станів юніта/екіпажу/відсіків намагається тримати **host snapshots** (`DamageState` + `UnitState` / `CrewState` / `CompartmentState`); локальний спал на клієнті може відрізнятись у деталях до застосування correction.
- **SFX audibility / distance:** `ImpactSFXManager` має внутрішні пороги чутності (terrain/vehicle) і speed-of-sound delay; `GHC recv ImpactFx` не гарантує, що звук має бути чутний з позиції камери клієнта.
- **UDP косметика:** при reorder/loss окремі ImpactFx можуть губитися; gameplay truth тримається через host-authoritative damage correction.
- Чи потрібна реплікація **інфантерії** / авіації окремим профілем (різні корені `Unit`)?
- Як узгоджувати **приціл/лінію прицілювання** клієнта з хостом без command mode (лише візуал)?
- Мінімальний набір полів для **EntitySnapshot**, щоб клієнт міг інстанціювати проксі без повного завантаження місії як на хості.
- Кампанія: **тільки хост зберігає** vs **повна синхронізація** `CampaignSave` (розмір, конфлікти).

---

## 9. Оновлення документа

1. Оновити `GAME_BUILD.txt` після зміни версії гри.
2. Перегенерувати `artifacts/decompiled/` (ILSpy / аналог).
3. Пройтися grep-ом по ключових типах (`Unit`, `WeaponSystem`, `LiveRound`, `CampaignSaveState`).
4. Оновити таблиці §3–§6 при значних змінах API (у т.ч. GHC типів подій і prefs Фази 5).

Окремий чеклист Фази 5: [Phase5CombatTruthTestMatrix.md](Phase5CombatTruthTestMatrix.md).
