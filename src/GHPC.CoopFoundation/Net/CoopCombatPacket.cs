using System;
using UnityEngine;

namespace GHPC.CoopFoundation.Net;

internal static class CoopCombatPacket
{
    public const byte Magic0 = (byte)'G';

    public const byte Magic1 = (byte)'H';

    public const byte Magic2 = (byte)'C';

    public const byte WireVersion1 = 1;

    public const byte EventWeaponFired = 1;

    public const byte EventUnitStruck = 2;

    /// <summary>Phase 4: cosmetic / SFX sync (no HP). Kinds: <see cref="CoopImpactFxKind" />.</summary>
    public const byte EventImpactFx = 3;

    /// <summary>Phase 4B: host-authoritative compact damage correction (spall parity without per-hit flood).</summary>
    public const byte EventDamageState = 4;

    /// <summary>Phase 5 P0: host-authoritative unit truth flags.</summary>
    public const byte EventUnitState = 5;

    /// <summary>Phase 5 P0: host-authoritative crew truth snapshot.</summary>
    public const byte EventCrewState = 6;

    /// <summary>Phase 5 P1: authoritative hit outcome marker.</summary>
    public const byte EventHitResolved = 7;

    /// <summary>Phase 5 P1: compact flammables/compartment critical state.</summary>
    public const byte EventCompartmentState = 8;

    public const int HeaderLength = 16;

    public const int FiredPayloadLength = 36;

    public const int StruckPayloadLength = 28;

    /// <summary>u16 kind + u16 pad + pos + normal (armor kinds: <c>x</c> = thickness) + ammoKey (0 = pen perspective only) + victimNetId + u8 flags + 3 pad.</summary>
    public const int ImpactFxPayloadLength = 40;

    /// <summary>victimNetId + unitDestroyed + 5x component HP% bytes + 2 bytes pad.</summary>
    public const int DamageStatePayloadLength = 12;

    /// <summary>unitNetId + flags + 3-byte pad.</summary>
    public const int UnitStatePayloadLength = 8;

    /// <summary>unitNetId + present/dead/incap/evac/suspended bitmasks + 3-byte pad.</summary>
    public const int CrewStatePayloadLength = 12;

    /// <summary>shotId + victimNetId + shooterNetId + ammoKey + impact + hitKind + flags + 2-byte pad.</summary>
    public const int HitResolvedPayloadLength = 32;

    /// <summary>unitNetId + firePresent + unsecuredFire + combinedFlameHeightPct + internalTempPct + 4-byte pad.</summary>
    public const int CompartmentStatePayloadLength = 12;

    public const int FiredTotalLength = HeaderLength + FiredPayloadLength;

    public const int StruckTotalLength = HeaderLength + StruckPayloadLength;

    public const int ImpactFxTotalLength = HeaderLength + ImpactFxPayloadLength;

    public const int DamageStateTotalLength = HeaderLength + DamageStatePayloadLength;

    public const int UnitStateTotalLength = HeaderLength + UnitStatePayloadLength;

    public const int CrewStateTotalLength = HeaderLength + CrewStatePayloadLength;

    public const int HitResolvedTotalLength = HeaderLength + HitResolvedPayloadLength;

    public const int CompartmentStateTotalLength = HeaderLength + CompartmentStatePayloadLength;

    /// <summary>Smallest valid GHC datagram (UnitState).</summary>
    public static int MinCombatDatagramLength => UnitStateTotalLength;

    public static bool IsCoopCombat(byte[] data, int length) =>
        data != null
        && length >= MinCombatDatagramLength
        && data[0] == Magic0
        && data[1] == Magic1
        && data[2] == Magic2;

    public static int WriteFired(
        byte[] buffer,
        uint hostCombatSeq,
        uint missionToken,
        byte missionPhase,
        uint shooterNetId,
        uint ammoKey,
        Vector3 muzzle,
        Vector3 direction,
        uint targetNetId)
    {
        if (buffer.Length < FiredTotalLength)
            throw new ArgumentException("buffer too small", nameof(buffer));
        WriteHeader(buffer, hostCombatSeq, missionToken, missionPhase, EventWeaponFired);
        int o = HeaderLength;
        o = WriteU32(buffer, o, shooterNetId);
        o = WriteU32(buffer, o, ammoKey);
        o = WriteVec3(buffer, o, muzzle);
        o = WriteVec3(buffer, o, direction);
        WriteU32(buffer, o, targetNetId);
        return FiredTotalLength;
    }

