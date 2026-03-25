using System;
using System.Collections.Generic;
using GHPC.Equipment;
using GHPC.PhysicsHelpers;
using GHPC.Weapons;
using GHPC.World;
using UnityEngine;

namespace GHPC.Effects;

public class Compartment : MonoBehaviour
{
	public string Name;

	public Compartment Parent;

	public DetachableParent DetachThisFromParent;

	public Collider[] Volumes;

	public float TotalVolume = 1f;

	public List<CompartmentExit> Exits;

	public List<DetachableParent> Detachables;

	private float _internalTemperature;

	public float InternalTempBoost = 5f;

	public float MaxTempC = 2000f;

	public bool UnsecuredFiresHere = true;

	public List<FlammablesCluster> Clusters = new List<FlammablesCluster>();

	[SerializeField]
	[Tooltip("A collider this destructible should treat as its hit zone, belonging to a proxy colliders group")]
	private ProxyHitZone _proxyHitZone;

	private FlammablesManager _manager;

	private bool _shutDown;

	private float _tntEquivalentBufferKg;

	private bool _didInitialTemp;

	private bool _detachedFromParent;

	private float _baseTemp;

	private int _proxyHitZoneID;

	private ProxyHitzoneManager _proxyManager;

	private const float BACKUP_OVERPRESSURE = 100000f;

	private const float VENTILATION_COOLING_FACTOR = 1f;

	private const float RADIATION_COOLING_FACTOR = 0.5f;

	private float[] _detachablesCumulativeOverpressure;

	private List<Tuple<Vector3, float>> _externalOverpressureQueue = new List<Tuple<Vector3, float>>();

	private List<CompartmentFirewall> _firewalls = new List<CompartmentFirewall>();

	private const float COOLDOWN_SMOKE_DELETE_TEMPERATURE = 60f;

	private int _smallHolesCount;

	private const int SMALL_HOLES_LIMIT = 10;

	private float _lastBurstEffectTime;

	private float _lastBurstEffectSize;

	private const float MIN_BURST_EFFECT_TIMEOUT = 0.5f;

	private const float BURST_EFFECT_SIZE_DIFF_OVERRIDE = 5f;

	public float InternalTemperature
	{
		get
		{
			if (!_didInitialTemp)
			{
				_internalTemperature = WorldEnvironmentManager.TempCelsius + InternalTempBoost;
				_didInitialTemp = true;
			}
			return _internalTemperature;
		}
		private set
		{
			_internalTemperature = Mathf.Min(value, MaxTempC);
		}
	}

	public bool FirePresent { get; private set; }

	public float CombinedFlameHeight { get; private set; }

	public bool IsCrewCompartment { get; set; }

	public bool ResidualSmokePresent { get; private set; }

	private bool _actingAsChild
	{
		get
		{
			if (Parent != null)
			{
				return !_detachedFromParent;
			}
			return false;
		}
	}

	public event Action FireStarted;

	private void Awake()
	{
		if (Volumes.Length == 0 && _proxyHitZone == null)
		{
			Collider component = GetComponent<Collider>();
			if (!(component != null))
			{
				Debug.LogError("No collider found on compartment: " + Name);
				base.gameObject.SetActive(value: false);
				base.enabled = false;
				return;
			}
			Volumes = new Collider[1] { component };
		}
		foreach (FlammablesCluster cluster in Clusters)
		{
			foreach (FlammableItem item in cluster.Items)
			{
				item.RegisterCluster(cluster);
			}
			if (Parent != null)
			{
				Parent.AddChildCluster(cluster);
			}
			SetUpCluster(cluster);
		}
		foreach (CompartmentExit exit in Exits)
		{
			exit.DoAwake(this);
			if (Parent != null)
			{
				Parent.Exits.Add(exit);
			}
		}
		if (DetachThisFromParent != null)
		{
			DetachThisFromParent.DidDetach += DetachedFromParent;
		}
		_baseTemp = WorldEnvironmentManager.TempCelsius + InternalTempBoost;
		if (Detachables != null && Detachables.Count > 0)
		{
			_detachablesCumulativeOverpressure = new float[Detachables.Count];
		}
		if (_proxyHitZone != null)
		{
			_proxyHitZoneID = _proxyHitZone.UniqueNameID;
			_proxyHitZone.FriendlyName = Name;
			_proxyManager = GetComponentInParent<ProxyHitzoneManager>();
			if (_proxyManager != null)
			{
				_proxyManager.CompartmentStruck += HandleProxyColliderStruck;
			}
		}
	}

