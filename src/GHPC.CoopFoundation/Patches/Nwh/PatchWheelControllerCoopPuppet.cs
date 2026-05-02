using System;
using System.Collections.Generic;
using System.Reflection;
using GHPC.CoopFoundation.Networking;
using GHPC.CoopFoundation.Networking.NwhPuppet;
using GHPC.CoopFoundation.Networking.Transport;
using HarmonyLib;
using NWH.WheelController3D;
using UnityEngine;

namespace GHPC.CoopFoundation.Patches.Nwh;

internal static class WheelControllerCoopPuppetShared
{
    private sealed class QueueDrainOps
    {
        internal readonly PropertyInfo CountProp;

        internal readonly MethodInfo DequeueMethod;

        internal QueueDrainOps(PropertyInfo countProp, MethodInfo dequeueMethod)
        {
            CountProp = countProp;
            DequeueMethod = dequeueMethod;
        }
    }

    /// <summary>Null value means queue type lacks Count/Dequeue (cached negative).</summary>
    private static readonly Dictionary<Type, QueueDrainOps?> QueueDrainCache = new();

    private static readonly Dictionary<Type, FieldInfo[]> WriteStructFieldsCache = new();

    internal static readonly FieldInfo? RbVelField = AccessTools.Field(typeof(WheelController), "rbVel");
    internal static readonly FieldInfo? RbAngVelField = AccessTools.Field(typeof(WheelController), "rbAngVel");
    internal static readonly FieldInfo? ParentRbField = AccessTools.Field(typeof(WheelController), "parentRigidbody");
    internal static readonly FieldInfo? ForceQueueField = AccessTools.Field(typeof(WheelController), "forceQueue");
    internal static readonly FieldInfo? PositionQueueField = AccessTools.Field(typeof(WheelController), "positionWriteQueue");
    internal static readonly FieldInfo? RotationQueueField = AccessTools.Field(typeof(WheelController), "rotationWriteQueue");

    internal static bool IsCoopClientPuppetWheel(WheelController wc)
    {
        return CoopPuppetWheelRegistry.IsRegisteredPuppetWheel(wc);
    }

    internal static bool TryGetPuppetVelocities(WheelController wc, out Vector3 linear, out Vector3 angular)
    {
        linear = angular = default;
        if (!CoopPuppetWheelRegistry.TryGetPuppetNetId(wc, out uint netId))
            return false;
        return CoopNwhPuppetContext.TryGetVelocitiesForNetId(netId, out linear, out angular);
    }

    private static bool TryGetQueueDrainOps(object queue, out QueueDrainOps? ops)
    {
        Type qt = queue.GetType();
        if (QueueDrainCache.TryGetValue(qt, out QueueDrainOps? cached))
        {
            ops = cached;
            return cached != null;
        }

        PropertyInfo? countProp = qt.GetProperty("Count");
        MethodInfo? deq = qt.GetMethod("Dequeue", Type.EmptyTypes);
        if (countProp == null || deq == null)
        {
            ops = null;
            QueueDrainCache[qt] = null;
            return false;
        }

        ops = new QueueDrainOps(countProp, deq);
        QueueDrainCache[qt] = ops;
        return true;
    }

    internal static void DrainQueue(FieldInfo? queueField, WheelController instance)
    {
        if (queueField?.GetValue(instance) is not { } q)
            return;
        if (!TryGetQueueDrainOps(q, out QueueDrainOps? ops) || ops == null)
            return;
        while ((int)ops.CountProp.GetValue(q)! > 0)
            ops.DequeueMethod.Invoke(q, null);
    }

    internal static void ApplyPositionRotationWrites(WheelController instance)
    {
        ProcessWriteQueue(PositionQueueField, instance, ApplyPositionWrite);
        ProcessWriteQueue(RotationQueueField, instance, ApplyRotationWrite);
    }

    private static void ProcessWriteQueue(FieldInfo? queueField, WheelController instance, Action<object> apply)
    {
        if (queueField?.GetValue(instance) is not { } q)
            return;
        if (!TryGetQueueDrainOps(q, out QueueDrainOps? ops) || ops == null)
            return;
        while ((int)ops.CountProp.GetValue(q)! > 0)
        {
            object item = ops.DequeueMethod.Invoke(q, null)!;
            apply(item);
        }
    }

    private static FieldInfo[] GetCachedInstanceFields(Type t)
    {
        if (!WriteStructFieldsCache.TryGetValue(t, out FieldInfo[]? fields))
        {
            fields = t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            WriteStructFieldsCache[t] = fields;
        }

        return fields;
    }

    private static void ApplyPositionWrite(object write)
    {
        Transform? tr = null;
        Vector3 pos = default;
        bool havePos = false;
        foreach (FieldInfo f in GetCachedInstanceFields(write.GetType()))
        {
            if (f.FieldType == typeof(Transform))
                tr = f.GetValue(write) as Transform;
            else if (f.FieldType == typeof(Vector3))
            {
                pos = (Vector3)f.GetValue(write)!;
                havePos = true;
            }
        }

        if (tr != null && havePos)
            tr.position = pos;
    }

    private static void ApplyRotationWrite(object write)
    {
        Transform? tr = null;
        Quaternion rot = default;
        bool haveRot = false;
        foreach (FieldInfo f in GetCachedInstanceFields(write.GetType()))
        {
            if (f.FieldType == typeof(Transform))
                tr = f.GetValue(write) as Transform;
            else if (f.FieldType == typeof(Quaternion))
            {
                rot = (Quaternion)f.GetValue(write)!;
                haveRot = true;
            }
        }

        if (tr != null && haveRot)
            tr.rotation = rot;
    }
}

[HarmonyPatch(typeof(WheelController), nameof(WheelController.CaptureThreadInputData))]
internal static class PatchWheelControllerCaptureThreadCoopPuppet
{
    [HarmonyPostfix]
    private static void Postfix(WheelController __instance)
    {
        if (!WheelControllerCoopPuppetShared.TryGetPuppetVelocities(__instance, out Vector3 lin, out Vector3 ang))
            return;
        WheelControllerCoopPuppetShared.RbVelField?.SetValue(__instance, lin);
        WheelControllerCoopPuppetShared.RbAngVelField?.SetValue(__instance, ang);
    }
}

[HarmonyPatch(typeof(WheelController), "UpdateForces")]
internal static class PatchWheelControllerUpdateForcesCoopPuppet
{
    [HarmonyPrefix]
    private static bool Prefix(WheelController __instance)
    {
        return !WheelControllerCoopPuppetShared.IsCoopClientPuppetWheel(__instance);
    }
}

[HarmonyPatch(typeof(WheelController), nameof(WheelController.ApplyThreadOutputData))]
internal static class PatchWheelControllerApplyThreadCoopPuppet
{
    [HarmonyPrefix]
    private static bool Prefix(WheelController __instance)
    {
        if (!WheelControllerCoopPuppetShared.IsCoopClientPuppetWheel(__instance))
            return true;
        WheelControllerCoopPuppetShared.DrainQueue(WheelControllerCoopPuppetShared.ForceQueueField, __instance);
        WheelControllerCoopPuppetShared.ApplyPositionRotationWrites(__instance);
        return false;
    }
}
