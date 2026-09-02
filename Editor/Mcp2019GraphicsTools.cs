#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.Profiling;

namespace UnityMcp2019
{
    internal static class Mcp2019GraphicsTools
    {
        private const BindingFlags AnyMember = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

        internal static string Execute(string argumentsJson)
        {
            GraphicsArguments a = string.IsNullOrEmpty(argumentsJson) || argumentsJson == "{}"
                ? new GraphicsArguments()
                : JsonUtility.FromJson<GraphicsArguments>(argumentsJson) ?? new GraphicsArguments();
            string action = (a.Action ?? string.Empty).Trim().ToLowerInvariant();
            try
            {
                GraphicsResult result;
                switch (action)
                {
                    case "ping": result = Ping(); break;
                    case "volume_create": result = VolumeCreate(a); break;
                    case "volume_add_effect": result = VolumeAddEffect(a); break;
                    case "volume_set_effect": result = VolumeSetEffect(a); break;
                    case "volume_remove_effect": result = VolumeRemoveEffect(a); break;
                    case "volume_get_info": result = VolumeGetInfo(a); break;
                    case "volume_set_properties": result = VolumeSetProperties(a); break;
                    case "volume_list_effects": result = VolumeListEffects(); break;
                    case "volume_create_profile": result = VolumeCreateProfile(a); break;
                    case "bake_start": result = BakeStart(a); break;
                    case "bake_cancel": result = BakeCancel(); break;
                    case "bake_status": result = BakeStatus(); break;
                    case "bake_clear": result = BakeClear(); break;
                    case "bake_reflection_probe": result = BakeReflectionProbe(a); break;
                    case "bake_get_settings": result = BakeGetSettings(); break;
                    case "bake_set_settings": result = BakeSetSettings(a); break;
                    case "bake_create_light_probe_group": result = BakeCreateLightProbeGroup(a); break;
                    case "bake_create_reflection_probe": result = BakeCreateReflectionProbe(a); break;
                    case "bake_set_probe_positions": result = BakeSetProbePositions(a); break;
                    case "stats_get": result = StatsGet(); break;
                    case "stats_list_counters": result = StatsListCounters(); break;
                    case "stats_set_scene_debug": result = StatsSetSceneDebug(a); break;
                    case "stats_get_memory": result = StatsGetMemory(); break;
                    case "pipeline_get_info": result = PipelineGetInfo(); break;
                    case "pipeline_set_quality": result = PipelineSetQuality(a); break;
                    case "pipeline_get_settings": result = PipelineGetSettings(); break;
                    case "pipeline_set_settings": result = PipelineSetSettings(a); break;
                    case "feature_list": result = FeatureList(); break;
                    case "feature_add": result = FeatureAdd(a); break;
                    case "feature_remove": result = FeatureRemove(a); break;
                    case "feature_configure": result = FeatureConfigure(a); break;
                    case "feature_toggle": result = FeatureToggle(a); break;
                    case "feature_reorder": result = FeatureReorder(a); break;
                    case "skybox_get": result = SkyboxGet(); break;
                    case "skybox_set_material": result = SkyboxSetMaterial(a); break;
                    case "skybox_set_properties": result = SkyboxSetProperties(a); break;
                    case "skybox_set_ambient": result = SkyboxSetAmbient(a); break;
                    case "skybox_set_fog": result = SkyboxSetFog(a); break;
                    case "skybox_set_reflection": result = SkyboxSetReflection(a); break;
                    case "skybox_set_sun": result = SkyboxSetSun(a); break;
                    default: return JsonUtility.ToJson(Fail("Unknown manage_graphics action: " + action));
                }
                return JsonUtility.ToJson(result);
            }
            catch (Exception ex)
            {
                return JsonUtility.ToJson(Fail(ex.GetBaseException().Message));
            }
        }

        private static GraphicsResult Ping()
        {
            UnityEngine.Object pipeline = ActivePipelineAsset();
            Type volumeType = FindType("UnityEngine.Rendering.Volume");
            return Ok("Graphics tool ready. Pipeline: " + PipelineName(pipeline), new GraphicsData
            {
                UnityVersion = Application.unityVersion,
                Pipeline = pipeline == null ? "BuiltIn" : pipeline.GetType().FullName,
                PipelineName = PipelineName(pipeline),
                HasVolumeSystem = volumeType != null,
                HasURP = IsUrp(pipeline),
                HasHDRP = IsHdrp(pipeline),
                AvailableEffects = volumeType == null ? 0 : AvailableEffectTypes().Count,
            });
        }

        private static GraphicsResult VolumeCreate(GraphicsArguments a)
        {
            Type volumeType = RequireType("UnityEngine.Rendering.Volume", "Volume system requires an installed SRP core package.");
            GameObject go = new GameObject(string.IsNullOrEmpty(a.Name) ? "Global Volume" : a.Name);
            Undo.RegisterCreatedObjectUndo(go, "MCP create volume");
            if (a.HasPosition) go.transform.position = Vector3Of(a.Position);
            Component volume = Undo.AddComponent(go, volumeType);
            SetMember(volume, "isGlobal", !a.HasIsGlobal || a.IsGlobal);
            if (a.HasWeight) SetMember(volume, "weight", a.Weight);
            if (a.HasPriority) SetMember(volume, "priority", a.Priority);
            UnityEngine.Object profile = null;
            if (!string.IsNullOrEmpty(a.ProfilePath)) profile = AssetDatabase.LoadAssetAtPath(a.ProfilePath, RequireType("UnityEngine.Rendering.VolumeProfile", "Volume profile type is unavailable."));
            if (profile == null && (a.Effects != null && a.Effects.Length > 0))
                profile = CreateVolumeProfileAsset(DefaultProfilePath(go.name));
            if (profile != null) SetMember(volume, "sharedProfile", profile);
            if (a.Effects != null)
                for (int i = 0; i < a.Effects.Length; i++) AddEffectToProfile(profile, a.Effects[i].Effect, a.Effects[i].Parameters, a.Effects[i].Active);
            EditorUtility.SetDirty(go);
            return Ok("Volume created.", VolumeData(go, volume, profile));
        }

        private static GraphicsResult VolumeCreateProfile(GraphicsArguments a)
        {
            string path = string.IsNullOrEmpty(a.Path) ? DefaultProfilePath(string.IsNullOrEmpty(a.Name) ? "VolumeProfile" : a.Name) : NormalizeAssetPath(a.Path, ".asset");
            UnityEngine.Object profile = CreateVolumeProfileAsset(path);
            return Ok("Volume profile created.", new GraphicsData { Path = path, Name = profile.name, InstanceId = profile.GetInstanceID() });
        }

        private static GraphicsResult VolumeAddEffect(GraphicsArguments a)
        {
            Component volume = FindVolume(a.Target);
            UnityEngine.Object profile = EnsureVolumeProfile(volume, a.ProfilePath);
            UnityEngine.Object effect = AddEffectToProfile(profile, a.Effect, a.Parameters, true);
            return Ok("Volume effect added.", VolumeData(volume.gameObject, volume, profile, effect));
        }