	private void OnDestroy()
	{
		foreach (CompartmentExit exit in Exits)
		{
			exit.DoOnDestroy();
		}
		if (DetachThisFromParent != null)
		{
			DetachThisFromParent.DidDetach -= DetachedFromParent;
		}
		if (_proxyManager != null)
		{
			_proxyManager.CompartmentStruck -= HandleProxyColliderStruck;
		}
	}

	public void RegisterFlammablesManager(FlammablesManager manager)
	{
		_manager = manager;
	}

	public void RegisterFirewall(CompartmentFirewall firewall)
	{
		if (_actingAsChild)
		{
			Parent.RegisterFirewall(firewall);
		}
		else
		{
			_firewalls.Add(firewall);
		}
	}

	public void NotifyIgnited(FlammablesCluster caller, bool explosive, float tntEquivalentKg = 0f)
	{
		StartFire(caller.FireMarker.position);
		if (explosive)
		{
			_tntEquivalentBufferKg += tntEquivalentKg * caller.ExplosionProximityFactor;
			EvaluateOverpressureAgainstFirewalls(tntEquivalentKg, caller.FireMarker.position);
		}
	}

	public void NotifyPenetrated(AmmoType roundType, Vector3 impactPoint, Vector3 roundPath, bool doBlast)
	{
		AddExit(impactPoint, roundPath, roundType.Radius, FlammableSourceType.SmallHole);
		if (doBlast)
		{
			InsertOverpressure(roundType.TntEquivalentKg, impactPoint);
		}
		if (IsCrewCompartment && _manager != null)
		{
			_manager.NotifyCrewCompartmentPenetrated(roundType);
		}
	}

	public void AddExit(Vector3 worldPosition, Vector3 worldDirectionIn, float radiusMeters, FlammableSourceType type)
	{
		if (type == FlammableSourceType.SmallHole)
		{
			if (_smallHolesCount >= 10)
			{
				return;
			}
			_smallHolesCount++;
		}
		GameObject gameObject = new GameObject("exit marker");
		gameObject.transform.position = worldPosition;
		gameObject.transform.forward = -worldDirectionIn;
		gameObject.transform.parent = base.transform;
		CompartmentExit compartmentExit = new CompartmentExit
		{
			Name = "impact exit",
			Radius = radiusMeters,
			SourceType = type,
			Closed = false,
			PositionMarker = gameObject.transform
		};
		compartmentExit.DoAwake(this);
		if (_actingAsChild)
		{
			Parent.Exits.Add(compartmentExit);
		}
		Exits.Add(compartmentExit);
	}

	public void RemoveExit(CompartmentExit exit)
	{
		Exits.Remove(exit);
	}

	public void AddCluster(FlammablesCluster cluster)
	{
		if (_actingAsChild)
		{
			Parent.AddChildCluster(cluster);
		}
		Clusters.Add(cluster);
		SetUpCluster(cluster);
	}

	public void AddChildCluster(FlammablesCluster cluster)
	{
		Clusters.Add(cluster);
	}

	public bool CanDoBurstEffect(float ratio)
	{
		if (Time.timeSinceLevelLoad - _lastBurstEffectTime > 0.5f)
		{
			return true;
		}
		if (ratio >= _lastBurstEffectSize + 5f)
		{
			return true;
		}
		return false;
	}

	public void NotifyDidBurstEffect(float ratio)
	{
		_lastBurstEffectSize = ratio;
		_lastBurstEffectTime = Time.timeSinceLevelLoad;
	}

	public void NotifyExplosion(float tntEquivalent)
	{
		if (_manager != null)
		{
			_manager.NotifyCompartmentExplosion(tntEquivalent);
		}
	}

	private void SetUpCluster(FlammablesCluster cluster)
	{
		cluster.RegisterCompartment(this);
		cluster.Temperature = InternalTemperature;
		cluster.DoAwake();
	}

