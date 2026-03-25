using System.Collections.Generic;
using MelonLoader;
using UnityEngine;

namespace GHPC.CoopFoundation.Net;

internal static class AimOverwriteProbe
{
    private struct ExpectedRotation
    {
        public int Frame;
        public Quaternion Rotation;
        public string Tag;
        public uint NetId;
    }

    private static readonly Dictionary<int, ExpectedRotation> ExpectedByTransformId = new();
    private static readonly Dictionary<int, int> LastWarnFrameByTransformId = new();
    private static bool _enabled;

    internal static void Configure(bool enabled)
    {
        _enabled = enabled;
        if (!enabled)
        {
            ExpectedByTransformId.Clear();
            LastWarnFrameByTransformId.Clear();
        }
    }

    internal static void RecordExpected(uint netId, Transform? tf, Quaternion rotation, string tag)
    {
        if (!_enabled || tf == null)
            return;
        int id = tf.GetInstanceID();
        ExpectedByTransformId[id] = new ExpectedRotation
        {
            Frame = Time.frameCount,
            Rotation = rotation,
            Tag = tag,
            NetId = netId
        };
    }

    internal static void CheckOverwrite(string writer, Transform? tf)
    {
        if (!_enabled || tf == null)
            return;
        int id = tf.GetInstanceID();
        if (!ExpectedByTransformId.TryGetValue(id, out ExpectedRotation expected))
            return;
        if (expected.Frame != Time.frameCount)
            return;

        float angle = Quaternion.Angle(tf.rotation, expected.Rotation);
        if (angle < 0.75f)
            return;

        if (LastWarnFrameByTransformId.TryGetValue(id, out int lastWarnFrame) &&
            lastWarnFrame == Time.frameCount)
            return;
        LastWarnFrameByTransformId[id] = Time.frameCount;

        string tfName = tf.name ?? "<unnamed>";
        string parentName = tf.parent != null ? tf.parent.name : "<root>";
        MelonLogger.Warning(
            $"[CoopAimTrace] overwrite writer={writer} netId={expected.NetId} tag={expected.Tag} frame={Time.frameCount} tf={tfName} parent={parentName} angle={angle:F2}");
    }
}
