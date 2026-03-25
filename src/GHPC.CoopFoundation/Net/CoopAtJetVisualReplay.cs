using System.Collections.Generic;
using GHPC;
using GHPC.Player;
using UnityEngine;

namespace GHPC.CoopFoundation.Net;

/// <summary>
/// Client-only cosmetic ballistic for peer AT grenade jets (no <see cref="GHPC.Weapons.LiveRound" /> on client).
/// Integrates with Unity gravity when host sets the gravity flag; host estimates max lifetime in deciseconds.
/// </summary>
internal static class CoopAtJetVisualReplay
{
    private const float MaxTravelMeters = 520f;

    private sealed class Jet
    {
        public Vector3 Origin;
        public Vector3 Position;
        public Vector3 Velocity;
        public bool UseGravity;
        public float Remaining;
        public GameObject Root = null!;
    }

    private static readonly List<Jet> Active = new();

    public static void ClearAll()
    {
        foreach (Jet j in Active)
        {
            if (j.Root != null)
                Object.Destroy(j.Root);
        }

        Active.Clear();
    }

    public static void TrySpawn(
        Vector3 worldPos,
        Vector3 velocity,
        bool useGravity,
        byte maxLifeDs,
        uint shooterNetId)
    {
        if (!CoopUdpTransport.IsClient || !CoopUdpTransport.IsNetworkActive)
            return;
        if (!CoopCosmeticInterest.ShouldEmitToPeer(worldPos))
            return;

        Unit? shooter = shooterNetId != 0 ? CoopUnitLookup.TryFindByNetId(shooterNetId) : null;
        if (PlayerInput.Instance != null && shooter != null && PlayerInput.Instance.CurrentPlayerUnit == shooter)
            return;

        float life = maxLifeDs > 0 ? maxLifeDs / 10f : 6f;
        life = Mathf.Clamp(life, 0.4f, 25f);

        GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        Object.Destroy(go.GetComponent<Collider>());
        go.name = "CoopATJetGhost";
        go.transform.position = worldPos;
        go.transform.localScale = new Vector3(0.06f, 0.06f, 2.2f);
        MeshRenderer? mr = go.GetComponent<MeshRenderer>();
        if (mr != null)
            mr.material.color = new Color(1f, 0.42f, 0.05f, 1f);

        if (velocity.sqrMagnitude > 0.01f)
            go.transform.rotation = Quaternion.LookRotation(velocity.normalized);

        Active.Add(new Jet
        {
            Origin = worldPos,
            Position = worldPos,
            Velocity = velocity,
            UseGravity = useGravity,
            Remaining = life,
            Root = go
        });
    }

    public static void Tick(float dt)
    {
        if (Active.Count == 0)
            return;
        Vector3 g = Physics.gravity;
        float maxSq = MaxTravelMeters * MaxTravelMeters;
        for (int i = Active.Count - 1; i >= 0; i--)
        {
            Jet j = Active[i];
            j.Remaining -= dt;
            if (j.Remaining <= 0f)
            {
                DestroyAt(i);
                continue;
            }

            if (j.UseGravity)
                j.Velocity += g * dt;
            j.Position += j.Velocity * dt;
            if ((j.Position - j.Origin).sqrMagnitude > maxSq)
            {
                DestroyAt(i);
                continue;
            }

            if (j.Root == null)
            {
                Active.RemoveAt(i);
                continue;
            }

            j.Root.transform.position = j.Position;
            if (j.Velocity.sqrMagnitude > 4f)
                j.Root.transform.rotation = Quaternion.LookRotation(j.Velocity.normalized);
        }
    }

    private static void DestroyAt(int index)
    {
        Jet j = Active[index];
        if (j.Root != null)
            Object.Destroy(j.Root);
        Active.RemoveAt(index);
    }
}
