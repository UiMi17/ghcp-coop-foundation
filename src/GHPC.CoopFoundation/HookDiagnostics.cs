using MelonLoader;

namespace GHPC.CoopFoundation;

internal static class HookDiagnostics
{
    internal static MelonPreferences_Entry<bool> LogGameHooks { get; private set; } = null!;

    internal static bool ShouldLog => LogGameHooks.Value;

    internal static void Init(MelonPreferences_Entry<bool> logGameHooks)
    {
        LogGameHooks = logGameHooks;
    }
}
