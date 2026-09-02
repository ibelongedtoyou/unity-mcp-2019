#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace UnityMcp2019
{
    internal static class Mcp2019VfxTools
    {
        private const BindingFlags AnyMember = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

        internal static string Execute(string argumentsJson)
        {
            VfxArguments a = string.IsNullOrEmpty(argumentsJson) || argumentsJson == "{}"
                ? new VfxArguments()
                : JsonUtility.FromJson<VfxArguments>(argumentsJson) ?? new VfxArguments();
            string action = (a.Action ?? string.Empty).Trim().ToLowerInvariant();
            Props p = new Props(a.Properties);
            try
            {
                VfxResult result;
                if (action == "ping") result = Ping();
                else if (action.StartsWith("particle_", StringComparison.Ordinal)) result = ParticleAction(action.Substring(9), a, p);
                else if (action.StartsWith("vfx_", StringComparison.Ordinal)) result = VfxGraphAction(action.Substring(4), a, p);
                else if (action.StartsWith("line_", StringComparison.Ordinal)) result = LineAction(action.Substring(5), a, p);
                else if (action.StartsWith("trail_", StringComparison.Ordinal)) result = TrailAction(action.Substring(6), a, p);
                else result = Fail("Unknown manage_vfx action: " + action);
                return JsonUtility.ToJson(result);
            }
            catch (Exception ex)
            {
                return JsonUtility.ToJson(Fail(ex.GetBaseException().Message));
            }
        }

        private static VfxResult Ping()
        {
            return Ok("manage_vfx is available.", new VfxData
            {
                UnityVersion = Application.unityVersion,
                Components = new[] { "ParticleSystem", "VisualEffect", "LineRenderer", "TrailRenderer" },
                HasVfxGraph = FindType("UnityEngine.VFX.VisualEffect") != null,
            });
        }

        private static VfxResult ParticleAction(string action, VfxArguments a, Props p)
        {
            if (action == "create") return ParticleCreate(a, p);
            ParticleSystem ps = FindComponent<ParticleSystem>(a);
            switch (action)
            {
                case "get_info": return ParticleInfo(ps);
                case "set_main": return ParticleSetMain(ps, p);
                case "set_emission": return ParticleSetEmission(ps, p);
                case "set_shape": return ParticleSetShape(ps, p);
                case "set_color_over_lifetime": return ParticleSetColor(ps, p);
                case "set_size_over_lifetime": return ParticleSetSize(ps, p);
                case "set_velocity_over_lifetime": return ParticleSetVelocity(ps, p);
                case "set_noise": return ParticleSetNoise(ps, p);
                case "set_renderer": return ParticleSetRenderer(ps, p);
                case "enable_module": return ParticleEnableModule(ps, p);
                case "play": return ParticleControl(ps, p, "play");
                case "stop": return ParticleControl(ps, p, "stop");
                case "pause": return ParticleControl(ps, p, "pause");
                case "restart": return ParticleControl(ps, p, "restart");
                case "clear": return ParticleControl(ps, p, "clear");
                case "add_burst": return ParticleAddBurst(ps, p);
                case "clear_bursts": return ParticleClearBursts(ps);
                default: return Fail("Unknown particle action: " + action);
            }
        }

        private static VfxResult ParticleCreate(VfxArguments a, Props p)
        {
            if (string.IsNullOrEmpty(a.Target)) throw new InvalidOperationException("target is required for particle_create.");
            GameObject go = TryFindTarget(a.Target, a.SearchMethod);
            bool created = go == null;
            if (go == null)
            {
                string name = a.Target.Replace('\\', '/').Split('/').LastOrDefault();
                go = new GameObject(string.IsNullOrEmpty(name) ? "Particle System" : name);
                Undo.RegisterCreatedObjectUndo(go, "MCP create ParticleSystem");
            }
            if (p.Has("position")) go.transform.position = p.Vector3("position", go.transform.position);
            if (p.Has("rotation")) go.transform.eulerAngles = p.Vector3("rotation", go.transform.eulerAngles);
            if (p.Has("scale")) go.transform.localScale = p.Vector3("scale", go.transform.localScale);
            ParticleSystem ps = go.GetComponent<ParticleSystem>();
            bool added = ps == null;
            if (ps == null) ps = Undo.AddComponent<ParticleSystem>(go);
            ParticleSystem.MainModule main = ps.main;
            if (p.Has("playOnAwake")) main.playOnAwake = p.Bool("playOnAwake", true);
            if (p.Has("looping") || p.Has("loop")) main.loop = p.Bool(p.Has("looping") ? "looping" : "loop", true);
            EnsureMaterial(go.GetComponent<ParticleSystemRenderer>(), true);
            EditorUtility.SetDirty(go);
            MarkSceneDirty(go);
            return Ok("ParticleSystem ready.", new VfxData { Target = FullPath(go), InstanceId = go.GetInstanceID(), CreatedGameObject = created, AddedComponent = added, ComponentType = typeof(ParticleSystem).FullName });
        }

        private static VfxResult ParticleInfo(ParticleSystem ps)
        {
            ParticleSystem.MainModule main = ps.main;
            ParticleSystem.EmissionModule emission = ps.emission;
            ParticleSystem.ShapeModule shape = ps.shape;
            ParticleSystemRenderer renderer = ps.GetComponent<ParticleSystemRenderer>();
            return Ok("ParticleSystem information read.", new VfxData
            {
                Target = FullPath(ps.gameObject), InstanceId = ps.GetInstanceID(), ComponentType = typeof(ParticleSystem).FullName,
                IsPlaying = ps.isPlaying, IsPaused = ps.isPaused, ParticleCount = ps.particleCount,
                Particle = new ParticleRecord
                {
                    Duration = main.duration, Looping = main.loop, StartLifetime = main.startLifetime.constant,
                    StartSpeed = main.startSpeed.constant, StartSize = main.startSize.constant,
                    GravityModifier = main.gravityModifier.constant, SimulationSpace = main.simulationSpace.ToString(),
                    MaxParticles = main.maxParticles, EmissionEnabled = emission.enabled,
                    RateOverTime = emission.rateOverTime.constant, BurstCount = emission.burstCount,
                    ShapeEnabled = shape.enabled, ShapeType = shape.shapeType.ToString(), ShapeRadius = shape.radius,
                    ShapeAngle = shape.angle, RenderMode = renderer == null ? string.Empty : renderer.renderMode.ToString(),
                    SortMode = renderer == null ? string.Empty : renderer.sortMode.ToString(),
                    Material = renderer == null || renderer.sharedMaterial == null ? string.Empty : AssetDatabase.GetAssetPath(renderer.sharedMaterial),
                },
            });
        }

        private static VfxResult ParticleSetMain(ParticleSystem ps, Props p)
        {
            Undo.RecordObject(ps, "MCP set ParticleSystem main");
            ParticleSystem.MainModule m = ps.main;
            if (p.Has("duration")) m.duration = Math.Max(.01f, p.Float("duration", m.duration));
            if (p.Has("looping") || p.Has("loop")) m.loop = p.Bool(p.Has("looping") ? "looping" : "loop", m.loop);
            if (p.Has("prewarm")) m.prewarm = p.Bool("prewarm", m.prewarm);
            if (p.Has("startDelay")) m.startDelay = p.Curve("startDelay", m.startDelay);
            if (p.Has("startLifetime")) m.startLifetime = p.Curve("startLifetime", m.startLifetime);
            if (p.Has("startSpeed")) m.startSpeed = p.Curve("startSpeed", m.startSpeed);
            if (p.Has("startSize")) m.startSize = p.Curve("startSize", m.startSize);
            if (p.Has("startRotation")) m.startRotation = p.Curve("startRotation", m.startRotation);
            if (p.Has("startColor")) m.startColor = new ParticleSystem.MinMaxGradient(p.Color("startColor", Color.white));
            if (p.Has("gravityModifier")) m.gravityModifier = p.Curve("gravityModifier", m.gravityModifier);
            if (p.Has("simulationSpace")) m.simulationSpace = p.Enum("simulationSpace", m.simulationSpace);
            if (p.Has("simulationSpeed")) m.simulationSpeed = p.Float("simulationSpeed", m.simulationSpeed);
            if (p.Has("scalingMode")) m.scalingMode = p.Enum("scalingMode", m.scalingMode);
            if (p.Has("playOnAwake")) m.playOnAwake = p.Bool("playOnAwake", m.playOnAwake);
            if (p.Has("maxParticles")) m.maxParticles = Math.Max(1, p.Int("maxParticles", m.maxParticles));
            if (p.Has("stopAction")) m.stopAction = p.Enum("stopAction", m.stopAction);
            if (p.Has("cullingMode")) m.cullingMode = p.Enum("cullingMode", m.cullingMode);
            EditorUtility.SetDirty(ps);
            return Ok("ParticleSystem main module updated.", ParticleInfo(ps).Data);
        }

        private static VfxResult ParticleSetEmission(ParticleSystem ps, Props p)
        {
            Undo.RecordObject(ps, "MCP set ParticleSystem emission");
            ParticleSystem.EmissionModule m = ps.emission;
            if (p.Has("enabled")) m.enabled = p.Bool("enabled", m.enabled);
            if (p.Has("rateOverTime")) m.rateOverTime = p.Curve("rateOverTime", m.rateOverTime);
            if (p.Has("rateOverDistance")) m.rateOverDistance = p.Curve("rateOverDistance", m.rateOverDistance);
            EditorUtility.SetDirty(ps);
            return Ok("ParticleSystem emission module updated.", ParticleInfo(ps).Data);
        }

        private static VfxResult ParticleSetShape(ParticleSystem ps, Props p)
        {
            Undo.RecordObject(ps, "MCP set ParticleSystem shape");
            ParticleSystem.ShapeModule m = ps.shape;
            if (p.Has("enabled")) m.enabled = p.Bool("enabled", m.enabled);
            if (p.Has("shapeType")) m.shapeType = p.Enum("shapeType", m.shapeType);
            if (p.Has("radius")) m.radius = p.Float("radius", m.radius);
            if (p.Has("angle")) m.angle = p.Float("angle", m.angle);
            if (p.Has("arc")) m.arc = p.Float("arc", m.arc);
            if (p.Has("position")) m.position = p.Vector3("position", m.position);
            if (p.Has("rotation")) m.rotation = p.Vector3("rotation", m.rotation);
            if (p.Has("scale")) m.scale = p.Vector3("scale", m.scale);
            if (p.Has("randomDirectionAmount")) m.randomDirectionAmount = p.Float("randomDirectionAmount", m.randomDirectionAmount);
            if (p.Has("sphericalDirectionAmount")) m.sphericalDirectionAmount = p.Float("sphericalDirectionAmount", m.sphericalDirectionAmount);
            EditorUtility.SetDirty(ps);
            return Ok("ParticleSystem shape module updated.", ParticleInfo(ps).Data);
        }

        private static VfxResult ParticleSetColor(ParticleSystem ps, Props p)
        {
            Undo.RecordObject(ps, "MCP set ParticleSystem color over lifetime");
            ParticleSystem.ColorOverLifetimeModule m = ps.colorOverLifetime;
            m.enabled = p.Bool("enabled", true);
            VfxValue value = p.Get("gradient") ?? p.Get("color") ?? p.Get("startColor");
            if (value != null) m.color = new ParticleSystem.MinMaxGradient(ParseGradient(value));
            EditorUtility.SetDirty(ps);
            return Ok("ParticleSystem color-over-lifetime module updated.", ParticleInfo(ps).Data);
        }

        private static VfxResult ParticleSetSize(ParticleSystem ps, Props p)
        {
            Undo.RecordObject(ps, "MCP set ParticleSystem size over lifetime");
            ParticleSystem.SizeOverLifetimeModule m = ps.sizeOverLifetime;
            m.enabled = p.Bool("enabled", true);
            if (p.Has("size")) m.size = p.Curve("size", m.size);
            if (p.Has("sizeMultiplier")) m.sizeMultiplier = p.Float("sizeMultiplier", m.sizeMultiplier);
            EditorUtility.SetDirty(ps);
            return Ok("ParticleSystem size-over-lifetime module updated.", ParticleInfo(ps).Data);
        }

        private static VfxResult ParticleSetVelocity(ParticleSystem ps, Props p)
        {
            Undo.RecordObject(ps, "MCP set ParticleSystem velocity over lifetime");
            ParticleSystem.VelocityOverLifetimeModule m = ps.velocityOverLifetime;
            m.enabled = p.Bool("enabled", true);
            if (p.Has("x")) m.x = p.Curve("x", m.x);
            if (p.Has("y")) m.y = p.Curve("y", m.y);
            if (p.Has("z")) m.z = p.Curve("z", m.z);
            if (p.Has("speedModifier")) m.speedModifier = p.Curve("speedModifier", m.speedModifier);
            if (p.Has("space")) m.space = p.Enum("space", m.space);
            EditorUtility.SetDirty(ps);
            return Ok("ParticleSystem velocity-over-lifetime module updated.", ParticleInfo(ps).Data);
        }

        private static VfxResult ParticleSetNoise(ParticleSystem ps, Props p)
        {
            Undo.RecordObject(ps, "MCP set ParticleSystem noise");
            ParticleSystem.NoiseModule m = ps.noise;
            m.enabled = p.Bool("enabled", true);
            if (p.Has("strength")) m.strength = p.Curve("strength", m.strength);
            if (p.Has("frequency")) m.frequency = p.Float("frequency", m.frequency);
            if (p.Has("scrollSpeed")) m.scrollSpeed = p.Curve("scrollSpeed", m.scrollSpeed);
            if (p.Has("damping")) m.damping = p.Bool("damping", m.damping);
            if (p.Has("octaveCount")) m.octaveCount = Mathf.Clamp(p.Int("octaveCount", m.octaveCount), 1, 4);
            if (p.Has("quality")) m.quality = p.Enum("quality", m.quality);
            EditorUtility.SetDirty(ps);
            return Ok("ParticleSystem noise module updated.", ParticleInfo(ps).Data);
        }

        private static VfxResult ParticleSetRenderer(ParticleSystem ps, Props p)
        {
            ParticleSystemRenderer r = ps.GetComponent<ParticleSystemRenderer>();
            if (r == null) throw new InvalidOperationException("ParticleSystemRenderer is missing.");
            Undo.RecordObject(r, "MCP configure ParticleSystemRenderer");
            if (p.Has("renderMode")) r.renderMode = p.Enum("renderMode", r.renderMode);
            if (p.Has("sortMode")) r.sortMode = p.Enum("sortMode", r.sortMode);
            if (p.Has("minParticleSize")) r.minParticleSize = p.Float("minParticleSize", r.minParticleSize);
            if (p.Has("maxParticleSize")) r.maxParticleSize = p.Float("maxParticleSize", r.maxParticleSize);
            ApplyRenderer(r, p);
            EnsureMaterial(r, true);
            EditorUtility.SetDirty(r);
            return Ok("ParticleSystemRenderer updated.", ParticleInfo(ps).Data);
        }

        private static VfxResult ParticleEnableModule(ParticleSystem ps, Props p)
        {
            string module = p.String("module", string.Empty).Replace("_", string.Empty).ToLowerInvariant();
            bool enabled = p.Bool("enabled", true);
            Undo.RecordObject(ps, "MCP toggle ParticleSystem module");
            switch (module)
            {
                case "emission": { var x = ps.emission; x.enabled = enabled; break; }
                case "shape": { var x = ps.shape; x.enabled = enabled; break; }
                case "coloroverlifetime": { var x = ps.colorOverLifetime; x.enabled = enabled; break; }
                case "sizeoverlifetime": { var x = ps.sizeOverLifetime; x.enabled = enabled; break; }
                case "velocityoverlifetime": { var x = ps.velocityOverLifetime; x.enabled = enabled; break; }
                case "noise": { var x = ps.noise; x.enabled = enabled; break; }
                case "collision": { var x = ps.collision; x.enabled = enabled; break; }
                case "trails": { var x = ps.trails; x.enabled = enabled; break; }
                case "lights": { var x = ps.lights; x.enabled = enabled; break; }
                default: throw new InvalidOperationException("Unknown ParticleSystem module: " + module);
            }
            EditorUtility.SetDirty(ps);
            return Ok("ParticleSystem module state updated.", ParticleInfo(ps).Data);
        }

        private static VfxResult ParticleControl(ParticleSystem ps, Props p, string action)
        {
            bool children = p.Bool("withChildren", true);
            if (action == "play") ps.Play(children);
            else if (action == "stop") ps.Stop(children, ParticleSystemStopBehavior.StopEmitting);
            else if (action == "pause") ps.Pause(children);
            else if (action == "restart") { ps.Stop(children, ParticleSystemStopBehavior.StopEmittingAndClear); ps.Play(children); }
            else if (action == "clear") ps.Clear(children);
            return Ok("ParticleSystem " + action + ".", ParticleInfo(ps).Data);
        }

        private static VfxResult ParticleAddBurst(ParticleSystem ps, Props p)
        {
            Undo.RecordObject(ps, "MCP add ParticleSystem burst");
            ParticleSystem.EmissionModule emission = ps.emission;
            short min = (short)Mathf.Clamp(p.Int("minCount", p.Int("count", 30)), 0, short.MaxValue);
            short max = (short)Mathf.Clamp(p.Int("maxCount", p.Int("count", 30)), 0, short.MaxValue);
            ParticleSystem.Burst burst = new ParticleSystem.Burst(p.Float("time", 0f), min, max, Math.Max(1, p.Int("cycles", 1)), Math.Max(.01f, p.Float("interval", .01f)));
            burst.probability = Mathf.Clamp01(p.Float("probability", 1f));
            ParticleSystem.Burst[] values = new ParticleSystem.Burst[emission.burstCount + 1];
            emission.GetBursts(values); values[values.Length - 1] = burst; emission.SetBursts(values);
            EditorUtility.SetDirty(ps);
            VfxData data = ParticleInfo(ps).Data; data.Index = values.Length - 1;
            return Ok("ParticleSystem burst added.", data);
        }

        private static VfxResult ParticleClearBursts(ParticleSystem ps)
        {
            Undo.RecordObject(ps, "MCP clear ParticleSystem bursts");
            int count = ps.emission.burstCount;
            ParticleSystem.EmissionModule emission = ps.emission; emission.SetBursts(new ParticleSystem.Burst[0]);
            EditorUtility.SetDirty(ps);
            VfxData data = ParticleInfo(ps).Data; data.Count = count;
            return Ok("ParticleSystem bursts cleared.", data);
        }

        private static VfxResult LineAction(string action, VfxArguments a, Props p)
        {
            LineRenderer line = FindComponent<LineRenderer>(a);
            switch (action)
            {
                case "get_info": return LineInfo(line);
                case "set_positions": return LineSetPositions(line, p);
                case "add_position": return LineAddPosition(line, p);
                case "set_position": return LineSetPosition(line, p);
                case "set_width": return LineSetWidth(line, p);
                case "set_color": return LineSetColor(line, p);
                case "set_material": return LineSetMaterial(line, p);
                case "set_properties": return LineSetProperties(line, p);
                case "clear": return LineClear(line);
                case "create_line": return LineCreateLine(line, p);
                case "create_circle": return LineCreateCircle(line, p);
                case "create_arc": return LineCreateArc(line, p);
                case "create_bezier": return LineCreateBezier(line, p);
                default: return Fail("Unknown line action: " + action);
            }
        }

        private static VfxResult LineInfo(LineRenderer line)
        {
            Vector3[] positions = new Vector3[line.positionCount]; line.GetPositions(positions);
            return Ok("LineRenderer information read.", new VfxData
            {
                Target = FullPath(line.gameObject), InstanceId = line.GetInstanceID(), ComponentType = typeof(LineRenderer).FullName,
                Positions = positions.Select(Vector).ToArray(), Count = line.positionCount,
                Renderer = ToRendererRecord(line, line.startWidth, line.endWidth, line.loop, line.useWorldSpace),
            });
        }

        private static VfxResult LineSetPositions(LineRenderer line, Props p)
        {
            Vector3[] positions = p.Vectors("positions");
            if (positions.Length == 0) throw new InvalidOperationException("positions is required.");
            Undo.RecordObject(line, "MCP set LineRenderer positions");
            line.positionCount = positions.Length; line.SetPositions(positions); EditorUtility.SetDirty(line);
            return Ok("LineRenderer positions updated.", LineInfo(line).Data);
        }

        private static VfxResult LineAddPosition(LineRenderer line, Props p)
        {
            Undo.RecordObject(line, "MCP add LineRenderer position");
            int index = line.positionCount; line.positionCount = index + 1; line.SetPosition(index, p.Vector3("position", Vector3.zero)); EditorUtility.SetDirty(line);
            return Ok("LineRenderer position added.", LineInfo(line).Data);
        }

        private static VfxResult LineSetPosition(LineRenderer line, Props p)
        {
            int index = p.Int("index", -1); if (index < 0 || index >= line.positionCount) throw new InvalidOperationException("index is out of range.");
            Undo.RecordObject(line, "MCP set LineRenderer position"); line.SetPosition(index, p.Vector3("position", line.GetPosition(index))); EditorUtility.SetDirty(line);
            return Ok("LineRenderer position updated.", LineInfo(line).Data);
        }

        private static VfxResult LineSetWidth(LineRenderer line, Props p)
        {
            Undo.RecordObject(line, "MCP set LineRenderer width"); ApplyWidth(line, p); EditorUtility.SetDirty(line); return Ok("LineRenderer width updated.", LineInfo(line).Data);
        }

        private static VfxResult LineSetColor(LineRenderer line, Props p)
        {
            Undo.RecordObject(line, "MCP set LineRenderer color"); ApplyColor(line, p, false); EditorUtility.SetDirty(line); return Ok("LineRenderer color updated.", LineInfo(line).Data);
        }

        private static VfxResult LineSetMaterial(LineRenderer line, Props p)
        {
            Undo.RecordObject(line, "MCP set LineRenderer material"); AssignMaterial(line, p.String("materialPath", string.Empty)); EditorUtility.SetDirty(line); return Ok("LineRenderer material updated.", LineInfo(line).Data);
        }

        private static VfxResult LineSetProperties(LineRenderer line, Props p)
        {
            Undo.RecordObject(line, "MCP configure LineRenderer");
            if (p.Has("positions")) { Vector3[] values = p.Vectors("positions"); line.positionCount = values.Length; line.SetPositions(values); }
            else if (p.Has("positionCount")) line.positionCount = Math.Max(0, p.Int("positionCount", line.positionCount));
            if (p.Has("loop")) line.loop = p.Bool("loop", line.loop);
            if (p.Has("useWorldSpace")) line.useWorldSpace = p.Bool("useWorldSpace", line.useWorldSpace);
            if (p.Has("alignment")) line.alignment = p.Enum("alignment", line.alignment);
            if (p.Has("textureMode")) line.textureMode = p.Enum("textureMode", line.textureMode);
            if (p.Has("numCornerVertices")) line.numCornerVertices = Math.Max(0, p.Int("numCornerVertices", line.numCornerVertices));
            if (p.Has("numCapVertices")) line.numCapVertices = Math.Max(0, p.Int("numCapVertices", line.numCapVertices));
            if (p.Has("generateLightingData")) line.generateLightingData = p.Bool("generateLightingData", line.generateLightingData);
            ApplyWidth(line, p); ApplyColor(line, p, false); ApplyRenderer(line, p); EnsureMaterial(line, false); EditorUtility.SetDirty(line);
            return Ok("LineRenderer properties updated.", LineInfo(line).Data);
        }

        private static VfxResult LineClear(LineRenderer line)
        {
            int count = line.positionCount; Undo.RecordObject(line, "MCP clear LineRenderer"); line.positionCount = 0; EditorUtility.SetDirty(line);
            VfxData data = LineInfo(line).Data; data.Count = count; return Ok("LineRenderer positions cleared.", data);
        }

        private static VfxResult LineCreateLine(LineRenderer line, Props p)
        {
            Undo.RecordObject(line, "MCP create line"); line.positionCount = 2; line.loop = false;
            line.SetPosition(0, p.Vector3("start", Vector3.zero)); line.SetPosition(1, p.Vector3("end", Vector3.right)); FinishLineShape(line, p);
            return Ok("Line created.", LineInfo(line).Data);
        }

        private static VfxResult LineCreateCircle(LineRenderer line, Props p)
        {
            Vector3 center = p.Vector3("center", Vector3.zero); float radius = p.Float("radius", 1f); int segments = Math.Max(3, p.Int("segments", 32)); Vector3 normal = p.Vector3("normal", Vector3.up).normalized;
            Basis(normal, out Vector3 right, out Vector3 forward); Undo.RecordObject(line, "MCP create circle"); line.positionCount = segments; line.loop = true;
            for (int i = 0; i < segments; i++) { float angle = i / (float)segments * Mathf.PI * 2f; line.SetPosition(i, center + (right * Mathf.Cos(angle) + forward * Mathf.Sin(angle)) * radius); }
            FinishLineShape(line, p); return Ok("Circle created.", LineInfo(line).Data);
        }

        private static VfxResult LineCreateArc(LineRenderer line, Props p)
        {
            Vector3 center = p.Vector3("center", Vector3.zero); float radius = p.Float("radius", 1f); int segments = Math.Max(1, p.Int("segments", 16)); Vector3 normal = p.Vector3("normal", Vector3.up).normalized;
            float start = p.Float("startAngle", 0f) * Mathf.Deg2Rad; float end = p.Float("endAngle", 180f) * Mathf.Deg2Rad; Basis(normal, out Vector3 right, out Vector3 forward);
            Undo.RecordObject(line, "MCP create arc"); line.positionCount = segments + 1; line.loop = false;
            for (int i = 0; i <= segments; i++) { float angle = Mathf.Lerp(start, end, i / (float)segments); line.SetPosition(i, center + (right * Mathf.Cos(angle) + forward * Mathf.Sin(angle)) * radius); }
            FinishLineShape(line, p); return Ok("Arc created.", LineInfo(line).Data);
        }

        private static VfxResult LineCreateBezier(LineRenderer line, Props p)
        {
            Vector3 start = p.Vector3("start", Vector3.zero), end = p.Vector3("end", Vector3.right), c1 = p.Vector3(p.Has("controlPoint1") ? "controlPoint1" : "control1", Vector3.up);
            bool cubic = p.Has("controlPoint2") || p.Has("control2"); Vector3 c2 = p.Vector3(p.Has("controlPoint2") ? "controlPoint2" : "control2", c1); int segments = Math.Max(1, p.Int("segments", 32));
            Undo.RecordObject(line, "MCP create bezier"); line.positionCount = segments + 1; line.loop = false;
            for (int i = 0; i <= segments; i++) { float t = i / (float)segments, u = 1f - t; Vector3 point = cubic ? u*u*u*start + 3*u*u*t*c1 + 3*u*t*t*c2 + t*t*t*end : u*u*start + 2*u*t*c1 + t*t*end; line.SetPosition(i, point); }
            FinishLineShape(line, p); return Ok("Bezier curve created.", LineInfo(line).Data);
        }

        private static void FinishLineShape(LineRenderer line, Props p) { ApplyWidth(line, p); ApplyColor(line, p, false); EnsureMaterial(line, false); EditorUtility.SetDirty(line); }
        private static void Basis(Vector3 normal, out Vector3 right, out Vector3 forward) { right = Vector3.Cross(normal, Vector3.forward); if (right.sqrMagnitude < .001f) right = Vector3.Cross(normal, Vector3.up); right.Normalize(); forward = Vector3.Cross(right, normal).normalized; }

        private static VfxResult TrailAction(string action, VfxArguments a, Props p)
        {
            TrailRenderer trail = FindComponent<TrailRenderer>(a);
            switch (action)
            {
                case "get_info": return TrailInfo(trail);
                case "set_time": Undo.RecordObject(trail, "MCP set trail time"); trail.time = Math.Max(0f, p.Float("time", 5f)); break;
                case "set_width": Undo.RecordObject(trail, "MCP set trail width"); ApplyWidth(trail, p); break;
                case "set_color": Undo.RecordObject(trail, "MCP set trail color"); ApplyColor(trail, p, true); break;
                case "set_material": Undo.RecordObject(trail, "MCP set trail material"); AssignMaterial(trail, p.String("materialPath", string.Empty)); break;
                case "set_properties": TrailSetProperties(trail, p); break;
                case "clear": trail.Clear(); break;
                case "emit": return TrailEmit(trail, p);
                default: return Fail("Unknown trail action: " + action);
            }
            EnsureMaterial(trail, false); EditorUtility.SetDirty(trail); return Ok("TrailRenderer " + action + " completed.", TrailInfo(trail).Data);
        }

        private static VfxResult TrailInfo(TrailRenderer trail)
        {
            return Ok("TrailRenderer information read.", new VfxData
            {
                Target = FullPath(trail.gameObject), InstanceId = trail.GetInstanceID(), ComponentType = typeof(TrailRenderer).FullName, Count = trail.positionCount,
                Renderer = ToRendererRecord(trail, trail.startWidth, trail.endWidth, false, false),
                Trail = new TrailRecord { Time = trail.time, MinVertexDistance = trail.minVertexDistance, Emitting = trail.emitting, Autodestruct = trail.autodestruct },
            });
        }

        private static void TrailSetProperties(TrailRenderer trail, Props p)
        {
            Undo.RecordObject(trail, "MCP configure TrailRenderer");
            if (p.Has("time")) trail.time = Math.Max(0f, p.Float("time", trail.time));
            if (p.Has("minVertexDistance")) trail.minVertexDistance = Math.Max(0f, p.Float("minVertexDistance", trail.minVertexDistance));
            if (p.Has("autodestruct")) trail.autodestruct = p.Bool("autodestruct", trail.autodestruct);
            if (p.Has("emitting")) trail.emitting = p.Bool("emitting", trail.emitting);
            if (p.Has("alignment")) trail.alignment = p.Enum("alignment", trail.alignment);
            if (p.Has("textureMode")) trail.textureMode = p.Enum("textureMode", trail.textureMode);
            if (p.Has("numCornerVertices")) trail.numCornerVertices = Math.Max(0, p.Int("numCornerVertices", trail.numCornerVertices));
            if (p.Has("numCapVertices")) trail.numCapVertices = Math.Max(0, p.Int("numCapVertices", trail.numCapVertices));
            if (p.Has("generateLightingData")) trail.generateLightingData = p.Bool("generateLightingData", trail.generateLightingData);
            ApplyWidth(trail, p); ApplyColor(trail, p, true); ApplyRenderer(trail, p);
        }

        private static VfxResult TrailEmit(TrailRenderer trail, Props p)
        {
            MethodInfo method = typeof(TrailRenderer).GetMethod("AddPosition", AnyMember, null, new[] { typeof(Vector3) }, null);
            if (method == null) throw new InvalidOperationException("TrailRenderer.AddPosition requires Unity 2021.1 or newer.");
            Vector3 point = p.Vector3("position", trail.transform.position); method.Invoke(trail, new object[] { point });
            return Ok("Trail point emitted.", TrailInfo(trail).Data);
        }

        private static void ApplyWidth(LineRenderer line, Props p)
        {
            if (p.Has("width")) line.startWidth = line.endWidth = p.Float("width", line.startWidth);
            if (p.Has("startWidth")) line.startWidth = p.Float("startWidth", line.startWidth);
            if (p.Has("endWidth")) line.endWidth = p.Float("endWidth", line.endWidth);
            if (p.Has("widthMultiplier")) line.widthMultiplier = p.Float("widthMultiplier", line.widthMultiplier);
            if (p.Has("widthCurve")) line.widthCurve = p.AnimationCurve("widthCurve", line.widthCurve);
        }

        private static void ApplyWidth(TrailRenderer trail, Props p)
        {
            if (p.Has("width")) trail.startWidth = trail.endWidth = p.Float("width", trail.startWidth);
            if (p.Has("startWidth")) trail.startWidth = p.Float("startWidth", trail.startWidth);
            if (p.Has("endWidth")) trail.endWidth = p.Float("endWidth", trail.endWidth);
            if (p.Has("widthMultiplier")) trail.widthMultiplier = p.Float("widthMultiplier", trail.widthMultiplier);
            if (p.Has("widthCurve")) trail.widthCurve = p.AnimationCurve("widthCurve", trail.widthCurve);
        }

        private static void ApplyColor(LineRenderer line, Props p, bool fade) { if (p.Has("color")) line.startColor = line.endColor = p.Color("color", Color.white); if (p.Has("startColor")) line.startColor = p.Color("startColor", line.startColor); if (p.Has("endColor")) line.endColor = p.Color("endColor", line.endColor); if (p.Has("gradient")) line.colorGradient = ParseGradient(p.Get("gradient")); }
        private static void ApplyColor(TrailRenderer trail, Props p, bool fade) { if (p.Has("color")) { Color c = p.Color("color", Color.white); trail.startColor = c; trail.endColor = fade ? new Color(c.r,c.g,c.b,0f) : c; } if (p.Has("startColor")) trail.startColor = p.Color("startColor", trail.startColor); if (p.Has("endColor")) trail.endColor = p.Color("endColor", trail.endColor); if (p.Has("gradient")) trail.colorGradient = ParseGradient(p.Get("gradient")); }

        private static void ApplyRenderer(Renderer renderer, Props p)
        {
            if (p.Has("materialPath")) AssignMaterial(renderer, p.String("materialPath", string.Empty));
            if (p.Has("sortingOrder")) renderer.sortingOrder = p.Int("sortingOrder", renderer.sortingOrder);
            if (p.Has("sortingLayerName")) renderer.sortingLayerName = p.String("sortingLayerName", renderer.sortingLayerName);
            if (p.Has("shadowCastingMode")) renderer.shadowCastingMode = p.Enum("shadowCastingMode", renderer.shadowCastingMode);
            if (p.Has("receiveShadows")) renderer.receiveShadows = p.Bool("receiveShadows", renderer.receiveShadows);
            if (p.Has("lightProbeUsage")) renderer.lightProbeUsage = p.Enum("lightProbeUsage", renderer.lightProbeUsage);
            if (p.Has("reflectionProbeUsage")) renderer.reflectionProbeUsage = p.Enum("reflectionProbeUsage", renderer.reflectionProbeUsage);
        }

        private static void AssignMaterial(Renderer renderer, string path)
        {
            if (string.IsNullOrEmpty(path)) throw new InvalidOperationException("materialPath is required.");
            Material material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null) throw new InvalidOperationException("Material was not found: " + path);
            renderer.sharedMaterial = material;
        }

        private static void EnsureMaterial(Renderer renderer, bool particle)
        {
            if (renderer == null || renderer.sharedMaterial != null) return;
            string resource = particle ? "Default-Particle.mat" : "Default-Line.mat";
            Material material = AssetDatabase.GetBuiltinExtraResource<Material>(resource);
            if (material == null)
            {
                Shader shader = Shader.Find(particle ? "Particles/Standard Unlit" : "Sprites/Default") ?? Shader.Find("Unlit/Color");
                if (shader != null) { material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave }; }
            }
            renderer.sharedMaterial = material;
        }

        private static RendererRecord ToRendererRecord(Renderer renderer, float startWidth, float endWidth, bool loop, bool world)
        {
            LineRenderer line = renderer as LineRenderer; TrailRenderer trail = renderer as TrailRenderer;
            return new RendererRecord
            {
                StartWidth = startWidth, EndWidth = endWidth, Loop = loop, UseWorldSpace = world,
                Alignment = line != null ? line.alignment.ToString() : trail.alignment.ToString(),
                TextureMode = line != null ? line.textureMode.ToString() : trail.textureMode.ToString(),
                NumCornerVertices = line != null ? line.numCornerVertices : trail.numCornerVertices,
                NumCapVertices = line != null ? line.numCapVertices : trail.numCapVertices,
                Material = renderer.sharedMaterial == null ? string.Empty : AssetDatabase.GetAssetPath(renderer.sharedMaterial),
                SortingOrder = renderer.sortingOrder, SortingLayerName = renderer.sortingLayerName,
                ShadowCastingMode = renderer.shadowCastingMode.ToString(), ReceiveShadows = renderer.receiveShadows,
            };
        }

        private static VfxResult VfxGraphAction(string action, VfxArguments a, Props p)
        {
            Type type = FindType("UnityEngine.VFX.VisualEffect");
            Type assetType = FindType("UnityEngine.VFX.VisualEffectAsset");
            if (type == null || assetType == null) throw new InvalidOperationException("VFX Graph package (com.unity.visualeffectgraph) is not installed.");
            if (action == "list_assets" || action == "list_templates")
            {
                string query = action == "list_templates" ? "t:VisualEffectAsset template" : "t:VisualEffectAsset";
                string folder = p.String("folder", "Assets");
                string[] guids = AssetDatabase.FindAssets(query, new[] { folder });
                AssetRecord[] assets = guids.Select(g => AssetDatabase.GUIDToAssetPath(g)).Where(x => !string.IsNullOrEmpty(x)).Select(x => new AssetRecord { Path = x, Name = System.IO.Path.GetFileNameWithoutExtension(x), Guid = AssetDatabase.AssetPathToGUID(x) }).ToArray();
                return Ok("VFX Graph assets listed.", new VfxData { Assets = assets, Count = assets.Length, HasVfxGraph = true });
            }
            if (action == "create_asset")
            {
                string template = p.String("template", string.Empty);
                string folder = p.String("folderPath", "Assets");
                string name = p.String("assetName", "NewVFX");
                string path = folder.TrimEnd('/', '\\') + "/" + (name.EndsWith(".vfx", StringComparison.OrdinalIgnoreCase) ? name : name + ".vfx");
                if (string.IsNullOrEmpty(template)) throw new InvalidOperationException("On Unity 2019, vfx_create_asset requires properties.template pointing to an existing .vfx template asset.");
                if (AssetDatabase.LoadMainAssetAtPath(path) != null && !p.Bool("overwrite", false)) throw new InvalidOperationException("VFX asset already exists: " + path);
                if (AssetDatabase.LoadMainAssetAtPath(path) != null) AssetDatabase.DeleteAsset(path);
                if (!AssetDatabase.CopyAsset(template, path)) throw new InvalidOperationException("Failed to copy VFX template to: " + path);
                AssetDatabase.ImportAsset(path);
                return Ok("VFX Graph asset created from template.", new VfxData { Path = path, HasVfxGraph = true });
            }
            GameObject go = FindTarget(a.Target, a.SearchMethod);
            Component vfx = go.GetComponent(type);
            if (vfx == null && action == "assign_asset") vfx = Undo.AddComponent(go, type);
            if (vfx == null) throw new InvalidOperationException("Target has no VisualEffect component.");
            if (action == "assign_asset")
            {
                string path = p.String("assetPath", string.Empty); UnityEngine.Object asset = AssetDatabase.LoadAssetAtPath(path, assetType);
                if (asset == null) throw new InvalidOperationException("VFX asset was not found: " + path);
                SetMember(vfx, "visualEffectAsset", asset); EditorUtility.SetDirty(vfx);
                return Ok("VFX asset assigned.", VfxInfoData(vfx));
            }
            if (action == "get_info") return Ok("VisualEffect information read.", VfxInfoData(vfx));
            if (action.StartsWith("set_", StringComparison.Ordinal) && action != "set_playback_speed" && action != "set_seed")
            {
                string parameter = p.String("parameter", string.Empty); if (string.IsNullOrEmpty(parameter)) throw new InvalidOperationException("properties.parameter is required.");
                object value;
                string method;
                if (action == "set_float") { method = "SetFloat"; value = p.Float("value", 0f); }
                else if (action == "set_int") { method = "SetInt"; value = p.Int("value", 0); }
                else if (action == "set_bool") { method = "SetBool"; value = p.Bool("value", false); }
                else if (action == "set_vector2") { method = "SetVector2"; Vector3 v=p.Vector3("value",Vector3.zero); value=new Vector2(v.x,v.y); }
                else if (action == "set_vector3") { method = "SetVector3"; value = p.Vector3("value", Vector3.zero); }
                else if (action == "set_vector4") { method = "SetVector4"; float[] v=p.Numbers("value"); value=new Vector4(Value(v,0),Value(v,1),Value(v,2),Value(v,3)); }
                else if (action == "set_color") { method = "SetVector4"; Color c=p.Color("color",p.Color("value",Color.white)); value=new Vector4(c.r,c.g,c.b,c.a); }
                else if (action == "set_gradient") { method = "SetGradient"; value=ParseGradient(p.Get("gradient") ?? p.Get("value")); }
                else if (action == "set_texture") { method = "SetTexture"; value=AssetDatabase.LoadAssetAtPath<Texture>(p.String("texturePath",string.Empty)); }
                else if (action == "set_mesh") { method = "SetMesh"; value=AssetDatabase.LoadAssetAtPath<Mesh>(p.String("meshPath",string.Empty)); }
                else if (action == "set_curve") { method = "SetAnimationCurve"; value=p.AnimationCurve("curve",AnimationCurve.Linear(0,0,1,1)); }
                else throw new InvalidOperationException("Unsupported VFX parameter action: " + action);
                InvokeCompatible(vfx, method, parameter, value); return Ok("VisualEffect parameter updated.", VfxInfoData(vfx));
            }
            if (action == "send_event") { InvokeCompatible(vfx, "SendEvent", p.String("eventName", "OnPlay")); return Ok("VisualEffect event sent.", VfxInfoData(vfx)); }
            if (action == "play" || action == "stop" || action == "reinit") { InvokeCompatible(vfx, action == "reinit" ? "Reinit" : char.ToUpperInvariant(action[0]) + action.Substring(1)); return Ok("VisualEffect " + action + ".", VfxInfoData(vfx)); }
            if (action == "pause") { SetMember(vfx, "pause", true); return Ok("VisualEffect paused.", VfxInfoData(vfx)); }
            if (action == "set_playback_speed") { SetMember(vfx, "playRate", p.Float("playRate", p.Float("value", 1f))); return Ok("VisualEffect playback speed updated.", VfxInfoData(vfx)); }
            if (action == "set_seed") { if (p.Has("seed")) SetMember(vfx,"startSeed",(uint)Math.Max(0,p.Int("seed",0))); if(p.Has("resetSeedOnPlay")) SetMember(vfx,"resetSeedOnPlay",p.Bool("resetSeedOnPlay",true)); return Ok("VisualEffect seed updated.", VfxInfoData(vfx)); }
            throw new InvalidOperationException("Unknown VFX Graph action: " + action);
        }

        private static VfxData VfxInfoData(Component vfx)
        {
            UnityEngine.Object asset = GetMember(vfx, "visualEffectAsset") as UnityEngine.Object;
            return new VfxData
            {
                Target = FullPath(vfx.gameObject), InstanceId = vfx.GetInstanceID(), ComponentType = vfx.GetType().FullName,
                Path = asset == null ? string.Empty : AssetDatabase.GetAssetPath(asset), HasVfxGraph = true,
                IsPaused = Convert.ToBoolean(GetMember(vfx, "pause") ?? false),
                PlaybackSpeed = Convert.ToSingle(GetMember(vfx, "playRate") ?? 1f),
            };
        }

        private static void InvokeCompatible(object target, string name, params object[] args)
        {
            MethodInfo method = target.GetType().GetMethods(AnyMember).FirstOrDefault(m => m.Name == name && m.GetParameters().Length == args.Length && ParametersCompatible(m.GetParameters(), args));
            if (method == null) throw new InvalidOperationException(target.GetType().Name + "." + name + " is unavailable in this package version.");
            method.Invoke(target, args);
        }

        private static bool ParametersCompatible(ParameterInfo[] parameters, object[] args)
        {
            for (int i=0;i<parameters.Length;i++) if(args[i]!=null && !parameters[i].ParameterType.IsInstanceOfType(args[i])) return false; return true;
        }

        private static Gradient ParseGradient(VfxValue value)
        {
            if (value == null || value.Kind != "object")
            {
                Color color = Color.white;
                if (value != null && value.Numbers != null) color = ColorFrom(value.Numbers, Color.white);
                Gradient simple = new Gradient(); simple.SetKeys(new[] { new GradientColorKey(color, 0f), new GradientColorKey(color, 1f) }, new[] { new GradientAlphaKey(color.a, 0f), new GradientAlphaKey(color.a, 1f) }); return simple;
            }
            Props nested = new Props(value.Children); VfxValue colorKeys = nested.Get("colorKeys"), alphaKeys = nested.Get("alphaKeys");
            List<GradientColorKey> colors = new List<GradientColorKey>();
            if (colorKeys != null && colorKeys.Items != null) foreach (VfxValue item in colorKeys.Items) { Props x=new Props(item.Children); colors.Add(new GradientColorKey(x.Color("color",Color.white),x.Float("time",0f))); }
            List<GradientAlphaKey> alphas = new List<GradientAlphaKey>();
            if (alphaKeys != null && alphaKeys.Items != null) foreach (VfxValue item in alphaKeys.Items) { Props x=new Props(item.Children); alphas.Add(new GradientAlphaKey(x.Float("alpha",1f),x.Float("time",0f))); }
            if (colors.Count == 0) { colors.Add(new GradientColorKey(Color.white,0)); colors.Add(new GradientColorKey(Color.white,1)); }
            if (alphas.Count == 0) { alphas.Add(new GradientAlphaKey(1,0)); alphas.Add(new GradientAlphaKey(1,1)); }
            Gradient gradient = new Gradient(); gradient.SetKeys(colors.ToArray(), alphas.ToArray()); return gradient;
        }

        private static AnimationCurve ParseCurve(VfxValue value, AnimationCurve fallback)
        {
            if (value == null) return fallback;
            if (value.Kind == "number" || value.Kind == "int") return AnimationCurve.Constant(0, 1, (float)value.NumberValue);
            Props nested = new Props(value.Children); VfxValue keys = nested.Get("keys");
            if (keys == null && value.Kind == "array") keys = value;
            List<Keyframe> frames = new List<Keyframe>();
            if (keys != null && keys.Items != null) foreach (VfxValue item in keys.Items) { Props x=new Props(item.Children); frames.Add(new Keyframe(x.Float("time",0),x.Float("value",0),x.Float("inTangent",0),x.Float("outTangent",0))); }
            return frames.Count == 0 ? fallback : new AnimationCurve(frames.ToArray());
        }

        private static Color ColorFrom(float[] values, Color fallback) { return values == null || values.Length < 3 ? fallback : new Color(values[0],values[1],values[2],values.Length>3?values[3]:1f); }
        private static float Value(float[] values,int index) { return values != null && values.Length > index ? values[index] : 0f; }

        private static T FindComponent<T>(VfxArguments a) where T : Component
        {
            GameObject go = FindTarget(a.Target, a.SearchMethod); T[] values = go.GetComponents<T>(); int index = a.HasComponentIndex ? a.ComponentIndex : 0;
            if (index < 0 || index >= values.Length) throw new InvalidOperationException(go.name + " has no " + typeof(T).Name + " at component_index " + index + "."); return values[index];
        }

        private static GameObject FindTarget(string target,string searchMethod)
        {
            GameObject go=TryFindTarget(target,searchMethod); if(go==null) throw new InvalidOperationException("GameObject was not found: "+target); return go;
        }

        private static GameObject TryFindTarget(string target,string searchMethod)
        {
            if(string.IsNullOrEmpty(target)) return null; string method=(searchMethod??"by_name").ToLowerInvariant();
            if(method=="by_id" || int.TryParse(target,out _)) { if(int.TryParse(target,out int id)) { GameObject found=EditorUtility.InstanceIDToObject(id) as GameObject; if(found!=null)return found; Component c=EditorUtility.InstanceIDToObject(id) as Component;if(c!=null)return c.gameObject; } }
            if(method=="by_tag") { try{return GameObject.FindWithTag(target);}catch{return null;} }
            GameObject direct=GameObject.Find(target); if(direct!=null)return direct; string normalized=target.Trim('/');
            foreach(GameObject go in Resources.FindObjectsOfTypeAll<GameObject>()) if(go.scene.IsValid() && (string.Equals(go.name,target,StringComparison.Ordinal) || string.Equals(FullPath(go),normalized,StringComparison.Ordinal))) return go; return null;
        }

        private static string FullPath(GameObject go) { string path=go.name; for(Transform p=go.transform.parent;p!=null;p=p.parent) path=p.name+"/"+path; return path; }
        private static VectorRecord Vector(Vector3 v) { return new VectorRecord { Values=new[]{v.x,v.y,v.z} }; }
        private static void MarkSceneDirty(GameObject go) { if(go.scene.IsValid()) UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(go.scene); }

        private static Type FindType(string fullName) { foreach(Assembly assembly in AppDomain.CurrentDomain.GetAssemblies()) { Type type=assembly.GetType(fullName,false);if(type!=null)return type;}return null; }
        private static object GetMember(object target,string name) { if(target==null)return null; FieldInfo f=target.GetType().GetField(name,AnyMember);if(f!=null)return f.GetValue(target);PropertyInfo p=target.GetType().GetProperty(name,AnyMember);return p==null?null:p.GetValue(target,null); }
        private static void SetMember(object target,string name,object value) { FieldInfo f=target.GetType().GetField(name,AnyMember);if(f!=null){f.SetValue(target,ConvertValue(value,f.FieldType));return;}PropertyInfo p=target.GetType().GetProperty(name,AnyMember);if(p!=null&&p.CanWrite){p.SetValue(target,ConvertValue(value,p.PropertyType),null);return;}throw new InvalidOperationException("Member unavailable: "+target.GetType().FullName+"."+name); }
        private static object ConvertValue(object value,Type type) { if(value==null||type.IsInstanceOfType(value))return value;if(type.IsEnum)return Enum.Parse(type,Convert.ToString(value),true);if(type==typeof(uint))return Convert.ToUInt32(value);return Convert.ChangeType(value,type); }
        private static VfxResult Ok(string message,VfxData data) { return new VfxResult{Success=true,Message=message,Data=data??new VfxData()}; }
        private static VfxResult Fail(string message) { return new VfxResult{Success=false,Message=message,Data=new VfxData()}; }

        private sealed class Props
        {
            private readonly Dictionary<string,VfxValue> values;
            internal Props(VfxValue[] source) { values=new Dictionary<string,VfxValue>(StringComparer.OrdinalIgnoreCase);foreach(VfxValue value in source??new VfxValue[0])if(value!=null&&!string.IsNullOrEmpty(value.Name))values[value.Name]=value; }
            internal bool Has(string name){return values.ContainsKey(name);} internal VfxValue Get(string name){values.TryGetValue(name,out VfxValue value);return value;}
            internal string String(string name,string fallback){VfxValue v=Get(name);return v==null?fallback:(v.StringValue??Convert.ToString(v.NumberValue));}
            internal bool Bool(string name,bool fallback){VfxValue v=Get(name);return v==null?fallback:v.Kind=="bool"?v.BoolValue:Convert.ToBoolean(v.IntValue);}
            internal int Int(string name,int fallback){VfxValue v=Get(name);return v==null?fallback:v.Kind=="int"?v.IntValue:(int)v.NumberValue;}
            internal float Float(string name,float fallback){VfxValue v=Get(name);return v==null?fallback:(float)(v.Kind=="int"?v.IntValue:v.NumberValue);}
            internal float[] Numbers(string name){VfxValue v=Get(name);return v==null?new float[0]:(v.Numbers??new float[0]);}
            internal Vector3 Vector3(string name,Vector3 fallback){float[] n=Numbers(name);return n.Length<2?fallback:new Vector3(n[0],n[1],n.Length>2?n[2]:0);}
            internal Vector3[] Vectors(string name){VfxValue v=Get(name);if(v==null)return new Vector3[0];if(v.Items!=null)return v.Items.Where(x=>x!=null&&x.Numbers!=null).Select(x=>new Vector3(Value(x.Numbers,0),Value(x.Numbers,1),Value(x.Numbers,2))).ToArray();return new Vector3[0];}
            internal Color Color(string name,Color fallback){VfxValue v=Get(name);return v==null?fallback:ColorFrom(v.Numbers,fallback);}
            internal T Enum<T>(string name,T fallback) where T:struct { VfxValue v=Get(name);if(v==null)return fallback; if(System.Enum.TryParse(v.StringValue,true,out T parsed))return parsed;return fallback; }
            internal ParticleSystem.MinMaxCurve Curve(string name,ParticleSystem.MinMaxCurve fallback){VfxValue v=Get(name);if(v==null)return fallback;if(v.Kind=="number"||v.Kind=="int")return new ParticleSystem.MinMaxCurve((float)(v.Kind=="int"?v.IntValue:v.NumberValue));Props x=new Props(v.Children);string mode=x.String("mode","").ToLowerInvariant();if(mode=="two_constants")return new ParticleSystem.MinMaxCurve(x.Float("min",0),x.Float("max",1));UnityEngine.AnimationCurve curve=ParseCurve(v,fallback.curve??UnityEngine.AnimationCurve.Linear(0,0,1,1));float multiplier=x.Float("multiplier",1);return new ParticleSystem.MinMaxCurve(multiplier,curve);}
            internal AnimationCurve AnimationCurve(string name,AnimationCurve fallback){return ParseCurve(Get(name),fallback);}
        }

        [Serializable] private sealed class VfxArguments { public string Action,Target,SearchMethod;public int ComponentIndex;public bool HasComponentIndex;public VfxValue[] Properties; }
        [Serializable] private sealed class VfxValue { public string Name,Kind,StringValue;public bool BoolValue;public int IntValue;public double NumberValue;public float[] Numbers;public VfxValue[] Children,Items; }
        [Serializable] private sealed class VfxResult { public bool Success;public string Message;public VfxData Data; }
        [Serializable] private sealed class VfxData
        {
            public string UnityVersion,Target,ComponentType,Path;public string[] Components;public bool HasVfxGraph,CreatedGameObject,AddedComponent,IsPlaying,IsPaused;public int InstanceId,Count,Index,ParticleCount;public float PlaybackSpeed;
            public ParticleRecord Particle;public RendererRecord Renderer;public TrailRecord Trail;public VectorRecord[] Positions;public AssetRecord[] Assets;
        }
        [Serializable] private sealed class ParticleRecord { public float Duration,StartLifetime,StartSpeed,StartSize,GravityModifier,RateOverTime,ShapeRadius,ShapeAngle;public bool Looping,EmissionEnabled,ShapeEnabled;public int MaxParticles,BurstCount;public string SimulationSpace,ShapeType,RenderMode,SortMode,Material; }
        [Serializable] private sealed class RendererRecord { public float StartWidth,EndWidth;public bool Loop,UseWorldSpace,ReceiveShadows;public string Alignment,TextureMode,Material,SortingLayerName,ShadowCastingMode;public int NumCornerVertices,NumCapVertices,SortingOrder; }
        [Serializable] private sealed class TrailRecord { public float Time,MinVertexDistance;public bool Emitting,Autodestruct; }
        [Serializable] private sealed class VectorRecord { public float[] Values; }
        [Serializable] private sealed class AssetRecord { public string Path,Name,Guid; }
    }
}
#endif
