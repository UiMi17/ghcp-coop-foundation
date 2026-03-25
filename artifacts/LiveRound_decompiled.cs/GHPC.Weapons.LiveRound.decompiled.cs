using System;
using System.Collections.Generic;
using System.Text;
using GHPC.AI.Interfaces;
using GHPC.Audio;
using GHPC.Camera;
using GHPC.Effects;
using GHPC.Equipment;
using GHPC.PhysicsHelpers;
using GHPC.Player;
using GHPC.Utility;
using GHPC.World;
using UnityEngine;

namespace GHPC.Weapons;

public class LiveRound : MonoBehaviour
{
	private enum ImpactSfxStrength
	{
		Penetration = 3,
		LargeNonPen = 2,
		SmallNonPen = 1,
		Nothing = 0
	}

	public class ShotStory
	{
		private StringBuilder builder = new StringBuilder();

		private HashSet<string> events = new HashSet<string>();

		public int ShotNumber;

		public float ImpactTime;

		public Vector3 ImpactPosition;

		public List<ShotData> PenShots = new List<ShotData>();

		public List<ShotData> EffectiveRhaShots = new List<ShotData>();

		public string FinalString => builder.ToString();

		public void TryAddStoryEvent(string storyEvent)
		{
			if (events.Add(storyEvent))
			{
				builder.AppendLine(storyEvent);
			}
		}
	}

	public struct ShotData
	{
		public float DamageValue;

		public bool IsSpall;
	}

	public AmmoType Info;

	public float CurrentSpeed;

	public float MaxSpeed;

	public bool UseGravity = true;

	public bool Guided;

	public float TurnSpeed;

	public bool NpcRound;

	public bool IsSpall;

	private bool _isApOrHe;

	private bool _isHeat;

	private bool _isPureAp;

	private bool _isHe;

	[HideInInspector]
	public LiveRound ParentRound;

	[HideInInspector]
	public ShotStory Story;

	[HideInInspector]
	public Unit Shooter;

	[HideInInspector]
	public ShotInfo ShotInfo;

	private bool _detonated;

	private bool _jetActive;

	private bool _didFuzedEffect;

	private Vector3 _jetStartPoint;

	private bool _reported;

	private bool _exploded;

	private bool _hitSolidObject;

	private bool _armed;

	private bool _hasStartBeenCalled;

	private bool _impactFuseActive;

	private float _impactFuseCountdown;

	private bool _rangedFuseActive;

	private float _rangedFuseCountdown;

	private bool _fuzeCompleted;

	private bool _failedToDetonate;

	private IUnit _parentUnit;

	private float _heHitNormalRha;

	private Vector3 _heHitNormal;

	private bool _debugIfPlayer;

	private bool _debugIfNpc;

	public static bool Debug = false;

	private bool _debugIncludesSpall;

	private float _timeTraveled;

	private float _maxTravelTime = 90f;

	private float _subTimeTraveled;

	private Vector3 _lastFramePosition;

	private float _impactTime = -1f;

	private Vector3 _initialVelocity;

	private Vector3 _initialPosition;

	private Vector3 _trueInitialPosition;

	private Vector3 _lastTotalDisp;

	private float _lastRayStepSquared;

	private Vector3 _lastEdgeHit0;

	private Vector3 _lastEdgeHit1;

	private Vector3 _lastEdgeHit2;

	private Vector3 _lastEdgeHit3;

	private Vector3 _lastEdgeHit4;

	private Vector3 _lastEdgeHit5;

	private Transform _lastTHitEdge;

	private Transform _lastTHitCenter;

	private Vector3 _prevPosition;

	private float _lastTHitEdgeMarch;

	private float _prevSpeed;

	private float _speedRatio = 1f;

	private bool _terrainHit;

	private bool _didTerrainHit;

	private ParticleEffectsManager.SurfaceMaterial _materialHit;

	private ParticleEffectsManager.FusedStatus _fusedStatus;

	private bool _hitSomething;

	private bool _sacrificedWarhead;

	private int _impactEffectCount;

	private bool _needsNormalization;

	private Vector3 _normalizationVector;

	private bool _shatter;

	private bool _ricochet;

	private int _ricochetCount;

	private bool _crushed;

	private bool _heatRicochet;

	private float _penetratorWidth;

	private const int RICOCHET_LIMIT = 3;

	private const int SPALL_RICOCHET_LIMIT = 2;

	private const float RICOCHET_SHATTER_PENALTY = 0.1f;

	private const float RICOCHET_SPALL_PENALTY = 0.6f;

	private const float RICOCHET_PENALTY = 0.5f;

	private const float SPALL_RICOCHET_PEN_LIMIT = 5f;

	private const float SPALL_RICOCHET_PEN_RATIO = 0.3f;

	private const float SPALL_RICOCHET_CHANCE = 0.5f;

	private const int MAX_SPALL_COUNT = 100;

	private const float MAX_NUTATION_PENALTY = 0.07f;

	private const float SPALL_DIMINISH_RADIUS_MAX = 0.01f;

	private const float SPALL_DIMINISH_RADIUS_MIN = 0.0035f;

	private const float SPALL_CUTOFF_ANGLE = 2f;

	private const float HEAT_INTERNAL_OVERPRESSURE_RATIO = 0.1f;

	private const float HE_DETONATION_WALK_BACK_DISTANCE = 0.1f;

	private const float HEAT_DETONATION_WALK_FORWARD_DISTANCE = 0.01f;

	private const float SQR_HEAT_BLAST_ZONE_LENGTH = 0.64f;

	private const float MINIMUM_JET_PEN_FOR_AIR_DEGRADATION = 50f;

	private const float JET_AIR_DEGRADATION_PER_METER = 75f;

	private const float MAX_LIFETIME = 60f;

	private const float MIN_ALTITUDE = -300f;

	private const float TREE_ANGLE_THRESHOLD = 85f;

	private const float DEBUG_STAR_RADIUS = 0.2f;

	private const float DEBUG_STAR_DURATION = 10f;

	private const float STRUCK_CHASSIS_UNFREEZE_DURATION = 1f;

	private const int SCAB_SPALL_MIN_COUNT = 5;

	private const int SCAB_SPALL_MAX_COUNT = 20;

	private const float SCAB_SPALL_MIN_BOUNDING_ANGLE = 20f;

	private const float SCAB_SPALL_MAX_BOUNDING_ANGLE = 45f;

	private const float SCAB_SPALL_MIN_PEN = 5f;

	private const float SCAB_SPALL_MAX_PEN = 20f;

	private const float SCAB_SPALL_MIN_SPEED = 300f;

	private const float SCAB_SPALL_MAX_SPEED = 600f;

	private const float MAX_SAFE_IMPACT_IMPULSE = 50000f;

	private static TreeArmor _treeArmor = new TreeArmor();

	private bool _needImpulse;

	private bool _calculatedImpulse;

	private float _impulseRatio = 1f;

	private Rigidbody _impulseTarget;

	private Vector3 _impulseDirection;

	private Vector3 _impulsePoint;

	private Vector3 _impactNormal;

	private ResistanceType _penetratorType;

	private int _layerMask;

	private Compartment _compartmentHit;

	private bool _didHeatBlast;

	private static float _spallSpeed = 200f;

	private static int _maxSpallCount = 50;

	public static ParticleEffectsManager.ImpactEffectDescriptor impactEffectDiscriptor = new ParticleEffectsManager.ImpactEffectDescriptor
	{
		HasImpactEffect = false
	};

	public static AmmoType SpallAmmoType = new AmmoType
	{
		Name = "spall",
		Category = AmmoType.AmmoCategory.Penetrator,
		RhaPenetration = 4f,
		MuzzleVelocity = _spallSpeed,
		NutationPenaltyDistance = 0f,
		Coeff = 20f,
		Mass = 0.1f,
		SectionalArea = 0.0002f,
		EdgeSetback = 0.002f,
		CertainRicochetAngle = 25f,
		UseTracer = false,
		ImpactEffectDescriptor = impactEffectDiscriptor
	};

	private ImpactSFXManager _impactSFXManager;

	private float _armorThicknessForImpactSFX;

	private Vector3 _penHitPositionToSendToSFXManager;

	private ImpactSfxStrength _largestFrameImpactSfx;

	private MotionState _currentMotionState;

	private MotionState _previousMotionState;

	private Vector3 _framePos;

	private Vector3 _frameDir;

	private ShotInfo.ShotFrameData _frameData;

	private bool _didDestroy;

	private bool _waitingForReport;

	private bool _needsRestart;

	private float _remainingTravel;

	public Vector3 GoalVector { get; set; }

	public bool UseErrorCorrection { get; set; }

	public float RhaPenetrationOverride { get; set; }

	public float CurrentPenRating
	{
		get
		{
			float num = ((RhaPenetrationOverride > 0f) ? RhaPenetrationOverride : Info.RhaPenetration);
			if (_isHeat)
			{
				if (!_armed)
				{
					return Info.MaxSpallRha;
				}
				if (!_jetActive)
				{
					return num;
				}
			}
			float num2 = CurrentSpeed * CurrentSpeed / (MaxSpeed * MaxSpeed);
			float num3 = _timeTraveled * Info.MuzzleVelocity;
			if (num3 < Info.NutationPenaltyDistance && !IsSpall)
			{
				float num4 = (Info.NutationPenaltyDistance - num3) / Info.NutationPenaltyDistance;
				num2 *= 1f - 0.07f * num4;
			}
			return num * num2;
		}
	}

	public float TimeTraveled => _timeTraveled;

	public int ID { get; private set; }

	public bool Pooled { get; set; }

	public int GuidanceStage { get; set; }

	public float TargetDistance { get; set; }

	public bool SkipGuidanceLockout { get; set; }

	public bool IsInGuidanceLockout
	{
		get
		{
			if (!SkipGuidanceLockout)
			{
				return _timeTraveled < Info.GuidanceLockoutTime;
			}
			return false;
		}
	}

	public bool IsDestroyed => _didDestroy;

	public event Action Destroying;

	public event Action<LiveRound, Vector3> Destroyed;

	public event Action RestoredFromPool;

