using System;

namespace GHPC.CoopFoundation.Lobby;

/// <summary>
/// Session-only coop friendly Customize **unit row** indices (which Tank/APC/… row each peer starts in),
/// synced via lobby snapshot <c>readyMask</c> bits 8–15 (host) and 16–23 (client). Bits 0–1 remain ready flags.
/// </summary>
internal static class CoopLobbyPlayerSlots
{
    public const int ReadyMaskHostRowShift = 8;

    public const int ReadyMaskClientRowShift = 16;

    public static byte HostFriendlyUnitRowIndex { get; private set; }

    public static byte ClientFriendlyUnitRowIndex { get; private set; } = 1;

    public static event Action? SlotsChanged;

    public static void UpdateFromReadyMask(uint readyMask)
    {
        HostFriendlyUnitRowIndex = (byte)((readyMask >> ReadyMaskHostRowShift) & 0xFF);
        ClientFriendlyUnitRowIndex = (byte)((readyMask >> ReadyMaskClientRowShift) & 0xFF);
        SlotsChanged?.Invoke();
    }

    public static void Reset()
    {
        HostFriendlyUnitRowIndex = 0;
        ClientFriendlyUnitRowIndex = 1;
        SlotsChanged?.Invoke();
    }

    /// <summary>Re-invoke <see cref="SlotsChanged" /> after host flex sync (same row indices, refreshed labels).</summary>
    public static void NotifySlotsRefresh()
    {
        SlotsChanged?.Invoke();
    }
}