        private static GraphicsResult VolumeSetEffect(GraphicsArguments a)
        {
            Component volume = FindVolume(a.Target);
            UnityEngine.Object profile = EnsureVolumeProfile(volume, a.ProfilePath);
            UnityEngine.Object effect = FindEffect(profile, a.Effect);
            if (effect == null) throw new InvalidOperationException("Volume effect was not found: " + a.Effect);
            Undo.RecordObject(effect, "MCP configure volume effect");
            ApplyPatches(effect, a.Parameters);
            EditorUtility.SetDirty(effect);
            AssetDatabase.SaveAssets();
            return Ok("Volume effect configured.", VolumeData(volume.gameObject, volume, profile, effect));
        }

        private static GraphicsResult VolumeRemoveEffect(GraphicsArguments a)
        {
            Component volume = FindVolume(a.Target);
            UnityEngine.Object profile = EnsureVolumeProfile(volume, a.ProfilePath);
            UnityEngine.Object effect = FindEffect(profile, a.Effect);
            if (effect == null) throw new InvalidOperationException("Volume effect was not found: " + a.Effect);
            IList components = VolumeComponents(profile);
            Undo.RecordObject(profile, "MCP remove volume effect");
            components.Remove(effect);
            UnityEngine.Object.DestroyImmediate(effect, true);
            EditorUtility.SetDirty(profile);
            AssetDatabase.SaveAssets();
            return Ok("Volume effect removed.", VolumeData(volume.gameObject, volume, profile));
        }

        private static GraphicsResult VolumeGetInfo(GraphicsArguments a)
        {
            Component volume = FindVolume(a.Target);
            UnityEngine.Object profile = GetMember(volume, "sharedProfile") as UnityEngine.Object;
            if (profile == null) profile = GetMember(volume, "profile") as UnityEngine.Object;
            return Ok("Volume information read.", VolumeData(volume.gameObject, volume, profile));
        }

        private static GraphicsResult VolumeSetProperties(GraphicsArguments a)
        {
            Component volume = FindVolume(a.Target);
            Undo.RecordObject(volume, "MCP configure volume");
            if (a.HasIsGlobal) SetMember(volume, "isGlobal", a.IsGlobal);
            if (a.HasWeight) SetMember(volume, "weight", a.Weight);
            if (a.HasPriority) SetMember(volume, "priority", a.Priority);
            ApplyPatches(volume, a.Properties);
            EditorUtility.SetDirty(volume);
            return Ok("Volume properties configured.", VolumeData(volume.gameObject, volume, GetMember(volume, "sharedProfile") as UnityEngine.Object));
        }

        private static GraphicsResult VolumeListEffects()
        {
            List<Type> types = AvailableEffectTypes();
            return Ok("Available volume effects listed.", new GraphicsData
            {
                Count = types.Count,
                Effects = types.Select(t => new NamedRecord { Name = t.Name, Type = t.FullName }).ToArray(),
            });
        }

        private static GraphicsResult BakeStart(GraphicsArguments a)
        {
            if (EditorApplication.isPlaying) throw new InvalidOperationException("Light baking is only available in Edit mode.");
            bool asynchronous = !a.HasAsyncBake || a.AsyncBake;
            bool started = asynchronous ? Lightmapping.BakeAsync() : Lightmapping.Bake();
            return Ok(started ? "Light bake started." : "Unity did not start a light bake.", BakeData());
        }

        private static GraphicsResult BakeCancel()
        {
            Lightmapping.Cancel();
            return Ok("Light bake cancellation requested.", BakeData());
        }

        private static GraphicsResult BakeStatus()
        {
            return Ok("Light bake status read.", BakeData());
        }

        private static GraphicsResult BakeClear()
        {
            Lightmapping.Clear();
            return Ok("Baked lighting data cleared.", BakeData());
        }

        private static GraphicsResult BakeReflectionProbe(GraphicsArguments a)
        {
            ReflectionProbe probe = FindTarget(a.Target).GetComponent<ReflectionProbe>();
            if (probe == null) throw new InvalidOperationException("Target has no ReflectionProbe.");
            Undo.RecordObject(probe, "MCP bake reflection probe");
            int renderId = probe.RenderProbe();
            return Ok("Reflection probe render started.", new GraphicsData { Target = FullPath(probe.gameObject), InstanceId = probe.GetInstanceID(), RenderId = renderId });
        }

        private static GraphicsResult BakeGetSettings()
        {
            Type type = typeof(LightmapEditorSettings);
            string[] names = { "realtimeResolution", "bakeResolution", "padding", "textureCompression", "ambientOcclusion", "aoMaxDistance", "aoExponentIndirect", "aoExponentDirect", "maxAtlasSize" };
            return Ok("Light bake settings read.", new GraphicsData { Settings = ReadStaticSettings(type, names) });
        }

        private static GraphicsResult BakeSetSettings(GraphicsArguments a)
        {
            ApplyStaticPatches(typeof(LightmapEditorSettings), a.Settings);
            return Ok("Light bake settings updated.", BakeGetSettings().Data);
        }

        private static GraphicsResult BakeCreateLightProbeGroup(GraphicsArguments a)
        {
            GameObject go = new GameObject(string.IsNullOrEmpty(a.Name) ? "Light Probe Group" : a.Name);
            Undo.RegisterCreatedObjectUndo(go, "MCP create light probe group");
            if (a.HasPosition) go.transform.position = Vector3Of(a.Position);
            LightProbeGroup group = Undo.AddComponent<LightProbeGroup>(go);
            Vector3[] points;
            if (a.Positions != null && a.Positions.Length > 0)
                points = a.Positions.Select(p => Vector3Of(p.Values)).ToArray();
            else
                points = ProbeGrid(a.GridSize, a.HasGridSize, a.HasSpacing ? a.Spacing : 1f);
            group.probePositions = points;
            EditorUtility.SetDirty(group);
            return Ok("Light probe group created.", new GraphicsData { Target = FullPath(go), InstanceId = go.GetInstanceID(), Count = points.Length });
        }

        private static GraphicsResult BakeCreateReflectionProbe(GraphicsArguments a)
        {
            GameObject go = new GameObject(string.IsNullOrEmpty(a.Name) ? "Reflection Probe" : a.Name);
            Undo.RegisterCreatedObjectUndo(go, "MCP create reflection probe");
            if (a.HasPosition) go.transform.position = Vector3Of(a.Position);
            ReflectionProbe probe = Undo.AddComponent<ReflectionProbe>(go);
            if (a.HasSize) probe.size = Vector3Of(a.Size);
            if (a.HasResolution) probe.resolution = Math.Max(16, a.Resolution);
            if (a.HasHdr) probe.hdr = a.Hdr;
            if (a.HasBoxProjection) probe.boxProjection = a.BoxProjection;
            if (!string.IsNullOrEmpty(a.Mode) && Enum.TryParse(a.Mode, true, out UnityEngine.Rendering.ReflectionProbeMode mode)) probe.mode = mode;
            EditorUtility.SetDirty(probe);
            return Ok("Reflection probe created.", new GraphicsData { Target = FullPath(go), InstanceId = go.GetInstanceID() });
        }

        private static GraphicsResult BakeSetProbePositions(GraphicsArguments a)
        {
            LightProbeGroup group = FindTarget(a.Target).GetComponent<LightProbeGroup>();
            if (group == null) throw new InvalidOperationException("Target has no LightProbeGroup.");
            if (a.Positions == null || a.Positions.Length == 0) throw new InvalidOperationException("positions must contain at least one vector.");
            Undo.RecordObject(group, "MCP set light probe positions");
            group.probePositions = a.Positions.Select(p => Vector3Of(p.Values)).ToArray();
            EditorUtility.SetDirty(group);
            return Ok("Light probe positions updated.", new GraphicsData { Target = FullPath(group.gameObject), Count = group.probePositions.Length });
        }