    public static int WriteStruck(
        byte[] buffer,
        uint hostCombatSeq,
        uint missionToken,
        byte missionPhase,
        uint victimNetId,
        uint shooterNetId,
        uint ammoKey,
        Vector3 impact,
        bool isSpall)
    {
        if (buffer.Length < StruckTotalLength)
            throw new ArgumentException("buffer too small", nameof(buffer));
        WriteHeader(buffer, hostCombatSeq, missionToken, missionPhase, EventUnitStruck);
        int o = HeaderLength;
        o = WriteU32(buffer, o, victimNetId);
        o = WriteU32(buffer, o, shooterNetId);
        o = WriteU32(buffer, o, ammoKey);
        o = WriteVec3(buffer, o, impact);
        byte flags = (byte)(isSpall ? 1 : 0);
        buffer[o++] = flags;
        buffer[o++] = 0;
        buffer[o++] = 0;
        buffer[o] = 0;
        return StruckTotalLength;
    }

    public const byte ImpactFxFlagTree = 1;

    public const byte ImpactFxFlagSpallHint = 2;

    public static int WriteImpactFx(
        byte[] buffer,
        uint hostCombatSeq,
        uint missionToken,
        byte missionPhase,
        ushort effectKind,
        Vector3 worldPos,
        Vector3 normal,
        uint ammoKey,
        uint victimNetId,
        byte flags)
    {
        if (buffer.Length < ImpactFxTotalLength)
            throw new ArgumentException("buffer too small", nameof(buffer));
        WriteHeader(buffer, hostCombatSeq, missionToken, missionPhase, EventImpactFx);
        int o = HeaderLength;
        o = WriteU16(buffer, o, effectKind);
        o = WriteU16(buffer, o, 0);
        o = WriteVec3(buffer, o, worldPos);
        o = WriteVec3(buffer, o, normal);
        o = WriteU32(buffer, o, ammoKey);
        o = WriteU32(buffer, o, victimNetId);
        buffer[o++] = flags;
        buffer[o++] = 0;
        buffer[o++] = 0;
        buffer[o] = 0;
        return ImpactFxTotalLength;
    }

    public static int WriteDamageState(
        byte[] buffer,
        uint hostCombatSeq,
        uint missionToken,
        byte missionPhase,
        uint victimNetId,
        in CoopDamageStateSnapshot state)
    {
        if (buffer.Length < DamageStateTotalLength)
            throw new ArgumentException("buffer too small", nameof(buffer));
        WriteHeader(buffer, hostCombatSeq, missionToken, missionPhase, EventDamageState);
        int o = HeaderLength;
        o = WriteU32(buffer, o, victimNetId);
        buffer[o++] = state.UnitDestroyed ? (byte)1 : (byte)0;
        buffer[o++] = state.EngineHpPct;
        buffer[o++] = state.TransmissionHpPct;
        buffer[o++] = state.RadiatorHpPct;
        buffer[o++] = state.LeftTrackHpPct;
        buffer[o++] = state.RightTrackHpPct;
        buffer[o++] = 0;
        buffer[o] = 0;
        return DamageStateTotalLength;
    }

    public static int WriteUnitState(
        byte[] buffer,
        uint hostCombatSeq,
        uint missionToken,
        byte missionPhase,
        uint unitNetId,
        in CoopUnitStateSnapshot state)
    {
        if (buffer.Length < UnitStateTotalLength)
            throw new ArgumentException("buffer too small", nameof(buffer));
        WriteHeader(buffer, hostCombatSeq, missionToken, missionPhase, EventUnitState);
        int o = HeaderLength;
        o = WriteU32(buffer, o, unitNetId);
        buffer[o++] = state.Flags;
        buffer[o++] = 0;
        buffer[o++] = 0;
        buffer[o] = 0;
        return UnitStateTotalLength;
    }

