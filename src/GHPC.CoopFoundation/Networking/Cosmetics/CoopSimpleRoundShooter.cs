using System.Reflection;
using GHPC;
using GHPC.Weaponry;

namespace GHPC.CoopFoundation.Networking.Cosmetics;

internal static class CoopSimpleRoundShooter
{
    private static readonly FieldInfo? ShooterField =
        typeof(SimpleRound).GetField("_shooter", BindingFlags.Instance | BindingFlags.NonPublic);

    public static Unit? GetShooter(SimpleRound round)
    {
        if (round == null || ShooterField == null)
            return null;
        return ShooterField.GetValue(round) as Unit;
    }
}