        private static GraphicsResult StatsGet()
        {
            Type stats = FindType("UnityEditor.UnityStats");
            string[] counters = { "batches", "drawCalls", "setPassCalls", "triangles", "vertices", "shadowCasters", "renderTextureChanges", "screenRes", "screenBytes", "textureBytes", "meshBytes" };
            return Ok("Rendering statistics read.", new GraphicsData { Counters = ReadStaticSettings(stats, counters) });
        }

        private static GraphicsResult StatsListCounters()
        {
            string[] counters = { "batches", "drawCalls", "setPassCalls", "triangles", "vertices", "shadowCasters", "renderTextureChanges", "screenRes", "screenBytes", "textureBytes", "meshBytes" };
            return Ok("Rendering counters listed.", new GraphicsData { Names = counters });
        }

        private static GraphicsResult StatsSetSceneDebug(GraphicsArguments a)
        {
            SceneView view = SceneView.lastActiveSceneView;
            if (view == null) throw new InvalidOperationException("No Scene view is open.");
            string mode = string.IsNullOrEmpty(a.Mode) ? "Shaded" : a.Mode;
            MethodInfo method = typeof(SceneView).GetMethod("GetBuiltinCameraMode", AnyMember);
            if (method == null) throw new InvalidOperationException("Scene debug modes are unavailable in this Unity editor.");
            object cameraMode = method.Invoke(null, new object[] { mode });
            PropertyInfo property = typeof(SceneView).GetProperty("cameraMode", AnyMember);
            if (property == null) throw new InvalidOperationException("Scene camera mode property is unavailable.");
            property.SetValue(view, cameraMode, null);
            view.Repaint();
            return Ok("Scene debug mode updated.", new GraphicsData { Mode = mode });
        }

        private static GraphicsResult StatsGetMemory()
        {
            return Ok("Rendering memory statistics read.", new GraphicsData
            {
                Memory = new MemoryRecord
                {
                    TotalAllocated = Profiler.GetTotalAllocatedMemoryLong(),
                    TotalReserved = Profiler.GetTotalReservedMemoryLong(),
                    TotalUnusedReserved = Profiler.GetTotalUnusedReservedMemoryLong(),
                    MonoHeap = Profiler.GetMonoHeapSizeLong(),
                    MonoUsed = Profiler.GetMonoUsedSizeLong(),
                }
            });
        }

        private static GraphicsResult PipelineGetInfo()
        {
            UnityEngine.Object pipeline = ActivePipelineAsset();
            return Ok("Render pipeline information read.", new GraphicsData
            {
                Pipeline = pipeline == null ? "BuiltIn" : pipeline.GetType().FullName,
                PipelineName = PipelineName(pipeline),
                Path = pipeline == null ? string.Empty : AssetDatabase.GetAssetPath(pipeline),
                QualityLevel = QualitySettings.GetQualityLevel(),
                QualityName = QualitySettings.names[QualitySettings.GetQualityLevel()],
                HasURP = IsUrp(pipeline),
                HasHDRP = IsHdrp(pipeline),
            });
        }

        private static GraphicsResult PipelineSetQuality(GraphicsArguments a)
        {
            if (string.IsNullOrEmpty(a.Level)) throw new InvalidOperationException("level is required.");
            int level;
            if (!int.TryParse(a.Level, out level)) level = Array.FindIndex(QualitySettings.names, n => string.Equals(n, a.Level, StringComparison.OrdinalIgnoreCase));
            if (level < 0 || level >= QualitySettings.names.Length) throw new InvalidOperationException("Unknown quality level: " + a.Level);
            QualitySettings.SetQualityLevel(level, true);
            return Ok("Quality level updated.", PipelineGetInfo().Data);
        }

        private static GraphicsResult PipelineGetSettings()
        {
            UnityEngine.Object pipeline = ActivePipelineAsset();
            if (pipeline == null)
                return Ok("Built-in render pipeline settings read.", new GraphicsData { Pipeline = "BuiltIn", Settings = BuiltInPipelineSettings() });
            return Ok("Render pipeline settings read.", new GraphicsData { Pipeline = pipeline.GetType().FullName, Path = AssetDatabase.GetAssetPath(pipeline), Settings = ReadSerializedSettings(pipeline) });
        }

        private static GraphicsResult PipelineSetSettings(GraphicsArguments a)
        {
            UnityEngine.Object pipeline = ActivePipelineAsset();
            if (pipeline == null)
            {
                ApplyStaticPatches(typeof(QualitySettings), a.Settings);
                return Ok("Built-in render pipeline settings updated.", PipelineGetSettings().Data);
            }
            Undo.RecordObject(pipeline, "MCP configure render pipeline");
            ApplyPatches(pipeline, a.Settings);
            EditorUtility.SetDirty(pipeline);
            AssetDatabase.SaveAssets();
            return Ok("Render pipeline settings updated.", PipelineGetSettings().Data);
        }

        private static GraphicsResult FeatureList()
        {
            UnityEngine.Object rendererData = RendererData();
            IList list = RendererFeatures(rendererData);
            List<FeatureRecord> records = new List<FeatureRecord>();
            for (int i = 0; i < list.Count; i++)
            {
                UnityEngine.Object feature = list[i] as UnityEngine.Object;
                if (feature == null) continue;
                records.Add(new FeatureRecord { Index = i, Name = feature.name, Type = feature.GetType().FullName, Active = ReadFeatureActive(feature) });
            }
            return Ok("Renderer features listed.", new GraphicsData { Path = AssetDatabase.GetAssetPath(rendererData), Count = records.Count, Features = records.ToArray() });
        }

        private static GraphicsResult FeatureAdd(GraphicsArguments a)
        {
            UnityEngine.Object rendererData = RendererData();
            IList list = RendererFeatures(rendererData);
            Type type = FindTypeBySuffix(a.FeatureType, "ScriptableRendererFeature");
            if (type == null || !typeof(ScriptableObject).IsAssignableFrom(type)) throw new InvalidOperationException("Renderer feature type was not found: " + a.FeatureType);
            ScriptableObject feature = ScriptableObject.CreateInstance(type);
            feature.name = string.IsNullOrEmpty(a.Name) ? type.Name : a.Name;
            ApplyPatches(feature, a.Properties != null && a.Properties.Length > 0 ? a.Properties : a.Parameters);
            AssetDatabase.AddObjectToAsset(feature, rendererData);
            Undo.RecordObject(rendererData, "MCP add renderer feature");
            list.Add(feature);
            EditorUtility.SetDirty(feature);
            EditorUtility.SetDirty(rendererData);
            AssetDatabase.SaveAssets();
            return Ok("Renderer feature added.", FeatureList().Data);
        }

        private static GraphicsResult FeatureRemove(GraphicsArguments a)
        {
            UnityEngine.Object rendererData = RendererData();
            IList list = RendererFeatures(rendererData);
            int index = ResolveFeatureIndex(list, a);
            UnityEngine.Object feature = list[index] as UnityEngine.Object;
            Undo.RecordObject(rendererData, "MCP remove renderer feature");
            list.RemoveAt(index);
            if (feature != null) UnityEngine.Object.DestroyImmediate(feature, true);
            EditorUtility.SetDirty(rendererData);
            AssetDatabase.SaveAssets();
            return Ok("Renderer feature removed.", FeatureList().Data);
        }

