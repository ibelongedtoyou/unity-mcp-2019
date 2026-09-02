#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace UnityMcp2019
{
    internal static class Mcp2019CameraTools
    {
        private static int forcedCameraId;
        private static float forcedCameraDepth;

        internal static string Execute(string argumentsJson)
        {
            CameraArguments arguments = string.IsNullOrEmpty(argumentsJson) || argumentsJson == "{}"
                ? new CameraArguments()
                : JsonUtility.FromJson<CameraArguments>(argumentsJson) ?? new CameraArguments();
            string action = (arguments.Action ?? string.Empty).Trim().ToLowerInvariant();
            try
            {
                CameraResult result;
                switch (action)
                {
                    case "ping": result = Ping(); break;
                    case "create_camera": result = CreateCamera(arguments); break;
                    case "list_cameras": result = ListCameras(); break;
                    case "set_target": result = SetTarget(arguments); break;
                    case "set_priority": result = SetPriority(arguments); break;
                    case "set_lens": result = SetLens(arguments); break;
                    case "ensure_brain": result = EnsureBrain(arguments); break;
                    case "get_brain_status": result = GetBrainStatus(); break;
                    case "set_body": result = SetPipelineComponent(arguments, arguments.BodyType, "Body"); break;
                    case "set_aim": result = SetPipelineComponent(arguments, arguments.AimType, "Aim"); break;
                    case "set_noise": result = SetPipelineComponent(arguments, "CinemachineBasicMultiChannelPerlin", "Noise"); break;
                    case "add_extension": result = AddExtension(arguments); break;
                    case "remove_extension": result = RemoveExtension(arguments); break;
                    case "set_blend": result = SetBlend(arguments); break;
                    case "force_camera": result = ForceCamera(arguments); break;
                    case "release_override": result = ReleaseOverride(); break;
                    case "screenshot": result = Screenshot(arguments, false); break;
                    case "screenshot_multiview": result = Screenshot(arguments, true); break;
                    default: result = Fail("Unknown camera action: " + action); break;
                }
                return JsonUtility.ToJson(result);
            }
            catch (Exception exception)
            {
                return JsonUtility.ToJson(Fail(exception.GetType().Name + ": " + exception.Message));
            }
        }

        private static CameraResult Ping()
        {
            Type cameraType = FindCinemachineCameraType();
            Type brainType = FindType("CinemachineBrain");
            return Ok("Camera system available.", new CameraData
            {
                CinemachineInstalled = cameraType != null && brainType != null,
                CinemachineCameraType = cameraType == null ? string.Empty : cameraType.FullName,
                CinemachineBrainType = brainType == null ? string.Empty : brainType.FullName,
                UnityVersion = Application.unityVersion
            });
        }

        private static CameraResult CreateCamera(CameraArguments arguments)
        {
            string name = string.IsNullOrEmpty(arguments.Name) ? "Camera" : arguments.Name;
            Type cinemachineType = FindCinemachineCameraType();
            bool useCinemachine = cinemachineType != null && !string.Equals(arguments.Preset, "basic", StringComparison.OrdinalIgnoreCase);
            GameObject gameObject = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(gameObject, "MCP Create Camera");
            if (arguments.HasPosition && arguments.Position != null && arguments.Position.Length >= 3)
                gameObject.transform.position = ToVector3(arguments.Position);
            if (arguments.HasRotation && arguments.Rotation != null && arguments.Rotation.Length >= 3)
                gameObject.transform.eulerAngles = ToVector3(arguments.Rotation);
            if (useCinemachine)
            {
                Component component = Undo.AddComponent(gameObject, cinemachineType);
                SetReflectionValue(component, "Priority", arguments.HasPriority ? (object)(int)arguments.Priority : 10);
                SetTransformReference(component, "Follow", arguments.Follow);
                SetTransformReference(component, "LookAt", arguments.LookAt);
                EditorUtility.SetDirty(gameObject);
                return Ok("Created Cinemachine camera '" + name + "'.", CameraObjectData(gameObject, null, true));
            }
            Camera camera = Undo.AddComponent<Camera>(gameObject);
            camera.fieldOfView = arguments.HasFieldOfView ? arguments.FieldOfView : 60f;
            camera.nearClipPlane = arguments.HasNearClipPlane ? arguments.NearClipPlane : 0.3f;
            camera.farClipPlane = arguments.HasFarClipPlane ? arguments.FarClipPlane : 1000f;
            camera.orthographic = arguments.HasOrthographic && arguments.Orthographic;
            if (arguments.HasOrthographicSize) camera.orthographicSize = arguments.OrthographicSize;
            GameObject follow = ResolveGameObject(arguments.Follow, "by_name");
            if (follow != null && !arguments.HasPosition) gameObject.transform.position = follow.transform.position + new Vector3(0f, 5f, -10f);
            GameObject lookAt = ResolveGameObject(arguments.LookAt, "by_name") ?? follow;
            if (lookAt != null) gameObject.transform.LookAt(lookAt.transform);
            EditorUtility.SetDirty(gameObject);
            return Ok("Created basic Camera '" + name + "'.", CameraObjectData(gameObject, camera, false));
        }

        private static CameraResult ListCameras()
        {
            List<CameraRecord> records = new List<CameraRecord>();
            foreach (Camera camera in Resources.FindObjectsOfTypeAll<Camera>())
            {
                if (camera == null || EditorUtility.IsPersistent(camera) || !camera.gameObject.scene.IsValid()) continue;
                records.Add(ToRecord(camera.gameObject, camera, false));
            }
            Type cinemachineType = FindCinemachineCameraType();
            if (cinemachineType != null)
            {
                foreach (Component component in Resources.FindObjectsOfTypeAll(cinemachineType))
                {
                    if (component == null || EditorUtility.IsPersistent(component) || !component.gameObject.scene.IsValid()) continue;
                    records.Add(ToRecord(component.gameObject, null, true));
                }
            }
            return Ok("Found " + records.Count + " camera(s).", new CameraData
            {
                Count = records.Count, Cameras = records.ToArray(),
                CinemachineInstalled = cinemachineType != null
            });
        }

        private static CameraResult SetTarget(CameraArguments arguments)
        {
            GameObject cameraObject = ResolveGameObject(arguments.Target, arguments.SearchMethod);
            if (cameraObject == null) return Fail("Target camera not found.");
            Component cinemachine = GetCinemachineCamera(cameraObject);
            if (cinemachine != null)
            {
                Undo.RecordObject(cinemachine, "MCP Set Cinemachine Target");
                if (!string.IsNullOrEmpty(arguments.Follow)) SetTransformReference(cinemachine, "Follow", arguments.Follow);
                if (!string.IsNullOrEmpty(arguments.LookAt)) SetTransformReference(cinemachine, "LookAt", arguments.LookAt);
                EditorUtility.SetDirty(cinemachine);
                return Ok("Cinemachine targets updated.", CameraObjectData(cameraObject, null, true));
            }
            Camera camera = cameraObject.GetComponent<Camera>();
            if (camera == null) return Fail("No Camera component on target.");
            GameObject lookAt = ResolveGameObject(!string.IsNullOrEmpty(arguments.LookAt) ? arguments.LookAt : arguments.Follow, "by_name");
            if (lookAt == null) return Fail("follow or lookAt target not found.");
            Undo.RecordObject(cameraObject.transform, "MCP Set Camera Target");
            cameraObject.transform.LookAt(lookAt.transform);
            EditorUtility.SetDirty(cameraObject.transform);
            return Ok("Camera target updated.", CameraObjectData(cameraObject, camera, false));
        }

        private static CameraResult SetPriority(CameraArguments arguments)
        {
            GameObject gameObject = ResolveGameObject(arguments.Target, arguments.SearchMethod);
            if (gameObject == null) return Fail("Target camera not found.");
            float priority = arguments.HasPriority ? arguments.Priority : 10f;
            Component cinemachine = GetCinemachineCamera(gameObject);
            if (cinemachine != null)
            {
                Undo.RecordObject(cinemachine, "MCP Set Cinemachine Priority");
                SetReflectionValue(cinemachine, "Priority", (int)priority);
                EditorUtility.SetDirty(cinemachine);
                return Ok("Cinemachine priority updated.", CameraObjectData(gameObject, null, true));
            }
            Camera camera = gameObject.GetComponent<Camera>();
            if (camera == null) return Fail("No Camera component on target.");
            Undo.RecordObject(camera, "MCP Set Camera Depth");
            camera.depth = priority;
            EditorUtility.SetDirty(camera);
            return Ok("Camera depth updated.", CameraObjectData(gameObject, camera, false));
        }

        private static CameraResult SetLens(CameraArguments arguments)
        {
            GameObject gameObject = ResolveGameObject(arguments.Target, arguments.SearchMethod);
            if (gameObject == null) return Fail("Target camera not found.");
            Camera camera = gameObject.GetComponent<Camera>();
            if (camera != null)
            {
                Undo.RecordObject(camera, "MCP Set Camera Lens");
                if (arguments.HasFieldOfView) camera.fieldOfView = arguments.FieldOfView;
                if (arguments.HasNearClipPlane) camera.nearClipPlane = arguments.NearClipPlane;
                if (arguments.HasFarClipPlane) camera.farClipPlane = arguments.FarClipPlane;
                if (arguments.HasOrthographicSize) camera.orthographicSize = arguments.OrthographicSize;
                if (arguments.HasOrthographic) camera.orthographic = arguments.Orthographic;
                EditorUtility.SetDirty(camera);
                return Ok("Camera lens updated.", CameraObjectData(gameObject, camera, false));
            }
            Component cinemachine = GetCinemachineCamera(gameObject);
            if (cinemachine == null) return Fail("No Camera or Cinemachine camera component on target.");
            SerializedObject serialized = new SerializedObject(cinemachine);
            SerializedProperty lens = serialized.FindProperty("m_Lens") ?? serialized.FindProperty("Lens");
            if (lens == null) return Fail("Cinemachine lens property was not found.");
            SetFloat(lens, "FieldOfView", arguments.FieldOfView, arguments.HasFieldOfView);
            SetFloat(lens, "NearClipPlane", arguments.NearClipPlane, arguments.HasNearClipPlane);
            SetFloat(lens, "FarClipPlane", arguments.FarClipPlane, arguments.HasFarClipPlane);
            SetFloat(lens, "OrthographicSize", arguments.OrthographicSize, arguments.HasOrthographicSize);
            SetFloat(lens, "Dutch", arguments.Dutch, arguments.HasDutch);
            serialized.ApplyModifiedProperties();
            return Ok("Cinemachine lens updated.", CameraObjectData(gameObject, null, true));
        }

        private static CameraResult EnsureBrain(CameraArguments arguments)
        {
            Type brainType = FindType("CinemachineBrain");
            if (brainType == null) return Fail("Cinemachine is not installed.");
            Component existing = Resources.FindObjectsOfTypeAll(brainType)
                .Cast<Component>().FirstOrDefault(item => item != null && !EditorUtility.IsPersistent(item) && item.gameObject.scene.IsValid());
            if (existing != null) return Ok("CinemachineBrain already exists.", CameraObjectData(existing.gameObject, existing.GetComponent<Camera>(), true));
            Camera camera = ResolveCamera(arguments.Camera) ?? Camera.main ?? Resources.FindObjectsOfTypeAll<Camera>().FirstOrDefault(item => !EditorUtility.IsPersistent(item));
            if (camera == null) return Fail("No Unity Camera found for CinemachineBrain.");
            Component brain = Undo.AddComponent(camera.gameObject, brainType);
            EditorUtility.SetDirty(camera.gameObject);
            return Ok("CinemachineBrain added.", CameraObjectData(camera.gameObject, camera, true));
        }

        private static CameraResult GetBrainStatus()
        {
            Type brainType = FindType("CinemachineBrain");
            if (brainType == null) return Fail("Cinemachine is not installed.");
            Component brain = Resources.FindObjectsOfTypeAll(brainType).Cast<Component>()
                .FirstOrDefault(item => item != null && !EditorUtility.IsPersistent(item) && item.gameObject.scene.IsValid());
            if (brain == null) return Fail("No CinemachineBrain found.");
            object active = GetReflectionValue(brain, "ActiveVirtualCamera");
            return Ok("CinemachineBrain status read.", new CameraData
            {
                Target = brain.gameObject.name, InstanceId = brain.gameObject.GetInstanceID(),
                ActiveCameraName = active == null ? string.Empty : Convert.ToString(GetReflectionValue(active, "Name")),
                IsBlending = ConvertToBool(GetReflectionValue(brain, "IsBlending"))
            });
        }

        private static CameraResult SetPipelineComponent(CameraArguments arguments, string typeName, string stage)
        {
            if (string.IsNullOrEmpty(typeName)) return Fail(stage + " component type is required.");
            GameObject gameObject = ResolveGameObject(arguments.Target, arguments.SearchMethod);
            if (gameObject == null || GetCinemachineCamera(gameObject) == null) return Fail("Target Cinemachine camera not found.");
            Type type = FindType(typeName);
            if (type == null || !typeof(Component).IsAssignableFrom(type)) return Fail("Cinemachine component type not found: " + typeName);
            Component component = gameObject.GetComponent(type) ?? Undo.AddComponent(gameObject, type);
            ApplyPatches(component, arguments.ComponentProperties);
            EditorUtility.SetDirty(gameObject);
            return Ok(stage + " component configured.", CameraObjectData(gameObject, null, true));
        }

        private static CameraResult AddExtension(CameraArguments arguments)
        {
            if (string.IsNullOrEmpty(arguments.ExtensionType)) return Fail("extensionType is required.");
            GameObject gameObject = ResolveGameObject(arguments.Target, arguments.SearchMethod);
            if (gameObject == null || GetCinemachineCamera(gameObject) == null) return Fail("Target Cinemachine camera not found.");
            Type type = FindType(arguments.ExtensionType);
            if (type == null || !typeof(Component).IsAssignableFrom(type)) return Fail("Extension type not found: " + arguments.ExtensionType);
            Component component = gameObject.GetComponent(type) ?? Undo.AddComponent(gameObject, type);
            ApplyPatches(component, arguments.ComponentProperties);
            EditorUtility.SetDirty(gameObject);
            return Ok("Cinemachine extension added.", CameraObjectData(gameObject, null, true));
        }

        private static CameraResult RemoveExtension(CameraArguments arguments)
        {
            GameObject gameObject = ResolveGameObject(arguments.Target, arguments.SearchMethod);
            Type type = FindType(arguments.ExtensionType);
            Component component = gameObject == null || type == null ? null : gameObject.GetComponent(type);
            if (component == null) return Fail("Extension not found.");
            Undo.DestroyObjectImmediate(component);
            return Ok("Cinemachine extension removed.", CameraObjectData(gameObject, null, true));
        }

        private static CameraResult SetBlend(CameraArguments arguments)
        {
            Type brainType = FindType("CinemachineBrain");
            Component brain = brainType == null ? null : Resources.FindObjectsOfTypeAll(brainType).Cast<Component>()
                .FirstOrDefault(item => item != null && !EditorUtility.IsPersistent(item) && item.gameObject.scene.IsValid());
            if (brain == null) return Fail("No CinemachineBrain found.");
            SerializedObject serialized = new SerializedObject(brain);
            SerializedProperty blend = serialized.FindProperty("m_DefaultBlend") ?? serialized.FindProperty("DefaultBlend");
            if (blend == null) return Fail("Default blend property was not found.");
            if (!string.IsNullOrEmpty(arguments.Style))
            {
                SerializedProperty style = blend.FindPropertyRelative("m_Style") ?? blend.FindPropertyRelative("Style");
                if (style != null && style.propertyType == SerializedPropertyType.Enum)
                {
                    int index = Array.FindIndex(style.enumNames, item => string.Equals(item, arguments.Style, StringComparison.OrdinalIgnoreCase));
                    if (index >= 0) style.enumValueIndex = index;
                }
            }
            SetFloat(blend, "Time", arguments.Duration, arguments.HasDuration);
            serialized.ApplyModifiedProperties();
            return Ok("Cinemachine default blend configured.");
        }

        private static CameraResult ForceCamera(CameraArguments arguments)
        {
            GameObject gameObject = ResolveGameObject(arguments.Target, arguments.SearchMethod);
            if (gameObject == null) return Fail("Target camera not found.");
            Camera camera = gameObject.GetComponent<Camera>();
            if (camera != null)
            {
                if (forcedCameraId != 0) ReleaseOverride();
                forcedCameraId = camera.GetInstanceID();
                forcedCameraDepth = camera.depth;
                Undo.RecordObject(camera, "MCP Force Camera");
                camera.depth = 10000f;
                camera.enabled = true;
                return Ok("Unity Camera forced by depth.", CameraObjectData(gameObject, camera, false));
            }
            Component cinemachine = GetCinemachineCamera(gameObject);
            if (cinemachine == null) return Fail("No camera component on target.");
            SetReflectionValue(cinemachine, "Priority", 999);
            forcedCameraId = cinemachine.GetInstanceID();
            return Ok("Cinemachine camera forced by priority.", CameraObjectData(gameObject, null, true));
        }

        private static CameraResult ReleaseOverride()
        {
            if (forcedCameraId == 0) return Ok("No active camera override.");
            UnityEngine.Object target = EditorUtility.InstanceIDToObject(forcedCameraId);
            Camera camera = target as Camera;
            if (camera != null) camera.depth = forcedCameraDepth;
            else
            {
                Component component = target as Component;
                if (component != null) SetReflectionValue(component, "Priority", 10);
            }
            forcedCameraId = 0;
            return Ok("Camera override released.");
        }

        private static CameraResult Screenshot(CameraArguments arguments, bool multiview)
        {
            Camera camera = string.Equals(arguments.CaptureSource, "scene_view", StringComparison.OrdinalIgnoreCase)
                ? (SceneView.lastActiveSceneView == null ? null : SceneView.lastActiveSceneView.camera)
                : ResolveCamera(arguments.Camera) ?? ResolveCamera(arguments.Target) ?? Camera.main;
            if (camera == null) camera = Resources.FindObjectsOfTypeAll<Camera>().FirstOrDefault(item => !EditorUtility.IsPersistent(item));
            if (camera == null) return Fail("No camera is available for capture.");
            int maxResolution = Mathf.Clamp(arguments.HasMaxResolution ? arguments.MaxResolution : 640, 64, 2048);
            int superSize = Mathf.Clamp(arguments.HasSuperSize ? arguments.SuperSize : 1, 1, 4);
            int width = Mathf.Clamp((camera.pixelWidth > 0 ? camera.pixelWidth : maxResolution) * superSize, 64, maxResolution);
            int height = Mathf.Clamp((camera.pixelHeight > 0 ? camera.pixelHeight : Mathf.RoundToInt(width * 0.5625f)) * superSize, 64, maxResolution);
            string folder = ResolveOutputFolder(arguments.OutputFolder);
            string baseName = string.IsNullOrEmpty(arguments.FileName)
                ? "screenshot_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") : Path.GetFileNameWithoutExtension(arguments.FileName);
            bool includeImage = multiview || (arguments.HasIncludeImage && arguments.IncludeImage);
            List<CameraImageRecord> images = new List<CameraImageRecord>();
            Vector3 originalPosition = camera.transform.position;
            Quaternion originalRotation = camera.transform.rotation;
            try
            {
                if (arguments.HasViewPosition) camera.transform.position = ToVector3(arguments.ViewPosition);
                if (arguments.HasViewRotation) camera.transform.eulerAngles = ToVector3(arguments.ViewRotation);
                GameObject viewTarget = ResolveGameObject(arguments.ViewTarget, "by_name");
                if (!arguments.HasViewRotation && viewTarget != null) camera.transform.LookAt(viewTarget.transform);
                if (multiview || string.Equals(arguments.Batch, "surround", StringComparison.OrdinalIgnoreCase))
                {
                    Vector3 center = viewTarget == null ? Vector3.zero : viewTarget.transform.position;
                    float distance = arguments.HasOrbitDistance ? Mathf.Max(0.1f, arguments.OrbitDistance) : 10f;
                    Vector3[] directions = { Vector3.forward, Vector3.back, Vector3.right, Vector3.left, Vector3.up, Vector3.down };
                    string[] names = { "front", "back", "right", "left", "top", "bottom" };
                    for (int index = 0; index < directions.Length; index++)
                    {
                        camera.transform.position = center - directions[index] * distance;
                        camera.transform.LookAt(center, index >= 4 ? Vector3.forward : Vector3.up);
                        images.Add(Capture(camera, width, height, folder, baseName + "_" + names[index] + ".png", includeImage));
                    }
                }
                else
                {
                    images.Add(Capture(camera, width, height, folder, baseName + ".png", includeImage));
                }
            }
            finally
            {
                camera.transform.position = originalPosition;
                camera.transform.rotation = originalRotation;
            }
            AssetDatabase.Refresh();
            return Ok("Captured " + images.Count + " camera image(s).", new CameraData
            {
                Count = images.Count, Images = images.ToArray(),
                ImageBase64 = images.Count == 1 ? images[0].Base64 : string.Empty,
                ImageMimeType = "image/png", Path = images.Count == 1 ? images[0].Path : string.Empty
            });
        }

        private static CameraImageRecord Capture(Camera camera, int width, int height, string folder, string fileName, bool includeImage)
        {
            RenderTexture previousTarget = camera.targetTexture;
            RenderTexture previousActive = RenderTexture.active;
            RenderTexture renderTexture = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
            Texture2D texture = new Texture2D(width, height, TextureFormat.RGB24, false);
            try
            {
                camera.targetTexture = renderTexture;
                camera.Render();
                RenderTexture.active = renderTexture;
                texture.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                texture.Apply();
                byte[] png = texture.EncodeToPNG();
                string absolute = Path.Combine(folder, fileName);
                File.WriteAllBytes(absolute, png);
                string project = Directory.GetParent(Application.dataPath).FullName.Replace('\\', '/');
                string normalized = absolute.Replace('\\', '/');
                string relative = normalized.StartsWith(project + "/", StringComparison.OrdinalIgnoreCase)
                    ? normalized.Substring(project.Length + 1) : normalized;
                return new CameraImageRecord
                {
                    Path = relative, Width = width, Height = height,
                    Base64 = includeImage ? Convert.ToBase64String(png) : string.Empty,
                    MimeType = "image/png"
                };
            }
            finally
            {
                camera.targetTexture = previousTarget;
                RenderTexture.active = previousActive;
                UnityEngine.Object.DestroyImmediate(texture);
                UnityEngine.Object.DestroyImmediate(renderTexture);
            }
        }

        private static string ResolveOutputFolder(string requested)
        {
            string project = Directory.GetParent(Application.dataPath).FullName;
            string path = string.IsNullOrWhiteSpace(requested) ? "Assets/Screenshots" : requested.Replace('\\', '/').Trim();
            string absolute = Path.IsPathRooted(path) ? Path.GetFullPath(path) : Path.GetFullPath(Path.Combine(project, path));
            string projectFull = Path.GetFullPath(project).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!absolute.StartsWith(projectFull, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Screenshot output_folder must stay inside the Unity project.");
            Directory.CreateDirectory(absolute);
            return absolute;
        }

        private static Camera ResolveCamera(string reference)
        {
            GameObject gameObject = ResolveGameObject(reference, "by_name");
            return gameObject == null ? null : gameObject.GetComponent<Camera>();
        }

        private static Component GetCinemachineCamera(GameObject gameObject)
        {
            Type type = FindCinemachineCameraType();
            return gameObject == null || type == null ? null : gameObject.GetComponent(type);
        }

        private static Type FindCinemachineCameraType()
        {
            return FindType("CinemachineCamera") ?? FindType("CinemachineVirtualCamera") ?? FindType("CinemachineFreeLook");
        }

        private static Type FindType(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type exact = assembly.GetType(name, false) ?? assembly.GetType("Cinemachine." + name, false) ?? assembly.GetType("Unity.Cinemachine." + name, false);
                if (exact != null) return exact;
                Type candidate;
                try { candidate = assembly.GetTypes().FirstOrDefault(item => item != null && item.Name == name); }
                catch (ReflectionTypeLoadException error) { candidate = error.Types.FirstOrDefault(item => item != null && item.Name == name); }
                if (candidate != null) return candidate;
            }
            return null;
        }

        private static GameObject ResolveGameObject(string target, string method)
        {
            if (string.IsNullOrWhiteSpace(target)) return null;
            int id;
            if (int.TryParse(target, out id)) return EditorUtility.InstanceIDToObject(id) as GameObject;
            IEnumerable<GameObject> objects = Resources.FindObjectsOfTypeAll<GameObject>()
                .Where(item => item != null && !EditorUtility.IsPersistent(item) && item.scene.IsValid());
            if (string.Equals(method, "by_path", StringComparison.OrdinalIgnoreCase))
                return objects.FirstOrDefault(item => HierarchyPath(item) == target);
            return objects.FirstOrDefault(item => string.Equals(item.name, target, StringComparison.OrdinalIgnoreCase));
        }

        private static string HierarchyPath(GameObject gameObject)
        {
            List<string> parts = new List<string>();
            for (Transform current = gameObject.transform; current != null; current = current.parent) parts.Add(current.name);
            parts.Reverse(); return string.Join("/", parts.ToArray());
        }

        private static void SetTransformReference(Component component, string property, string target)
        {
            if (component == null || string.IsNullOrEmpty(target)) return;
            GameObject gameObject = ResolveGameObject(target, "by_name");
            if (gameObject != null) SetReflectionValue(component, property, gameObject.transform);
        }

        private static object GetReflectionValue(object target, string name)
        {
            if (target == null) return null;
            Type type = target.GetType();
            PropertyInfo property = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (property != null && property.CanRead) return property.GetValue(target, null);
            FieldInfo field = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                ?? type.GetField("m_" + name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            return field == null ? null : field.GetValue(target);
        }

        private static void SetReflectionValue(object target, string name, object value)
        {
            if (target == null) return;
            Type type = target.GetType();
            PropertyInfo property = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (property != null && property.CanWrite)
            {
                try { property.SetValue(target, ConvertValue(value, property.PropertyType), null); return; }
                catch { }
            }
            FieldInfo field = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                ?? type.GetField("m_" + name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (field != null) field.SetValue(target, ConvertValue(value, field.FieldType));
        }

        private static object ConvertValue(object value, Type type)
        {
            if (value == null || type.IsInstanceOfType(value)) return value;
            if (type.IsEnum) return Enum.Parse(type, Convert.ToString(value), true);
            return Convert.ChangeType(value, type, System.Globalization.CultureInfo.InvariantCulture);
        }

        private static bool ConvertToBool(object value)
        { try { return value != null && Convert.ToBoolean(value); } catch { return false; } }

        private static void SetFloat(SerializedProperty parent, string name, float value, bool enabled)
        {
            if (!enabled || parent == null) return;
            SerializedProperty property = parent.FindPropertyRelative(name) ?? parent.FindPropertyRelative("m_" + name);
            if (property != null && property.propertyType == SerializedPropertyType.Float) property.floatValue = value;
        }

        private static void ApplyPatches(Component component, SerializedPatch[] patches)
        {
            if (component == null) return;
            SerializedObject serialized = new SerializedObject(component);
            foreach (SerializedPatch patch in patches ?? new SerializedPatch[0])
            {
                SerializedProperty property = serialized.FindProperty(patch.Path) ?? serialized.FindProperty("m_" + patch.Path);
                if (property == null) continue;
                if (patch.Kind == "bool" && property.propertyType == SerializedPropertyType.Boolean) property.boolValue = patch.BoolValue;
                else if ((patch.Kind == "float" || patch.Kind == "int") && property.propertyType == SerializedPropertyType.Float) property.floatValue = patch.Kind == "float" ? patch.FloatValue : patch.IntValue;
                else if (patch.Kind == "int" && property.propertyType == SerializedPropertyType.Integer) property.intValue = patch.IntValue;
                else if (patch.Kind == "string" && property.propertyType == SerializedPropertyType.String) property.stringValue = patch.StringValue;
            }
            serialized.ApplyModifiedProperties();
        }

        private static Vector3 ToVector3(float[] values) { return new Vector3(values[0], values[1], values[2]); }

        private static CameraData CameraObjectData(GameObject gameObject, Camera camera, bool cinemachine)
        {
            CameraData data = new CameraData
            {
                Target = gameObject.name, InstanceId = gameObject.GetInstanceID(),
                Cinemachine = cinemachine
            };
            if (camera != null)
            {
                data.FieldOfView = camera.fieldOfView; data.NearClipPlane = camera.nearClipPlane;
                data.FarClipPlane = camera.farClipPlane; data.Orthographic = camera.orthographic;
                data.OrthographicSize = camera.orthographicSize; data.Priority = camera.depth;
            }
            return data;
        }

        private static CameraRecord ToRecord(GameObject gameObject, Camera camera, bool cinemachine)
        {
            return new CameraRecord
            {
                Name = gameObject.name, InstanceId = gameObject.GetInstanceID(),
                Path = HierarchyPath(gameObject), Cinemachine = cinemachine,
                Enabled = camera == null || camera.enabled,
                Priority = camera == null ? 0f : camera.depth,
                FieldOfView = camera == null ? 0f : camera.fieldOfView
            };
        }

        private static CameraResult Ok(string message, CameraData data = null)
        { return new CameraResult { Success = true, Message = message, Data = data }; }
        private static CameraResult Fail(string message)
        { return new CameraResult { Success = false, Message = message }; }

        [Serializable] private sealed class CameraArguments
        {
            public string Action, Target, SearchMethod, Name, Preset, Follow, LookAt, ExtensionType, Style, Camera;
            public string CaptureSource, Batch, ViewTarget, OutputFolder, BodyType, AimType, FileName;
            public float FieldOfView, NearClipPlane, FarClipPlane, OrthographicSize, Priority, Duration, Dutch, OrbitDistance, OrbitFov;
            public bool HasFieldOfView, HasNearClipPlane, HasFarClipPlane, HasOrthographicSize, HasPriority, HasDuration, HasDutch, HasOrbitDistance, HasOrbitFov;
            public bool Orthographic, IncludeImage; public bool HasOrthographic, HasIncludeImage;
            public int SuperSize, MaxResolution, OrbitAngles; public bool HasSuperSize, HasMaxResolution, HasOrbitAngles;
            public float[] Position, Rotation, ViewPosition, ViewRotation, OrbitElevations;
            public bool HasPosition, HasRotation, HasViewPosition, HasViewRotation;
            public SerializedPatch[] ComponentProperties;
        }
        [Serializable] private sealed class SerializedPatch
        { public string Path, Kind, StringValue; public bool BoolValue; public int IntValue; public float FloatValue; }
        [Serializable] private sealed class CameraResult { public bool Success; public string Message; public CameraData Data; }
        [Serializable] private sealed class CameraData
        {
            public string UnityVersion, CinemachineCameraType, CinemachineBrainType, Target, ActiveCameraName, Path, ImageBase64, ImageMimeType;
            public bool CinemachineInstalled, Cinemachine, IsBlending, Orthographic;
            public int InstanceId, Count; public float FieldOfView, NearClipPlane, FarClipPlane, OrthographicSize, Priority;
            public CameraRecord[] Cameras; public CameraImageRecord[] Images;
        }
        [Serializable] private sealed class CameraRecord
        { public string Name, Path; public int InstanceId; public bool Cinemachine, Enabled; public float Priority, FieldOfView; }
        [Serializable] private sealed class CameraImageRecord
        { public string Path, Base64, MimeType; public int Width, Height; }
    }
}
#endif