	private void logHit(Vector3 hitPoint)
	{
		if (_isHeat && !_jetActive && !_heatRicochet && _armed && !_failedToDetonate)
		{
			_jetActive = true;
			_jetStartPoint = hitPoint;
			_remainingTravel = Info.RhaPenetration / 100f;
			CurrentSpeed = MaxSpeed;
			AddEvent("Warhead detonated");
		}
		if (!_hitSomething)
		{
			_hitSomething = true;
			ShotInfo.Distance = Vector3.Distance(_initialPosition, hitPoint);
			Story.ImpactPosition = hitPoint;
		}
	}

	private void reportShotTraceFrame()
	{
		ShotInfo.AddShotFrame(_framePos, _frameDir, _frameData, base.transform.localPosition, base.transform.parent);
	}

	private bool penCheck(Collider cObject, Vector3 surfaceNormal, Vector3 roundPath, Vector3 impactPoint, Vector3 proxySpacePos, Vector3 proxySpaceDir, Vector3 proxySpaceNormal, ITarget targetStruck)
	{
		IArmor armor = cObject.GetComponent<IArmor>();
		bool flag = armor != null;
		if (!IsSpall && flag && armor.Unit != null && ShotInfo.Shooter == armor.Unit)
		{
			if (Debug && (!IsSpall || _debugIncludesSpall))
			{
				UnityEngine.Debug.Log(base.gameObject.name + " skipped collider " + cObject.name + " because it belongs to the shooter Unit");
			}
			base.transform.position = base.transform.position + base.transform.forward * 0.1f * Time.deltaTime;
			return true;
		}
		bool jetActive = _jetActive;
		_parentUnit = targetStruck?.Owner ?? null;
		_framePos = impactPoint;
		_frameDir = roundPath;
		_frameData = new ShotInfo.ShotFrameData
		{
			VehicleStruck = null,
			ObjectStruck = null,
			ArmorStruck = null,
			IsKill = false,
			HitSomething = true,
			IsSpall = IsSpall,
			IsJet = _jetActive
		};
		_impactNormal = surfaceNormal;
		GameObject gameObject = cObject.gameObject;
		float currentPenRating = CurrentPenRating;
		if (jetActive && currentPenRating > 50f)
		{
			float currentSpeed = CurrentSpeed;
			float num = currentSpeed * currentSpeed;
			float num2 = 75f * Vector3.Distance(_lastFramePosition, _framePos);
			float num3 = Mathf.Max(currentPenRating - num2, 50f);
			float f = num * (num3 / currentPenRating);
			CurrentSpeed = Mathf.Pow(f, 0.5f);
			_speedRatio *= CurrentSpeed / currentSpeed;
			if (Debug)
			{
				UnityEngine.Debug.Log($"{base.gameObject.name} (jet) was degraded from {currentPenRating}mm RHAe to {num3}mm RHAe due to " + $"{Vector3.Distance(_lastFramePosition, _framePos)} meters of travel");
			}
		}
		if (gameObject.CompareTag("Detection"))
		{
			ShotDetectionZone component = cObject.GetComponent<ShotDetectionZone>();
			if (component != null)
			{
				base.transform.position = base.transform.position + base.transform.forward * 0.1f * Time.deltaTime;
				component.NotifyShot(Info, impactPoint, IsSpall);
				reportShotTraceFrame();
				_lastFramePosition = _framePos;
				if (Debug && (!IsSpall || _debugIncludesSpall))
				{
					UnityEngine.Debug.Log(base.gameObject.name + " passed through detection zone " + gameObject.name);
					DrawDebugStar(_framePos, Color.magenta);
				}
				return true;
			}
		}
		if (_impactTime <= 0f)
		{
			_impactTime = SceneController.MissionTime;
			Story.ImpactTime = _impactTime;
		}
		if (flag)
		{
			_materialHit = armor.SurfaceMaterial;
		}
		else
		{
			_materialHit = ParticleEffectsManager.SurfaceMaterial.Steel;
		}
		if (!_armed)
		{
			float armingDistance = Info.ArmingDistance;
			if (armingDistance == 0f)
			{
				_armed = true;
			}
			else if (Vector3.Distance(impactPoint, _trueInitialPosition) >= armingDistance)
			{
				_armed = true;
			}
		}
		bool flag2 = false;
		bool flag3 = false;
		float num4 = 90f - Vector3.Angle(surfaceNormal, roundPath * -1f);
		float num5 = 0.8f;
		if (num4 < Info.CertainRicochetAngle && !(_isHeat && jetActive) && (!(Info.ShatterOnRicochet && flag) || armor.CanShatterLongRods))
		{
			if (!IsSpall && _isApOrHe && flag && armor.SabotRha * armor.CrushThicknessModifier * 0.001f < _penetratorWidth * num5)
			{
				flag3 = true;
			}
			else if (flag && !armor.CanRicochet)
			{
				flag3 = true;
			}
			else
			{
				flag2 = true;
				if (_isHeat)
				{
					_heatRicochet = true;
				}
			}
		}
		else if (num4 < 3f && _isHeat && jetActive && (armor == null || armor.CanRicochet))
		{
			flag2 = true;
		}
		if (!flag2)
		{
			_heatRicochet = false;
		}
		bool flag4 = false;
		bool tempUnitPoses = ShotInfo.TempUnitPoses;
		if (gameObject.layer == CodeUtils.LAYER_INDEX_TERRAIN)
		{
			if (!(Vector3.Angle(Vector3.up, surfaceNormal) > 85f))
			{
				if (Debug && (!IsSpall || _debugIncludesSpall))
				{
					UnityEngine.Debug.Log(base.gameObject.name + " failed to penetrate terrain");
					DrawDebugStar(_framePos, Color.red);
				}
				if (!IsSpall)
				{
					AddEvent("Stopped by terrain");
				}
				_terrainHit = true;
				_materialHit = ParticleEffectsManager.SurfaceMaterial.Dirt;
				_heatRicochet = false;
				_impactSFXManager.PlayTerrainImpactSFX(base.transform.position, Info, isTree: false, IsSpall);
				logHit(impactPoint);
				_hitSolidObject = true;
				if (_isHe)
				{
					_heHitNormalRha = 9999f;
					_heHitNormal = surfaceNormal;
				}
				reportShotTraceFrame();
				_lastFramePosition = _framePos;
				return false;
			}
			flag4 = true;
			_materialHit = ParticleEffectsManager.SurfaceMaterial.Wood;
			_impactSFXManager.PlayTerrainImpactSFX(base.transform.position, Info, isTree: true, IsSpall);
		}
		bool flag5 = false;
		ProxyHitZone component2 = cObject.GetComponent<ProxyHitZone>();
		if (component2 != null)
		{
			if (component2.Compartment)
			{
				flag5 = true;
				AddEvent("Entered " + component2.FriendlyName);
				if (!IsSpall)
				{
					TellSFXManagerToPlayImpactPenIntPerspSFXEvent(armor?.SabotRha ?? 0f, impactPoint);
				}
			}
			if (component2.DestructibleComponent)
			{
				logHit(impactPoint);
				_frameData.IsDamaging = true;
				_lastFramePosition = impactPoint;
				AddEvent("Hit " + component2.FriendlyName);
			}
			bool doBlast = false;
			if (_isHeat && !_didHeatBlast && _jetActive && Info.TntEquivalentKg > 0f)
			{
				_didHeatBlast = true;
				doBlast = true;
			}
			ProxyHitzoneHitParams parameters = new ProxyHitzoneHitParams
			{
				AmmoInfo = Info,
				DoBlast = doBlast,
				ImpactPoint = impactPoint,
				IsSpall = IsSpall,
				RhaPen = CurrentPenRating,
				Ricocheted = flag2,
				RoundPath = roundPath,
				TargetStruck = targetStruck,
				Round = this,
				RoundID = ID
			};
			component2.NotifyStruck(ref parameters, component2.DestructibleComponent, component2.Compartment, this, ID);
		}
		if (component2 == null)
		{
			GHPC.Equipment.DestructibleComponent destructibleComponent = cObject.GetComponent<GHPC.Equipment.DestructibleComponent>();
			if (destructibleComponent != null && !destructibleComponent.Hidden)
			{
				logHit(impactPoint);
				if (_parentUnit == null)
				{
					_parentUnit = destructibleComponent.Unit;
				}
				int num6 = 0;
				bool flag6 = false;
				while (!flag6 && num6 < 16)
				{
					num6++;
					if (destructibleComponent.Parent != null)
					{
						destructibleComponent = destructibleComponent.Parent as GHPC.Equipment.DestructibleComponent;
					}
					else
					{
						flag6 = true;
					}
				}
				if (Debug && (!IsSpall || _debugIncludesSpall))
				{
					UnityEngine.Debug.Log(base.gameObject.name + " hit component " + destructibleComponent.Name + " (" + CurrentPenRating.ToString("0.0") + "mm RHAe of " + destructibleComponent.DamageThreshold + "mm RHAe required to damage");
				}
				AddEvent("Hit " + destructibleComponent.Name);
				if (CurrentPenRating > destructibleComponent.DamageThreshold && (!flag2 || destructibleComponent.DamageThreshold == 0f))
				{
					ResistanceType threatType = ((Info.Category == AmmoType.AmmoCategory.ShapedCharge) ? ResistanceType.CE : ResistanceType.KE);
					destructibleComponent.ApplyProjectileDamage(impactPoint, roundPath, CurrentPenRating, threatType, Info.Caliber);
					if (_frameData.VehicleStruck == null && targetStruck != null)
					{
						_frameData.VehicleStruck = ((targetStruck.Owner == null) ? destructibleComponent.Unit : targetStruck.Owner);
					}
					_frameData.ObjectStruck = destructibleComponent.gameObject;
					_frameData.IsDamaging = true;
					_lastFramePosition = impactPoint;
					ShotData item = new ShotData
					{
						DamageValue = CurrentPenRating,
						IsSpall = IsSpall
					};
					Story.PenShots.Add(item);
				}
			}
		}
		if (_frameData.ObjectStruck == null && gameObject.CompareTag("Target"))
		{
			_frameData.ObjectStruck = gameObject;
			_lastFramePosition = impactPoint;
		}
		if (component2 == null && !IsSpall && gameObject.CompareTag("Compartment"))
		{
			flag5 = true;
			Compartment component3 = gameObject.GetComponent<Compartment>();
			if (component3 != null)
			{
				_compartmentHit = component3;
				bool flag7 = _isHeat && _armed && !_didHeatBlast && _jetActive && Info.TntEquivalentKg > 0f;
				component3.NotifyPenetrated(Info, impactPoint, roundPath, flag7);
				AddEvent("Entered " + component3.Name);
				if (flag7)
				{
					component3.InsertOverpressure(Info.TntEquivalentKg, base.transform.position);
					_didHeatBlast = true;
				}
				TellSFXManagerToPlayImpactPenIntPerspSFXEvent(flag ? armor.SabotRha : 0f, impactPoint);
			}
		}
		if (armor == null && !flag4)
		{
			base.transform.position = base.transform.position + base.transform.forward * 0.1f * Time.deltaTime;
			if (Debug && (!IsSpall || _debugIncludesSpall))
			{
				UnityEngine.Debug.Log(base.gameObject.name + " passed through object " + gameObject.name + " with no armor");
				DrawDebugStar(_framePos, Color.grey);
			}
			IUnit unit = null;
			if (targetStruck != null)
			{
				unit = targetStruck.Owner;
				if (_frameData.VehicleStruck == null)
				{
					_frameData.VehicleStruck = unit;
				}
			}
			if (unit == null)
			{
				IUnit unit2 = CodeUtils.FindComponentInParentFollowAware<IUnit>(cObject.transform);
				if (unit2 != null)
				{
					if (_frameData.VehicleStruck == null)
					{
						_frameData.VehicleStruck = unit2;
					}
					unit = unit2;
				}
			}
			if (unit != null)
			{
				_lastFramePosition = impactPoint;
				if (_parentUnit == null)
				{
					_parentUnit = unit;
				}
			}
			reportShotTraceFrame();
			return true;
		}
		if (flag4)
		{
			armor = _treeArmor;
		}
		if (_parentUnit == null)
		{
			_parentUnit = armor.Unit;
		}
		if (!_armed)
		{
			_failedToDetonate = true;
			AddEvent("Failed to detonate: not armed yet");
			if (Debug && (!IsSpall || _debugIncludesSpall))
			{
				float num7 = Vector3.Distance(impactPoint, _trueInitialPosition);
				UnityEngine.Debug.Log(base.gameObject.name + " failed to detonate at distance " + num7 + " of " + Info.ArmingDistance);
			}
			logHit(impactPoint);
			_hitSolidObject = true;
			reportShotTraceFrame();
		}
		if (flag3)
		{
			if (Debug && (!IsSpall || _debugIncludesSpall))
			{
				UnityEngine.Debug.Log(base.gameObject.name + " crushed armor (" + armor.SabotRha + " mm) at " + num4 + " degrees");
				DrawDebugStar(_framePos, Color.cyan);
			}
			if (!IsSpall)
			{
				AddEvent("Crushed into thin armor surface");
			}
		}
		if (!flag5 && !flag4 && !gameObject.CompareTag("Penetrable") && !cObject.CompareTag("Target"))
		{
			logHit(impactPoint);
			if (Debug && (!IsSpall || _debugIncludesSpall))
			{
				UnityEngine.Debug.Log(base.gameObject.name + " failed to penetrate generic collider " + gameObject.name);
				DrawDebugStar(_framePos, Color.red);
			}
			if (!IsSpall)
			{
				AddEvent("Stopped by " + gameObject.name);
			}
			_hitSolidObject = true;
			if (_isHe)
			{
				_heHitNormalRha = 9999f;
				_heHitNormal = surfaceNormal;
			}
			reportShotTraceFrame();
			return false;
		}
		_penetratorType = ((Info.Category == AmmoType.AmmoCategory.ShapedCharge) ? ResistanceType.CE : ResistanceType.KE);
		bool flag8 = proxySpacePos != Vector3.zero || proxySpaceDir != Vector3.zero;
		float normalThickness = armor.GetNormalThickness(_penetratorType, flag8 ? proxySpacePos : impactPoint, flag8 ? proxySpaceNormal : surfaceNormal, IsSpall, forcePhysicalDimensionsOnly: true);
		if (armor.IsDetonated && normalThickness == 0f)
		{
			reportShotTraceFrame();
			return true;
		}
		logHit(impactPoint);
		_hitSolidObject = true;
		if (_isHe)
		{
			_heHitNormalRha = normalThickness;
			_heHitNormal = surfaceNormal;
		}
		if (_frameData.VehicleStruck == null)
		{
			if (targetStruck != null && targetStruck.Owner != null)
			{
				_frameData.VehicleStruck = targetStruck.Owner;
			}
			else
			{
				_frameData.VehicleStruck = armor.Unit;
			}
		}
		_frameData.ArmorStruck = armor;
		_lastFramePosition = impactPoint;
		if (armor.IsSlatArmor && _isHeat && !jetActive)
		{
			if (Info.Tandem || Info.IgnoreSlat)
			{
				if (Debug)
				{
					UnityEngine.Debug.Log(base.gameObject.name + " defeated slat armor");
				}
				AddEvent("Defeated slat armor");
			}
			else
			{
				float num8 = UnityEngine.Random.Range(0f, 1f);
				float num9 = 0.65f;
				if (num8 < num9)
				{
					if (Debug)
					{
						UnityEngine.Debug.Log(base.gameObject.name + " destroyed by slat armor");
						DrawDebugStar(_framePos, Color.magenta);
					}
					AddEvent("Destroyed by slat armor");
					if (!IsSpall)
					{
						_needImpulse = true;
					}
					reportShotTraceFrame();
					return false;
				}
				if (Debug)
				{
					UnityEngine.Debug.Log(base.gameObject.name + " detonated by slat armor");
					DrawDebugStar(_framePos, Color.yellow);
				}
				AddEvent("Detonated by slat armor");
			}
		}
		if (flag2)
		{
			doRicochet(cObject, armor, roundPath, surfaceNormal, num4, jetActive);
			return false;
		}
		float num10 = armor.GetLosThickness(_penetratorType, flag8 ? proxySpacePos : impactPoint, flag8 ? proxySpaceNormal : surfaceNormal, flag8 ? proxySpaceDir : roundPath, IsSpall);
		if (Info.ArmorOptimizations != null && Info.ArmorOptimizations.Length != 0)
		{
			AmmoType.ArmorOptimization[] armorOptimizations = Info.ArmorOptimizations;
			for (int i = 0; i < armorOptimizations.Length; i++)
			{
				AmmoType.ArmorOptimization armorOptimization = armorOptimizations[i];
				if (armorOptimization.Armor == armor.ArmorType)
				{
					num10 *= armorOptimization.RhaRatio;
					if (Debug)
					{
						string[] obj = new string[5] { "Armor effectiveness of ", armor.Name, " set to ", null, null };
						float rhaRatio = armorOptimization.RhaRatio;
						obj[3] = rhaRatio.ToString();
						obj[4] = " ratio due to ammo design";
						UnityEngine.Debug.Log(string.Concat(obj));
					}
					break;
				}
			}
		}
		if (!IsSpall)
		{
			_needImpulse = true;
		}
		float currentPenRating2 = CurrentPenRating;
		if (num10 > currentPenRating2)
		{
			if (armor.IsEra && !armor.IsDetonated)
			{
				if (Debug)
				{
					UnityEngine.Debug.Log(base.gameObject.name + " detonated ERA section");
				}
				AddEvent("Detonated ERA");
				armor.Detonate();
				if (Info.Tandem)
				{
					_sacrificedWarhead = true;
				}
			}
			if (Debug && (!IsSpall || _debugIncludesSpall))
			{
				UnityEngine.Debug.Log(base.gameObject.name + " failed to penetrate object " + gameObject.name + " (" + currentPenRating2.ToString("0") + "mm of " + num10.ToString("0") + "mm RHAE LOS, " + normalThickness.ToString("0") + "mm actual at " + num4.ToString("0") + " degrees, speed: " + CurrentSpeed + " m/s)");
				DrawDebugStar(_framePos, Color.red);
			}
			if (!IsSpall)
			{
				AddEvent("Stopped by " + armor.Name);
				if (!flag4)
				{
					ResolveImpactAudio(armor.SabotRha);
				}
			}
			else if (armor.CanRicochet && armor.CanShatterLongRods && currentPenRating2 < 5f && num10 * 0.3f > currentPenRating2)
			{
				float num11 = 0.5f * (1f - currentPenRating2 / 5f);
				if (UnityEngine.Random.Range(0f, 1f) < num11)
				{
					doRicochet(cObject, armor, roundPath, surfaceNormal, num4, jetActive);
					return false;
				}
			}
			reportShotTraceFrame();
			return false;
		}
		armor.NotifyPunctured();
		ShotData item2 = new ShotData
		{
			DamageValue = num10,
			IsSpall = IsSpall
		};
		Story.EffectiveRhaShots.Add(item2);
		if (!IsSpall && !flag4)
		{
			ResolveImpactAudio(armor.SabotRha);
		}
		if (armor.IsEra && !armor.IsDetonated)
		{
			if (Debug)
			{
				UnityEngine.Debug.Log(base.gameObject.name + " detonated ERA section");
			}
			AddEvent("Detonated ERA");
			armor.Detonate();
		}
		float currentSpeed2 = CurrentSpeed;
		float f2 = currentSpeed2 * currentSpeed2 * (1f - num10 / currentPenRating2);
		CurrentSpeed = Mathf.Pow(f2, 0.5f);
		_speedRatio *= CurrentSpeed / _prevSpeed;
		base.transform.position = base.transform.position + base.transform.forward * 0.01f;
		if (Debug && (!IsSpall || _debugIncludesSpall))
		{
			UnityEngine.Debug.Log(base.gameObject.name + " penetrated " + gameObject.name + " (" + currentPenRating2.ToString("0") + "mm of " + num10.ToString("0") + "mm RHAe LOS, " + normalThickness.ToString("0") + "mm actual at " + num4.ToString("0") + " degrees, speed: " + currentSpeed2.ToString("0.0") + " -> " + CurrentSpeed.ToString("0.0") + " m/s, travel time: " + _timeTraveled.ToString("0.000") + " sec)");
			DrawDebugStar(_framePos, Color.green);
		}
		AddEvent("Penetrated " + armor.Name);
		if (!IsSpall && Info.Normalize && armor.NormalizesHits)
		{
			float num12 = num10 / currentPenRating2;
			num12 *= num12 * num12 * num12;
			float num13 = (90f - num4) * num12;
			if (num13 > 5f)
			{
				num13 = 5f;
			}
			Vector3 vector = Vector3.RotateTowards(roundPath, -surfaceNormal, num13 * (MathF.PI / 180f), 0f);
			UnityEngine.Debug.DrawRay(base.transform.position, vector, Color.red);
			_needsNormalization = true;
			_normalizationVector = vector;
		}
		if (!IsSpall && !Info.NoPenSpall && (_isHeat || Info.Radius > 0.0035f))
		{
			float num14 = (_isHeat ? armor.MaxSpallAngleCe : armor.MaxSpallAngleKe);
			Vector3 originPoint = base.transform.position;
			if (armor.SpallForwardRatio > 0f)
			{
				float losThickness = armor.GetLosThickness(_penetratorType, flag8 ? proxySpacePos : impactPoint, flag8 ? proxySpaceNormal : surfaceNormal, flag8 ? proxySpaceDir : roundPath, IsSpall, forcePhysicalDimensionsOnly: true);
				originPoint = base.transform.position + roundPath.normalized * armor.SpallForwardRatio * losThickness / 1000f;
			}
			if (num14 > 0f)
			{
				float sqrDistanceFromDetonation = ((_isHeat && jetActive) ? Vector3.SqrMagnitude(base.transform.position - _jetStartPoint) : (-1f));
				createSpall(armor, roundPath, surfaceNormal, originPoint, num10, currentPenRating2, jetActive, sqrDistanceFromDetonation);
			}
		}
		reportShotTraceFrame();
		if (flag4 && tempUnitPoses)
		{
			ShotInfo.TempUnitPoses = true;
		}
		return true;
	}