    public static int WriteCrewState(
        byte[] buffer,
        uint hostCombatSeq,
        uint missionToken,
        byte missionPhase,
        uint unitNetId,
        in CoopCrewStateSnapshot state)
    {
        if (buffer.Length < CrewStateTotalLength)
            throw new ArgumentException("buffer too small", nameof(buffer));
        WriteHeader(buffer, hostCombatSeq, missionToken, missionPhase, EventCrewState);
        int o = HeaderLength;
        o = WriteU32(buffer, o, unitNetId);
        buffer[o++] = state.PresentMask;
        buffer[o++] = state.DeadMask;
        buffer[o++] = state.IncapacitatedMask;
        buffer[o++] = state.EvacuatedMask;
        buffer[o++] = state.SuspendedMask;
        buffer[o++] = 0;
        buffer[o++] = 0;
        buffer[o] = 0;
        return CrewStateTotalLength;
    }

    public static int WriteHitResolved(
        byte[] buffer,
        uint hostCombatSeq,
        uint missionToken,
        byte missionPhase,
        uint shotId,
        uint victimNetId,
        uint shooterNetId,
        uint ammoKey,
        Vector3 impact,
        byte hitKind,
        byte flags)
    {
        if (buffer.Length < HitResolvedTotalLength)
            throw new ArgumentException("buffer too small", nameof(buffer));
        WriteHeader(buffer, hostCombatSeq, missionToken, missionPhase, EventHitResolved);
        int o = HeaderLength;
        o = WriteU32(buffer, o, shotId);
        o = WriteU32(buffer, o, victimNetId);
        o = WriteU32(buffer, o, shooterNetId);
        o = WriteU32(buffer, o, ammoKey);
        o = WriteVec3(buffer, o, impact);
        buffer[o++] = hitKind;
        buffer[o++] = flags;
        buffer[o++] = 0;
        buffer[o] = 0;
        return HitResolvedTotalLength;
    }

    public static int WriteCompartmentState(
        byte[] buffer,
        uint hostCombatSeq,
        uint missionToken,
        byte missionPhase,
        uint unitNetId,
        in CoopCompartmentStateSnapshot state)
    {
        if (buffer.Length < CompartmentStateTotalLength)
            throw new ArgumentException("buffer too small", nameof(buffer));
        WriteHeader(buffer, hostCombatSeq, missionToken, missionPhase, EventCompartmentState);
        int o = HeaderLength;
        o = WriteU32(buffer, o, unitNetId);
        buffer[o++] = state.FirePresent ? (byte)1 : (byte)0;
        buffer[o++] = state.UnsecuredFirePresent ? (byte)1 : (byte)0;
        buffer[o++] = state.CombinedFlameHeightPct;
        buffer[o++] = state.InternalTemperaturePct;
        buffer[o++] = 0;
        buffer[o++] = 0;
        buffer[o++] = 0;
        buffer[o] = 0;
        return CompartmentStateTotalLength;
    }

    public static bool TryRead(byte[] data, int length, out byte eventType, out uint hostCombatSeq, out uint missionToken, out byte missionPhase)
    {
        eventType = 0;
        hostCombatSeq = 0;
        missionToken = 0;
        missionPhase = 0;
        if (!IsCoopCombat(data, length) || data[3] != WireVersion1)
            return false;
        int o = 4;
        hostCombatSeq = ReadU32(data, ref o);
        missionToken = ReadU32(data, ref o);
        missionPhase = data[o++];
        eventType = data[o++];
        o += 2;
        return true;
    }

    public static bool TryReadFired(byte[] data, int length, out uint hostCombatSeq, out uint missionToken, out byte missionPhase, out uint shooterNetId, out uint ammoKey, out Vector3 muzzle, out Vector3 direction, out uint targetNetId)
    {
        shooterNetId = 0;
        ammoKey = 0;
        muzzle = default;
        direction = default;
        targetNetId = 0;
        if (!TryRead(data, length, out byte et, out hostCombatSeq, out missionToken, out missionPhase))
            return false;
        if (et != EventWeaponFired || length < FiredTotalLength)
            return false;
        int o = HeaderLength;
        shooterNetId = ReadU32(data, ref o);
        ammoKey = ReadU32(data, ref o);
        muzzle = ReadVec3(data, ref o);
        direction = ReadVec3(data, ref o);
        targetNetId = ReadU32(data, ref o);
        return true;
    }

