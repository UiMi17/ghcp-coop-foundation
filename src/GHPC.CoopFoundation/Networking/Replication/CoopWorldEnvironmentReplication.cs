using System;
using System.Collections;
using System.Reflection;
using GHPC.World;
using MelonLoader;
using UnityEngine;

namespace GHPC.CoopFoundation.Networking.Replication;

/// <summary>Host-authoritative <see cref="WorldEnvironmentManager" /> + <see cref="Weather" /> (rain + sky/cloud layers + sun attenuation) for coop peers.</summary>
internal static class CoopWorldEnvironmentReplication
{
    private const BindingFlags StaticProp = BindingFlags.Public | BindingFlags.Static;
    private const BindingFlags InstNonPub = BindingFlags.Instance | BindingFlags.NonPublic;

    private static bool _reflectReady;
    private static Action<float>? _setTempCelsius;
    private static Action<float>? _setAirDensity;
    private static Action<float>? _setAmmoTempFahrenheit;
    private static Action<float>? _setAirCoefficient;

    private static bool _weatherReflectReady;
    private static FieldInfo? _wfRaininess;
    private static FieldInfo? _wfTargetRaininess;
    private static FieldInfo? _wfPrevRaininess;
    private static FieldInfo? _wfCloudiness;
    private static FieldInfo? _wfTargetCloudiness;
    private static FieldInfo? _wfPrevCloudiness;
    private static FieldInfo? _wfWindiness;
    private static FieldInfo? _wfTargetWindiness;
    private static FieldInfo? _wfPrevWindiness;
    private static FieldInfo? _wfUseDynamicWeather;
    private static FieldInfo? _wfCloudSpeed;
    private static FieldInfo? _wfCurrentCloudCondition;
    private static FieldInfo? _wfCloudValues;
    private static FieldInfo? _wfTargetCloudValues;
    private static FieldInfo? _wfCloudDictionary;
    private static FieldInfo? _wfCloudShadowDirectionX;
    private static FieldInfo? _wfCloudShadowDirectionY;

    private static MethodInfo? _mUpdateCelestialSky;
    private static MethodInfo? _mGetCloudConfiguration;

    private static bool _pendingWemInstances;
    private static float _pendingTempC;
    private static float _pendingAirD;
    private static float _pendingAmmoF;
    private static float _pendingAirCoeff;
    private static bool _pendingNight;

    private static bool _pendingWeather;
    private static bool _pendingWeatherValid;
    private static bool _pendingUseDynamicWeather;
    private static float _pendingRaininess;
    private static float _pendingCloudiness;
    private static float _pendingWindiness;
    private static float _pendingCloudBias;
    private static bool _pendingCloudLayerFromHost;
    private static byte _pendingCloudCondition;
    private static float _pendingCloudSpeed;
    private static bool _pendingCloudDirectionFromHost;
    private static float _pendingCloudDirX;
    private static float _pendingCloudDirY;

    /// <summary>Client: last host weather so we can re-apply after <see cref="GHPC.Mission.RandomEnvironment.RandomizeNow" />.</summary>
    private static bool _clientHostWeatherSnapshotValid;

    private static bool _hostSnapUseDynamicWeather;
    private static float _hostSnapRaininess;
    private static float _hostSnapCloudiness;
    private static float _hostSnapWindiness;
    private static float _hostSnapCloudBias;
    private static bool _hostSnapCloudLayerFromHost;
    private static byte _hostSnapCloudCondition;
    private static float _hostSnapCloudSpeed;
    private static bool _hostSnapCloudDirectionFromHost;
    private static float _hostSnapCloudDirX;
    private static float _hostSnapCloudDirY;

    /// <summary>Deterministic bracket from <see cref="Weather.GetNewCloudCondition" /> (game uses Random inside each band).</summary>
    private static int InferCloudCondition(float cloudiness)
    {
        if (cloudiness < 0.2f)
            return 1;
        if (cloudiness < 0.7f)
            return 4;
        if (cloudiness < 0.9f)
            return 7;
        return 8;
    }