	private int getSpallCount(IArmor armor, float maxSpallAngle)
	{
		int num = 100;
		if (armor != null)
		{
			num = Mathf.RoundToInt((float)_maxSpallCount * (maxSpallAngle / 45f) * Info.SpallMultiplier);
		}
		if (num > 100)
		{
			num = 100;
		}
		return num;
	}

	private float getSpallMaxAngle(IArmor armor, float penThickness, float losThickness = -1f, bool jetWasActive = false, float sqrDistanceFromDetonation = -1f)
	{
		if (!_isHeat && Info.Radius < 0.0035f)
		{
			return 0f;
		}
		float num = 0f;
		if (armor != null)
		{
			num = (_isHeat ? armor.MaxSpallAngleCe : armor.MaxSpallAngleKe);
		}
		if (_isHeat)
		{
			float num2 = ((losThickness < 0f) ? armor.HeatRha : losThickness);
			float num3 = Mathf.Clamp01(50f / num2);
			if (!jetWasActive)
			{
				num *= num3;
			}
			else
			{
				float num4 = num * num3;
				float num5 = 0f;
				float num6 = (penThickness - num2) / penThickness;
				if (num6 < 0.16f)
				{
					num5 = 60f * num6 / 0.16f;
				}
				else if (num6 < 0.305f)
				{
					float num7 = (num6 - 0.16f) / 0.145f;
					num5 = 60f + 20f * num7;
				}
				else if (num6 < 45f)
				{
					float num8 = (num6 - 0.305f) / 0.145f;
					num5 = 80f - 20f * num8;
				}
				else
				{
					float num9 = (num6 - 45f) / 55f;
					num5 = 60f - 60f * num9;
				}
				if (sqrDistanceFromDetonation < 0f || sqrDistanceFromDetonation > 0.64f)
				{
					num = num5;
				}
				else
				{
					float num10 = sqrDistanceFromDetonation / 0.64f;
					float b = num10 * num5 + (1f - num10) * num4;
					num = Mathf.Max(num5, b);
				}
			}
		}
		else
		{
			float num11 = ((losThickness < 0f) ? armor.SabotRha : losThickness);
			float num12 = 30f + Mathf.Log(10f * num11);
			if (num11 < 30f)
			{
				num12 *= 0.3f + 0.7f * num11 / 30f;
			}
			float num13 = (penThickness - num11) / 100f;
			if (num13 < 1f)
			{
				num12 *= 0.1f + 0.9f * num13;
			}
			num = Mathf.Min(num, num12);
		}
		if (armor != null && armor.ArmorType != null)
		{
			num *= armor.ArmorType.ArmorType.SpallAngleMultiplier;
		}
		return num;
	}