        private static GraphicsResult FeatureConfigure(GraphicsArguments a)
        {
            UnityEngine.Object rendererData = RendererData();
            IList list = RendererFeatures(rendererData);
            UnityEngine.Object feature = list[ResolveFeatureIndex(list, a)] as UnityEngine.Object;
            Undo.RecordObject(feature, "MCP configure renderer feature");
            ApplyPatches(feature, a.Properties != null && a.Properties.Length > 0 ? a.Properties : a.Parameters);
            EditorUtility.SetDirty(feature);
            AssetDatabase.SaveAssets();
            return Ok("Renderer feature configured.", FeatureList().Data);
        }

        private static GraphicsResult FeatureToggle(GraphicsArguments a)
        {
            UnityEngine.Object rendererData = RendererData();
            IList list = RendererFeatures(rendererData);
            UnityEngine.Object feature = list[ResolveFeatureIndex(list, a)] as UnityEngine.Object;
            bool active = !a.HasActive || a.Active;
            MethodInfo method = feature.GetType().GetMethod("SetActive", AnyMember);
            if (method != null) method.Invoke(feature, new object[] { active });
            else SetMember(feature, "m_Active", active);
            EditorUtility.SetDirty(feature);
            AssetDatabase.SaveAssets();
            return Ok("Renderer feature state updated.", FeatureList().Data);
        }

        private static GraphicsResult FeatureReorder(GraphicsArguments a)
        {
            if (a.Order == null || a.Order.Length == 0) throw new InvalidOperationException("order is required.");
            UnityEngine.Object rendererData = RendererData();
            IList list = RendererFeatures(rendererData);
            if (a.Order.Length != list.Count || a.Order.Distinct().Count() != list.Count || a.Order.Any(i => i < 0 || i >= list.Count))
                throw new InvalidOperationException("order must be a complete permutation of renderer feature indices.");
            object[] values = new object[list.Count];
            for (int i = 0; i < values.Length; i++) values[i] = list[a.Order[i]];
            Undo.RecordObject(rendererData, "MCP reorder renderer features");
            list.Clear();
            for (int i = 0; i < values.Length; i++) list.Add(values[i]);
            EditorUtility.SetDirty(rendererData);
            AssetDatabase.SaveAssets();
            return Ok("Renderer features reordered.", FeatureList().Data);
        }

        private static GraphicsResult SkyboxGet()
        {
            Material skybox = RenderSettings.skybox;
            return Ok("Skybox and environment settings read.", new GraphicsData
            {
                Material = skybox == null ? string.Empty : AssetDatabase.GetAssetPath(skybox),
                AmbientMode = RenderSettings.ambientMode.ToString(),
                AmbientSkyColor = ColorOf(RenderSettings.ambientSkyColor),
                AmbientEquatorColor = ColorOf(RenderSettings.ambientEquatorColor),
                AmbientGroundColor = ColorOf(RenderSettings.ambientGroundColor),
                AmbientIntensity = RenderSettings.ambientIntensity,
                FogEnabled = RenderSettings.fog,
                FogMode = RenderSettings.fogMode.ToString(),
                FogColor = ColorOf(RenderSettings.fogColor),
                FogDensity = RenderSettings.fogDensity,
                FogStart = RenderSettings.fogStartDistance,
                FogEnd = RenderSettings.fogEndDistance,
                ReflectionMode = RenderSettings.defaultReflectionMode.ToString(),
                ReflectionIntensity = RenderSettings.reflectionIntensity,
                ReflectionBounces = RenderSettings.reflectionBounces,
                Sun = RenderSettings.sun == null ? string.Empty : FullPath(RenderSettings.sun.gameObject),
            });
        }

        private static GraphicsResult SkyboxSetMaterial(GraphicsArguments a)
        {
            string path = !string.IsNullOrEmpty(a.Material) ? a.Material : a.Path;
            Material material = string.IsNullOrEmpty(path) ? null : AssetDatabase.LoadAssetAtPath<Material>(path);
            if (!string.IsNullOrEmpty(path) && material == null) throw new InvalidOperationException("Skybox material was not found: " + path);
            RecordRenderSettingsUndo("MCP set skybox material");
            RenderSettings.skybox = material;
            DirtyRenderSettings();
            DynamicGI.UpdateEnvironment();
            return Ok("Skybox material updated.", SkyboxGet().Data);
        }

        private static GraphicsResult SkyboxSetProperties(GraphicsArguments a)
        {
            Material material = RenderSettings.skybox;
            if (material == null) throw new InvalidOperationException("No skybox material is assigned.");
            Undo.RecordObject(material, "MCP configure skybox material");
            ApplyMaterialPatches(material, a.Properties);
            EditorUtility.SetDirty(material);
            AssetDatabase.SaveAssets();
            DynamicGI.UpdateEnvironment();
            return Ok("Skybox material properties updated.", SkyboxGet().Data);
        }

        private static GraphicsResult SkyboxSetAmbient(GraphicsArguments a)
        {
            RecordRenderSettingsUndo("MCP configure ambient lighting");
            if (!string.IsNullOrEmpty(a.AmbientMode))
            {
                string value = string.Equals(a.AmbientMode, "Custom", StringComparison.OrdinalIgnoreCase) ? "Flat" : a.AmbientMode;
                if (!Enum.TryParse(value, true, out AmbientMode mode)) throw new InvalidOperationException("Unknown ambient_mode: " + a.AmbientMode);
                RenderSettings.ambientMode = mode;
            }
            if (a.HasColor) RenderSettings.ambientSkyColor = ColorOf(a.Color);
            if (a.HasEquatorColor) RenderSettings.ambientEquatorColor = ColorOf(a.EquatorColor);
            if (a.HasGroundColor) RenderSettings.ambientGroundColor = ColorOf(a.GroundColor);
            if (a.HasIntensity) RenderSettings.ambientIntensity = a.Intensity;
            DirtyRenderSettings();
            DynamicGI.UpdateEnvironment();
            return Ok("Ambient lighting updated.", SkyboxGet().Data);
        }

        private static GraphicsResult SkyboxSetFog(GraphicsArguments a)
        {
            RecordRenderSettingsUndo("MCP configure fog");
            if (a.HasFogEnabled) RenderSettings.fog = a.FogEnabled;
            if (!string.IsNullOrEmpty(a.FogMode))
            {
                string value = a.FogMode.Replace("ExponentialSquared", "ExponentialSquared");
                if (!Enum.TryParse(value, true, out FogMode mode)) throw new InvalidOperationException("Unknown fog_mode: " + a.FogMode);
                RenderSettings.fogMode = mode;
            }
            if (a.HasFogColor) RenderSettings.fogColor = ColorOf(a.FogColor);
            if (a.HasFogDensity) RenderSettings.fogDensity = a.FogDensity;
            if (a.HasFogStart) RenderSettings.fogStartDistance = a.FogStart;
            if (a.HasFogEnd) RenderSettings.fogEndDistance = a.FogEnd;
            DirtyRenderSettings();
            return Ok("Fog settings updated.", SkyboxGet().Data);
        }

