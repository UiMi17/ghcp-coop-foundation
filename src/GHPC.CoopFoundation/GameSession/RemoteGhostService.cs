using UnityEngine;

namespace GHPC.CoopFoundation.GameSession;

/// <summary>
///     World-space proxy for <see cref="CoopRemoteState" />: hull + turret + barrel (capsule + cube), no colliders.
/// </summary>
internal static class RemoteGhostService
{
    private static GameObject? _root;

    private static Transform? _hull;

    private static Transform? _turretPivot;

    private static Transform? _barrel;

    private static bool _hadRemoteData;

    private static Vector3 _smoothPos;

    private static Quaternion _smoothHull;

    private static Quaternion _smoothTurretWorld;

    private static Quaternion _smoothGunWorld;

    public static void TickLateUpdate(bool showGhost, float smoothing, float yOffsetWorld)
    {
        if (!showGhost || !CoopSessionState.IsPlaying)
        {
            SetRootActive(false);
            return;
        }

        EnsureCreated();
        if (_root == null || _hull == null || _turretPivot == null || _barrel == null)
            return;

        if (!CoopRemoteState.HasData)
        {
            _hadRemoteData = false;
            SetRootActive(false);
            return;
        }

        SetRootActive(true);

        Vector3 targetPos = CoopRemoteState.RemotePosition + new Vector3(0f, yOffsetWorld, 0f);
        Quaternion hull = CoopRemoteState.RemoteHullRotation;
        Quaternion tWorld = CoopRemoteState.RemoteTurretWorldRotation;
        Quaternion gWorld = CoopRemoteState.RemoteGunWorldRotation;

        if (!_hadRemoteData)
        {
            _smoothPos = targetPos;
            _smoothHull = hull;
            _smoothTurretWorld = tWorld;
            _smoothGunWorld = gWorld;
            ApplyHierarchy();
            _hadRemoteData = true;
            return;
        }

        float t = Mathf.Clamp01(Time.deltaTime * smoothing);
        _smoothPos = Vector3.Lerp(_smoothPos, targetPos, t);
        _smoothHull = Quaternion.Slerp(_smoothHull, hull, t);
        _smoothTurretWorld = Quaternion.Slerp(_smoothTurretWorld, tWorld, t);
        _smoothGunWorld = Quaternion.Slerp(_smoothGunWorld, gWorld, t);
        ApplyHierarchy();
    }

    public static void Destroy()
    {
        if (_root != null)
        {
            Object.Destroy(_root);
            _root = null;
            _hull = null;
            _turretPivot = null;
            _barrel = null;
        }

        _hadRemoteData = false;
    }

    private static void ApplyHierarchy()
    {
        _hull!.position = _smoothPos;
        _hull.rotation = _smoothHull;
        _turretPivot!.localRotation = Quaternion.Inverse(_smoothHull) * _smoothTurretWorld;
        _barrel!.localRotation = Quaternion.Inverse(_smoothTurretWorld) * _smoothGunWorld;
    }

    private static void SetRootActive(bool active)
    {
        if (_root != null && _root.activeSelf != active)
            _root.SetActive(active);
    }

    private static void EnsureCreated()
    {
        if (_root != null)
            return;

        _root = new GameObject("GHPC_Coop_RemoteGhost");
        Object.DontDestroyOnLoad(_root);

        GameObject hullGo = new GameObject("HullPivot");
        hullGo.transform.SetParent(_root.transform, false);
        _hull = hullGo.transform;

        GameObject body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
        body.name = "HullCapsule";
        body.transform.SetParent(_hull, false);
        body.transform.localScale = new Vector3(2.8f, 1.6f, 5.5f);
        DestroyCollider(body);

        Renderer? bodyR = body.GetComponent<Renderer>();
        if (bodyR != null)
            bodyR.material.color = new Color(0.2f, 0.65f, 1f, 1f);

        GameObject turretGo = new GameObject("TurretPivot");
        turretGo.transform.SetParent(_hull, false);
        _turretPivot = turretGo.transform;

        GameObject barrelGo = GameObject.CreatePrimitive(PrimitiveType.Cube);
        barrelGo.name = "Barrel";
        barrelGo.transform.SetParent(_turretPivot, false);
        barrelGo.transform.localScale = new Vector3(0.35f, 0.35f, 2.2f);
        barrelGo.transform.localPosition = new Vector3(0f, 0f, 1.1f);
        DestroyCollider(barrelGo);

        Renderer? barrelR = barrelGo.GetComponent<Renderer>();
        if (barrelR != null)
            barrelR.material.color = new Color(0.95f, 0.55f, 0.15f, 1f);

        _barrel = barrelGo.transform;
        _root.SetActive(false);
    }

    private static void DestroyCollider(GameObject go)
    {
        Collider? col = go.GetComponent<Collider>();
        if (col != null)
            Object.Destroy(col);
    }
}