	private void createSpall(IArmor armorInfo, Vector3 roundPath, Vector3 surfaceNormal, Vector3 originPoint, float losThickness, float penThickness, bool jetWasActive, float sqrDistanceFromDetonation = -1f)
	{
		float spallMaxAngle = getSpallMaxAngle(armorInfo, penThickness, losThickness, jetWasActive, sqrDistanceFromDetonation);
		if (spallMaxAngle < 2f)
		{
			return;
		}
		float num = 1f;
		if (Info.Radius < 0.01f)
		{
			num = (Info.Radius - 0.0035f) / 0.0064999997f;
		}
		spallMaxAngle *= num;
		if (spallMaxAngle < 2f)
		{
			return;
		}
		float num2 = (float)getSpallCount(armorInfo, spallMaxAngle) * num;
		float num3 = ((num == 1f) ? (num2 / 8f) : 0f);
		for (int i = 0; (float)i < num2; i++)
		{
			LiveRound liveRound = null;
			liveRound = LiveRoundMarshaller.Instance.GetRoundOfVisualType(LiveRoundMarshaller.LiveRoundVisualType.Spall);
			liveRound.Info = SpallAmmoType;
			liveRound.NpcRound = NpcRound;
			liveRound.Info.VisualType = LiveRoundMarshaller.LiveRoundVisualType.Spall;
			liveRound.IsSpall = true;
			liveRound.Shooter = Shooter;
			liveRound.transform.position = originPoint;
			liveRound.transform.forward = roundPath;
			if (_needsNormalization)
			{
				liveRound.transform.forward = _normalizationVector;
			}
			float num4 = 90f;
			if (Info.ForcedSpallAngle > 0f)
			{
				num4 = Info.ForcedSpallAngle;
			}
			if (spallMaxAngle > 0f)
			{
				num4 = Mathf.Min(num4, spallMaxAngle);
			}
			if (Info.SphericalSpall)
			{
				num4 = 200f;
			}
			Quaternion quaternion = Quaternion.Euler(UnityEngine.Random.Range((0f - num4) * 0.5f, num4 * 0.5f), UnityEngine.Random.Range((0f - num4) * 0.5f, num4 * 0.5f), UnityEngine.Random.Range((0f - num4) * 0.5f, num4 * 0.5f));
			Vector3 vector;
			if (Info.Normalize && armorInfo.NormalizesHits && _isApOrHe && (float)i < num2 / 2f)
			{
				vector = Vector3.RotateTowards(-surfaceNormal, _normalizationVector, num4 * 0.5f * (MathF.PI / 180f), 0f);
			}
			else
			{
				vector = quaternion * roundPath;
				if (_needsNormalization)
				{
					vector = quaternion * _normalizationVector;
				}
			}
			Vector3 to = (_needsNormalization ? _normalizationVector : roundPath);
			float num5 = Vector3.Angle(vector, to);
			float num6 = num5 / num4;
			float f = 1f - num6;
			f = Mathf.Pow(f, 3f);
			float num7 = 100f;
			float num8 = 1000f;
			if (_isApOrHe && CurrentSpeed < Info.MuzzleVelocity)
			{
				num8 = CurrentSpeed;
			}
			if (num7 > num8)
			{
				num7 = num8;
			}
			float num9 = num8 - num7;
			float value = num7 + num9 * f;
			liveRound.MaxSpeed = (liveRound.CurrentSpeed = Mathf.Clamp(value, num7, num8));
			if (_isApOrHe && (num6 < 0.2f || num5 < 10f) && num3 > 0f)
			{
				float num10 = Info.RhaPenetration / 15f;
				float num11 = Info.RhaPenetration / 8f - num10;
				num11 *= f;
				float rhaPenetrationOverride = UnityEngine.Random.Range(num10, num10 + num11);
				liveRound.RhaPenetrationOverride = rhaPenetrationOverride;
				num3 -= 1f;
			}
			else if (_isHeat || (_isApOrHe && UnityEngine.Random.Range(0f, 1f) > 0.2f))
			{
				float num12 = Info.MaxSpallRha * Mathf.Clamp01(f + 0.15f);
				float num13 = num12 * 0.8f;
				if (num13 < Info.MinSpallRha)
				{
					num13 = Info.MinSpallRha;
				}
				if (_isHeat && !jetWasActive)
				{
					num12 *= 2f;
					num13 *= 2f;
				}
				float num14 = num12 - num13;
				float num15 = UnityEngine.Random.Range(num13, num13 + num14);
				num15 *= armorInfo?.ArmorType?.ArmorType.SpallPowerMultiplier ?? 1f;
				liveRound.RhaPenetrationOverride = num15;
			}
			else
			{
				float num16 = UnityEngine.Random.Range(Info.MinSpallRha, Info.MaxSpallRha);
				num16 *= armorInfo?.ArmorType?.ArmorType.SpallPowerMultiplier ?? 1f;
				liveRound.RhaPenetrationOverride = num16;
			}
			liveRound.transform.forward = vector;
			liveRound.Init(this);
			liveRound.gameObject.name = "spall " + liveRound.ID;
		}
	}

	private void createExplosion(bool hitSurface, float surfaceRha, Vector3 surfaceNormal, float setback, int spallCount = 50)
	{
		if (!_exploded)
		{
			if (Info.DetonateSpallCount > 0)
			{
				spallCount = Info.DetonateSpallCount;
			}
			Vector3 position = base.transform.position + base.transform.forward * (0f - setback);
			for (int i = 0; i < spallCount; i++)
			{
				float rhaPenetrationOverride = UnityEngine.Random.Range(Info.MinSpallRha, Info.MaxSpallRha);
				float num = 360f;
				Vector3 forward = Quaternion.Euler(UnityEngine.Random.Range((0f - num) * 0.5f, num * 0.5f), UnityEngine.Random.Range((0f - num) * 0.5f, num * 0.5f), UnityEngine.Random.Range((0f - num) * 0.5f, num * 0.5f)) * Vector3.forward;
				LiveRound liveRound = null;
				liveRound = LiveRoundMarshaller.Instance.GetRoundOfVisualType(LiveRoundMarshaller.LiveRoundVisualType.Invisible);
				liveRound.Info = SpallAmmoType;
				liveRound.RhaPenetrationOverride = rhaPenetrationOverride;
				liveRound.CurrentSpeed = SpallAmmoType.MuzzleVelocity;
				liveRound.MaxSpeed = SpallAmmoType.MuzzleVelocity;
				liveRound.NpcRound = NpcRound;
				liveRound.IsSpall = true;
				liveRound.Shooter = Shooter;
				liveRound.transform.position = position;
				liveRound.transform.forward = forward;
				liveRound.Info.VisualType = LiveRoundMarshaller.LiveRoundVisualType.Invisible;
				liveRound.Init(this);
				liveRound.gameObject.name = "explosion spall invisible " + liveRound.ID;
			}
			_exploded = true;
		}
	}