    public static bool TryReadStruck(byte[] data, int length, out uint hostCombatSeq, out uint missionToken, out byte missionPhase, out uint victimNetId, out uint shooterNetId, out uint ammoKey, out Vector3 impact, out bool isSpall)
    {
        victimNetId = 0;
        shooterNetId = 0;
        ammoKey = 0;
        impact = default;
        isSpall = false;
        if (!TryRead(data, length, out byte et, out hostCombatSeq, out missionToken, out missionPhase))
            return false;
        if (et != EventUnitStruck || length < StruckTotalLength)
            return false;
        int o = HeaderLength;
        victimNetId = ReadU32(data, ref o);
        shooterNetId = ReadU32(data, ref o);
        ammoKey = ReadU32(data, ref o);
        impact = ReadVec3(data, ref o);
        isSpall = (data[o] & 1) != 0;
        return true;
    }

    public static bool TryReadImpactFx(
        byte[] data,
        int length,
        out uint hostCombatSeq,
        out uint missionToken,
        out byte missionPhase,
        out ushort effectKind,
        out Vector3 worldPos,
        out Vector3 normal,
        out uint ammoKey,
        out uint victimNetId,
        out byte flags)
    {
        effectKind = 0;
        worldPos = default;
        normal = default;
        ammoKey = 0;
        victimNetId = 0;
        flags = 0;
        if (!TryRead(data, length, out byte et, out hostCombatSeq, out missionToken, out missionPhase))
            return false;
        if (et != EventImpactFx || length < ImpactFxTotalLength)
            return false;
        int o = HeaderLength;
        effectKind = ReadU16(data, ref o);
        o += 2;
        worldPos = ReadVec3(data, ref o);
        normal = ReadVec3(data, ref o);
        ammoKey = ReadU32(data, ref o);
        victimNetId = ReadU32(data, ref o);
        flags = data[o];
        return true;
    }

    public static bool TryReadDamageState(
        byte[] data,
        int length,
        out uint hostCombatSeq,
        out uint missionToken,
        out byte missionPhase,
        out uint victimNetId,
        out CoopDamageStateSnapshot state)
    {
        victimNetId = 0;
        state = default;
        if (!TryRead(data, length, out byte et, out hostCombatSeq, out missionToken, out missionPhase))
            return false;
        if (et != EventDamageState || length < DamageStateTotalLength)
            return false;
        int o = HeaderLength;
        victimNetId = ReadU32(data, ref o);
        bool unitDestroyed = data[o++] != 0;
        byte enginePct = data[o++];
        byte transPct = data[o++];
        byte radiatorPct = data[o++];
        byte leftTrackPct = data[o++];
        byte rightTrackPct = data[o++];
        state = new CoopDamageStateSnapshot(
            unitDestroyed,
            enginePct,
            transPct,
            radiatorPct,
            leftTrackPct,
            rightTrackPct);
        return true;
    }

    public static bool TryReadUnitState(
        byte[] data,
        int length,
        out uint hostCombatSeq,
        out uint missionToken,
        out byte missionPhase,
        out uint unitNetId,
        out CoopUnitStateSnapshot state)
    {
        unitNetId = 0;
        state = default;
        if (!TryRead(data, length, out byte et, out hostCombatSeq, out missionToken, out missionPhase))
            return false;
        if (et != EventUnitState || length < UnitStateTotalLength)
            return false;
        int o = HeaderLength;
        unitNetId = ReadU32(data, ref o);
        state = new CoopUnitStateSnapshot(data[o]);
        return true;
    }

    public static bool TryReadCrewState(
        byte[] data,
        int length,
        out uint hostCombatSeq,
        out uint missionToken,
        out byte missionPhase,
        out uint unitNetId,
        out CoopCrewStateSnapshot state)
    {
        unitNetId = 0;
        state = default;
        if (!TryRead(data, length, out byte et, out hostCombatSeq, out missionToken, out missionPhase))
            return false;
        if (et != EventCrewState || length < CrewStateTotalLength)
            return false;
        int o = HeaderLength;
        unitNetId = ReadU32(data, ref o);
        byte present = data[o++];
        byte dead = data[o++];
        byte incapacitated = data[o++];
        byte evacuated = data[o++];
        byte suspended = data[o];
        state = new CoopCrewStateSnapshot(present, dead, incapacitated, evacuated, suspended);
        return true;
    }

