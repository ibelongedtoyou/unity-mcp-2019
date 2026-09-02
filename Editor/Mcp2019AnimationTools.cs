#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityMcp2019
{
    internal static class Mcp2019AnimationTools
    {
        private static readonly string[] Actions =
        {
            "animator_get_info", "animator_get_parameter", "animator_play",
            "animator_crossfade", "animator_set_parameter", "animator_set_speed",
            "animator_set_enabled", "controller_create", "controller_add_state",
            "controller_add_transition", "controller_add_parameter", "controller_get_info",
            "controller_assign", "controller_add_layer", "controller_remove_layer",
            "controller_set_layer_weight", "controller_create_blend_tree_1d",
            "controller_create_blend_tree_2d", "controller_add_blend_tree_child",
            "clip_create", "clip_get_info", "clip_add_curve", "clip_set_curve",
            "clip_set_vector_curve", "clip_create_preset", "clip_assign",
            "clip_add_event", "clip_remove_event"
        };

        internal static string Execute(string argumentsJson)
        {
            AnimationArguments arguments = string.IsNullOrEmpty(argumentsJson) || argumentsJson == "{}"
                ? new AnimationArguments()
                : JsonUtility.FromJson<AnimationArguments>(argumentsJson) ?? new AnimationArguments();
            string action = (arguments.Action ?? string.Empty).Trim().ToLowerInvariant();
            if (Array.IndexOf(Actions, action) < 0)
                return JsonUtility.ToJson(Fail(
                    "Unknown action '" + action +
                    "'. Use animator_*, controller_*, or clip_* actions."));

            try
            {
                AnimationResult result;
                if (action.StartsWith("animator_", StringComparison.Ordinal))
                    result = ExecuteAnimator(action.Substring(9), arguments);
                else if (action.StartsWith("controller_", StringComparison.Ordinal))
                    result = ExecuteController(action.Substring(11), arguments);
                else
                    result = ExecuteClip(action.Substring(5), arguments);
                return JsonUtility.ToJson(result);
            }
            catch (Exception exception)
            {
                return JsonUtility.ToJson(Fail(
                    exception.GetType().Name + ": " + exception.Message));
            }
        }

        private static AnimationResult ExecuteAnimator(string action, AnimationArguments arguments)
        {
            GameObject gameObject = ResolveGameObject(arguments.Target, arguments.SearchMethod);
            if (gameObject == null) return Fail("Target GameObject not found.");
            Animator animator = gameObject.GetComponent<Animator>();
            if (animator == null) return Fail("No Animator component on '" + gameObject.name + "'.");

            if (action == "get_info") return GetAnimatorInfo(gameObject, animator);
            if (action == "get_parameter") return GetAnimatorParameter(animator, arguments.ParameterName);
            if (action == "play")
            {
                if (string.IsNullOrEmpty(arguments.StateName)) return Fail("'stateName' is required.");
                Undo.RecordObject(animator, "MCP Play Animation State");
                animator.Play(arguments.StateName, arguments.HasLayer ? arguments.Layer : -1);
                return Ok("Playing state '" + arguments.StateName + "' on '" + gameObject.name + "'.");
            }
            if (action == "crossfade")
            {
                if (string.IsNullOrEmpty(arguments.StateName)) return Fail("'stateName' is required.");
                float duration = arguments.HasDuration ? arguments.Duration : 0.25f;
                Undo.RecordObject(animator, "MCP Crossfade Animation State");
                animator.CrossFade(arguments.StateName, duration, arguments.HasLayer ? arguments.Layer : -1);
                return Ok("Crossfading to '" + arguments.StateName + "' on '" + gameObject.name + "'.");
            }
            if (action == "set_parameter") return SetAnimatorParameter(animator, arguments);
            if (action == "set_speed")
            {
                float speed = arguments.HasSpeed ? arguments.Speed : 1f;
                Undo.RecordObject(animator, "MCP Set Animator Speed");
                animator.speed = speed;
                EditorUtility.SetDirty(animator);
                return Ok("Set animator speed to " + speed + ".");
            }
            if (action == "set_enabled")
            {
                bool enabled = arguments.HasEnabled ? arguments.Enabled : true;
                Undo.RecordObject(animator, "MCP Set Animator Enabled");
                animator.enabled = enabled;
                EditorUtility.SetDirty(animator);
                return Ok("Animator " + (enabled ? "enabled." : "disabled."));
            }
            return Fail("Unknown animator action: " + action);
        }

        private static AnimationResult GetAnimatorInfo(GameObject gameObject, Animator animator)
        {
            List<ParameterRecord> parameters = new List<ParameterRecord>();
            for (int index = 0; index < animator.parameterCount; index++)
            {
                AnimatorControllerParameter parameter = animator.GetParameter(index);
                parameters.Add(ToParameterRecord(parameter, null));
            }

            List<LayerRecord> layers = new List<LayerRecord>();
            for (int index = 0; index < animator.layerCount; index++)
            {
                AnimatorStateInfo state = animator.IsInTransition(index)
                    ? animator.GetNextAnimatorStateInfo(index)
                    : animator.GetCurrentAnimatorStateInfo(index);
                layers.Add(new LayerRecord
                {
                    Index = index,
                    Name = animator.GetLayerName(index),
                    Weight = animator.GetLayerWeight(index),
                    CurrentStateHash = state.fullPathHash,
                    CurrentStateNormalizedTime = state.normalizedTime,
                    CurrentStateLength = state.length,
                    IsInTransition = animator.IsInTransition(index)
                });
            }

            AnimationClip[] animationClips = animator.runtimeAnimatorController == null
                ? new AnimationClip[0]
                : animator.runtimeAnimatorController.animationClips;
            return Ok("Animator information read.", new AnimationData
            {
                Target = gameObject.name,
                Enabled = animator.enabled,
                Speed = animator.speed,
                HasController = animator.runtimeAnimatorController != null,
                ControllerName = animator.runtimeAnimatorController == null
                    ? string.Empty : animator.runtimeAnimatorController.name,
                ApplyRootMotion = animator.applyRootMotion,
                UpdateMode = animator.updateMode.ToString(),
                CullingMode = animator.cullingMode.ToString(),
                ParameterCount = parameters.Count,
                LayerCount = layers.Count,
                Parameters = parameters.ToArray(),
                Layers = layers.ToArray(),
                Clips = animationClips.Select(ToClipRecord).ToArray()
            });
        }

        private static AnimationResult GetAnimatorParameter(Animator animator, string name)
        {
            if (string.IsNullOrEmpty(name)) return Fail("'parameterName' is required.");
            for (int index = 0; index < animator.parameterCount; index++)
            {
                AnimatorControllerParameter parameter = animator.GetParameter(index);
                if (!string.Equals(parameter.name, name, StringComparison.Ordinal)) continue;
                string value;
                switch (parameter.type)
                {
                    case AnimatorControllerParameterType.Float:
                        value = animator.GetFloat(name).ToString(System.Globalization.CultureInfo.InvariantCulture);
                        break;
                    case AnimatorControllerParameterType.Int:
                        value = animator.GetInteger(name).ToString();
                        break;
                    default:
                        value = animator.GetBool(name).ToString();
                        break;
                }
                return Ok("Animator parameter read.", new AnimationData
                {
                    Parameters = new[] { ToParameterRecord(parameter, value) },
                    ParameterCount = 1
                });
            }
            return Fail("Parameter '" + name + "' not found on Animator.");
        }

        private static AnimationResult SetAnimatorParameter(Animator animator, AnimationArguments arguments)
        {
            string name = arguments.ParameterName;
            if (string.IsNullOrEmpty(name)) return Fail("'parameterName' is required.");
            AnimatorControllerParameter found = null;
            for (int index = 0; index < animator.parameterCount; index++)
            {
                AnimatorControllerParameter parameter = animator.GetParameter(index);
                if (parameter.name == name) { found = parameter; break; }
            }
            string typeName = string.IsNullOrEmpty(arguments.ParameterType)
                ? (found == null ? string.Empty : found.type.ToString().ToLowerInvariant())
                : arguments.ParameterType.ToLowerInvariant();
            if (string.IsNullOrEmpty(typeName))
                return Fail("Parameter '" + name + "' not found. Specify parameterType.");

            if (Application.isPlaying)
            {
                Undo.RecordObject(animator, "MCP Set Animator Parameter");
                if (typeName == "float") animator.SetFloat(name, DynamicFloat(arguments, false));
                else if (typeName == "int" || typeName == "integer") animator.SetInteger(name, DynamicInt(arguments, false));
                else if (typeName == "bool" || typeName == "boolean") animator.SetBool(name, DynamicBool(arguments, false));
                else if (typeName == "trigger") animator.SetTrigger(name);
                else return Fail("Unknown parameter type: " + typeName);
                return Ok("Animator parameter '" + name + "' set.");
            }

            AnimatorController controller = animator.runtimeAnimatorController as AnimatorController;
            if (controller == null) return Fail("AnimatorController is required to set defaults in Edit Mode.");
            AnimatorControllerParameter[] parameters = controller.parameters;
            int parameterIndex = Array.FindIndex(parameters, item => item.name == name);
            if (parameterIndex < 0) return Fail("Parameter '" + name + "' not found on controller.");
            Undo.RecordObject(controller, "MCP Set Animator Parameter Default");
            if (typeName == "float") parameters[parameterIndex].defaultFloat = DynamicFloat(arguments, false);
            else if (typeName == "int" || typeName == "integer") parameters[parameterIndex].defaultInt = DynamicInt(arguments, false);
            else if (typeName == "bool" || typeName == "boolean") parameters[parameterIndex].defaultBool = DynamicBool(arguments, false);
            else if (typeName != "trigger") return Fail("Unknown parameter type: " + typeName);
            controller.parameters = parameters;
            Save(controller);
            return Ok("Animator parameter default '" + name + "' set.");
        }

        private static AnimationResult ExecuteController(string action, AnimationArguments arguments)
        {
            if (action == "create") return CreateController(arguments);
            AnimatorController controller = LoadController(arguments.ControllerPath);
            if (controller == null) return Fail("AnimatorController not found at '" + arguments.ControllerPath + "'.");
            if (action == "get_info") return GetControllerInfo(controller);
            if (action == "add_state") return AddState(controller, arguments);
            if (action == "add_transition") return AddTransition(controller, arguments);
            if (action == "add_parameter") return AddParameter(controller, arguments);
            if (action == "assign") return AssignController(controller, arguments);
            if (action == "add_layer") return AddLayer(controller, arguments);
            if (action == "remove_layer") return RemoveLayer(controller, arguments);
            if (action == "set_layer_weight") return SetLayerWeight(controller, arguments);
            if (action == "create_blend_tree_1d") return CreateBlendTree(controller, arguments, false);
            if (action == "create_blend_tree_2d") return CreateBlendTree(controller, arguments, true);
            if (action == "add_blend_tree_child") return AddBlendTreeChild(controller, arguments);
            return Fail("Unknown controller action: " + action);
        }

        private static AnimationResult CreateController(AnimationArguments arguments)
        {
            string path = NormalizeAssetPath(arguments.ControllerPath, ".controller");
            if (string.IsNullOrEmpty(path)) return Fail("'controllerPath' is required.");
            if (AssetDatabase.LoadAssetAtPath<AnimatorController>(path) != null)
                return Fail("AnimatorController already exists at '" + path + "'.");
            EnsureAssetFolder(Path.GetDirectoryName(path).Replace('\\', '/'));
            AnimatorController controller = AnimatorController.CreateAnimatorControllerAtPath(path);
            AssetDatabase.SaveAssets();
            return Ok("Created AnimatorController at '" + path + "'.", ControllerData(controller));
        }

        private static AnimationResult AddState(AnimatorController controller, AnimationArguments arguments)
        {
            if (string.IsNullOrEmpty(arguments.StateName)) return Fail("'stateName' is required.");
            int layerIndex = arguments.HasLayerIndex ? arguments.LayerIndex : 0;
            AnimatorStateMachine machine = GetStateMachine(controller, layerIndex);
            if (machine == null) return Fail("Layer index out of range.");
            if (machine.states.Any(item => item.state.name == arguments.StateName))
                return Fail("State '" + arguments.StateName + "' already exists.");
            Undo.RecordObject(controller, "MCP Add Animator State");
            AnimatorState state = machine.AddState(arguments.StateName);
            if (!string.IsNullOrEmpty(arguments.ClipPath))
                state.motion = AssetDatabase.LoadAssetAtPath<AnimationClip>(NormalizeAssetPath(arguments.ClipPath, ".anim"));
            state.speed = arguments.HasSpeed ? arguments.Speed : 1f;
            if (arguments.HasIsDefault && arguments.IsDefault) machine.defaultState = state;
            Save(controller);
            return Ok("Added state '" + arguments.StateName + "'.", new AnimationData
            {
                Path = AssetDatabase.GetAssetPath(controller), StateName = state.name,
                LayerIndex = layerIndex, Speed = state.speed, HasMotion = state.motion != null
            });
        }

        private static AnimationResult AddTransition(AnimatorController controller, AnimationArguments arguments)
        {
            if (string.IsNullOrEmpty(arguments.FromState) || string.IsNullOrEmpty(arguments.ToState))
                return Fail("'fromState' and 'toState' are required.");
            int layerIndex = arguments.HasLayerIndex ? arguments.LayerIndex : 0;
            AnimatorStateMachine machine = GetStateMachine(controller, layerIndex);
            if (machine == null) return Fail("Layer index out of range.");
            AnimatorState toState = FindState(machine, arguments.ToState);
            if (toState == null) return Fail("Destination state not found: " + arguments.ToState);
            bool anyState = string.Equals(arguments.FromState.Replace(" ", string.Empty), "AnyState", StringComparison.OrdinalIgnoreCase)
                || string.Equals(arguments.FromState, "Any", StringComparison.OrdinalIgnoreCase);
            AnimatorState fromState = anyState ? null : FindState(machine, arguments.FromState);
            if (!anyState && fromState == null) return Fail("Source state not found: " + arguments.FromState);
            Undo.RecordObject(controller, "MCP Add Animator Transition");
            AnimatorStateTransition transition = anyState
                ? machine.AddAnyStateTransition(toState)
                : fromState.AddTransition(toState);
            transition.hasExitTime = arguments.HasHasExitTime ? arguments.HasExitTime : true;
            transition.duration = arguments.HasDuration ? arguments.Duration : 0.25f;
            transition.exitTime = arguments.HasExitTimeValue ? arguments.ExitTime : 0.75f;
            int conditionCount = 0;
            foreach (AnimationConditionArgument condition in arguments.Conditions ?? new AnimationConditionArgument[0])
            {
                if (string.IsNullOrEmpty(condition.Parameter)) continue;
                transition.AddCondition(ParseConditionMode(condition.Mode), condition.Threshold, condition.Parameter);
                conditionCount++;
            }
            Save(controller);
            return Ok("Added transition from '" + arguments.FromState + "' to '" + arguments.ToState + "'.",
                new AnimationData { LayerIndex = layerIndex, ConditionCount = conditionCount });
        }

        private static AnimationResult AddParameter(AnimatorController controller, AnimationArguments arguments)
        {
            if (string.IsNullOrEmpty(arguments.ParameterName)) return Fail("'parameterName' is required.");
            if (controller.parameters.Any(item => item.name == arguments.ParameterName))
                return Fail("Parameter already exists: " + arguments.ParameterName);
            AnimatorControllerParameterType type;
            if (!TryParseParameterType(arguments.ParameterType, out type)) return Fail("Unknown parameterType.");
            Undo.RecordObject(controller, "MCP Add Animator Parameter");
            controller.AddParameter(arguments.ParameterName, type);
            if (arguments.HasDefaultValue)
            {
                AnimatorControllerParameter[] parameters = controller.parameters;
                AnimatorControllerParameter parameter = parameters[parameters.Length - 1];
                if (type == AnimatorControllerParameterType.Float) parameter.defaultFloat = DynamicFloat(arguments, true);
                else if (type == AnimatorControllerParameterType.Int) parameter.defaultInt = DynamicInt(arguments, true);
                else if (type == AnimatorControllerParameterType.Bool) parameter.defaultBool = DynamicBool(arguments, true);
                controller.parameters = parameters;
            }
            Save(controller);
            return Ok("Added parameter '" + arguments.ParameterName + "'.", ControllerData(controller));
        }

        private static AnimationResult GetControllerInfo(AnimatorController controller)
        {
            List<LayerRecord> layers = new List<LayerRecord>();
            AnimatorControllerLayer[] controllerLayers = controller.layers;
            for (int index = 0; index < controllerLayers.Length; index++)
            {
                AnimatorControllerLayer layer = controllerLayers[index];
                List<StateRecord> states = new List<StateRecord>();
                foreach (ChildAnimatorState child in layer.stateMachine.states)
                {
                    states.Add(new StateRecord
                    {
                        Name = child.state.name,
                        Speed = child.state.speed,
                        HasMotion = child.state.motion != null,
                        MotionName = child.state.motion == null ? string.Empty : child.state.motion.name,
                        IsDefault = layer.stateMachine.defaultState == child.state,
                        TransitionCount = child.state.transitions.Length
                    });
                }
                layers.Add(new LayerRecord
                {
                    Index = index, Name = layer.name, Weight = layer.defaultWeight,
                    StateCount = states.Count, States = states.ToArray()
                });
            }
            AnimationData data = ControllerData(controller);
            data.Layers = layers.ToArray();
            data.Parameters = controller.parameters.Select(item => ToParameterRecord(item, null)).ToArray();
            return Ok("AnimatorController information read.", data);
        }

        private static AnimationResult AssignController(AnimatorController controller, AnimationArguments arguments)
        {
            GameObject gameObject = ResolveGameObject(arguments.Target, arguments.SearchMethod);
            if (gameObject == null) return Fail("Target GameObject not found.");
            Animator animator = gameObject.GetComponent<Animator>();
            if (animator == null) animator = Undo.AddComponent<Animator>(gameObject);
            Undo.RecordObject(animator, "MCP Assign AnimatorController");
            animator.runtimeAnimatorController = controller;
            EditorUtility.SetDirty(animator);
            return Ok("Assigned controller to '" + gameObject.name + "'.", new AnimationData
            {
                Target = gameObject.name, ControllerName = controller.name,
                Path = AssetDatabase.GetAssetPath(controller)
            });
        }

        private static AnimationResult AddLayer(AnimatorController controller, AnimationArguments arguments)
        {
            if (string.IsNullOrEmpty(arguments.LayerName)) return Fail("'layerName' is required.");
            Undo.RecordObject(controller, "MCP Add Animator Layer");
            controller.AddLayer(arguments.LayerName);
            AnimatorControllerLayer[] layers = controller.layers;
            AnimatorControllerLayer layer = layers[layers.Length - 1];
            layer.defaultWeight = arguments.HasWeight ? arguments.Weight : 1f;
            layer.blendingMode = string.Equals(arguments.BlendingMode, "additive", StringComparison.OrdinalIgnoreCase)
                ? AnimatorLayerBlendingMode.Additive : AnimatorLayerBlendingMode.Override;
            layers[layers.Length - 1] = layer;
            controller.layers = layers;
            Save(controller);
            return Ok("Added layer '" + arguments.LayerName + "'.", ControllerData(controller));
        }

        private static AnimationResult RemoveLayer(AnimatorController controller, AnimationArguments arguments)
        {
            int index = ResolveLayerIndex(controller, arguments);
            if (index <= 0) return Fail(index == 0 ? "Cannot remove the base layer." : "Layer not found.");
            string name = controller.layers[index].name;
            Undo.RecordObject(controller, "MCP Remove Animator Layer");
            controller.RemoveLayer(index);
            Save(controller);
            return Ok("Removed layer '" + name + "'.", ControllerData(controller));
        }

        private static AnimationResult SetLayerWeight(AnimatorController controller, AnimationArguments arguments)
        {
            int index = ResolveLayerIndex(controller, arguments);
            if (index < 0) return Fail("Layer not found.");
            AnimatorControllerLayer[] layers = controller.layers;
            AnimatorControllerLayer layer = layers[index];
            layer.defaultWeight = arguments.HasWeight ? arguments.Weight : 1f;
            layers[index] = layer;
            Undo.RecordObject(controller, "MCP Set Animator Layer Weight");
            controller.layers = layers;
            Save(controller);
            return Ok("Layer weight updated.", ControllerData(controller));
        }

        private static AnimationResult CreateBlendTree(
            AnimatorController controller, AnimationArguments arguments, bool twoDimensional)
        {
            if (string.IsNullOrEmpty(arguments.StateName)) return Fail("'stateName' is required.");
            if ((!twoDimensional && string.IsNullOrEmpty(arguments.BlendParameter)) ||
                (twoDimensional && (string.IsNullOrEmpty(arguments.BlendParameterX) || string.IsNullOrEmpty(arguments.BlendParameterY))))
                return Fail(twoDimensional ? "'blendParameterX' and 'blendParameterY' are required." : "'blendParameter' is required.");
            int layerIndex = arguments.HasLayerIndex ? arguments.LayerIndex : 0;
            AnimatorStateMachine machine = GetStateMachine(controller, layerIndex);
            if (machine == null) return Fail("Layer index out of range.");
            if (FindState(machine, arguments.StateName) != null) return Fail("State already exists: " + arguments.StateName);
            Undo.RecordObject(controller, "MCP Create Blend Tree");
            AnimatorState state = machine.AddState(arguments.StateName);
            BlendTree tree = new BlendTree();
            tree.name = arguments.StateName;
            tree.hideFlags = HideFlags.HideInHierarchy;
            if (twoDimensional)
            {
                tree.blendType = ParseBlendTreeType(arguments.BlendType);
                tree.blendParameter = arguments.BlendParameterX;
                tree.blendParameterY = arguments.BlendParameterY;
            }
            else
            {
                tree.blendType = BlendTreeType.Simple1D;
                tree.blendParameter = arguments.BlendParameter;
            }
            AssetDatabase.AddObjectToAsset(tree, controller);
            state.motion = tree;
            Save(controller);
            return Ok("Created blend tree state '" + arguments.StateName + "'.", new AnimationData
            {
                Path = AssetDatabase.GetAssetPath(controller), StateName = arguments.StateName,
                LayerIndex = layerIndex, BlendType = tree.blendType.ToString()
            });
        }

        private static AnimationResult AddBlendTreeChild(AnimatorController controller, AnimationArguments arguments)
        {
            int layerIndex = arguments.HasLayerIndex ? arguments.LayerIndex : 0;
            AnimatorStateMachine machine = GetStateMachine(controller, layerIndex);
            AnimatorState state = machine == null ? null : FindState(machine, arguments.StateName);
            BlendTree tree = state == null ? null : state.motion as BlendTree;
            if (tree == null) return Fail("BlendTree state not found: " + arguments.StateName);
            AnimationClip clip = LoadClip(arguments.ClipPath);
            if (clip == null) return Fail("AnimationClip not found at '" + arguments.ClipPath + "'.");
            Undo.RecordObject(tree, "MCP Add Blend Tree Child");
            if (tree.blendType == BlendTreeType.Simple1D)
            {
                if (!arguments.HasThreshold) return Fail("'threshold' is required for 1D blend trees.");
                tree.AddChild(clip, arguments.Threshold);
            }
            else
            {
                if (!arguments.HasPosition || arguments.Position == null || arguments.Position.Length < 2)
                    return Fail("'position' as [x, y] is required for 2D blend trees.");
                tree.AddChild(clip, new Vector2(arguments.Position[0], arguments.Position[1]));
            }
            Save(tree);
            Save(controller);
            return Ok("Added clip '" + clip.name + "' to blend tree.", new AnimationData
            {
                StateName = arguments.StateName, ClipPath = AssetDatabase.GetAssetPath(clip),
                ChildCount = tree.children.Length
            });
        }

        private static AnimationResult ExecuteClip(string action, AnimationArguments arguments)
        {
            if (action == "create") return CreateClip(arguments);
            if (action == "create_preset") return CreatePreset(arguments);
            AnimationClip clip = LoadClip(arguments.ClipPath);
            if (clip == null) return Fail("AnimationClip not found at '" + arguments.ClipPath + "'.");
            if (action == "get_info") return GetClipInfo(clip);
            if (action == "add_curve") return SetCurve(clip, arguments, true);
            if (action == "set_curve") return SetCurve(clip, arguments, false);
            if (action == "set_vector_curve") return SetVectorCurve(clip, arguments);
            if (action == "assign") return AssignClip(clip, arguments);
            if (action == "add_event") return AddEvent(clip, arguments);
            if (action == "remove_event") return RemoveEvent(clip, arguments);
            return Fail("Unknown clip action: " + action);
        }

        private static AnimationResult CreateClip(AnimationArguments arguments)
        {
            string path = NormalizeAssetPath(arguments.ClipPath, ".anim");
            if (string.IsNullOrEmpty(path)) return Fail("'clipPath' is required.");
            if (AssetDatabase.LoadAssetAtPath<AnimationClip>(path) != null)
                return Fail("AnimationClip already exists at '" + path + "'.");
            EnsureAssetFolder(Path.GetDirectoryName(path).Replace('\\', '/'));
            AnimationClip clip = new AnimationClip();
            clip.name = string.IsNullOrEmpty(arguments.Name) ? Path.GetFileNameWithoutExtension(path) : arguments.Name;
            clip.frameRate = arguments.HasFrameRate ? arguments.FrameRate : 60f;
            AnimationClipSettings settings = AnimationUtility.GetAnimationClipSettings(clip);
            settings.loopTime = arguments.HasLoop && arguments.Loop;
            settings.stopTime = arguments.HasLength ? arguments.Length : 1f;
            AnimationUtility.SetAnimationClipSettings(clip, settings);
            AssetDatabase.CreateAsset(clip, path);
            if (settings.loopTime) SetLegacyWrapMode(clip, WrapMode.Loop);
            AssetDatabase.SaveAssets();
            return Ok("Created AnimationClip at '" + path + "'.", ClipData(clip));
        }

        private static AnimationResult GetClipInfo(AnimationClip clip)
        {
            EditorCurveBinding[] bindings = AnimationUtility.GetCurveBindings(clip);
            AnimationEvent[] events = AnimationUtility.GetAnimationEvents(clip);
            AnimationData data = ClipData(clip);
            data.CurveCount = bindings.Length;
            data.Curves = bindings.Select(binding => new CurveRecord
            {
                Path = binding.path, PropertyName = binding.propertyName,
                Type = binding.type == null ? string.Empty : binding.type.Name,
                KeyCount = AnimationUtility.GetEditorCurve(clip, binding) == null
                    ? 0 : AnimationUtility.GetEditorCurve(clip, binding).length
            }).ToArray();
            data.EventCount = events.Length;
            data.Events = events.Select(ToEventRecord).ToArray();
            return Ok("AnimationClip information read.", data);
        }

        private static AnimationResult SetCurve(AnimationClip clip, AnimationArguments arguments, bool append)
        {
            if (string.IsNullOrEmpty(arguments.PropertyPath)) return Fail("'propertyPath' is required.");
            Type type = ResolveType(arguments.Type);
            if (type == null) return Fail("Could not resolve type '" + arguments.Type + "'.");
            Keyframe[] keys = ParseScalarKeys(arguments.Keys);
            if (keys.Length == 0) return Fail("'keys' is required.");
            EditorCurveBinding binding = EditorCurveBinding.FloatCurve(
                arguments.RelativePath ?? string.Empty, type, arguments.PropertyPath);
            AnimationCurve curve = append
                ? (AnimationUtility.GetEditorCurve(clip, binding) ?? new AnimationCurve())
                : new AnimationCurve(keys);
            if (append) foreach (Keyframe key in keys) curve.AddKey(key);
            Undo.RecordObject(clip, append ? "MCP Add Animation Curve" : "MCP Set Animation Curve");
            AnimationUtility.SetEditorCurve(clip, binding, curve);
            Save(clip);
            return Ok((append ? "Added" : "Set") + " animation curve.", new AnimationData
            {
                ClipPath = AssetDatabase.GetAssetPath(clip), PropertyPath = arguments.PropertyPath,
                KeyframeCount = curve.length
            });
        }

        private static AnimationResult SetVectorCurve(AnimationClip clip, AnimationArguments arguments)
        {
            string property = string.IsNullOrEmpty(arguments.Property) ? arguments.PropertyPath : arguments.Property;
            if (string.IsNullOrEmpty(property)) return Fail("'property' or 'propertyPath' is required.");
            Type type = ResolveType(arguments.Type);
            if (type == null) return Fail("Could not resolve type '" + arguments.Type + "'.");
            AnimationKeyArgument[] source = arguments.Keys ?? new AnimationKeyArgument[0];
            if (source.Length == 0 || source.Any(item => !item.IsVector || item.VectorValue == null || item.VectorValue.Length < 3))
                return Fail("Vector keys require values in [x, y, z] form.");
            string canonical = property.ToLowerInvariant() == "localposition" ? "localPosition"
                : property.ToLowerInvariant() == "localeulerangles" ? "localEulerAngles"
                : property.ToLowerInvariant() == "localscale" ? "localScale" : property;
            List<Keyframe> x = new List<Keyframe>();
            List<Keyframe> y = new List<Keyframe>();
            List<Keyframe> z = new List<Keyframe>();
            foreach (AnimationKeyArgument key in source)
            {
                x.Add(new Keyframe(key.Time, key.VectorValue[0]));
                y.Add(new Keyframe(key.Time, key.VectorValue[1]));
                z.Add(new Keyframe(key.Time, key.VectorValue[2]));
            }
            string relativePath = arguments.RelativePath ?? string.Empty;
            Undo.RecordObject(clip, "MCP Set Vector Animation Curve");
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(relativePath, type, canonical + ".x"), new AnimationCurve(x.ToArray()));
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(relativePath, type, canonical + ".y"), new AnimationCurve(y.ToArray()));
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(relativePath, type, canonical + ".z"), new AnimationCurve(z.ToArray()));
            Save(clip);
            return Ok("Set vector animation curves.", new AnimationData
            {
                ClipPath = AssetDatabase.GetAssetPath(clip), PropertyPath = canonical,
                KeyframeCount = source.Length, CurveCount = 3
            });
        }

        private static AnimationResult CreatePreset(AnimationArguments arguments)
        {
            string path = NormalizeAssetPath(arguments.ClipPath, ".anim");
            if (string.IsNullOrEmpty(path)) return Fail("'clipPath' is required.");
            if (AssetDatabase.LoadAssetAtPath<AnimationClip>(path) != null)
                return Fail("AnimationClip already exists at '" + path + "'.");
            string preset = (arguments.Preset ?? string.Empty).ToLowerInvariant();
            string[] valid = { "bounce", "rotate", "pulse", "fade", "shake", "hover", "spin", "sway", "bob", "wiggle", "blink", "slide_in", "elastic", "grow", "shrink" };
            if (Array.IndexOf(valid, preset) < 0) return Fail("Unknown animation preset: " + preset);
            float duration = arguments.HasDuration ? arguments.Duration : 1f;
            float amplitude = arguments.HasAmplitude ? arguments.Amplitude : 1f;
            bool loop = arguments.HasLoop ? arguments.Loop : true;
            Vector3 offset = Vector3.zero;
            GameObject target = ResolveGameObject(arguments.Target, arguments.SearchMethod);
            if (target != null) offset = target.transform.localPosition;
            if (arguments.HasOffset && arguments.Offset != null && arguments.Offset.Length >= 3)
                offset = new Vector3(arguments.Offset[0], arguments.Offset[1], arguments.Offset[2]);
            AnimationClip clip = new AnimationClip();
            clip.name = Path.GetFileNameWithoutExtension(path);
            clip.frameRate = 60f;
            AnimationClipSettings settings = AnimationUtility.GetAnimationClipSettings(clip);
            settings.loopTime = loop;
            settings.stopTime = duration;
            AnimationUtility.SetAnimationClipSettings(clip, settings);
            ApplyPreset(clip, preset, duration, amplitude, offset);
            EnsureAssetFolder(Path.GetDirectoryName(path).Replace('\\', '/'));
            AssetDatabase.CreateAsset(clip, path);
            AssetDatabase.SaveAssets();
            AnimationData data = ClipData(clip);
            data.Preset = preset;
            data.CurveCount = AnimationUtility.GetCurveBindings(clip).Length;
            return Ok("Created '" + preset + "' animation preset.", data);
        }

        private static AnimationResult AssignClip(AnimationClip clip, AnimationArguments arguments)
        {
            GameObject gameObject = ResolveGameObject(arguments.Target, arguments.SearchMethod);
            if (gameObject == null) return Fail("Target GameObject not found.");
            UnityEngine.Animation animation = gameObject.GetComponent<UnityEngine.Animation>();
            Animator animator = gameObject.GetComponent<Animator>();
            if (animation == null && animator == null) animation = Undo.AddComponent<UnityEngine.Animation>(gameObject);
            if (animation == null)
                return Ok("GameObject has Animator. The clip is ready for assignment to a controller state.");
            MakeLegacy(clip);
            Undo.RecordObject(animation, "MCP Assign Animation Clip");
            animation.clip = clip;
            animation.AddClip(clip, clip.name);
            animation.playAutomatically = true;
            EditorUtility.SetDirty(animation);
            AssetDatabase.SaveAssets();
            return Ok("Assigned clip '" + clip.name + "' to '" + gameObject.name + "'.");
        }

        private static AnimationResult AddEvent(AnimationClip clip, AnimationArguments arguments)
        {
            if (string.IsNullOrEmpty(arguments.FunctionName)) return Fail("'functionName' is required.");
            AnimationEvent animationEvent = new AnimationEvent
            {
                time = arguments.HasTime ? arguments.Time : 0f,
                functionName = arguments.FunctionName,
                stringParameter = arguments.StringParameter ?? string.Empty,
                floatParameter = arguments.HasFloatParameter ? arguments.FloatParameter : 0f,
                intParameter = arguments.HasIntParameter ? arguments.IntParameter : 0
            };
            List<AnimationEvent> events = AnimationUtility.GetAnimationEvents(clip).ToList();
            events.Add(animationEvent);
            Undo.RecordObject(clip, "MCP Add Animation Event");
            AnimationUtility.SetAnimationEvents(clip, events.ToArray());
            Save(clip);
            return Ok("Added animation event '" + arguments.FunctionName + "'.", ClipData(clip));
        }

        private static AnimationResult RemoveEvent(AnimationClip clip, AnimationArguments arguments)
        {
            List<AnimationEvent> events = AnimationUtility.GetAnimationEvents(clip).ToList();
            int before = events.Count;
            if (arguments.HasEventIndex)
            {
                if (arguments.EventIndex < 0 || arguments.EventIndex >= events.Count)
                    return Fail("eventIndex is out of range.");
                events.RemoveAt(arguments.EventIndex);
            }
            else
            {
                if (string.IsNullOrEmpty(arguments.FunctionName)) return Fail("'eventIndex' or 'functionName' is required.");
                events.RemoveAll(item => item.functionName == arguments.FunctionName &&
                    (!arguments.HasTime || Mathf.Approximately(item.time, arguments.Time)));
            }
            if (events.Count == before) return Fail("No matching animation events found.");
            Undo.RecordObject(clip, "MCP Remove Animation Event");
            AnimationUtility.SetAnimationEvents(clip, events.ToArray());
            Save(clip);
            return Ok("Removed " + (before - events.Count) + " animation event(s).", ClipData(clip));
        }

        private static void ApplyPreset(AnimationClip clip, string preset, float duration, float amplitude, Vector3 offset)
        {
            float quarter = duration * 0.25f;
            if (preset == "fade")
            {
                SetCurve(clip, typeof(CanvasGroup), "m_Alpha", new Keyframe(0f, 1f), new Keyframe(duration, 0f));
                return;
            }
            if (preset == "rotate" || preset == "spin")
            {
                string property = preset == "rotate" ? "localEulerAngles.y" : "localEulerAngles.z";
                SetCurve(clip, typeof(Transform), property, new Keyframe(0f, 0f), new Keyframe(duration, 360f * amplitude));
                return;
            }
            if (preset == "pulse" || preset == "blink" || preset == "elastic" || preset == "grow" || preset == "shrink")
            {
                Keyframe[] keys;
                if (preset == "grow") keys = new[] { new Keyframe(0f, Mathf.Clamp01(1f - Mathf.Max(0f, amplitude))), new Keyframe(duration, 1f) };
                else if (preset == "shrink") keys = new[] { new Keyframe(0f, 1f), new Keyframe(duration, Mathf.Clamp01(1f - Mathf.Max(0f, amplitude))) };
                else if (preset == "blink") keys = new[] { new Keyframe(0f, 1f), new Keyframe(duration * 0.5f, 0.05f), new Keyframe(duration, 1f) };
                else if (preset == "elastic") keys = new[] { new Keyframe(0f, 1f), new Keyframe(duration / 3f, 1f + amplitude * 1.2f), new Keyframe(duration * 2f / 3f, 1f + amplitude * 0.8f), new Keyframe(duration, 1f) };
                else keys = new[] { new Keyframe(0f, 1f), new Keyframe(duration * 0.5f, 1f + amplitude * 0.5f), new Keyframe(duration, 1f) };
                SetCurve(clip, typeof(Transform), "localScale.x", keys);
                SetCurve(clip, typeof(Transform), "localScale.y", keys);
                SetCurve(clip, typeof(Transform), "localScale.z", keys);
                return;
            }
            if (preset == "sway" || preset == "wiggle")
            {
                if (preset == "sway") SetCurve(clip, typeof(Transform), "localEulerAngles.z",
                    new Keyframe(0f, 0f), new Keyframe(quarter, amplitude), new Keyframe(quarter * 2f, 0f),
                    new Keyframe(quarter * 3f, -amplitude), new Keyframe(duration, 0f));
                else SetDecayCurve(clip, "localEulerAngles.z", duration, amplitude, 0f);
                return;
            }
            if (preset == "slide_in")
            {
                SetCurve(clip, typeof(Transform), "localPosition.x", new Keyframe(0f, offset.x - amplitude), new Keyframe(duration, offset.x));
                return;
            }
            if (preset == "shake")
            {
                SetDecayCurve(clip, "localPosition.x", duration, amplitude, offset.x);
                SetDecayCurve(clip, "localPosition.z", duration, amplitude * 0.5f, offset.z);
                return;
            }
            string axis = preset == "bob" ? "localPosition.z" : "localPosition.y";
            float origin = preset == "bob" ? offset.z : offset.y;
            float peak = preset == "bounce" ? amplitude : amplitude * 0.5f;
            SetCurve(clip, typeof(Transform), axis,
                new Keyframe(0f, origin), new Keyframe(quarter, origin + peak),
                new Keyframe(quarter * 2f, origin), new Keyframe(quarter * 3f, origin - (preset == "bounce" ? -peak : peak)),
                new Keyframe(duration, origin));
        }

        private static void SetDecayCurve(AnimationClip clip, string property, float duration, float amplitude, float origin)
        {
            Keyframe[] keys = new Keyframe[9];
            for (int index = 0; index < keys.Length; index++)
            {
                float ratio = index / 8f;
                float sign = index % 2 == 0 ? 1f : -1f;
                keys[index] = new Keyframe(duration * ratio, origin + sign * amplitude * (1f - ratio));
            }
            keys[8] = new Keyframe(duration, origin);
            SetCurve(clip, typeof(Transform), property, keys);
        }

        private static void SetCurve(AnimationClip clip, Type type, string property, params Keyframe[] keys)
        {
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(string.Empty, type, property), new AnimationCurve(keys));
        }

        private static Keyframe[] ParseScalarKeys(AnimationKeyArgument[] source)
        {
            List<Keyframe> keys = new List<Keyframe>();
            foreach (AnimationKeyArgument item in source ?? new AnimationKeyArgument[0])
            {
                if (item.IsVector) continue;
                Keyframe key = new Keyframe(item.Time, item.Value);
                if (item.HasInTangent) key.inTangent = item.InTangent;
                if (item.HasOutTangent) key.outTangent = item.OutTangent;
#if UNITY_2018_1_OR_NEWER
                if (item.HasInWeight) key.inWeight = item.InWeight;
                if (item.HasOutWeight) key.outWeight = item.OutWeight;
#endif
                keys.Add(key);
            }
            return keys.ToArray();
        }

        private static AnimationData ControllerData(AnimatorController controller)
        {
            return new AnimationData
            {
                Path = AssetDatabase.GetAssetPath(controller), Name = controller.name,
                LayerCount = controller.layers.Length, ParameterCount = controller.parameters.Length
            };
        }

        private static AnimationData ClipData(AnimationClip clip)
        {
            AnimationClipSettings settings = AnimationUtility.GetAnimationClipSettings(clip);
            return new AnimationData
            {
                Path = AssetDatabase.GetAssetPath(clip), ClipPath = AssetDatabase.GetAssetPath(clip),
                Name = clip.name, Length = clip.length, FrameRate = clip.frameRate,
                IsLooping = settings.loopTime, WrapMode = clip.wrapMode.ToString(),
                EventCount = AnimationUtility.GetAnimationEvents(clip).Length
            };
        }

        private static ClipRecord ToClipRecord(AnimationClip clip)
        {
            return new ClipRecord
            {
                Name = clip.name, Length = clip.length, FrameRate = clip.frameRate,
                IsLooping = AnimationUtility.GetAnimationClipSettings(clip).loopTime,
                WrapMode = clip.wrapMode.ToString()
            };
        }

        private static ParameterRecord ToParameterRecord(AnimatorControllerParameter parameter, string value)
        {
            return new ParameterRecord
            {
                Name = parameter.name, Type = parameter.type.ToString(), Value = value ?? string.Empty,
                DefaultFloat = parameter.defaultFloat, DefaultInt = parameter.defaultInt,
                DefaultBool = parameter.defaultBool
            };
        }

        private static EventRecord ToEventRecord(AnimationEvent item)
        {
            return new EventRecord
            {
                Time = item.time, FunctionName = item.functionName,
                StringParameter = item.stringParameter, FloatParameter = item.floatParameter,
                IntParameter = item.intParameter
            };
        }

        private static AnimatorController LoadController(string path)
        {
            return string.IsNullOrEmpty(path) ? null
                : AssetDatabase.LoadAssetAtPath<AnimatorController>(NormalizeAssetPath(path, ".controller"));
        }

        private static AnimationClip LoadClip(string path)
        {
            return string.IsNullOrEmpty(path) ? null
                : AssetDatabase.LoadAssetAtPath<AnimationClip>(NormalizeAssetPath(path, ".anim"));
        }

        private static AnimatorStateMachine GetStateMachine(AnimatorController controller, int index)
        {
            AnimatorControllerLayer[] layers = controller.layers;
            return index < 0 || index >= layers.Length ? null : layers[index].stateMachine;
        }

        private static AnimatorState FindState(AnimatorStateMachine machine, string name)
        {
            foreach (ChildAnimatorState child in machine.states)
                if (child.state.name == name) return child.state;
            return null;
        }

        private static int ResolveLayerIndex(AnimatorController controller, AnimationArguments arguments)
        {
            if (arguments.HasLayerIndex)
                return arguments.LayerIndex >= 0 && arguments.LayerIndex < controller.layers.Length
                    ? arguments.LayerIndex : -1;
            if (string.IsNullOrEmpty(arguments.LayerName)) return -1;
            AnimatorControllerLayer[] layers = controller.layers;
            for (int index = 0; index < layers.Length; index++)
                if (layers[index].name == arguments.LayerName) return index;
            return -1;
        }

        private static AnimatorConditionMode ParseConditionMode(string value)
        {
            switch ((value ?? "greater").Replace("_", string.Empty).ToLowerInvariant())
            {
                case "less": return AnimatorConditionMode.Less;
                case "equals": return AnimatorConditionMode.Equals;
                case "notequal": return AnimatorConditionMode.NotEqual;
                case "if": case "true": return AnimatorConditionMode.If;
                case "ifnot": case "false": return AnimatorConditionMode.IfNot;
                default: return AnimatorConditionMode.Greater;
            }
        }

        private static BlendTreeType ParseBlendTreeType(string value)
        {
            switch ((value ?? string.Empty).ToLowerInvariant())
            {
                case "freeformdirectional2d": return BlendTreeType.FreeformDirectional2D;
                case "freeformcartesian2d": return BlendTreeType.FreeformCartesian2D;
                default: return BlendTreeType.SimpleDirectional2D;
            }
        }

        private static bool TryParseParameterType(string value, out AnimatorControllerParameterType result)
        {
            switch ((value ?? "float").ToLowerInvariant())
            {
                case "float": result = AnimatorControllerParameterType.Float; return true;
                case "int": case "integer": result = AnimatorControllerParameterType.Int; return true;
                case "bool": case "boolean": result = AnimatorControllerParameterType.Bool; return true;
                case "trigger": result = AnimatorControllerParameterType.Trigger; return true;
                default: result = AnimatorControllerParameterType.Float; return false;
            }
        }

        private static Type ResolveType(string typeName)
        {
            if (string.IsNullOrEmpty(typeName)) return typeof(Transform);
            Type type = Type.GetType(typeName, false);
            if (type != null) return type;
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                type = assembly.GetType(typeName, false) ?? assembly.GetType("UnityEngine." + typeName, false);
                if (type != null) return type;
            }
            return null;
        }

        private static GameObject ResolveGameObject(string target, string searchMethod)
        {
            if (string.IsNullOrEmpty(target)) return null;
            int instanceId;
            if ((string.Equals(searchMethod, "by_id", StringComparison.OrdinalIgnoreCase) || int.TryParse(target, out instanceId)) &&
                int.TryParse(target, out instanceId))
                return EditorUtility.InstanceIDToObject(instanceId) as GameObject;
            IEnumerable<GameObject> objects = Resources.FindObjectsOfTypeAll<GameObject>()
                .Where(item => item != null && !EditorUtility.IsPersistent(item) && item.scene.IsValid());
            if (string.Equals(searchMethod, "by_path", StringComparison.OrdinalIgnoreCase))
                return objects.FirstOrDefault(item => HierarchyPath(item) == target || HierarchyPath(item).EndsWith("/" + target, StringComparison.Ordinal));
            if (string.Equals(searchMethod, "by_tag", StringComparison.OrdinalIgnoreCase))
                return objects.FirstOrDefault(item => string.Equals(SafeTag(item), target, StringComparison.OrdinalIgnoreCase));
            if (string.Equals(searchMethod, "by_layer", StringComparison.OrdinalIgnoreCase))
            {
                int layer = LayerMask.NameToLayer(target);
                if (layer < 0) int.TryParse(target, out layer);
                return objects.FirstOrDefault(item => item.layer == layer);
            }
            return objects.FirstOrDefault(item => string.Equals(item.name, target, StringComparison.OrdinalIgnoreCase))
                ?? objects.FirstOrDefault(item => item.name.IndexOf(target, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static string HierarchyPath(GameObject gameObject)
        {
            List<string> segments = new List<string>();
            Transform current = gameObject.transform;
            while (current != null) { segments.Add(current.name); current = current.parent; }
            segments.Reverse();
            return string.Join("/", segments.ToArray());
        }

        private static string SafeTag(GameObject gameObject)
        {
            try { return gameObject.tag; }
            catch (UnityException) { return string.Empty; }
        }

        private static string NormalizeAssetPath(string path, string extension)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            string normalized = path.Replace('\\', '/').Trim();
            if (!normalized.StartsWith("Assets/", StringComparison.Ordinal) ||
                normalized.Contains("../") || normalized.EndsWith("/..", StringComparison.Ordinal))
                throw new ArgumentException("Asset path must stay under Assets/.");
            if (!normalized.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) normalized += extension;
            return normalized;
        }

        private static void EnsureAssetFolder(string path)
        {
            if (string.IsNullOrEmpty(path) || path == "Assets" || AssetDatabase.IsValidFolder(path)) return;
            string parent = Path.GetDirectoryName(path).Replace('\\', '/');
            EnsureAssetFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
        }

        private static void Save(UnityEngine.Object target)
        {
            EditorUtility.SetDirty(target);
            AssetDatabase.SaveAssets();
        }

        private static void MakeLegacy(AnimationClip clip)
        {
            SerializedObject serialized = new SerializedObject(clip);
            SerializedProperty property = serialized.FindProperty("m_Legacy");
            if (property != null) property.boolValue = true;
            serialized.ApplyModifiedProperties();
            Save(clip);
        }

        private static void SetLegacyWrapMode(AnimationClip clip, WrapMode mode)
        {
            SerializedObject serialized = new SerializedObject(clip);
            SerializedProperty property = serialized.FindProperty("m_WrapMode");
            if (property != null) property.intValue = (int)mode;
            serialized.ApplyModifiedProperties();
        }

        private static float DynamicFloat(AnimationArguments arguments, bool defaultValue)
        {
            return defaultValue ? arguments.DefaultValueFloat : arguments.ValueFloat;
        }

        private static int DynamicInt(AnimationArguments arguments, bool defaultValue)
        {
            return defaultValue ? arguments.DefaultValueInt : arguments.ValueInt;
        }

        private static bool DynamicBool(AnimationArguments arguments, bool defaultValue)
        {
            return defaultValue ? arguments.DefaultValueBool : arguments.ValueBool;
        }

        private static AnimationResult Ok(string message, AnimationData data = null)
        {
            return new AnimationResult { Success = true, Message = message, Data = data };
        }

        private static AnimationResult Fail(string message)
        {
            return new AnimationResult { Success = false, Message = message };
        }

        [Serializable]
        private sealed class AnimationArguments
        {
            public string Action, Target, SearchMethod, ClipPath, ControllerPath;
            public string Name, StateName, FromState, ToState, ParameterName, ParameterType;
            public string PropertyPath, Property, Type, RelativePath, LayerName, BlendingMode;
            public string BlendParameter, BlendParameterX, BlendParameterY, BlendType;
            public string FunctionName, StringParameter, Preset;
            public int Layer, LayerIndex, IntParameter, EventIndex;
            public bool HasLayer, HasLayerIndex, HasIntParameter, HasEventIndex;
            public float Duration, Speed, Length, FrameRate, ExitTime, Weight, Threshold, Time, FloatParameter, Amplitude;
            public bool HasDuration, HasSpeed, HasLength, HasFrameRate, HasExitTimeValue;
            public bool HasWeight, HasThreshold, HasTime, HasFloatParameter, HasAmplitude;
            public bool Enabled, Loop, IsDefault, HasExitTime;
            public bool HasEnabled, HasLoop, HasIsDefault, HasHasExitTime;
            public float[] Offset, Position;
            public bool HasOffset, HasPosition;
            public AnimationConditionArgument[] Conditions;
            public AnimationKeyArgument[] Keys;
            public bool HasValue, ValueBool, HasDefaultValue, DefaultValueBool;
            public string ValueKind, ValueString, DefaultValueKind, DefaultValueString;
            public float ValueFloat, DefaultValueFloat;
            public int ValueInt, DefaultValueInt;
        }

        [Serializable] private sealed class AnimationConditionArgument
        { public string Parameter, Mode; public float Threshold; }

        [Serializable] private sealed class AnimationKeyArgument
        {
            public float Time, Value, InTangent, OutTangent, InWeight, OutWeight;
            public bool IsVector, HasInTangent, HasOutTangent, HasInWeight, HasOutWeight;
            public float[] VectorValue;
        }

        [Serializable] private sealed class AnimationResult
        { public bool Success; public string Message; public AnimationData Data; }

        [Serializable] private sealed class AnimationData
        {
            public string Path, ClipPath, Name, Target, ControllerName, UpdateMode, CullingMode;
            public string StateName, BlendType, PropertyPath, Preset, WrapMode;
            public bool Enabled, HasController, ApplyRootMotion, HasMotion, IsLooping;
            public float Speed, Length, FrameRate;
            public int ParameterCount, LayerCount, LayerIndex, ConditionCount, ChildCount;
            public int CurveCount, KeyframeCount, EventCount;
            public ParameterRecord[] Parameters;
            public LayerRecord[] Layers;
            public ClipRecord[] Clips;
            public CurveRecord[] Curves;
            public EventRecord[] Events;
        }

        [Serializable] private sealed class ParameterRecord
        { public string Name, Type, Value; public float DefaultFloat; public int DefaultInt; public bool DefaultBool; }

        [Serializable] private sealed class LayerRecord
        {
            public int Index, CurrentStateHash, StateCount;
            public string Name;
            public float Weight, CurrentStateNormalizedTime, CurrentStateLength;
            public bool IsInTransition;
            public StateRecord[] States;
        }

        [Serializable] private sealed class StateRecord
        { public string Name, MotionName; public float Speed; public bool HasMotion, IsDefault; public int TransitionCount; }

        [Serializable] private sealed class ClipRecord
        { public string Name, WrapMode; public float Length, FrameRate; public bool IsLooping; }

        [Serializable] private sealed class CurveRecord
        { public string Path, PropertyName, Type; public int KeyCount; }

        [Serializable] private sealed class EventRecord
        { public float Time, FloatParameter; public string FunctionName, StringParameter; public int IntParameter; }
    }
}
#endif