	private void HandleScabEffect(float armorRha, Vector3 armorNormal)
	{
		Vector3 vector = -armorNormal;
		float num = Vector3.Angle(base.transform.forward, vector);
		if (num > 70f)
		{
			if (Debug)
			{
				UnityEngine.Debug.Log(base.gameObject.name + " failed to scab due to impact angle", this);
			}
			AddEvent("Failed to squash due to impact angle");
			return;
		}
		float num2 = Info.Caliber;
		if (num2 == 0f)
		{
			num2 = Info.Radius * 2000f;
		}
		float num3 = armorRha / num2;
		if (Debug)
		{
			UnityEngine.Debug.Log($"{base.gameObject.name} found armor thickness of {num3} calibers for squash head", this);
		}
		float num4 = 0f;
		num4 = ((num < 25f) ? (0.01f * num + 1f) : ((num < 35f) ? (0.025f * num + 0.625f) : ((!(num < 40f)) ? (-0.05f * num + 3.5f) : 1.5f)));
		float num5 = num4 * 1.3333f;
		float num6 = num4 * 1.5f;
		float num7 = 0f;
		if (num3 > num6)
		{
			if (Debug)
			{
				UnityEngine.Debug.Log(base.gameObject.name + " failed to scab due to armor thickness", this);
			}
			AddEvent("Failed to produce armor scab");
			return;
		}
		if (num3 > num5)
		{
			float num8 = (num3 - num5) / (num6 - num5);
			num7 = 0.5f * (1f - num8);
		}
		else if (num3 > num4)
		{
			float num9 = (num3 - num4) / (num5 - num4);
			num7 = 0.5f + 0.5f * (1f - num9);
		}
		else
		{
			num7 = 1f;
		}
		if (Debug)
		{
			UnityEngine.Debug.Log($"{base.gameObject.name} determined scab chance of {num7}", this);
		}
		if (UnityEngine.Random.Range(0f, 1f) > num7)
		{
			if (Debug)
			{
				UnityEngine.Debug.Log(base.gameObject.name + " failed to scab due to random roll", this);
			}
			AddEvent("Failed to produce armor scab");
			return;
		}
		if (Debug)
		{
			UnityEngine.Debug.Log(base.gameObject.name + " successfully scabbed!", this);
		}
		AddEvent("Blasted off armor scab");
		int num10 = 5 + Mathf.RoundToInt(15f * num7);
		float num11 = 20f + 25f * num7;
		float num12 = 15f;
		float num13 = 300f;
		for (int i = 0; i < num10; i++)
		{
			Vector3 vector2 = Quaternion.Euler(UnityEngine.Random.Range((0f - num11) * 0.5f, num11 * 0.5f), UnityEngine.Random.Range((0f - num11) * 0.5f, num11 * 0.5f), UnityEngine.Random.Range((0f - num11) * 0.5f, num11 * 0.5f)) * vector;
			float num14 = Mathf.Clamp01(Vector3.Angle(vector2, vector) / num11);
			float rhaPenetrationOverride = 5f + num12 * (1f - num14);
			float num15 = 300f + num13 * num14;
			LiveRound liveRound = null;
			liveRound = LiveRoundMarshaller.Instance.GetRoundOfVisualType(LiveRoundMarshaller.LiveRoundVisualType.Spall);
			liveRound.Info = SpallAmmoType;
			liveRound.NpcRound = NpcRound;
			liveRound.Info.VisualType = LiveRoundMarshaller.LiveRoundVisualType.Spall;
			liveRound.IsSpall = true;
			liveRound.Shooter = Shooter;
			liveRound.transform.forward = vector2;
			liveRound.transform.position = base.transform.position + liveRound.transform.forward * 0.01f;
			liveRound.gameObject.name = "scab spall " + liveRound.ID;
			liveRound.CurrentSpeed = num15;
			liveRound.MaxSpeed = num15;
			liveRound.RhaPenetrationOverride = rhaPenetrationOverride;
			liveRound.Init(this);
		}
	}

	private void doRicochet(Collider cObject, IArmor armorInfo, Vector3 roundPath, Vector3 surfaceNormal, float impactAngle, bool jetWasActive)
	{
		GameObject gameObject = cObject.gameObject;
		_impulseRatio = 0.5f;
		if (!IsSpall)
		{
			_needImpulse = true;
		}
		if (_isHeat && !jetWasActive && Info.ShatterOnRicochet)
		{
			_crushed = true;
			if (Debug && (!IsSpall || _debugIncludesSpall))
			{
				UnityEngine.Debug.Log(base.gameObject.name + " was crushed by angle on " + gameObject.name + " at " + impactAngle + " degrees");
			}
			if (!IsSpall)
			{
				AddEvent("Crushed by impact angle");
			}
			_jetActive = false;
			reportShotTraceFrame();
			return;
		}
		bool flag = Info.ShatterOnRicochet && armorInfo.CanShatterLongRods;
		if (Debug && (!IsSpall || _debugIncludesSpall))
		{
			if (flag)
			{
				UnityEngine.Debug.Log(base.gameObject.name + " shattered due to impact angle");
			}
			UnityEngine.Debug.Log(base.gameObject.name + " ricocheted off of " + gameObject.name + " at " + impactAngle + " degrees");
			DrawDebugStar(_framePos, Color.yellow);
		}
		if (!IsSpall)
		{
			if (flag)
			{
				AddEvent("Shattered due to impact angle");
			}
			string text = gameObject.name;
			if (armorInfo != null && armorInfo.Name != "")
			{
				text = armorInfo.Name;
			}
			AddEvent("Ricocheted off of " + text);
		}
		Vector3 normalizationVector = Vector3.Reflect(roundPath, surfaceNormal);
		_needsNormalization = true;
		_normalizationVector = normalizationVector;
		_ricochet = true;
		_ricochetCount++;
		if (!_isHeat)
		{
			if (flag)
			{
				CurrentSpeed *= 0.1f;
			}
			else if (IsSpall)
			{
				CurrentSpeed *= 0.6f;
			}
			else
			{
				CurrentSpeed *= 0.5f;
			}
		}
		_speedRatio *= CurrentSpeed / _prevSpeed;
		if (!IsSpall)
		{
			doImpactEffect();
		}
		reportShotTraceFrame();
		if (!IsSpall)
		{
			TellSFXManagerToPlayRicochetSFXEvent();
		}
	}

	private void AddEvent(string eventString)
	{
		if (ParentRound != null)
		{
			ParentRound.Story.TryAddStoryEvent(eventString);
		}
		Story.TryAddStoryEvent(eventString);
	}

	private void ReportShotStory()
	{
	}

	public void Detonate()
	{
		if (!IsSpall)
		{
			doImpactEffect(_terrainHit);
			if (!_failedToDetonate && (_isHeat || _isHe))
			{
				AddEvent("Warhead detonated");
			}
		}
		if (_terrainHit)
		{
			_didTerrainHit = true;
		}
		_detonated = true;
		if (!_reported)
		{
			ReportShotStory();
			_reported = true;
		}
		if (Debug && (!IsSpall || _debugIncludesSpall))
		{
			UnityEngine.Debug.Log(base.gameObject.name + " destroyed");
		}
	}

	private void SetEffectsData(bool terrainHit)
	{
		_fusedStatus = ParticleEffectsManager.FusedStatus.Unfuzed;
		if (terrainHit)
		{
			_materialHit = ParticleEffectsManager.SurfaceMaterial.Dirt;
		}
		if (!_shatter && !_didFuzedEffect && !_ricochet && _armed && !(Info.TntEquivalentKg <= 0f) && !(_heHitNormalRha < Info.RhaToFuse))
		{
			_fusedStatus = ParticleEffectsManager.FusedStatus.Fuzed;
			if (!_didFuzedEffect && (_fuzeCompleted || _jetActive))
			{
				_fusedStatus = ParticleEffectsManager.FusedStatus.Fuzed;
			}
		}
	}

	private void doImpactEffect(bool terrainHit = false)
	{
		SetEffectsData(terrainHit);
		if ((_impactEffectCount > 0 && (_isHeat || _isHe)) || (!terrainHit && (_impactEffectCount > 0 || (_impactEffectCount > 0 && IsSpall)) && !_ricochet))
		{
			return;
		}
		_impactEffectCount++;
		bool fuzed = false;
		if (terrainHit)
		{
			if (_didTerrainHit)
			{
				return;
			}
			if (!_didFuzedEffect && (_fuzeCompleted || _jetActive))
			{
				_didFuzedEffect = true;
				fuzed = true;
			}
		}
		else if (!_didFuzedEffect && (_fuzeCompleted || _jetActive))
		{
			_didFuzedEffect = true;
			fuzed = true;
		}
		if (_didFuzedEffect && !_failedToDetonate && Info.TntEquivalentKg > 0f)
		{
			Explosions.RegisterExplosion(base.transform.position, Info.TntEquivalentKg);
			if (_isHeat && _compartmentHit != null)
			{
				GHPC.Equipment.DestructibleComponent.TryOverpressureDamage(Info.TntEquivalentKg * 0.1f, base.transform.position + base.transform.forward * -0.1f);
				GHPC.Equipment.DestructibleComponent.TryOverpressureDamage(Info.TntEquivalentKg * 0.9f, base.transform.position + base.transform.forward * 0.01f);
			}
			else
			{
				GHPC.Equipment.DestructibleComponent.TryOverpressureDamage(Info.TntEquivalentKg, base.transform.position + base.transform.forward * -0.1f);
			}
			if (_isHe)
			{
				_compartmentHit?.InsertOverpressure(Info.TntEquivalentKg, base.transform.position);
			}
			CameraJiggler.RequestExplosiveReaction(base.transform.position, Info.TntEquivalentKg);
			BlurManager.RequestExplosiveReaction(base.transform.position, Info.TntEquivalentKg);
		}
		if (!_shatter)
		{
			GameObject gameObject = ParticleEffectsManager.Instance.CreateImpactEffectOfType(Info, _fusedStatus, _materialHit, _ricochet, base.transform.position);
			if (gameObject != null)
			{
				if (terrainHit)
				{
					if (_isPureAp)
					{
						gameObject.transform.forward = base.transform.forward;
					}
					else
					{
						gameObject.transform.rotation = Quaternion.identity;
					}
				}
				else if (!_ricochet)
				{
					if (_isHeat)
					{
						gameObject.transform.forward = base.transform.forward;
						HeatJet componentInChildren = gameObject.GetComponentInChildren<HeatJet>();
						if (componentInChildren != null)
						{
							componentInChildren.gameObject.SetActive(value: true);
							float jetLength = Vector3.Distance(_jetStartPoint, base.transform.position);
							componentInChildren.SetJetLength(jetLength);
						}
					}
					else
					{
						gameObject.transform.forward = _impactNormal;
					}
				}
			}
		}
		ImpactSFXManager.Instance?.PlaySimpleImpactAudio(Info.ImpactAudio, base.transform.position, fuzed);
		if (_ricochet && _needsNormalization && !IsSpall && ParticleEffectsManager.Instance.RicochetEffectPrefab != null)
		{
			GameObject gameObject2 = ParticleEffectsManager.Instance.CreateImpactEffectOfType(Info, _fusedStatus, _materialHit, isRicochet: true, base.transform.position, ParticleEffectsManager.Instance.transform);
			if (gameObject2 != null)
			{
				gameObject2.transform.forward = _normalizationVector;
			}
		}
	}