	public void InsertOverpressure(float tntEquivalentKg, Vector3 worldPosition)
	{
		if (tntEquivalentKg != 0f)
		{
			Vector3 item = base.transform.InverseTransformPoint(worldPosition);
			_externalOverpressureQueue.Add(new Tuple<Vector3, float>(item, tntEquivalentKg));
			EvaluateOverpressureAgainstFirewalls(tntEquivalentKg, worldPosition);
			NotifyExplosion(tntEquivalentKg);
		}
	}

	private void EvaluateOverpressureAgainstFirewalls(float tntEquivalentKg, Vector3 worldPosition)
	{
		foreach (CompartmentFirewall firewall in _firewalls)
		{
			if (firewall.DoorOpen)
			{
				firewall.GetOtherCompartment(this).InsertOverpressure(tntEquivalentKg, worldPosition);
			}
		}
	}

	private void StartFire(Vector3 worldPosition)
	{
		if (_actingAsChild)
		{
			this.FireStarted?.Invoke();
			Parent.StartFire(worldPosition);
		}
		else
		{
			if (FirePresent)
			{
				return;
			}
			FirePresent = true;
			if (FlammablesManager.Verbose)
			{
				Debug.Log("Fire started in " + Name);
			}
			foreach (CompartmentExit exit in Exits)
			{
				exit.SetSmokeRatio(0.4f);
			}
			this.FireStarted?.Invoke();
		}
	}

	private void DoBurst(float ratio)
	{
		if (_actingAsChild)
		{
			Parent.DoBurst(ratio);
			return;
		}
		foreach (CompartmentExit exit in Exits)
		{
			exit.MakeFireBurst(ratio);
		}
		if (_manager != null)
		{
			_manager.NotifyDidBurst(ratio);
		}
	}

	private void AddProximityHeat(FlammablesCluster cluster, float dt)
	{
		if (cluster.HasFire)
		{
			return;
		}
		for (int i = 0; i < Clusters.Count; i++)
		{
			FlammablesCluster flammablesCluster = Clusters[i];
			if (flammablesCluster != cluster && flammablesCluster.HasFire)
			{
				float num = flammablesCluster.CurrentFlameHeight * 0.5f;
				float num2 = ((cluster.FireMarker == null || flammablesCluster.FireMarker == null) ? 1f : Vector3.Distance(cluster.FireMarker.position, flammablesCluster.FireMarker.position));
				if (num2 > num)
				{
					break;
				}
				float num3 = 1f - num2 / num;
				float num4 = num3 * num3 * flammablesCluster.GetFrameTempGrowFromFire(dt) * cluster.FireProximityFactor;
				cluster.Temperature += num4;
			}
		}
	}

	private void AddFirewallBreachedHeat(FlammablesCluster cluster, float dt)
	{
		if (cluster.HasFire)
		{
			return;
		}
		for (int i = 0; i < _firewalls.Count; i++)
		{
			CompartmentFirewall compartmentFirewall = _firewalls[i];
			if (!compartmentFirewall.Compromised)
			{
				continue;
			}
			Compartment otherCompartment = compartmentFirewall.GetOtherCompartment(this);
			if (otherCompartment.FirePresent)
			{
				float num = 0f;
				if (compartmentFirewall.DoorOpen)
				{
					num = otherCompartment.CombinedFlameHeight * compartmentFirewall.DoorOpenFlameHeightRatio;
				}
				else if (compartmentFirewall.Punctured)
				{
					num = otherCompartment.CombinedFlameHeight * compartmentFirewall.PuncturedFlameHeightRatio;
				}
				float num2 = Vector3.Distance(cluster.FireMarker.position, compartmentFirewall.FireMarker.position);
				if (num2 > num)
				{
					break;
				}
				float num3 = 1f - num2 / num;
				float num4 = num3 * num3;
				float num5 = 0f;
				float num6 = 1000f * otherCompartment.CombinedFlameHeight;
				float num7 = 120000f;
				float num8 = Mathf.Clamp(GetVentilationArea() * num7 / num6, 0f, 1f);
				num5 = compartmentFirewall.TempGrowCoeff * dt * num8 * num6;
				float num9 = num4 * num5 * cluster.FireProximityFactor;
				cluster.Temperature += num9;
				if (FlammablesManager.Verbose)
				{
					Debug.Log($"Boosting {cluster.Name} temp by {num9} C due to heat from breached firewall {compartmentFirewall.Name}, {num2} meters away");
				}
			}
		}
	}