        private static GraphicsResult SkyboxSetReflection(GraphicsArguments a)
        {
            RecordRenderSettingsUndo("MCP configure environment reflection");
            if (!string.IsNullOrEmpty(a.ReflectionMode) && Enum.TryParse(a.ReflectionMode, true, out DefaultReflectionMode mode)) RenderSettings.defaultReflectionMode = mode;
            if (a.HasIntensity) RenderSettings.reflectionIntensity = a.Intensity;
            if (a.HasBounces) RenderSettings.reflectionBounces = Math.Max(1, a.Bounces);
            DirtyRenderSettings();
            DynamicGI.UpdateEnvironment();
            return Ok("Environment reflection updated.", SkyboxGet().Data);
        }

        private static GraphicsResult SkyboxSetSun(GraphicsArguments a)
        {
            Light light = null;
            if (!string.IsNullOrEmpty(a.Target))
            {
                light = FindTarget(a.Target).GetComponent<Light>();
                if (light == null) throw new InvalidOperationException("Target has no Light component.");
            }
            RecordRenderSettingsUndo("MCP set sun source");
            RenderSettings.sun = light;
            DirtyRenderSettings();
            return Ok("Sun source updated.", SkyboxGet().Data);
        }

        private static GraphicsData BakeData()
        {
            return new GraphicsData { IsRunning = Lightmapping.isRunning, WorkflowMode = Lightmapping.giWorkflowMode.ToString() };
        }

        private static UnityEngine.Object RenderSettingsObject()
        {
            MethodInfo method = typeof(RenderSettings).GetMethod("GetRenderSettings", AnyMember);
            return method == null ? null : method.Invoke(null, null) as UnityEngine.Object;
        }

        private static void RecordRenderSettingsUndo(string label)
        {
            UnityEngine.Object settings = RenderSettingsObject();
            if (settings != null) Undo.RecordObject(settings, label);
        }

        private static void DirtyRenderSettings()
        {
            UnityEngine.Object settings = RenderSettingsObject();
            if (settings != null) EditorUtility.SetDirty(settings);
            Scene active = SceneManager.GetActiveScene();
            if (active.IsValid()) UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(active);
        }

        private static GraphicsData VolumeData(GameObject go, Component volume, UnityEngine.Object profile, UnityEngine.Object effect = null)
        {
            IList effects = profile == null ? null : VolumeComponents(profile);
            return new GraphicsData
            {
                Target = FullPath(go), InstanceId = go.GetInstanceID(), ComponentType = volume.GetType().FullName,
                IsGlobal = Convert.ToBoolean(GetMember(volume, "isGlobal") ?? false),
                Weight = Convert.ToSingle(GetMember(volume, "weight") ?? 0f),
                Priority = Convert.ToSingle(GetMember(volume, "priority") ?? 0f),
                Path = profile == null ? string.Empty : AssetDatabase.GetAssetPath(profile),
                Count = effects == null ? 0 : effects.Count,
                Effect = effect == null ? string.Empty : effect.GetType().FullName,
                Effects = effects == null ? new NamedRecord[0] : effects.Cast<object>().Where(x => x != null).Select(x => new NamedRecord { Name = ((UnityEngine.Object)x).name, Type = x.GetType().FullName }).ToArray(),
            };
        }

        private static Component FindVolume(string target)
        {
            Type type = RequireType("UnityEngine.Rendering.Volume", "Volume system requires an installed SRP core package.");
            GameObject go = FindTarget(target);
            Component component = go.GetComponent(type);
            if (component == null) throw new InvalidOperationException("Target has no Volume component.");
            return component;
        }

        private static UnityEngine.Object EnsureVolumeProfile(Component volume, string profilePath)
        {
            UnityEngine.Object profile = GetMember(volume, "sharedProfile") as UnityEngine.Object;
            if (!string.IsNullOrEmpty(profilePath)) profile = AssetDatabase.LoadAssetAtPath(profilePath, RequireType("UnityEngine.Rendering.VolumeProfile", "VolumeProfile type unavailable."));
            if (profile == null)
            {
                profile = CreateVolumeProfileAsset(DefaultProfilePath(volume.gameObject.name));
                SetMember(volume, "sharedProfile", profile);
                EditorUtility.SetDirty(volume);
            }
            return profile;
        }

        private static UnityEngine.Object CreateVolumeProfileAsset(string path)
        {
            path = NormalizeAssetPath(path, ".asset");
            EnsureAssetFolder(System.IO.Path.GetDirectoryName(path).Replace('\\', '/'));
            if (AssetDatabase.LoadMainAssetAtPath(path) != null) throw new InvalidOperationException("Asset already exists: " + path);
            Type type = RequireType("UnityEngine.Rendering.VolumeProfile", "VolumeProfile type unavailable.");
            ScriptableObject profile = ScriptableObject.CreateInstance(type);
            profile.name = System.IO.Path.GetFileNameWithoutExtension(path);
            AssetDatabase.CreateAsset(profile, path);
            AssetDatabase.SaveAssets();
            return profile;
        }

        private static UnityEngine.Object AddEffectToProfile(UnityEngine.Object profile, string effectName, SerializedPatch[] patches, bool active)
        {
            if (profile == null) throw new InvalidOperationException("A VolumeProfile is required.");
            Type effectType = FindTypeBySuffix(effectName, "VolumeComponent");
            if (effectType == null) throw new InvalidOperationException("Volume effect type was not found: " + effectName);
            MethodInfo add = profile.GetType().GetMethods(AnyMember).FirstOrDefault(m => m.Name == "Add" && !m.IsGenericMethod && m.GetParameters().Length > 0 && m.GetParameters()[0].ParameterType == typeof(Type));
            object effect;
            if (add != null)
            {
                ParameterInfo[] ps = add.GetParameters();
                effect = add.Invoke(profile, ps.Length > 1 ? new object[] { effectType, true } : new object[] { effectType });
            }
            else
            {
                add = profile.GetType().GetMethods(AnyMember).FirstOrDefault(m => m.Name == "Add" && m.IsGenericMethodDefinition);
                if (add == null) throw new InvalidOperationException("VolumeProfile.Add is unavailable.");
                MethodInfo generic = add.MakeGenericMethod(effectType);
                effect = generic.Invoke(profile, generic.GetParameters().Length > 0 ? new object[] { true } : null);
            }
            UnityEngine.Object unityEffect = effect as UnityEngine.Object;
            if (unityEffect == null) throw new InvalidOperationException("Unity failed to create the volume effect.");
            SetMember(unityEffect, "active", active);
            ApplyPatches(unityEffect, patches);
            EditorUtility.SetDirty(profile);
            EditorUtility.SetDirty(unityEffect);
            AssetDatabase.SaveAssets();
            return unityEffect;
        }

        private static UnityEngine.Object FindEffect(UnityEngine.Object profile, string effect)
        {
            if (string.IsNullOrEmpty(effect)) throw new InvalidOperationException("effect is required.");
            IList list = VolumeComponents(profile);
            foreach (object item in list)
                if (item != null && (string.Equals(item.GetType().Name, effect, StringComparison.OrdinalIgnoreCase) || string.Equals(item.GetType().FullName, effect, StringComparison.OrdinalIgnoreCase))) return item as UnityEngine.Object;
            return null;
        }

        private static IList VolumeComponents(UnityEngine.Object profile)
        {
            object value = GetMember(profile, "components");
            IList list = value as IList;
            if (list == null) throw new InvalidOperationException("VolumeProfile components are unavailable.");
            return list;
        }