	public void ForceDestroy()
	{
		doDestroy();
	}

	private void doDestroy()
	{
		if (_didDestroy)
		{
			return;
		}
		destroyVisuals();
		if (!_reported)
		{
			_waitingForReport = true;
			return;
		}
		this.Destroyed?.Invoke(this, base.transform.position);
		ShotInfo.StopPosition = base.transform.position;
		ShotInfo.Story = Story;
		ShotInfo.NotifyShotTerminated();
		LiveRoundBatchHandler.RemoveRound(this, Info.DoNotDestroy);
		if (!Info.DoNotDestroy)
		{
			_didDestroy = true;
			IsSpall = false;
			this.Destroying?.Invoke();
		}
		else
		{
			_didDestroy = true;
			base.enabled = false;
		}
	}

	private void destroyVisuals()
	{
		Light[] componentsInChildren = GetComponentsInChildren<Light>();
		foreach (Light obj in componentsInChildren)
		{
			obj.enabled = false;
			foreach (Transform item in obj.gameObject.transform)
			{
				item.gameObject.SetActive(value: false);
			}
		}
		ParticleSystem[] componentsInChildren2 = GetComponentsInChildren<ParticleSystem>();
		for (int i = 0; i < componentsInChildren2.Length; i++)
		{
			componentsInChildren2[i].Stop();
		}
		MeshRenderer[] componentsInChildren3 = GetComponentsInChildren<MeshRenderer>();
		for (int i = 0; i < componentsInChildren3.Length; i++)
		{
			componentsInChildren3[i].enabled = false;
		}
		SkinnedMeshRenderer[] componentsInChildren4 = GetComponentsInChildren<SkinnedMeshRenderer>();
		for (int i = 0; i < componentsInChildren4.Length; i++)
		{
			componentsInChildren4[i].enabled = false;
		}
		AudioSource[] componentsInChildren5 = GetComponentsInChildren<AudioSource>();
		for (int i = 0; i < componentsInChildren5.Length; i++)
		{
			componentsInChildren5[i].Stop();
		}
		AutoBlendshape[] componentsInChildren6 = GetComponentsInChildren<AutoBlendshape>();
		for (int i = 0; i < componentsInChildren6.Length; i++)
		{
			componentsInChildren6[i].Abort = true;
		}
	}

	public void RestoreFromPool()
	{
		Light[] componentsInChildren = GetComponentsInChildren<Light>();
		for (int i = 0; i < componentsInChildren.Length; i++)
		{
			componentsInChildren[i].enabled = true;
		}
		ParticleSystem[] componentsInChildren2 = GetComponentsInChildren<ParticleSystem>();
		for (int i = 0; i < componentsInChildren2.Length; i++)
		{
			componentsInChildren2[i].Play();
		}
		MeshRenderer[] componentsInChildren3 = GetComponentsInChildren<MeshRenderer>();
		for (int i = 0; i < componentsInChildren3.Length; i++)
		{
			componentsInChildren3[i].enabled = true;
		}
		SkinnedMeshRenderer[] componentsInChildren4 = GetComponentsInChildren<SkinnedMeshRenderer>();
		for (int i = 0; i < componentsInChildren4.Length; i++)
		{
			componentsInChildren4[i].enabled = true;
		}
		AudioSource[] componentsInChildren5 = GetComponentsInChildren<AudioSource>();
		foreach (AudioSource audioSource in componentsInChildren5)
		{
			if (audioSource.playOnAwake)
			{
				audioSource.Play();
			}
		}
		_needsRestart = true;
		this.RestoredFromPool?.Invoke();
	}

	public void Init(LiveRound parentRound = null, IUnit target = null)
	{
		ID = LiveRoundMarshaller.Instance.GetNextShotId();
		ParentRound = parentRound;
		ShotInfo = new ShotInfo(base.transform.position, base.transform.forward, Info, Shooter, (ParentRound != null) ? ParentRound.ShotInfo : null, RhaPenetrationOverride);
		Unit currentPlayerUnit = PlayerInput.Instance.CurrentPlayerUnit;
		if (Shooter != null && Shooter == currentPlayerUnit)
		{
			ShotInfo.IsPlayerShot = true;
		}
		if (target == currentPlayerUnit)
		{
			ShotInfo.PlayerTargeted = true;
		}
		resetLocalValues();
		UseErrorCorrection = !IsSpall && Info.UseErrorCorrection;
		if (!IsSpall)
		{
			Story.ShotNumber = ID;
			ScenarioResultManager.NotifyShotFired(Shooter);
		}
		LiveRoundBatchHandler.AddRound(this);
	}

	private void resetLocalValues()
	{
		_detonated = false;
		_jetActive = false;
		_didFuzedEffect = false;
		_jetStartPoint = Vector3.zero;
		_reported = false;
		_exploded = false;
		_hitSolidObject = false;
		_armed = false;
		GuidanceStage = 0;
		_impactFuseActive = false;
		_impactFuseCountdown = 0f;
		_rangedFuseActive = false;
		_rangedFuseCountdown = 0f;
		_fuzeCompleted = false;
		_failedToDetonate = false;
		_parentUnit = null;
		_heHitNormalRha = 0f;
		_heHitNormal = Vector3.zero;
		_timeTraveled = 0f;
		_subTimeTraveled = 0f;
		_lastFramePosition = Vector3.zero;
		_impactTime = -1f;
		_initialVelocity = Vector3.zero;
		_initialPosition = Vector3.zero;
		_trueInitialPosition = Vector3.zero;
		_lastTotalDisp = Vector3.zero;
		_prevSpeed = 0f;
		_speedRatio = 1f;
		_terrainHit = false;
		_didTerrainHit = false;
		_hitSomething = false;
		_sacrificedWarhead = false;
		_impactEffectCount = 0;
		_needsNormalization = false;
		_normalizationVector = Vector3.zero;
		_shatter = false;
		_ricochet = false;
		_ricochetCount = 0;
		_crushed = false;
		_heatRicochet = false;
		_needImpulse = false;
		_calculatedImpulse = false;
		_impulseRatio = 1f;
		_impulseTarget = null;
		_impulseDirection = Vector3.zero;
		_impulsePoint = Vector3.zero;
		_layerMask = 0;
		_didDestroy = false;
		_waitingForReport = false;
		_remainingTravel = 0f;
		_lastTHitEdge = null;
		_lastTHitCenter = null;
		_lastTHitEdgeMarch = 0f;
		_prevPosition = Vector3.zero;
		_isApOrHe = Info.Category == AmmoType.AmmoCategory.Penetrator || Info.Category == AmmoType.AmmoCategory.Explosive;
		_isHeat = Info.Category == AmmoType.AmmoCategory.ShapedCharge;
		_isPureAp = Info.Category == AmmoType.AmmoCategory.Penetrator;
		_isHe = Info.Category == AmmoType.AmmoCategory.Explosive;
		_compartmentHit = null;
		_didHeatBlast = false;
		_impactNormal = Vector3.zero;
		Story = new ShotStory();
		RhaPenetrationOverride = 0f;
	}

	public void Restart()
	{
		_needsRestart = false;
		Start();
	}

	private void Start()
	{
		_hasStartBeenCalled = true;
		_initialVelocity = CurrentSpeed * base.transform.forward;
		_initialPosition = base.transform.position;
		_trueInitialPosition = base.transform.position;
		_prevSpeed = _initialVelocity.magnitude;
		_lastTotalDisp = Vector3.zero;
		_penetratorWidth = Mathf.Sqrt(Info.SectionalArea / MathF.PI) * 2f;
		_layerMask = ConstantsAndInfoManager.Instance.LiveRoundLayerMask.value;
		if (IsSpall)
		{
			_maxTravelTime = 1f;
		}
		if (!IsSpall)
		{
			ShotInfo.ForceUnitPoses(temp: true);
		}
		else if (ParentRound != null)
		{
			Story.ShotNumber = ParentRound.Story.ShotNumber;
			if (ParentRound.ShotInfo.UnitPoses.Count == 0)
			{
				ParentRound.ShotInfo.ForceUnitPoses(temp: true);
			}
		}
		float num = CurrentSpeed * (1f + 0.0004f * (WorldEnvironmentManager.AmmoTempFahrenheit - 70f));
		_currentMotionState = new MotionState
		{
			position = base.transform.position,
			velocity = base.transform.forward * num
		};
		if (Info.RangedFuseTime > 0f)
		{
			if (Debug)
			{
				UnityEngine.Debug.Log("Timed range fuse activated");
			}
			AddEvent("Timed fuse activated on launch");
			_rangedFuseActive = true;
			_rangedFuseCountdown = Info.RangedFuseTime;
		}
		_impactSFXManager = ImpactSFXManager.Instance;
	}