	public void DoUpdate(float dt)
	{
		if (_actingAsChild || _shutDown)
		{
			return;
		}
		CombinedFlameHeight = 0f;
		foreach (FlammablesCluster cluster in Clusters)
		{
			cluster.DoUpdate(dt);
			CombinedFlameHeight += cluster.CurrentFlameHeight;
		}
		if (_detachablesCumulativeOverpressure != null)
		{
			for (int i = 0; i < _detachablesCumulativeOverpressure.Length; i++)
			{
				_detachablesCumulativeOverpressure[i] = 0f;
			}
		}
		foreach (CompartmentFirewall firewall in _firewalls)
		{
			if (!firewall.Compromised)
			{
				continue;
			}
			Compartment otherCompartment = firewall.GetOtherCompartment(this);
			if (!otherCompartment.FirePresent)
			{
				continue;
			}
			if (firewall.DoorOpen)
			{
				CombinedFlameHeight += otherCompartment.CombinedFlameHeight * firewall.DoorOpenFlameHeightRatio;
			}
			else if (firewall.Punctured)
			{
				CombinedFlameHeight += otherCompartment.CombinedFlameHeight * firewall.PuncturedFlameHeightRatio;
			}
			if (!FirePresent)
			{
				if (FlammablesManager.Verbose)
				{
					string text = (firewall.DoorOpen ? "door open" : "punctured");
					Debug.Log("Starting fire across " + firewall.Name + " (" + text + ")...");
				}
				StartFire(firewall.FireMarker.position);
			}
		}
		if (FirePresent)
		{
			if (CombinedFlameHeight == 0f)
			{
				foreach (CompartmentExit exit in Exits)
				{
					exit.SetFireRatio(0f);
					exit.SetSmokeRatio(0.3f);
				}
				FirePresent = false;
				ResidualSmokePresent = true;
			}
			else
			{
				float num = 0f;
				foreach (CompartmentExit exit2 in Exits)
				{
					if (!exit2.Closed)
					{
						num += exit2.Area;
					}
				}
				foreach (CompartmentExit exit3 in Exits)
				{
					if (!exit3.Closed)
					{
						float num2 = exit3.Area / num;
						if (CombinedFlameHeight < exit3.FlameHeightThreshold)
						{
							exit3.SetFireRatio(0f);
						}
						else
						{
							exit3.SetFireRatioByFlameHeight(CombinedFlameHeight * num2);
						}
						exit3.SetSmokeRatio(0.1f + CombinedFlameHeight * num2 * 0.5f);
					}
				}
			}
		}
		if (ResidualSmokePresent && InternalTemperature < 60f)
		{
			ResidualSmokePresent = false;
			foreach (CompartmentExit exit4 in Exits)
			{
				exit4.SetFireRatio(0f);
				exit4.SetSmokeRatio(0f);
			}
		}
		foreach (FlammablesCluster cluster2 in Clusters)
		{
			AddProximityHeat(cluster2, dt);
			AddFirewallBreachedHeat(cluster2, dt);
			if (cluster2.HasFire && cluster2.Temperature > InternalTemperature)
			{
				float num3 = cluster2.Temperature - InternalTemperature;
				InternalTemperature += num3 * 0.1f * dt;
			}
			if (!(cluster2.TntEquivalentBufferKg > 0f))
			{
				continue;
			}
			for (int j = 0; j < Clusters.Count; j++)
			{
				FlammablesCluster flammablesCluster = Clusters[j];
				if (flammablesCluster != cluster2 && !flammablesCluster.HadAnyDetonation && flammablesCluster.ContainsUndetonatedExplosives)
				{
					float num4 = Vector3.Distance(flammablesCluster.FireMarker.position, cluster2.FireMarker.position);
					float overpressureBar = 100000f;
					if (num4 > 0.01f)
					{
						overpressureBar = WeaponMath.GetOverpressureBar(cluster2.TntEquivalentBufferKg, num4);
					}
					flammablesCluster.TryDetonateExplosive(overpressureBar);
				}
			}
			if (Detachables != null && Detachables.Count > 0)
			{
				float ventilationArea = GetVentilationArea();
				for (int k = 0; k < Detachables.Count; k++)
				{
					DetachableParent detachableParent = Detachables[k];
					if (!detachableParent.Detached && detachableParent.OverpressureTrigger != 0f)
					{
						float num5 = Vector3.Distance(detachableParent.ForceReceivingLocation.position, cluster2.FireMarker.position);
						float num6 = 100000f;
						if (num5 > 0.01f)
						{
							num6 = WeaponMath.GetOverpressureBar(cluster2.TntEquivalentBufferKg, num5);
						}
						if (ventilationArea > 0.2f)
						{
							num6 = num6 * 0.9f * 0.2f / ventilationArea + num6 * 0.1f;
						}
						if (FlammablesManager.Verbose)
						{
							Debug.Log($"Hitting {detachableParent.gameObject.name} with overpressure of {num6} from {cluster2.Name}...");
						}
						_detachablesCumulativeOverpressure[k] += num6;
					}
				}
			}
			for (int l = 0; l < Exits.Count; l++)
			{
				CompartmentExit compartmentExit = Exits[l];
				if (!compartmentExit.Closed || compartmentExit.PressureToOpenBar == 0f)
				{
					continue;
				}
				float num7 = Vector3.Distance(compartmentExit.PositionMarker.position, cluster2.FireMarker.position);
				float num8 = 100000f;
				if (num7 > 0.01f)
				{
					num8 = WeaponMath.GetOverpressureBar(cluster2.TntEquivalentBufferKg, num7);
				}
				if (num8 > compartmentExit.PressureToOpenBar)
				{
					if (FlammablesManager.Verbose)
					{
						Debug.Log($"Blowing open {compartmentExit.Name} due to overpressure of {num8} from {cluster2.Name}!");
					}
					compartmentExit.Open(0.1f, lockAnim: true);
				}
			}
			GHPC.Equipment.DestructibleComponent.TryOverpressureDamage(cluster2.TntEquivalentBufferKg, cluster2.FireMarker.position);
			Explosions.RegisterExplosion(cluster2.FireMarker.position, cluster2.TntEquivalentBufferKg);
			NotifyExplosion(_tntEquivalentBufferKg);
			cluster2.TntEquivalentBufferKg = 0f;
		}
		foreach (Tuple<Vector3, float> item2 in _externalOverpressureQueue)
		{
			Vector3 b = base.transform.TransformPoint(item2.Item1);
			float item = item2.Item2;
			for (int m = 0; m < Exits.Count; m++)
			{
				CompartmentExit compartmentExit2 = Exits[m];
				if (!compartmentExit2.Closed || compartmentExit2.PressureToOpenBar == 0f)
				{
					continue;
				}
				float num9 = Vector3.Distance(compartmentExit2.PositionMarker.position, b);
				float num10 = 100000f;
				if (num9 > 0.01f)
				{
					num10 = WeaponMath.GetOverpressureBar(item, num9);
				}
				if (num10 > compartmentExit2.PressureToOpenBar)
				{
					if (FlammablesManager.Verbose)
					{
						Debug.Log($"Blowing open {compartmentExit2.Name} due to externally added overpressure of {num10}!");
					}
					compartmentExit2.Open(0.1f, lockAnim: true);
				}
			}
			if (Detachables == null || Detachables.Count <= 0)
			{
				continue;
			}
			float ventilationArea2 = GetVentilationArea();
			for (int n = 0; n < Detachables.Count; n++)
			{
				DetachableParent detachableParent2 = Detachables[n];
				if (detachableParent2.Detached || detachableParent2.OverpressureTrigger == 0f)
				{
					continue;
				}
				float num11 = Vector3.Distance(detachableParent2.ForceReceivingLocation.position, b);
				float num12 = 100000f;
				if (num11 > 0.01f)
				{
					num12 = WeaponMath.GetOverpressureBar(item, num11);
				}
				if (ventilationArea2 > 0.2f)
				{
					float num13 = num12 * 0.9f * 0.2f / ventilationArea2 + num12 * 0.1f;
					if (num13 < num12)
					{
						num12 = num13;
					}
				}
				if (FlammablesManager.Verbose)
				{
					Debug.Log($"Hitting {detachableParent2.gameObject.name} with externally added overpressure of {num12}...");
				}
				_detachablesCumulativeOverpressure[n] += num12;
			}
		}
		_externalOverpressureQueue.Clear();
		if (Detachables != null && Detachables.Count > 0)
		{
			for (int num14 = 0; num14 < Detachables.Count; num14++)
			{
				DetachableParent detachableParent3 = Detachables[num14];
				if (detachableParent3.Detached)
				{
					continue;
				}
				float num15 = _detachablesCumulativeOverpressure[num14];
				if (detachableParent3.OverpressureTrigger > 0f && num15 > 0f)
				{
					if (FlammablesManager.Verbose)
					{
						Debug.Log($"Attempting to blow off {detachableParent3.gameObject.name} due to cumulative overpressure of {num15}...");
					}
					detachableParent3.TryOverpressureDetach(num15);
				}
				else if (detachableParent3.FlameHeightTrigger > 0f && CombinedFlameHeight > 0f)
				{
					if (FlammablesManager.Verbose)
					{
						Debug.Log($"Attempting to blow off {detachableParent3.gameObject.name} due to combined flame height of {CombinedFlameHeight}...");
					}
					detachableParent3.TryFlameHeightDetach(CombinedFlameHeight);
				}
			}
		}
		if (_tntEquivalentBufferKg > 0f)
		{
			DoBurst(_tntEquivalentBufferKg);
			NotifyExplosion(_tntEquivalentBufferKg);
			for (int num16 = 0; num16 < Exits.Count; num16++)
			{
				CompartmentExit compartmentExit3 = Exits[num16];
				if (!compartmentExit3.Closed || compartmentExit3.PressureToOpenBar == 0f)
				{
					continue;
				}
				float overpressureBar2 = WeaponMath.GetOverpressureBar(_tntEquivalentBufferKg * 0.5f, 2f);
				if (overpressureBar2 > compartmentExit3.PressureToOpenBar)
				{
					if (FlammablesManager.Verbose)
					{
						Debug.Log($"Blowing open {compartmentExit3.Name} due to estimated total compartment overpressure of {overpressureBar2}!");
					}
					compartmentExit3.Open(0.1f, lockAnim: true);
				}
			}
			_tntEquivalentBufferKg = 0f;
		}
		float num17 = (GetVentilationArea() * 1f + 0.5f) * dt;
		InternalTemperature -= num17;
		if (InternalTemperature < _baseTemp)
		{
			InternalTemperature = _baseTemp;
		}
		foreach (FlammablesCluster cluster3 in Clusters)
		{
			cluster3.Temperature -= num17;
			if (cluster3.Temperature < _baseTemp)
			{
				cluster3.Temperature = _baseTemp;
			}
		}
	}

	public float GetVentilationArea()
	{
		float num = 0f;
		foreach (CompartmentExit exit in Exits)
		{
			if (!exit.Closed)
			{
				num += exit.Area;
			}
		}
		return num;
	}

	public void DetachedFromParent()
	{
		if (Parent == null)
		{
			return;
		}
		_detachedFromParent = true;
		foreach (CompartmentExit exit in Exits)
		{
			Parent.RemoveExit(exit);
		}
		FirePresent = Parent.FirePresent;
		InternalTemperature = Parent.InternalTemperature;
		_manager.NotifyCompartmentDetach(Parent, this);
	}

	public void ShutDown()
	{
		foreach (CompartmentExit exit in Exits)
		{
			exit.SetFireRatio(0f);
			exit.SetSmokeRatio(0f);
		}
		_shutDown = true;
	}

	private void HandleProxyColliderStruck(int uniqueNameID, AmmoType ammo, Vector3 impactPoint, Vector3 roundPath, bool doBlast, LiveRound round, int roundID)
	{
		if (uniqueNameID == _proxyHitZoneID)
		{
			NotifyPenetrated(ammo, impactPoint, roundPath, doBlast);
			round.NotifyEnteredCompartment(roundID, this);
		}
	}
}