    public static bool TryReadHitResolved(
        byte[] data,
        int length,
        out uint hostCombatSeq,
        out uint missionToken,
        out byte missionPhase,
        out uint shotId,
        out uint victimNetId,
        out uint shooterNetId,
        out uint ammoKey,
        out Vector3 impact,
        out byte hitKind,
        out byte flags)
    {
        shotId = 0;
        victimNetId = 0;
        shooterNetId = 0;
        ammoKey = 0;
        impact = default;
        hitKind = 0;
        flags = 0;
        if (!TryRead(data, length, out byte et, out hostCombatSeq, out missionToken, out missionPhase))
            return false;
        if (et != EventHitResolved || length < HitResolvedTotalLength)
            return false;
        int o = HeaderLength;
        shotId = ReadU32(data, ref o);
        victimNetId = ReadU32(data, ref o);
        shooterNetId = ReadU32(data, ref o);
        ammoKey = ReadU32(data, ref o);
        impact = ReadVec3(data, ref o);
        hitKind = data[o++];
        flags = data[o];
        return true;
    }

    public static bool TryReadCompartmentState(
        byte[] data,
        int length,
        out uint hostCombatSeq,
        out uint missionToken,
        out byte missionPhase,
        out uint unitNetId,
        out CoopCompartmentStateSnapshot state)
    {
        unitNetId = 0;
        state = default;
        if (!TryRead(data, length, out byte et, out hostCombatSeq, out missionToken, out missionPhase))
            return false;
        if (et != EventCompartmentState || length < CompartmentStateTotalLength)
            return false;
        int o = HeaderLength;
        unitNetId = ReadU32(data, ref o);
        bool firePresent = data[o++] != 0;
        bool unsecured = data[o++] != 0;
        byte flamePct = data[o++];
        byte tempPct = data[o];
        state = new CoopCompartmentStateSnapshot(firePresent, unsecured, flamePct, tempPct);
        return true;
    }

    private static void WriteHeader(byte[] buffer, uint hostCombatSeq, uint missionToken, byte missionPhase, byte eventType)
    {
        buffer[0] = Magic0;
        buffer[1] = Magic1;
        buffer[2] = Magic2;
        buffer[3] = WireVersion1;
        int o = 4;
        o = WriteU32(buffer, o, hostCombatSeq);
        o = WriteU32(buffer, o, missionToken);
        buffer[o++] = missionPhase;
        buffer[o++] = eventType;
        buffer[o++] = 0;
        buffer[o] = 0;
    }

    private static int WriteU32(byte[] b, int o, uint v)
    {
        BitConverter.GetBytes(v).CopyTo(b, o);
        return o + 4;
    }

    private static int WriteU16(byte[] b, int o, ushort v)
    {
        BitConverter.GetBytes(v).CopyTo(b, o);
        return o + 2;
    }

    private static ushort ReadU16(byte[] b, ref int o)
    {
        ushort v = BitConverter.ToUInt16(b, o);
        o += 2;
        return v;
    }

    private static int WriteVec3(byte[] b, int o, Vector3 v)
    {
        o = WriteF32(b, o, v.x);
        o = WriteF32(b, o, v.y);
        return WriteF32(b, o, v.z);
    }

    private static int WriteF32(byte[] b, int o, float v)
    {
        BitConverter.GetBytes(v).CopyTo(b, o);
        return o + 4;
    }

    private static uint ReadU32(byte[] b, ref int o)
    {
        uint v = BitConverter.ToUInt32(b, o);
        o += 4;
        return v;
    }

    private static Vector3 ReadVec3(byte[] b, ref int o)
    {
        float x = BitConverter.ToSingle(b, o);
        o += 4;
        float y = BitConverter.ToSingle(b, o);
        o += 4;
        float z = BitConverter.ToSingle(b, o);
        o += 4;
        return new Vector3(x, y, z);
    }
}