	public void DoUpdate(float dt)
	{
		if (!_hasStartBeenCalled)
		{
			return;
		}
		if (_needsRestart)
		{
			Restart();
		}
		if (!_reported && _impactTime > 0f && Time.timeSinceLevelLoad - _impactTime > 0.5f)
		{
			ReportShotStory();
			_reported = true;
		}
		if (_waitingForReport && _reported)
		{
			doDestroy();
		}
		if (Pooled)
		{
			return;
		}
		ResetFrameImpactSounds();
		if (_needImpulse && _calculatedImpulse)
		{
			float num = Info.Mass * CurrentSpeed;
			num *= _impulseRatio;
			num = Mathf.Min(num, 50000f);
			if (Info.Radius > 0.0099f)
			{
				_impulseTarget.AddForceAtPosition(num * _impulseDirection, _impulsePoint, ForceMode.Impulse);
			}
			_needImpulse = false;
			_calculatedImpulse = false;
		}
		_timeTraveled += dt;
		_subTimeTraveled += dt;
		if (_timeTraveled > 60f || (_timeTraveled > _maxTravelTime && !Guided))
		{
			if (Debug && (!IsSpall || _debugIncludesSpall))
			{
				UnityEngine.Debug.Log("Destroying " + base.gameObject.name + " because of age");
			}
			if (!_reported)
			{
				ReportShotStory();
				_reported = true;
			}
			doDestroy();
			return;
		}
		if (base.transform.position.y < -300f)
		{
			if (Debug && (!IsSpall || _debugIncludesSpall))
			{
				UnityEngine.Debug.Log("Destroying " + base.gameObject.name + " because it fell below minimum altitude");
			}
			if (!_reported)
			{
				ReportShotStory();
				_reported = true;
			}
			doDestroy();
			return;
		}
		if ((!IsSpall && !Guided && CurrentSpeed <= 20f) || (IsSpall && CurrentSpeed < 10f))
		{
			if (Debug && (!IsSpall || _debugIncludesSpall))
			{
				UnityEngine.Debug.Log("Destroying " + base.gameObject.name + " due to speed loss");
			}
			if (!_reported)
			{
				ReportShotStory();
				_reported = true;
			}
			doDestroy();
			return;
		}
		if (_detonated)
		{
			doDestroy();
			return;
		}
		if (_jetActive)
		{
			if (Debug)
			{
				UnityEngine.Debug.Log(base.gameObject.name + " HEAT jet expired");
			}
			_detonated = true;
			if (!_reported)
			{
				ReportShotStory();
				_reported = true;
			}
			return;
		}
		Vector3 current;
		if (Guided)
		{
			base.transform.Rotate(0f, 0f, Info.SpiralAngularRate * dt);
			base.transform.Rotate(Info.SpiralPower * dt, 0f, 0f);
			Vector3 forward = base.transform.forward;
			current = forward * Info.MuzzleVelocity * dt;
			Vector3 target = (_failedToDetonate ? Vector3.down : GoalVector);
			current = Vector3.RotateTowards(current, target, Info.TurnSpeed * dt, 0f);
			Quaternion quaternion = MathUtil.FromToRotation(forward, current);
			if (!IsInGuidanceLockout)
			{
				base.transform.rotation = quaternion * base.transform.rotation;
			}
			if (Info.NoisePowerX > 0f || Info.NoisePowerY > 0f)
			{
				float timeSinceLevelLoad = Time.timeSinceLevelLoad;
				Vector3 vector = Quaternion.AngleAxis(90f, Vector3.up) * base.transform.forward * (Info.NoisePowerX * dt * (-0.5f + Mathf.PerlinNoise(timeSinceLevelLoad * Info.NoiseTimeScale, 1f)));
				Vector3 vector2 = Info.NoisePowerY * dt * (-0.5f + Mathf.PerlinNoise(1f, timeSinceLevelLoad * Info.NoiseTimeScale)) * Vector3.up;
				Vector3 vector3 = vector + vector2;
				forward = base.transform.forward;
				Vector3 v = vector3 + forward * Info.MuzzleVelocity;
				Quaternion quaternion2 = MathUtil.FromToRotation(forward, v);
				base.transform.rotation = quaternion2 * base.transform.rotation;
			}
		}
		else
		{
			_previousMotionState = _currentMotionState;
			_currentMotionState = (UseErrorCorrection ? BallisticEvaluatorRK4.EvaluateTimestep(_currentMotionState, dt, Info) : BallisticEvaluatorNonCorrected.EvaluateTimestep(_currentMotionState, dt, Info));
			base.transform.forward = _currentMotionState.position - _previousMotionState.position;
			current = _currentMotionState.position - _previousMotionState.position;
		}
		bool flag = false;
		_remainingTravel = current.magnitude;
		int num2 = 0;
		int num3 = 20;
		bool flag2 = false;
		if (_impactFuseActive)
		{
			_impactFuseCountdown -= dt;
			if (_impactFuseCountdown <= 0f)
			{
				if (Debug)
				{
					UnityEngine.Debug.Log("Detonating explosive due to impact fuse");
				}
				_fuzeCompleted = true;
				AddEvent("Impact delay fuse triggered");
				ShotInfo.ShotFrameData frameData = new ShotInfo.ShotFrameData
				{
					ArmorStruck = null,
					ObjectStruck = null,
					VehicleStruck = null,
					IsKill = false,
					IsSpall = IsSpall,
					IsJet = _jetActive
				};
				ShotInfo.AddShotFrame(base.transform.position, current, frameData, base.transform.localPosition, base.transform.parent);
				_lastFramePosition = base.transform.position;
				createExplosion(hitSurface: false, 0f, Vector3.zero, 0.03f);
				Detonate();
				flag = true;
			}
			else
			{
				float num4 = CurrentSpeed * _impactFuseCountdown;
				if (num4 < _remainingTravel)
				{
					_remainingTravel = num4;
					current *= num4 / current.magnitude;
					flag2 = true;
					if (Debug)
					{
						UnityEngine.Debug.Log("Timed fuse activated with only " + num4 + "m to go");
					}
				}
			}
		}
		else if (_rangedFuseActive)
		{
			_rangedFuseCountdown -= dt;
			if (_rangedFuseCountdown <= 0f)
			{
				if (Debug)
				{
					UnityEngine.Debug.Log("Detonating explosive due to timed range fuse");
				}
				_fuzeCompleted = true;
				AddEvent("Timed fuse triggered");
				createExplosion(hitSurface: false, 0f, Vector3.zero, 0.03f);
				Detonate();
				flag = true;
			}
		}
		while (!flag && num2 < num3)
		{
			num2++;
			Ray ray = new Ray(base.transform.position, current);
			RaycastHit rayHit = default(RaycastHit);
			Vector3 vector4 = Vector3.zero;
			bool flag3 = false;
			if (_remainingTravel <= 0f)
			{
				flag = true;
				break;
			}
			Vector3 surfaceNormal = -base.transform.forward;
			bool flag4 = true;
			Vector3 vector5 = base.transform.position;
			Transform lastTHitCenter = null;
			Vector3 proxyPos = Vector3.zero;
			Vector3 proxyDir = Vector3.zero;
			Vector3 proxyNormal = Vector3.zero;
			float num5 = Info.Radius;
			bool flag5 = true;
			bool flag6 = true;
			if (_isHeat && _jetActive)
			{
				num5 = 0.0025f;
				flag5 = false;
			}
			if (flag5)
			{
				Vector3 origin = ray.origin;
				Vector3 end = ray.origin + ray.direction.normalized * _remainingTravel;
				flag6 = Physics.CheckCapsule(origin, end, Info.Radius, _layerMask);
				flag5 = flag5 && flag6;
			}
			Transform finalNewParent = null;
			if (flag6 && ProxyHitCheck.HitCheck(ray, _remainingTravel, _layerMask, out rayHit, out finalNewParent, out proxyPos, out proxyDir, out proxyNormal))
			{
				vector5 = rayHit.point;
				_remainingTravel -= rayHit.distance;
				flag3 = true;
				surfaceNormal = rayHit.normal;
				flag4 = false;
				lastTHitCenter = rayHit.collider.transform;
			}
			ITarget target2 = null;
			if (finalNewParent != null)
			{
				target2 = finalNewParent.GetComponentInParent<ITarget>();
			}
			int num6 = 0;
			float num7 = float.MaxValue;
			RaycastHit raycastHit = default(RaycastHit);
			Transform transform = null;
			if (flag5)
			{
				for (int i = 0; i < 6; i++)
				{
					Vector3 vector6 = Vector3.zero;
					float num8 = num5;
					Vector3 b = Vector3.zero;
					switch (i)
					{
					case 0:
						vector6 = base.transform.right * num8;
						b = _lastEdgeHit0;
						break;
					case 1:
						vector6 = base.transform.right * 0.5f * num8 + base.transform.up * 0.866f * num8;
						b = _lastEdgeHit1;
						break;
					case 2:
						vector6 = base.transform.right * -0.5f * num8 + base.transform.up * 0.866f * num8;
						b = _lastEdgeHit2;
						break;
					case 3:
						vector6 = base.transform.right * (0f - num8);
						b = _lastEdgeHit3;
						break;
					case 4:
						vector6 = base.transform.right * -0.5f * num8 + base.transform.up * -0.866f * num8;
						b = _lastEdgeHit4;
						break;
					case 5:
						vector6 = base.transform.right * 0.5f * num8 + base.transform.up * -0.866f * num8;
						b = _lastEdgeHit5;
						break;
					}
					vector6 += base.transform.forward * _lastTHitEdgeMarch;
					Ray ray2 = new Ray(base.transform.position + vector6, current);
					_ = ray2.origin + current;
					if (!ProxyHitCheck.HitCheck(ray2, _remainingTravel, _layerMask, out var rayHit2, out var finalNewParent2, out var proxyPos2, out var proxyDir2, out var proxyNormal2))
					{
						continue;
					}
					_ = rayHit2.point;
					if ((double)Vector3.Distance(rayHit2.point, b) > 0.01 && rayHit2.distance <= rayHit.distance - Info.EdgeSetback)
					{
						float num9 = rayHit.distance - rayHit2.distance;
						if (num9 * num9 < _lastRayStepSquared)
						{
							bool num10 = _lastTHitEdge != null && _lastTHitEdge == rayHit2.collider.transform;
							bool flag7 = _lastTHitCenter != null && _lastTHitCenter == rayHit2.collider.transform;
							if (!num10 && !flag7)
							{
								flag4 = false;
								num6++;
								if (rayHit2.distance < num7)
								{
									num7 = rayHit2.distance;
									raycastHit = rayHit2;
									vector4 = vector6;
									transform = rayHit2.collider.transform;
									proxyPos = proxyPos2;
									proxyDir = proxyDir2;
									proxyNormal = proxyNormal2;
									if (finalNewParent2 != null)
									{
										ITarget componentInParent = finalNewParent2.GetComponentInParent<ITarget>();
										if (componentInParent != null)
										{
											target2 = componentInParent;
										}
									}
								}
							}
						}
					}
					b = rayHit2.point;
					if (transform != null)
					{
						_lastTHitEdge = transform;
					}
					_lastTHitCenter = lastTHitCenter;
				}
			}
			if (num6 > 0)
			{
				_ = rayHit.distance;
				_ = raycastHit.distance;
				rayHit = raycastHit;
				vector5 = rayHit.point - vector4;
				_remainingTravel -= rayHit.distance;
				flag3 = true;
				surfaceNormal = rayHit.normal;
			}
			if (flag4)
			{
				_lastRayStepSquared = current.sqrMagnitude;
				try
				{
					base.transform.position = base.transform.position + current;
				}
				catch
				{
					UnityEngine.Debug.LogError("Malformed numerical elements in live round!", this);
					current = base.transform.forward * 200f;
					base.transform.position = base.transform.position + current;
				}
				flag = true;
				break;
			}
			_lastRayStepSquared = (vector5 - base.transform.position).sqrMagnitude;
			base.transform.position = vector5;
			float num11 = 90f - Vector3.Angle(rayHit.normal, current * -1f);
			_lastTHitEdgeMarch = num5 / Mathf.Sin(num11 * (MathF.PI / 180f));
			if (!flag3)
			{
				continue;
			}
			bool flag8 = penCheck(rayHit.collider, surfaceNormal, current, rayHit.point - vector4, proxyPos, proxyDir, proxyNormal, target2);
			if (!flag8)
			{
				ShotData item = new ShotData
				{
					DamageValue = 0f,
					IsSpall = IsSpall
				};
				Story.PenShots.Add(item);
			}
			current *= _remainingTravel / current.magnitude;
			if (_needImpulse && !_calculatedImpulse && target2 != null)
			{
				Unit owner = target2.Owner;
				if (owner != null)
				{
					Rigidbody component = owner.transform.GetComponent<Rigidbody>();
					if (component != null)
					{
						_impulseTarget = component;
						_impulsePoint = rayHit.point;
						_impulseDirection = current.normalized;
						_calculatedImpulse = true;
						if (owner.Chassis != null && Info.Radius > 0.0099f)
						{
							owner.Chassis.Unfreeze(1f, rotationOnly: true);
						}
					}
				}
			}
			if (_isHeat && _hitSolidObject && Info.AlwaysProduceBlast && Info.DetonateSpallCount == 0 && !_crushed && !_heatRicochet)
			{
				_fuzeCompleted = true;
			}
			bool flag9 = _isHe || (_isHeat && Info.DetonateSpallCount > 0 && !_crushed && !_heatRicochet);
			if (!flag8 && !_ricochet)
			{
				if (flag9 && _armed && _hitSolidObject)
				{
					createExplosion(hitSurface: true, _heHitNormalRha, _heHitNormal, 0.03f);
					if (Info.DoScabEffect)
					{
						HandleScabEffect(_heHitNormalRha, _heHitNormal);
					}
					_fuzeCompleted = true;
				}
				Detonate();
				flag = true;
				break;
			}
			if (!flag8 && _ricochet && _ricochetCount > 2 && IsSpall)
			{
				Detonate();
				flag = true;
			}
			else if (!flag8 && _ricochet && _ricochetCount > 3 && !IsSpall)
			{
				Detonate();
				flag = true;
			}
			else if (flag8)
			{
				if (_hitSolidObject && !IsSpall)
				{
					doImpactEffect();
				}
				Light componentInChildren = GetComponentInChildren<Light>();
				if (componentInChildren != null)
				{
					componentInChildren.enabled = false;
				}
				if (flag9 && _hitSolidObject && _armed && _heHitNormalRha >= Info.RhaToFuse)
				{
					if (Info.ImpactFuseTime == 0f)
					{
						AddEvent("Detonated on impact");
						createExplosion(hitSurface: true, _heHitNormalRha, _heHitNormal, 0.03f);
						if (_isHe)
						{
							Detonate();
							flag = true;
						}
					}
					else if (!_impactFuseActive)
					{
						AddEvent("Impact delay fuse activated");
						_impactFuseActive = true;
						_impactFuseCountdown = Info.ImpactFuseTime;
						float num12 = CurrentSpeed * _impactFuseCountdown;
						if (num12 < _remainingTravel)
						{
							_remainingTravel = num12;
							current *= num12 / current.magnitude;
							flag2 = true;
						}
					}
				}
			}
			if (_needsNormalization)
			{
				base.transform.forward = _normalizationVector;
				float magnitude = current.magnitude;
				current = _normalizationVector;
				current *= magnitude / current.magnitude;
				_initialPosition = base.transform.position;
				_initialVelocity = base.transform.forward * CurrentSpeed;
				_lastTotalDisp = Vector3.zero;
				_subTimeTraveled = 0f;
				_currentMotionState.velocity = Vector3.RotateTowards(_currentMotionState.velocity, base.transform.forward, 7f, 0f);
			}
			_ricochet = false;
			_heatRicochet = false;
		}
		EvaluateFrameImpactSounds();
		bool flag10 = false;
		if (flag2)
		{
			_fuzeCompleted = true;
			AddEvent("Impact delay fuse triggered");
			ShotInfo.ShotFrameData frameData2 = new ShotInfo.ShotFrameData
			{
				ArmorStruck = null,
				ObjectStruck = null,
				VehicleStruck = null,
				IsKill = false
			};
			ShotInfo.AddShotFrame(base.transform.position, current, frameData2, base.transform.localPosition, base.transform.parent);
			_lastFramePosition = base.transform.position;
			flag10 = true;
			createExplosion(hitSurface: false, 0f, Vector3.zero, 0.03f);
			Detonate();
		}
		if (!flag10)
		{
			float num13 = Vector3.Distance(_lastFramePosition, base.transform.position);
			float num14 = (IsSpall ? 1f : 5f);
			if (num13 > num14)
			{
				ShotInfo.ShotFrameData frameData3 = new ShotInfo.ShotFrameData
				{
					ArmorStruck = null,
					ObjectStruck = null,
					VehicleStruck = null,
					IsKill = false
				};
				ShotInfo.AddShotFrame(base.transform.position, current, frameData3, base.transform.localPosition, base.transform.parent);
				_lastFramePosition = base.transform.position;
			}
		}
		if (!_detonated)
		{
			float num15 = _prevSpeed - CurrentSpeed;
			float magnitude2 = _currentMotionState.velocity.magnitude;
			float num16 = magnitude2 - num15;
			if (num16 < 0f)
			{
				num16 = 0f;
			}
			_currentMotionState.velocity *= num16 / magnitude2;
			CurrentSpeed = num16;
			_prevSpeed = CurrentSpeed;
			_prevPosition = base.transform.position;
			_needsNormalization = false;
		}
	}