        private static List<Type> AvailableEffectTypes()
        {
            Type baseType = FindType("UnityEngine.Rendering.VolumeComponent");
            if (baseType == null) return new List<Type>();
            return AppDomain.CurrentDomain.GetAssemblies().SelectMany(SafeTypes).Where(t => t != null && !t.IsAbstract && baseType.IsAssignableFrom(t)).OrderBy(t => t.FullName).ToList();
        }

        private static UnityEngine.Object ActivePipelineAsset()
        {
            PropertyInfo graphics = typeof(GraphicsSettings).GetProperty("renderPipelineAsset", AnyMember) ?? typeof(GraphicsSettings).GetProperty("defaultRenderPipeline", AnyMember);
            UnityEngine.Object pipeline = graphics == null ? null : graphics.GetValue(null, null) as UnityEngine.Object;
            PropertyInfo quality = typeof(QualitySettings).GetProperty("renderPipeline", AnyMember);
            UnityEngine.Object qualityPipeline = quality == null ? null : quality.GetValue(null, null) as UnityEngine.Object;
            return qualityPipeline ?? pipeline;
        }

        private static UnityEngine.Object RendererData()
        {
            UnityEngine.Object pipeline = ActivePipelineAsset();
            if (!IsUrp(pipeline)) throw new InvalidOperationException("Renderer features require URP.");
            object value = GetMember(pipeline, "m_RendererDataList") ?? GetMember(pipeline, "m_RendererData");
            IEnumerable enumerable = value as IEnumerable;
            if (enumerable != null && !(value is string))
                foreach (object item in enumerable) if (item is UnityEngine.Object) return (UnityEngine.Object)item;
            UnityEngine.Object renderer = value as UnityEngine.Object;
            if (renderer == null) throw new InvalidOperationException("The active URP renderer data asset could not be resolved.");
            return renderer;
        }

        private static IList RendererFeatures(UnityEngine.Object rendererData)
        {
            IList list = GetMember(rendererData, "m_RendererFeatures") as IList ?? GetMember(rendererData, "rendererFeatures") as IList;
            if (list == null) throw new InvalidOperationException("Renderer feature list is unavailable for this URP version.");
            return list;
        }

        private static int ResolveFeatureIndex(IList list, GraphicsArguments a)
        {
            if (a.HasIndex)
            {
                if (a.Index < 0 || a.Index >= list.Count) throw new InvalidOperationException("Renderer feature index is out of range.");
                return a.Index;
            }
            string name = !string.IsNullOrEmpty(a.Target) ? a.Target : a.Name;
            for (int i = 0; i < list.Count; i++)
            {
                UnityEngine.Object item = list[i] as UnityEngine.Object;
                if (item != null && (string.Equals(item.name, name, StringComparison.OrdinalIgnoreCase) || string.Equals(item.GetType().Name, name, StringComparison.OrdinalIgnoreCase))) return i;
            }
            throw new InvalidOperationException("Renderer feature was not found: " + name);
        }

        private static bool ReadFeatureActive(UnityEngine.Object feature)
        {
            object value = GetMember(feature, "isActive") ?? GetMember(feature, "m_Active");
            return value != null && Convert.ToBoolean(value);
        }

        private static string PipelineName(UnityEngine.Object pipeline)
        {
            if (pipeline == null) return "Built-in Render Pipeline";
            if (IsUrp(pipeline)) return "Universal Render Pipeline";
            if (IsHdrp(pipeline)) return "High Definition Render Pipeline";
            return pipeline.GetType().Name;
        }

        private static bool IsUrp(UnityEngine.Object pipeline) { return pipeline != null && pipeline.GetType().FullName.IndexOf("Universal", StringComparison.OrdinalIgnoreCase) >= 0; }
        private static bool IsHdrp(UnityEngine.Object pipeline) { return pipeline != null && pipeline.GetType().FullName.IndexOf("HDRender", StringComparison.OrdinalIgnoreCase) >= 0; }

        private static SettingRecord[] BuiltInPipelineSettings()
        {
            return new[]
            {
                Setting("pixelLightCount", QualitySettings.pixelLightCount), Setting("shadows", QualitySettings.shadows),
                Setting("shadowResolution", QualitySettings.shadowResolution), Setting("shadowDistance", QualitySettings.shadowDistance),
                Setting("antiAliasing", QualitySettings.antiAliasing), Setting("vSyncCount", QualitySettings.vSyncCount),
                Setting("lodBias", QualitySettings.lodBias), Setting("anisotropicFiltering", QualitySettings.anisotropicFiltering),
            };
        }

        private static SettingRecord[] ReadSerializedSettings(UnityEngine.Object target)
        {
            List<SettingRecord> values = new List<SettingRecord>();
            SerializedObject serialized = new SerializedObject(target);
            SerializedProperty iterator = serialized.GetIterator();
            bool enter = true;
            while (iterator.NextVisible(enter))
            {
                enter = false;
                if (iterator.propertyPath == "m_Script") continue;
                values.Add(new SettingRecord { Name = iterator.propertyPath, Value = SerializedValue(iterator), Type = iterator.propertyType.ToString() });
                if (values.Count >= 128) break;
            }
            return values.ToArray();
        }

        private static SettingRecord[] ReadStaticSettings(Type type, string[] names)
        {
            if (type == null) return new SettingRecord[0];
            List<SettingRecord> values = new List<SettingRecord>();
            foreach (string name in names)
            {
                MemberInfo member = FindMember(type, name, true);
                if (member == null) continue;
                object value = ReadMember(member, null);
                values.Add(Setting(name, value));
            }
            return values.ToArray();
        }

        private static void ApplyStaticPatches(Type type, SerializedPatch[] patches)
        {
            if (patches == null) return;
            foreach (SerializedPatch patch in patches)
            {
                MemberInfo member = FindMember(type, patch.Path, true);
                if (member == null) throw new InvalidOperationException("Setting was not found: " + patch.Path);
                Type valueType = MemberType(member);
                WriteMember(member, null, ConvertPatch(patch, valueType));
            }
        }

        private static void ApplyPatches(UnityEngine.Object target, SerializedPatch[] patches)
        {
            if (target == null || patches == null || patches.Length == 0) return;
            SerializedObject serialized = new SerializedObject(target);
            foreach (SerializedPatch patch in patches)
            {
                if (string.IsNullOrEmpty(patch.Path)) continue;
                SerializedProperty property = serialized.FindProperty(patch.Path);
                if (property == null)
                {
                    object member = GetMember(target, patch.Path);
                    if (member != null && member.GetType().Name.EndsWith("Parameter", StringComparison.Ordinal))
                    {
                        SetMember(member, "overrideState", true);
                        SetMember(member, "value", ConvertPatch(patch, MemberType(FindMember(member.GetType(), "value", false))));
                        EditorUtility.SetDirty(target);
                        continue;
                    }
                    MemberInfo info = FindMember(target.GetType(), patch.Path, false);
                    if (info == null) throw new InvalidOperationException("Serialized property was not found: " + patch.Path);
                    WriteMember(info, target, ConvertPatch(patch, MemberType(info)));
                    continue;
                }
                SetSerialized(property, patch);
            }
            serialized.ApplyModifiedProperties();
        }

