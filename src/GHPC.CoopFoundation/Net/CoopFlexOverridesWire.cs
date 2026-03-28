using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using GHPC.Mission;
using GHPC.Weaponry;
using MelonLoader;
using UnityEngine;

namespace GHPC.CoopFoundation.Net;

/// <summary>Binary flex overrides for <see cref="DynamicMissionLauncher.AllFlexOverrides" /> (wire v1).</summary>
internal static class CoopFlexOverridesWire
{
    private const byte SchemaV1 = 1;

    private static readonly FieldInfo? UnitListField =
        typeof(DynamicMissionMetadataOverrides).GetField("_unitReplacements", BindingFlags.Instance | BindingFlags.NonPublic);

    public static byte[] Serialize(string flexMissionName, DynamicMissionMetadataOverrides? o)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        bw.Write(SchemaV1);
        WriteUtf8Short(bw, flexMissionName ?? "");
        if (o == null)
        {
            WriteUtf8Short(bw, "");
            WriteUtf8Short(bw, "");
            bw.Write((ushort)0);
            bw.Write((ushort)0);
            bw.Write((ushort)0);
            return ms.ToArray();
        }

        WriteUtf8Short(bw, o.FriendlyInfantryArmyOverride ?? "");
        WriteUtf8Short(bw, o.EnemyInfantryArmyOverride ?? "");
        WriteUnits(bw, o);
        WriteAmmoList(bw, o.FriendlyAmmoOverrides);
        WriteAmmoList(bw, o.EnemyAmmoOverrides);
        return ms.ToArray();
    }

    public static bool TryApplyToGame(byte[] blob, out string flexMissionName)
    {
        flexMissionName = "";
        try
        {
            using var ms = new MemoryStream(blob, writable: false);
            using var br = new BinaryReader(ms, Encoding.UTF8, leaveOpen: true);
            if (ms.Length < 1 || br.ReadByte() != SchemaV1)
                return false;
            flexMissionName = ReadUtf8Short(br);
            if (string.IsNullOrEmpty(flexMissionName))
                return false;
            string friendlyInf = ReadUtf8Short(br);
            string enemyInf = ReadUtf8Short(br);
            int unitCount = br.ReadUInt16();
            if (unitCount < 0 || unitCount > 512)
                return false;
            var units = new List<(bool friendly, int uClass, int variant, string key)>(unitCount);
            for (int i = 0; i < unitCount; i++)
            {
                bool friendly = br.ReadByte() != 0;
                int uClass = br.ReadInt32();
                int variant = br.ReadInt32();
                string key = ReadUtf8Short(br);
                if (string.IsNullOrEmpty(key))
                    return false;
                units.Add((friendly, uClass, variant, key));
            }

            var friendlyAmmo = ReadAmmoEntries(br, br.ReadUInt16());
            var enemyAmmo = ReadAmmoEntries(br, br.ReadUInt16());

            if (units.Count == 0
                && string.IsNullOrEmpty(friendlyInf)
                && string.IsNullOrEmpty(enemyInf)
                && friendlyAmmo.Count == 0
                && enemyAmmo.Count == 0)
            {
                DynamicMissionLauncher.ClearFlexOverrides(flexMissionName);
                return true;
            }

            var created = new DynamicMissionMetadataOverrides
            {
                MissionName = flexMissionName,
                FriendlyInfantryArmyOverride = friendlyInf,
                EnemyInfantryArmyOverride = enemyInf
            };

            foreach ((bool friendly, int uClass, int variant, string key) in units)
            {
                if (!Enum.IsDefined(typeof(UnitClass), uClass))
                    continue;
                var uc = (UnitClass)uClass;
                created.AddUnitReplacement(friendly, uc, variant, key, overwrite: true);
            }

            foreach ((string name, int idx) in friendlyAmmo)
            {
                AmmoLogisticsScriptable? so = TryResolveAmmoLogisticsByName(name);
                if (so == null)
                {
                    MelonLogger.Warning($"[CoopNet][Lobby] Skip friendly ammo '{name}' (not resolved)");
                    continue;
                }

                created.AddAmmoReplacement(isFriendly: true, so, idx);
            }

            foreach ((string name, int idx) in enemyAmmo)
            {
                AmmoLogisticsScriptable? so = TryResolveAmmoLogisticsByName(name);
                if (so == null)
                {
                    MelonLogger.Warning($"[CoopNet][Lobby] Skip enemy ammo '{name}' (not resolved)");
                    continue;
                }

                created.AddAmmoReplacement(isFriendly: false, so, idx);
            }

            DynamicMissionLauncher.SaveFlexOverrides(created);
            return true;
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"[CoopNet][Lobby] Flex wire apply failed: {ex.Message}");
            return false;
        }
    }

    private static List<(string name, int idx)> ReadAmmoEntries(BinaryReader br, int count)
    {
        var list = new List<(string, int)>(count);
        for (int i = 0; i < count; i++)
        {
            string name = ReadUtf8Short(br);
            int idx = br.ReadInt32();
            if (!string.IsNullOrEmpty(name))
                list.Add((name, idx));
        }

        return list;
    }

    private static void WriteUnits(BinaryWriter bw, DynamicMissionMetadataOverrides o)
    {
        var rows = new List<(bool f, int uc, int v, string k)>(16);
        if (UnitListField?.GetValue(o) is IList list && list.Count > 0)
        {
            object? sample = list[0];
            Type rowType = sample?.GetType() ?? typeof(object);
            FieldInfo? fFriendly = rowType.GetField("Friendly");
            FieldInfo? fClass = rowType.GetField("UClass");
            FieldInfo? fVar = rowType.GetField("VariantIndex");
            FieldInfo? fKey = rowType.GetField("NewUnitKey");
            if (fFriendly != null && fClass != null && fVar != null && fKey != null)
            {
                foreach (object? item in list)
                {
                    if (item == null)
                        continue;
                    bool friendly = (bool)(fFriendly.GetValue(item) ?? false);
                    int uClass = (int)(fClass.GetValue(item) ?? 0);
                    int variant = (int)(fVar.GetValue(item) ?? 0);
                    string key = (fKey.GetValue(item) as string) ?? "";
                    if (string.IsNullOrEmpty(key))
                        continue;
                    rows.Add((friendly, uClass, variant, key));
                }
            }
        }

        if (rows.Count > ushort.MaxValue)
            rows.RemoveRange(ushort.MaxValue, rows.Count - ushort.MaxValue);
        bw.Write((ushort)rows.Count);
        for (int i = 0; i < rows.Count; i++)
        {
            (bool f, int uc, int v, string k) = rows[i];
            bw.Write((byte)(f ? 1 : 0));
            bw.Write(uc);
            bw.Write(v);
            WriteUtf8Short(bw, k);
        }
    }

    private static void WriteAmmoList(BinaryWriter bw, List<DynamicMissionAmmoAdjustment>? list)
    {
        var entries = new List<(string name, int idx)>(8);
        if (list != null)
        {
            for (int i = 0; i < list.Count; i++)
            {
                DynamicMissionAmmoAdjustment a = list[i];
                if (a?.AmmoSet == null)
                    continue;
                string name = a.AmmoSet.name;
                if (string.IsNullOrEmpty(name))
                    continue;
                entries.Add((name, a.SelectedIndex));
            }
        }

        if (entries.Count > ushort.MaxValue)
            entries.RemoveRange(ushort.MaxValue, entries.Count - ushort.MaxValue);
        bw.Write((ushort)entries.Count);
        for (int i = 0; i < entries.Count; i++)
        {
            WriteUtf8Short(bw, entries[i].name);
            bw.Write(entries[i].idx);
        }
    }

    private static void WriteUtf8Short(BinaryWriter bw, string s)
    {
        byte[] b = Encoding.UTF8.GetBytes(s ?? "");
        if (b.Length > ushort.MaxValue)
            throw new InvalidOperationException("string too long");
        bw.Write((ushort)b.Length);
        if (b.Length > 0)
            bw.Write(b);
    }

    private static string ReadUtf8Short(BinaryReader br)
    {
        int len = br.ReadUInt16();
        if (len < 0 || len > 65535)
            return "";
        if (len == 0)
            return "";
        byte[] b = br.ReadBytes(len);
        return Encoding.UTF8.GetString(b);
    }

    private static AmmoLogisticsScriptable? TryResolveAmmoLogisticsByName(string name)
    {
        if (string.IsNullOrEmpty(name))
            return null;
        AmmoLogisticsScriptable[] all = Resources.FindObjectsOfTypeAll<AmmoLogisticsScriptable>();
        for (int i = 0; i < all.Length; i++)
        {
            AmmoLogisticsScriptable? s = all[i];
            if (s != null && s.name == name)
                return s;
        }

        return null;
    }
}