	public void NotifyEnteredCompartment(int validationID, Compartment compartment)
	{
		if (ID == validationID)
		{
			_compartmentHit = compartment;
		}
	}

	private void ResolveImpactAudio(float armorInfoSabotRha)
	{
		if (Info.Radius < 0.01f)
		{
			TellSFXManagerToPlaySmallCalImpactSFXEvent(armorInfoSabotRha);
		}
		else
		{
			TellSFXManagerToPlayLargeCalImpactSFXEvent(armorInfoSabotRha);
		}
	}

	private void TellSFXManagerToPlayRicochetSFXEvent()
	{
		_impactSFXManager.PlayRicochetSFX(base.gameObject.transform.position, Info);
	}

	private void TellSFXManagerToPlaySmallCalImpactSFXEvent(float armorInfoSabotRha)
	{
		SubmitFrameImpactSound(ImpactSfxStrength.SmallNonPen);
		_armorThicknessForImpactSFX = Mathf.Max(armorInfoSabotRha, _armorThicknessForImpactSFX);
	}

	private void TellSFXManagerToPlayLargeCalImpactSFXEvent(float armorInfoSabotRha)
	{
		SubmitFrameImpactSound(ImpactSfxStrength.LargeNonPen);
		_armorThicknessForImpactSFX = Mathf.Max(armorInfoSabotRha, _armorThicknessForImpactSFX);
	}

	private void TellSFXManagerToPlayImpactPenIntPerspSFXEvent(float armorInfoSabotRha, Vector3 penHitPosition)
	{
		SubmitFrameImpactSound(ImpactSfxStrength.Penetration);
		_armorThicknessForImpactSFX = Mathf.Max(armorInfoSabotRha, _armorThicknessForImpactSFX);
		_penHitPositionToSendToSFXManager = penHitPosition;
	}

	private void SubmitFrameImpactSound(ImpactSfxStrength strength)
	{
		if (strength > _largestFrameImpactSfx)
		{
			_largestFrameImpactSfx = strength;
		}
	}

	private void ResetFrameImpactSounds()
	{
		_largestFrameImpactSfx = ImpactSfxStrength.Nothing;
		_armorThicknessForImpactSFX = 0f;
	}

	private void EvaluateFrameImpactSounds()
	{
		if (_largestFrameImpactSfx != ImpactSfxStrength.Nothing)
		{
			switch (_largestFrameImpactSfx)
			{
			case ImpactSfxStrength.Penetration:
				_impactSFXManager.PlayImpactPenIntPerspSFX(_penHitPositionToSendToSFXManager, _armorThicknessForImpactSFX);
				break;
			case ImpactSfxStrength.LargeNonPen:
				_impactSFXManager.PlayLargeCalImpactSFX(base.gameObject.transform.position, Info, _armorThicknessForImpactSFX);
				break;
			case ImpactSfxStrength.SmallNonPen:
				_impactSFXManager.PlaySmallCalImpactSFX(base.gameObject.transform.position, Info, _armorThicknessForImpactSFX);
				break;
			}
		}
	}

	private void DrawDebugStar(Vector3 worldPosition, Color color)
	{
	}
}