        private static void ApplyMaterialPatches(Material material, SerializedPatch[] patches)
        {
            if (patches == null) return;
            foreach (SerializedPatch patch in patches)
            {
                string name = patch.Path;
                if (string.IsNullOrEmpty(name) || !material.HasProperty(name)) throw new InvalidOperationException("Skybox material property was not found: " + name);
                string kind = (patch.Kind ?? string.Empty).ToLowerInvariant();
                if (kind == "color") material.SetColor(name, ColorOf(patch.VectorValue));
                else if (kind == "vector" || kind == "vector4" || kind == "vector3" || kind == "vector2") material.SetVector(name, Vector4Of(patch.VectorValue));
                else if (kind == "texture" || kind == "reference") material.SetTexture(name, AssetDatabase.LoadAssetAtPath<Texture>(patch.StringValue));
                else material.SetFloat(name, kind == "int" ? patch.IntValue : patch.FloatValue);
            }
        }

        private static void SetSerialized(SerializedProperty property, SerializedPatch patch)
        {
            switch (property.propertyType)
            {
                case SerializedPropertyType.Boolean: property.boolValue = patch.BoolValue; break;
                case SerializedPropertyType.Integer: property.intValue = patch.IntValue; break;
                case SerializedPropertyType.Float: property.floatValue = patch.FloatValue; break;
                case SerializedPropertyType.String: property.stringValue = patch.StringValue ?? string.Empty; break;
                case SerializedPropertyType.Color: property.colorValue = ColorOf(patch.VectorValue); break;
                case SerializedPropertyType.Vector2: { Vector4 v = Vector4Of(patch.VectorValue); property.vector2Value = new Vector2(v.x, v.y); break; }
                case SerializedPropertyType.Vector3: { Vector4 v = Vector4Of(patch.VectorValue); property.vector3Value = new Vector3(v.x, v.y, v.z); break; }
                case SerializedPropertyType.Vector4: property.vector4Value = Vector4Of(patch.VectorValue); break;
                case SerializedPropertyType.Enum: property.enumValueIndex = Math.Max(0, Array.FindIndex(property.enumNames, x => string.Equals(x, patch.StringValue, StringComparison.OrdinalIgnoreCase))); break;
                case SerializedPropertyType.ObjectReference: property.objectReferenceValue = string.IsNullOrEmpty(patch.StringValue) ? null : AssetDatabase.LoadMainAssetAtPath(patch.StringValue); break;
                default: throw new InvalidOperationException("Unsupported serialized property type: " + property.propertyType);
            }
        }

        private static string SerializedValue(SerializedProperty p)
        {
            switch (p.propertyType)
            {
                case SerializedPropertyType.Boolean: return p.boolValue.ToString();
                case SerializedPropertyType.Integer: return p.intValue.ToString();
                case SerializedPropertyType.Float: return p.floatValue.ToString("R");
                case SerializedPropertyType.String: return p.stringValue;
                case SerializedPropertyType.Enum: return p.enumDisplayNames.Length > p.enumValueIndex ? p.enumDisplayNames[p.enumValueIndex] : p.enumValueIndex.ToString();
                case SerializedPropertyType.ObjectReference: return p.objectReferenceValue == null ? string.Empty : AssetDatabase.GetAssetPath(p.objectReferenceValue);
                default: return p.propertyType.ToString();
            }
        }

        private static object ConvertPatch(SerializedPatch patch, Type type)
        {
            if (type == null) return null;
            if (type == typeof(string)) return patch.StringValue ?? string.Empty;
            if (type == typeof(bool)) return patch.BoolValue;
            if (type == typeof(int)) return patch.IntValue;
            if (type == typeof(float)) return patch.FloatValue;
            if (type == typeof(double)) return (double)patch.FloatValue;
            if (type == typeof(Color)) return ColorOf(patch.VectorValue);
            if (type == typeof(Vector2)) { Vector4 v = Vector4Of(patch.VectorValue); return new Vector2(v.x, v.y); }
            if (type == typeof(Vector3)) return Vector3Of(patch.VectorValue);
            if (type == typeof(Vector4)) return Vector4Of(patch.VectorValue);
            if (type.IsEnum)
            {
                if (!string.IsNullOrEmpty(patch.StringValue)) return Enum.Parse(type, patch.StringValue, true);
                return Enum.ToObject(type, patch.IntValue);
            }
            if (typeof(UnityEngine.Object).IsAssignableFrom(type)) return string.IsNullOrEmpty(patch.StringValue) ? null : AssetDatabase.LoadAssetAtPath(patch.StringValue, type);
            return Convert.ChangeType(!string.IsNullOrEmpty(patch.StringValue) ? (object)patch.StringValue : patch.FloatValue, type);
        }

