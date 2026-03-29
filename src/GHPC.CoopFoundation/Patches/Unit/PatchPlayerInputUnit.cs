using GHPC;
using GHPC.Player;
using HarmonyLib;
using MelonLoader;

namespace GHPC.CoopFoundation.Patches;

/// <summary>
///     Vehicle swaps: <see cref="PlayerInput.SetPlayerUnit" />. Default spawn uses <see cref="PlayerInput.SetDefaultUnit" />.
///     Coop: block entering units held by another peer (net id from <see cref="Unit.UniqueName" />).
/// </summary>
[HarmonyPatch(typeof(PlayerInput), nameof(PlayerInput.SetPlayerUnit))]
internal static class PatchPlayerInputSetPlayerUnit
{
    [HarmonyPrefix]
    private static bool Prefix(PlayerInput __instance, Unit? newUnit, ref object? __state)
    {
        __state = __instance.CurrentPlayerUnit;
        if (newUnit == null)
        {
            CoopSessionState.SetControlledUnit(null);
            if (HookDiagnostics.ShouldLog)
                MelonLogger.Msg("[CoopDiag] PlayerInput.SetPlayerUnit → null");
            return true;
        }

        if (CoopVehicleOwnership.ShouldEnforce())
        {
            uint nid = CoopUnitWireRegistry.GetWireId(newUnit);
            if (!CoopVehicleOwnership.CanLocalEnter(nid))
            {
                CoopVehicleOwnership.LogEnterBlocked(nid, newUnit);
                return false;
            }
        }

        CoopSessionState.SetControlledUnit(newUnit);
        if (HookDiagnostics.ShouldLog)
            MelonLogger.Msg("[CoopDiag] PlayerInput.SetPlayerUnit → " + FormatUnit(newUnit));
        return true;
    }

    [HarmonyPostfix]
    private static void Postfix(Unit? newUnit, object? __state)
    {
        if (newUnit == null)
            return;
        CoopVehicleOwnership.NotifyLocalUnitChanged(__state as Unit, newUnit);
    }

    private static string FormatUnit(Unit u) => $"{u.FriendlyName} (go={u.gameObject.name})";
}

[HarmonyPatch(typeof(PlayerInput), nameof(PlayerInput.SetDefaultUnit))]
internal static class PatchPlayerInputSetDefaultUnit
{
    [HarmonyPrefix]
    private static bool Prefix(PlayerInput __instance, IUnit? unit, ref object? __state)
    {
        __state = __instance.CurrentPlayerUnit;
        if (unit is not Unit u)
        {
            CoopSessionState.SetControlledUnit(null);
            if (HookDiagnostics.ShouldLog)
                MelonLogger.Msg("[CoopDiag] PlayerInput.SetDefaultUnit → " + (unit == null ? "null" : unit.GetType().Name));
            return true;
        }

        if (CoopVehicleOwnership.ShouldEnforce())
        {
            uint nid = CoopUnitWireRegistry.GetWireId(u);
            if (!CoopVehicleOwnership.CanLocalEnter(nid))
            {
                CoopVehicleOwnership.LogEnterBlocked(nid, u);
                return false;
            }
        }

        CoopSessionState.SetControlledUnit(u);
        if (HookDiagnostics.ShouldLog)
            MelonLogger.Msg($"[CoopDiag] PlayerInput.SetDefaultUnit → {u.FriendlyName} (go={u.gameObject.name})");
        return true;
    }

    [HarmonyPostfix]
    private static void Postfix(PlayerInput __instance, IUnit? unit, object? __state)
    {
        if (unit is not Unit u)
            return;
        CoopVehicleOwnership.NotifyLocalUnitChanged(__state as Unit, u);
        CoopCustomizeRowSpawnApply.TryApplyAfterSetDefaultUnit(__instance, u);
    }
}
