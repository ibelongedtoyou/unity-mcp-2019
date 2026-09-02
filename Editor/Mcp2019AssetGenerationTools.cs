#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace UnityMcp2019
{
    /// <summary>
    /// Imports files produced or staged by the Python MCP process. The public
    /// MCP provider calls remain outside Unity; this class only performs the
    /// deterministic AssetDatabase/importer portion of the pipeline.
    /// </summary>
    internal static class Mcp2019AssetGenerationTools
    {
        internal static string Execute(string argumentsJson)
        {
            Arguments arguments = string.IsNullOrEmpty(argumentsJson) || argumentsJson == "{}"
                ? new Arguments()
                : JsonUtility.FromJson<Arguments>(argumentsJson) ?? new Arguments();

            string kind = Require(arguments.Kind, "kind").ToLowerInvariant();
            string assetPath = NormalizeAssetPath(arguments.AssetPath);
            if (!File.Exists(ToAbsolutePath(assetPath)))
            {
                throw new FileNotFoundException("Generated asset file was not found.", assetPath);
            }

            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            AssetDatabase.ImportAsset(
                assetPath,
                ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);

            switch (kind)
            {
                case "image":
                    ImportImage(assetPath, arguments.Transparent);
                    break;
                case "audio":
                    ImportAudio(assetPath);
                    break;
                case "model":
                    ImportModel(assetPath, arguments.TargetSize, arguments.AnimationType);
                    break;
                default:
                    throw new ArgumentException("kind must be image, audio, or model.");
            }

            UnityEngine.Object imported = AssetDatabase.LoadMainAssetAtPath(assetPath);
            if (imported == null)
            {
                throw new InvalidOperationException(
                    "Unity did not create an imported asset for " + assetPath + ". " +
                    ImporterHint(assetPath));
            }

            Result result = new Result
            {
                Success = true,
                Message = "Imported generated " + kind + " asset: " + assetPath,
                Data = new ResultData
                {
                    AssetPath = assetPath,
                    AssetGuid = AssetDatabase.AssetPathToGUID(assetPath),
                    Kind = kind,
                    Type = imported.GetType().FullName
                }
            };
            return JsonUtility.ToJson(result);
        }

        private static void ImportImage(string assetPath, bool transparent)
        {
            TextureImporter importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
            if (importer == null)
            {
                throw new InvalidOperationException("Unity has no TextureImporter for " + assetPath + ".");
            }

            importer.textureType = TextureImporterType.Sprite;
            importer.spriteImportMode = SpriteImportMode.Single;
            importer.alphaIsTransparency = transparent;
            importer.sRGBTexture = true;
            importer.mipmapEnabled = false;
            importer.SaveAndReimport();
        }

        private static void ImportAudio(string assetPath)
        {
            AudioImporter importer = AssetImporter.GetAtPath(assetPath) as AudioImporter;
            if (importer == null)
            {
                throw new InvalidOperationException("Unity has no AudioImporter for " + assetPath + ".");
            }

            AudioClip clip = AssetDatabase.LoadAssetAtPath<AudioClip>(assetPath);
            float duration = clip == null ? 0f : clip.length;
            AudioImporterSampleSettings settings = importer.defaultSampleSettings;
            settings.loadType = duration > 30f
                ? AudioClipLoadType.Streaming
                : duration > 10f
                    ? AudioClipLoadType.CompressedInMemory
                    : AudioClipLoadType.DecompressOnLoad;
            importer.defaultSampleSettings = settings;
            importer.forceToMono = false;
            importer.SaveAndReimport();
        }

        private static void ImportModel(
            string assetPath,
            float requestedTargetSize,
            string requestedAnimationType)
        {
            string extension = Path.GetExtension(assetPath).ToLowerInvariant();
            if ((extension == ".glb" || extension == ".gltf") &&
                AssetImporter.GetAtPath(assetPath) == null)
            {
                throw new InvalidOperationException(
                    "GLB/glTF import requires a compatible importer package such as glTFast. " +
                    "Unity 2019 has no built-in glTF importer.");
            }

            ModelImporter importer = AssetImporter.GetAtPath(assetPath) as ModelImporter;
            if (importer == null)
            {
                throw new InvalidOperationException(
                    "Unity has no ModelImporter for " + assetPath + ". " + ImporterHint(assetPath));
            }

            importer.useFileScale = true;
            importer.animationType = ParseAnimationType(requestedAnimationType);
            importer.SaveAndReimport();

            float targetSize = requestedTargetSize > 0f ? requestedTargetSize : 1f;
            float currentSize = MeasureImportedModel(assetPath);
            if (currentSize > 0.000001f)
            {
                float multiplier = Mathf.Clamp(targetSize / currentSize, 0.0001f, 10000f);
                importer = AssetImporter.GetAtPath(assetPath) as ModelImporter;
                if (importer == null)
                {
                    throw new InvalidOperationException("ModelImporter disappeared while normalizing scale.");
                }

                importer.globalScale = Mathf.Clamp(importer.globalScale * multiplier, 0.0001f, 10000f);
                importer.SaveAndReimport();
            }
        }

        private static ModelImporterAnimationType ParseAnimationType(string value)
        {
            switch ((value ?? "none").Trim().ToLowerInvariant())
            {
                case "none":
                    return ModelImporterAnimationType.None;
                case "generic":
                    return ModelImporterAnimationType.Generic;
                case "humanoid":
                case "human":
                    return ModelImporterAnimationType.Human;
                case "legacy":
                    return ModelImporterAnimationType.Legacy;
                default:
                    throw new ArgumentException(
                        "animation_type must be none, generic, humanoid, or legacy.");
            }
        }

        private static float MeasureImportedModel(string assetPath)
        {
            GameObject source = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            if (source == null)
            {
                return 0f;
            }

            GameObject instance = UnityEngine.Object.Instantiate(source);
            instance.hideFlags = HideFlags.HideAndDontSave;
            try
            {
                Renderer[] renderers = instance.GetComponentsInChildren<Renderer>(true);
                if (renderers.Length > 0)
                {
                    Bounds bounds = renderers[0].bounds;
                    for (int index = 1; index < renderers.Length; index++)
                    {
                        bounds.Encapsulate(renderers[index].bounds);
                    }

                    Vector3 size = bounds.size;
                    return Mathf.Max(size.x, Mathf.Max(size.y, size.z));
                }

                Bounds? meshBounds = null;
                foreach (MeshFilter filter in instance.GetComponentsInChildren<MeshFilter>(true))
                {
                    if (filter.sharedMesh == null)
                    {
                        continue;
                    }

                    Bounds transformed = TransformBounds(filter.transform, filter.sharedMesh.bounds);
                    if (meshBounds.HasValue)
                    {
                        Bounds current = meshBounds.Value;
                        current.Encapsulate(transformed);
                        meshBounds = current;
                    }
                    else
                    {
                        meshBounds = transformed;
                    }
                }

                if (!meshBounds.HasValue)
                {
                    return 0f;
                }

                Vector3 meshSize = meshBounds.Value.size;
                return Mathf.Max(meshSize.x, Mathf.Max(meshSize.y, meshSize.z));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(instance);
            }
        }

        private static Bounds TransformBounds(Transform transform, Bounds localBounds)
        {
            Vector3 center = transform.TransformPoint(localBounds.center);
            Vector3 extents = localBounds.extents;
            Vector3 axisX = transform.TransformVector(extents.x, 0f, 0f);
            Vector3 axisY = transform.TransformVector(0f, extents.y, 0f);
            Vector3 axisZ = transform.TransformVector(0f, 0f, extents.z);
            Vector3 worldExtents = new Vector3(
                Mathf.Abs(axisX.x) + Mathf.Abs(axisY.x) + Mathf.Abs(axisZ.x),
                Mathf.Abs(axisX.y) + Mathf.Abs(axisY.y) + Mathf.Abs(axisZ.y),
                Mathf.Abs(axisX.z) + Mathf.Abs(axisY.z) + Mathf.Abs(axisZ.z));
            return new Bounds(center, worldExtents * 2f);
        }

        private static string NormalizeAssetPath(string value)
        {
            string path = Require(value, "asset_path").Replace('\\', '/').Trim();
            if (!path.StartsWith("Assets/", StringComparison.Ordinal) || path.Contains("../"))
            {
                throw new ArgumentException("asset_path must be a file below Assets/.");
            }

            string extension = Path.GetExtension(path).ToLowerInvariant();
            HashSet<string> allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ".png", ".jpg", ".jpeg", ".webp",
                ".wav", ".mp3", ".ogg", ".aiff", ".aif",
                ".fbx", ".obj", ".glb", ".gltf"
            };
            if (!allowed.Contains(extension))
            {
                throw new ArgumentException("Generated asset has an unsupported extension: " + extension);
            }

            string absolute = ToAbsolutePath(path);
            string projectAssets = Path.GetFullPath(Application.dataPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                Path.DirectorySeparatorChar;
            if (!absolute.StartsWith(projectAssets, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("asset_path escaped the Unity Assets folder.");
            }

            return path;
        }

        private static string ToAbsolutePath(string assetPath)
        {
            string projectRoot = Directory.GetParent(Application.dataPath).FullName;
            return Path.GetFullPath(Path.Combine(projectRoot, assetPath));
        }

        private static string Require(string value, string field)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException(field + " is required.");
            }

            return value.Trim();
        }

        private static string ImporterHint(string assetPath)
        {
            string extension = Path.GetExtension(assetPath).ToLowerInvariant();
            return extension == ".glb" || extension == ".gltf"
                ? "Install a Unity 2019-compatible glTF importer package first."
                : "Check the Unity Editor import log for this file.";
        }

        [Serializable]
        private sealed class Arguments
        {
            public string Kind;
            public string AssetPath;
            public bool Transparent;
            public float TargetSize;
            public string AnimationType;
        }

        [Serializable]
        private sealed class Result
        {
            public bool Success;
            public string Message;
            public ResultData Data;
        }

        [Serializable]
        private sealed class ResultData
        {
            public string AssetPath;
            public string AssetGuid;
            public string Kind;
            public string Type;
        }
    }
}
#endif