    private static void EnsureReflection()
    {
        if (_reflectReady)
            return;
        _reflectReady = true;
        try
        {
            Type t = typeof(WorldEnvironmentManager);
            _setTempCelsius = CreateStaticFloatSetter(t, nameof(WorldEnvironmentManager.TempCelsius));
            _setAirDensity = CreateStaticFloatSetter(t, nameof(WorldEnvironmentManager.AirDensity));
            _setAmmoTempFahrenheit = CreateStaticFloatSetter(t, nameof(WorldEnvironmentManager.AmmoTempFahrenheit));
            _setAirCoefficient = CreateStaticFloatSetter(t, nameof(WorldEnvironmentManager.AirCoefficient));
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"[CoopNet] WorldEnv: reflection init failed — {ex.Message}");
        }
    }

    private static void EnsureWeatherReflection()
    {
        if (_weatherReflectReady)
            return;
        _weatherReflectReady = true;
        try
        {
            Type t = typeof(Weather);
            _wfRaininess = t.GetField("_raininess", InstNonPub);
            _wfTargetRaininess = t.GetField("_targetRaininess", InstNonPub);
            _wfPrevRaininess = t.GetField("_prevRaininess", InstNonPub);
            _wfCloudiness = t.GetField("_cloudiness", InstNonPub);
            _wfTargetCloudiness = t.GetField("_targetCloudiness", InstNonPub);
            _wfPrevCloudiness = t.GetField("_prevCloudiness", InstNonPub);
            _wfWindiness = t.GetField("_windiness", InstNonPub);
            _wfTargetWindiness = t.GetField("_targetWindiness", InstNonPub);
            _wfPrevWindiness = t.GetField("_prevWindiness", InstNonPub);
            _wfUseDynamicWeather = t.GetField("_useDynamicWeather", InstNonPub);
            _wfCloudSpeed = t.GetField("_cloudSpeed", InstNonPub);
            _wfCurrentCloudCondition = t.GetField("_currentCloudCondition", InstNonPub);
            _wfCloudValues = t.GetField("_cloudValues", InstNonPub);
            _wfTargetCloudValues = t.GetField("_targetCloudValues", InstNonPub);
            _wfCloudDictionary = t.GetField("_cloudDictionary", InstNonPub);
            _wfCloudShadowDirectionX = t.GetField("_cloudShadowDirectionX", InstNonPub);
            _wfCloudShadowDirectionY = t.GetField("_cloudShadowDirectionY", InstNonPub);
            _mUpdateCelestialSky = t.GetMethod("UpdateCelestialSky", InstNonPub, null, Type.EmptyTypes, null);
            _mGetCloudConfiguration = t.GetMethod(
                "GetCloudConfiguration",
                InstNonPub,
                null,
                new[] { typeof(int) },
                null);
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"[CoopNet] WorldEnv: Weather field reflection failed — {ex.Message}");
        }
    }

    private static Action<float>? CreateStaticFloatSetter(Type declaringType, string propertyName)
    {
        PropertyInfo? pi = declaringType.GetProperty(propertyName, StaticProp);
        MethodInfo? set = pi?.GetSetMethod(true);
        if (set == null)
            return null;
        return (Action<float>)Delegate.CreateDelegate(typeof(Action<float>), set);
    }

    private static Weather? ResolveWeather()
    {
        try
        {
            Weather? inst = Weather.Instance;
            if (inst != null)
                return inst;
        }
        catch
        {
            // ignored
        }

        return UnityEngine.Object.FindObjectOfType<Weather>();
    }

    private static bool ApplyCloudLayerAndCelestial(
        Weather w,
        bool cloudLayerFromHost,
        byte cloudConditionWire,
        float cloudSpeedFromHost,
        bool cloudDirectionFromHost,
        float cloudDirX,
        float cloudDirY,
        float cloudiness)
    {
        EnsureWeatherReflection();
        if (_wfCloudDictionary?.GetValue(w) is not IDictionary dict || dict.Count == 0)
            return false;

        int cond = cloudLayerFromHost ? Mathf.Clamp(cloudConditionWire, 0, 8) : InferCloudCondition(cloudiness);
        if (_wfCloudSpeed != null && cloudLayerFromHost)
            _wfCloudSpeed.SetValue(w, cloudSpeedFromHost);
        if (_wfCloudShadowDirectionX != null && _wfCloudShadowDirectionY != null && cloudDirectionFromHost)
        {
            _wfCloudShadowDirectionX.SetValue(w, cloudDirX);
            _wfCloudShadowDirectionY.SetValue(w, cloudDirY);
        }
        if (_wfCurrentCloudCondition != null)
            _wfCurrentCloudCondition.SetValue(w, cond);
        if (_mGetCloudConfiguration != null && _wfCloudValues != null && _wfTargetCloudValues != null)
        {
            try
            {
                object? tuple = _mGetCloudConfiguration.Invoke(w, new object[] { cond });
                if (tuple != null)
                {
                    _wfCloudValues.SetValue(w, tuple);
                    _wfTargetCloudValues.SetValue(w, tuple);
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[CoopNet] WorldEnv: GetCloudConfiguration apply failed — {ex.Message}");
                return false;
            }
        }

        try
        {
            _mUpdateCelestialSky?.Invoke(w, null);
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"[CoopNet] WorldEnv: UpdateCelestialSky failed — {ex.Message}");
        }

        return true;
    }

    private static bool PushWeatherToInstance(
        Weather w,
        bool useDynamicWeather,
        float raininess,
        float cloudiness,
        float windiness,
        float cloudBias,
        bool cloudLayerFromHost,
        byte cloudCondition,
        float cloudSpeed,
        bool cloudDirectionFromHost,
        float cloudDirX,
        float cloudDirY)
    {
        w.CloudBias = cloudBias;
        EnsureWeatherReflection();
        if (_wfRaininess != null
            && _wfTargetRaininess != null
            && _wfPrevRaininess != null
            && _wfCloudiness != null
            && _wfTargetCloudiness != null
            && _wfPrevCloudiness != null
            && _wfWindiness != null
            && _wfTargetWindiness != null
            && _wfPrevWindiness != null
            && _wfUseDynamicWeather != null)
        {
            // Client is host-authoritative for weather; disable local dynamic loop to prevent random skybox drift.
            _wfUseDynamicWeather.SetValue(w, false);
            _wfRaininess.SetValue(w, raininess);
            _wfTargetRaininess.SetValue(w, raininess);
            _wfPrevRaininess.SetValue(w, raininess);
            _wfCloudiness.SetValue(w, cloudiness);
            _wfTargetCloudiness.SetValue(w, cloudiness);
            _wfPrevCloudiness.SetValue(w, cloudiness);
            _wfWindiness.SetValue(w, windiness);
            _wfTargetWindiness.SetValue(w, windiness);
            _wfPrevWindiness.SetValue(w, windiness);
            if (!ApplyCloudLayerAndCelestial(
                    w,
                    cloudLayerFromHost,
                    cloudCondition,
                    cloudSpeed,
                    cloudDirectionFromHost,
                    cloudDirX,
                    cloudDirY,
                    cloudiness))
                return false;
            w.UpdateWeather();
            return true;
        }

        if (raininess > 0.08f)
            w.ForceRain();
        else
            w.ForceClearRain();
        return true;
    }

    /// <summary>Client only: remember COO payload so mission random env can be overwritten after local <see cref="GHPC.Mission.RandomEnvironment" /> runs.</summary>
    internal static void RememberClientHostWeatherSnapshot(
        bool weatherValid,
        bool useDynamicWeather,
        float raininess,
        float cloudiness,
        float windiness,
        float cloudBias,
        bool cloudLayerFromHost,
        byte cloudCondition,
        float cloudSpeed,
        bool cloudDirectionFromHost,
        float cloudDirX,
        float cloudDirY)
    {
        if (!weatherValid)
        {
            _clientHostWeatherSnapshotValid = false;
            return;
        }

        _clientHostWeatherSnapshotValid = true;
        _hostSnapUseDynamicWeather = useDynamicWeather;
        _hostSnapRaininess = raininess;
        _hostSnapCloudiness = cloudiness;
        _hostSnapWindiness = windiness;
        _hostSnapCloudBias = cloudBias;
        _hostSnapCloudLayerFromHost = cloudLayerFromHost;
        _hostSnapCloudCondition = cloudCondition;
        _hostSnapCloudSpeed = cloudSpeed;
        _hostSnapCloudDirectionFromHost = cloudDirectionFromHost;
        _hostSnapCloudDirX = cloudDirX;
        _hostSnapCloudDirY = cloudDirY;
    }

    /// <summary>Client: re-apply after local random environment rolls different weather.</summary>
    internal static void ClientReapplyStoredHostWeather()
    {
        if (!_clientHostWeatherSnapshotValid)
            return;
        Weather? w = ResolveWeather();
        if (w == null)
            return;
        PushWeatherToInstance(
            w,
            _hostSnapUseDynamicWeather,
            _hostSnapRaininess,
            _hostSnapCloudiness,
            _hostSnapWindiness,
            _hostSnapCloudBias,
            _hostSnapCloudLayerFromHost,
            _hostSnapCloudCondition,
            _hostSnapCloudSpeed,
            _hostSnapCloudDirectionFromHost,
            _hostSnapCloudDirX,
            _hostSnapCloudDirY);
    }

    public static bool TryCaptureHost(
        out float tempC,
        out float airD,
        out float ammoF,
        out float airCoeff,
        out bool night,
        out bool weatherValid,
        out bool useDynamicWeather,
        out float raininess,
        out float cloudiness,
        out float windiness,
        out float cloudBias,
        out byte cloudCondition,
        out float cloudSpeed,
        out float cloudDirX,
        out float cloudDirY)
    {
        tempC = WorldEnvironmentManager.TempCelsius;
        airD = WorldEnvironmentManager.AirDensity;
        ammoF = WorldEnvironmentManager.AmmoTempFahrenheit;
        airCoeff = WorldEnvironmentManager.AirCoefficient;
        night = false;
        WorldEnvironmentManager? mgr = UnityEngine.Object.FindObjectOfType<WorldEnvironmentManager>();
        if (mgr != null)
            night = mgr.Night;

        weatherValid = false;
        useDynamicWeather = false;
        raininess = 0f;
        cloudiness = 0f;
        windiness = 0f;
        cloudBias = 0f;
        cloudCondition = 0;
        cloudSpeed = 0f;
        cloudDirX = 0f;
        cloudDirY = 0f;
        Weather? w = ResolveWeather();
        if (w != null)
        {
            weatherValid = true;
            cloudBias = w.CloudBias;
            raininess = w.Raininess;
            EnsureWeatherReflection();
            if (_wfCloudiness != null && _wfWindiness != null && _wfUseDynamicWeather != null)
            {
                cloudiness = (float)_wfCloudiness.GetValue(w)!;
                windiness = (float)_wfWindiness.GetValue(w)!;
                useDynamicWeather = (bool)_wfUseDynamicWeather.GetValue(w)!;
            }

            if (_wfCurrentCloudCondition != null)
                cloudCondition = (byte)Mathf.Clamp((int)_wfCurrentCloudCondition.GetValue(w)!, 0, 8);
            if (_wfCloudSpeed != null)
                cloudSpeed = (float)_wfCloudSpeed.GetValue(w)!;
            if (_wfCloudShadowDirectionX != null)
                cloudDirX = (float)_wfCloudShadowDirectionX.GetValue(w)!;
            if (_wfCloudShadowDirectionY != null)
                cloudDirY = (float)_wfCloudShadowDirectionY.GetValue(w)!;
        }

        return true;
    }

    /// <summary>Client: apply host snapshot (queues instance/weather if components not in scene yet).</summary>
    public static void ApplyFromHost(
        float tempC,
        float airD,
        float ammoF,
        float airCoeff,
        bool night,
        bool weatherValid,
        bool useDynamicWeather,
        float raininess,
        float cloudiness,
        float windiness,
        float cloudBias,
        bool cloudLayerFromHost,
        byte cloudCondition,
        float cloudSpeed,
        bool cloudDirectionFromHost,
        float cloudDirX,
        float cloudDirY,
        bool log)
    {
        RememberClientHostWeatherSnapshot(
            weatherValid,
            useDynamicWeather,
            raininess,
            cloudiness,
            windiness,
            cloudBias,
            cloudLayerFromHost,
            cloudCondition,
            cloudSpeed,
            cloudDirectionFromHost,
            cloudDirX,
            cloudDirY);
        EnsureReflection();
        if (_setTempCelsius == null
            || _setAirDensity == null
            || _setAmmoTempFahrenheit == null
            || _setAirCoefficient == null)
        {
            if (log)
                MelonLogger.Warning("[CoopNet] WorldEnv recv: static setters unavailable — skip");
            return;
        }

        _setTempCelsius(tempC);
        _setAirDensity(airD);
        _setAmmoTempFahrenheit(ammoF);
        _setAirCoefficient(airCoeff);

        WorldEnvironmentManager[] managers = UnityEngine.Object.FindObjectsOfType<WorldEnvironmentManager>();
        if (managers == null || managers.Length == 0)
        {
            _pendingWemInstances = true;
            _pendingTempC = tempC;
            _pendingAirD = airD;
            _pendingAmmoF = ammoF;
            _pendingAirCoeff = airCoeff;
            _pendingNight = night;
            if (log)
                MelonLogger.Msg("[CoopNet] WorldEnv recv: statics applied; instance sync pending (no WorldEnvironmentManager yet)");
        }
        else
        {
            PushToInstances(managers, tempC, airD, ammoF, airCoeff, night);
            _pendingWemInstances = false;
        }

        if (weatherValid)
        {
            Weather? w = ResolveWeather();
            if (w == null)
            {
                _pendingWeather = true;
                _pendingWeatherValid = true;
                _pendingUseDynamicWeather = useDynamicWeather;
                _pendingRaininess = raininess;
                _pendingCloudiness = cloudiness;
                _pendingWindiness = windiness;
                _pendingCloudBias = cloudBias;
                _pendingCloudLayerFromHost = cloudLayerFromHost;
                _pendingCloudCondition = cloudCondition;
                _pendingCloudSpeed = cloudSpeed;
                _pendingCloudDirectionFromHost = cloudDirectionFromHost;
                _pendingCloudDirX = cloudDirX;
                _pendingCloudDirY = cloudDirY;
                if (log)
                    MelonLogger.Msg("[CoopNet] WorldEnv recv: weather pending (no Weather yet)");
            }
            else
            {
                bool applied = PushWeatherToInstance(
                    w,
                    useDynamicWeather,
                    raininess,
                    cloudiness,
                    windiness,
                    cloudBias,
                    cloudLayerFromHost,
                    cloudCondition,
                    cloudSpeed,
                    cloudDirectionFromHost,
                    cloudDirX,
                    cloudDirY);
                _pendingWeather = !applied;
                if (!applied)
                {
                    _pendingWeatherValid = true;
                    _pendingUseDynamicWeather = useDynamicWeather;
                    _pendingRaininess = raininess;
                    _pendingCloudiness = cloudiness;
                    _pendingWindiness = windiness;
                    _pendingCloudBias = cloudBias;
                    _pendingCloudLayerFromHost = cloudLayerFromHost;
                    _pendingCloudCondition = cloudCondition;
                    _pendingCloudSpeed = cloudSpeed;
                    _pendingCloudDirectionFromHost = cloudDirectionFromHost;
                    _pendingCloudDirX = cloudDirX;
                    _pendingCloudDirY = cloudDirY;
                }
                if (log)
                {
                    MelonLogger.Msg(
                        $"[CoopNet] WorldEnv recv: T={tempC:F1}°C night={night} rain={raininess:F2} cloud={cloudiness:F2} wind={windiness:F2} cloudCond={(cloudLayerFromHost ? cloudCondition.ToString() : "inf")} dyn={useDynamicWeather}");
                }
            }
        }
        else if (log)
        {
            MelonLogger.Msg(
                $"[CoopNet] WorldEnv recv: T={tempC:F1}°C rho={airD:F3} ammo°F={ammoF:F1} aCoeff={airCoeff:F2} night={night} (no weather block)");
        }
    }

    /// <summary>Client: flush queued instance/weather after scene objects spawn.</summary>
    public static void TryFlushPendingIfPossible(bool log)
    {
        if (_pendingWemInstances)
        {
            WorldEnvironmentManager[] managers = UnityEngine.Object.FindObjectsOfType<WorldEnvironmentManager>();
            if (managers is { Length: > 0 })
            {
                PushToInstances(managers, _pendingTempC, _pendingAirD, _pendingAmmoF, _pendingAirCoeff, _pendingNight);
                _pendingWemInstances = false;
                if (log)
                {
                    MelonLogger.Msg(
                        $"[CoopNet] WorldEnv flush WEM: T={_pendingTempC:F1}°C night={_pendingNight}");
                }
            }
        }

        if (!_pendingWeather || !_pendingWeatherValid)
            return;
        Weather? w = ResolveWeather();
        if (w == null)
            return;
        bool applied = PushWeatherToInstance(
            w,
            _pendingUseDynamicWeather,
            _pendingRaininess,
            _pendingCloudiness,
            _pendingWindiness,
            _pendingCloudBias,
            _pendingCloudLayerFromHost,
            _pendingCloudCondition,
            _pendingCloudSpeed,
            _pendingCloudDirectionFromHost,
            _pendingCloudDirX,
            _pendingCloudDirY);
        _pendingWeather = !applied;
        if (!applied)
            return;
        if (log)
        {
            MelonLogger.Msg(
                $"[CoopNet] WorldEnv flush weather: rain={_pendingRaininess:F2} cloud={_pendingCloudiness:F2} wind={_pendingWindiness:F2}");
        }
    }

    public static void ClearSession()
    {
        _pendingWemInstances = false;
        _pendingWeather = false;
        _pendingWeatherValid = false;
        _pendingCloudDirectionFromHost = false;
        _clientHostWeatherSnapshotValid = false;
    }

    private static void PushToInstances(
        WorldEnvironmentManager[] managers,
        float tempC,
        float airD,
        float ammoF,
        float airCoeff,
        bool night)
    {
        foreach (WorldEnvironmentManager? m in managers)
        {
            if (m == null)
                continue;
            m.LocalTempCelsius = tempC;
            m.LocalAirDenisty = airD;
            m.LocalAmmoTempFahrenheit = ammoF;
            m.LocalAirCoefficient = airCoeff;
            m.Night = night;
        }
    }
}