        private static Type FindType(string fullName)
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type type = assembly.GetType(fullName, false);
                if (type != null) return type;
            }
            return null;
        }

        private static Type RequireType(string fullName, string message)
        {
            Type type = FindType(fullName);
            if (type == null) throw new InvalidOperationException(message);
            return type;
        }

        private static Type FindTypeBySuffix(string requested, string requiredBaseSuffix)
        {
            if (string.IsNullOrEmpty(requested)) return null;
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                foreach (Type type in SafeTypes(assembly))
                    if (type != null && !type.IsAbstract && (string.Equals(type.Name, requested, StringComparison.OrdinalIgnoreCase) || string.Equals(type.FullName, requested, StringComparison.OrdinalIgnoreCase)))
                    {
                        Type cursor = type;
                        while (cursor != null)
                        {
                            if (cursor.Name.EndsWith(requiredBaseSuffix, StringComparison.Ordinal)) return type;
                            cursor = cursor.BaseType;
                        }
                    }
            return null;
        }

        private static IEnumerable<Type> SafeTypes(Assembly assembly)
        {
            try { return assembly.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t != null); }
            catch { return new Type[0]; }
        }

        private static object GetMember(object target, string name)
        {
            if (target == null) return null;
            MemberInfo member = FindMember(target.GetType(), name, false);
            return member == null ? null : ReadMember(member, target);
        }

        private static void SetMember(object target, string name, object value)
        {
            if (target == null) throw new InvalidOperationException("Cannot set a member on null.");
            MemberInfo member = FindMember(target.GetType(), name, false);
            if (member == null) throw new InvalidOperationException("Member was not found: " + target.GetType().FullName + "." + name);
            WriteMember(member, target, value);
        }

        private static MemberInfo FindMember(Type type, string name, bool isStatic)
        {
            if (type == null || string.IsNullOrEmpty(name)) return null;
            BindingFlags flags = AnyMember;
            FieldInfo field = type.GetField(name, flags);
            if (field != null && field.IsStatic == isStatic) return field;
            PropertyInfo property = type.GetProperty(name, flags);
            MethodInfo accessor = property == null ? null : (property.GetGetMethod(true) ?? property.GetSetMethod(true));
            return property != null && accessor != null && accessor.IsStatic == isStatic ? property : null;
        }

        private static object ReadMember(MemberInfo member, object target)
        {
            FieldInfo field = member as FieldInfo;
            if (field != null) return field.GetValue(target);
            return ((PropertyInfo)member).GetValue(target, null);
        }

        private static void WriteMember(MemberInfo member, object target, object value)
        {
            Type type = MemberType(member);
            if (value != null && !type.IsInstanceOfType(value))
            {
                if (type.IsEnum) value = value is string ? Enum.Parse(type, (string)value, true) : Enum.ToObject(type, value);
                else value = Convert.ChangeType(value, type);
            }
            FieldInfo field = member as FieldInfo;
            if (field != null) field.SetValue(target, value);
            else ((PropertyInfo)member).SetValue(target, value, null);
        }

        private static Type MemberType(MemberInfo member)
        {
            FieldInfo field = member as FieldInfo;
            return field != null ? field.FieldType : ((PropertyInfo)member).PropertyType;
        }

        private static GameObject FindTarget(string value)
        {
            if (string.IsNullOrEmpty(value)) throw new InvalidOperationException("target is required.");
            if (int.TryParse(value, out int id))
            {
                GameObject byId = EditorUtility.InstanceIDToObject(id) as GameObject;
                if (byId != null) return byId;
                Component component = EditorUtility.InstanceIDToObject(id) as Component;
                if (component != null) return component.gameObject;
            }
            GameObject exact = GameObject.Find(value);
            if (exact != null) return exact;
            foreach (GameObject go in Resources.FindObjectsOfTypeAll<GameObject>())
                if (go.scene.IsValid() && (string.Equals(go.name, value, StringComparison.Ordinal) || string.Equals(FullPath(go), value.TrimStart('/'), StringComparison.Ordinal))) return go;
            throw new InvalidOperationException("GameObject was not found: " + value);
        }

        private static string FullPath(GameObject go)
        {
            string path = go.name;
            Transform parent = go.transform.parent;
            while (parent != null) { path = parent.name + "/" + path; parent = parent.parent; }
            return path;
        }

        private static Vector3[] ProbeGrid(int[] grid, bool hasGrid, float spacing)
        {
            int x = hasGrid && grid != null && grid.Length > 0 ? Math.Max(1, grid[0]) : 2;
            int y = hasGrid && grid != null && grid.Length > 1 ? Math.Max(1, grid[1]) : 2;
            int z = hasGrid && grid != null && grid.Length > 2 ? Math.Max(1, grid[2]) : 2;
            List<Vector3> points = new List<Vector3>(x * y * z);
            for (int ix = 0; ix < x; ix++) for (int iy = 0; iy < y; iy++) for (int iz = 0; iz < z; iz++)
                points.Add(new Vector3((ix - (x - 1) * .5f) * spacing, (iy - (y - 1) * .5f) * spacing, (iz - (z - 1) * .5f) * spacing));
            return points.ToArray();
        }

        private static string NormalizeAssetPath(string path, string extension)
        {
            path = (path ?? string.Empty).Replace('\\', '/').Trim();
            if (!path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)) path = "Assets/" + path.TrimStart('/');
            if (!path.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) path += extension;
            if (path.Contains("..")) throw new InvalidOperationException("Asset path cannot contain '..'.");
            return path;
        }

        private static string DefaultProfilePath(string name)
        {
            return AssetDatabase.GenerateUniqueAssetPath("Assets/" + Sanitize(name) + "Profile.asset");
        }

        private static string Sanitize(string value)
        {
            foreach (char c in System.IO.Path.GetInvalidFileNameChars()) value = value.Replace(c, '_');
            return string.IsNullOrEmpty(value) ? "Volume" : value;
        }

        private static void EnsureAssetFolder(string folder)
        {
            if (string.IsNullOrEmpty(folder) || folder == "Assets") return;
            string current = "Assets";
            foreach (string part in folder.Substring("Assets".Length).Trim('/').Split('/'))
            {
                if (string.IsNullOrEmpty(part)) continue;
                string next = current + "/" + part;
                if (!AssetDatabase.IsValidFolder(next)) AssetDatabase.CreateFolder(current, part);
                current = next;
            }
        }

        private static Vector3 Vector3Of(float[] values) { return new Vector3(Value(values, 0), Value(values, 1), Value(values, 2)); }
        private static Vector4 Vector4Of(float[] values) { return new Vector4(Value(values, 0), Value(values, 1), Value(values, 2), Value(values, 3)); }
        private static Color ColorOf(float[] values) { return new Color(Value(values, 0), Value(values, 1), Value(values, 2), values != null && values.Length > 3 ? values[3] : 1f); }
        private static float[] ColorOf(Color value) { return new[] { value.r, value.g, value.b, value.a }; }
        private static float Value(float[] values, int index) { return values != null && values.Length > index ? values[index] : 0f; }
        private static SettingRecord Setting(string name, object value) { return new SettingRecord { Name = name, Value = value == null ? string.Empty : value.ToString(), Type = value == null ? string.Empty : value.GetType().Name }; }
        private static GraphicsResult Ok(string message, GraphicsData data) { return new GraphicsResult { Success = true, Message = message, Data = data ?? new GraphicsData() }; }
        private static GraphicsResult Fail(string message) { return new GraphicsResult { Success = false, Message = message, Data = new GraphicsData() }; }

        [Serializable] private sealed class GraphicsArguments
        {
            public string Action, Target, Effect, Name, ProfilePath, Path, Level, Mode, FeatureType, Material, AmbientMode, FogMode, ReflectionMode;
            public bool IsGlobal, Hdr, BoxProjection, Active, AsyncBake, FogEnabled;
            public bool HasIsGlobal, HasHdr, HasBoxProjection, HasActive, HasAsyncBake, HasFogEnabled;
            public float Weight, Priority, Spacing, Intensity, FogDensity, FogStart, FogEnd;
            public bool HasWeight, HasPriority, HasSpacing, HasIntensity, HasFogDensity, HasFogStart, HasFogEnd;
            public int Resolution, Index, Bounces;
            public bool HasResolution, HasIndex, HasBounces, HasPosition, HasSize, HasColor, HasEquatorColor, HasGroundColor, HasFogColor, HasGridSize;
            public float[] Position, Size, Color, EquatorColor, GroundColor, FogColor;
            public int[] GridSize, Order;
            public VectorRecord[] Positions;
            public SerializedPatch[] Parameters, Properties, Settings;
            public EffectDefinition[] Effects;
        }

        [Serializable] private sealed class EffectDefinition { public string Effect; public bool Active; public SerializedPatch[] Parameters; }
        [Serializable] private sealed class VectorRecord { public float[] Values; }
        [Serializable] private sealed class SerializedPatch { public string Path, Kind, StringValue; public bool BoolValue; public int IntValue; public float FloatValue; public float[] VectorValue; }
        [Serializable] private sealed class GraphicsResult { public bool Success; public string Message; public GraphicsData Data; }
        [Serializable] private sealed class GraphicsData
        {
            public string UnityVersion, Pipeline, PipelineName, Path, Name, Target, ComponentType, Effect, Mode, Material, AmbientMode, FogMode, ReflectionMode, Sun, QualityName, WorkflowMode;
            public bool HasVolumeSystem, HasURP, HasHDRP, IsGlobal, IsRunning, FogEnabled;
            public int AvailableEffects, InstanceId, Count, RenderId, QualityLevel, ReflectionBounces;
            public float Weight, Priority, AmbientIntensity, FogDensity, FogStart, FogEnd, ReflectionIntensity;
            public float[] AmbientSkyColor, AmbientEquatorColor, AmbientGroundColor, FogColor;
            public string[] Names;
            public NamedRecord[] Effects;
            public FeatureRecord[] Features;
            public SettingRecord[] Settings, Counters;
            public MemoryRecord Memory;
        }
        [Serializable] private sealed class NamedRecord { public string Name, Type; }
        [Serializable] private sealed class FeatureRecord { public int Index; public string Name, Type; public bool Active; }
        [Serializable] private sealed class SettingRecord { public string Name, Value, Type; }
        [Serializable] private sealed class MemoryRecord { public long TotalAllocated, TotalReserved, TotalUnusedReserved, MonoHeap, MonoUsed; }
    }
}
#endif
