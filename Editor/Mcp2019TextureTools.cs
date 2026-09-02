#if UNITY_EDITOR
using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace UnityMcp2019
{
    internal static class Mcp2019TextureTools
    {
        internal static string Execute(string argumentsJson)
        {
            TextureArguments arguments = string.IsNullOrEmpty(argumentsJson) || argumentsJson == "{}"
                ? new TextureArguments()
                : JsonUtility.FromJson<TextureArguments>(argumentsJson) ?? new TextureArguments();
            string action = Require(arguments.Action, "action").ToLowerInvariant();
            string path = NormalizeTexturePath(arguments.Path);
            if (action == "delete")
            {
                if (!AssetDatabase.DeleteAsset(path))
                {
                    throw new InvalidOperationException("Unity failed to delete texture: " + path);
                }

                return JsonUtility.ToJson(TextureResult.SuccessResult(action, path, 0, 0));
            }

            if (action == "set_import_settings")
            {
                ApplyImportSettings(path, arguments, false);
                Texture2D configured = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
                return JsonUtility.ToJson(TextureResult.SuccessResult(
                    action,
                    path,
                    configured == null ? 0 : configured.width,
                    configured == null ? 0 : configured.height));
            }

            Texture2D texture;
            if (action == "create" || action == "create_sprite")
            {
                if (!string.IsNullOrEmpty(arguments.ImagePath))
                {
                    texture = LoadExternalOrAssetImage(arguments.ImagePath);
                }
                else
                {
                    texture = CreateTexture(arguments.Width, arguments.Height, PatchColor(arguments.FillColor));
                    if (!string.IsNullOrEmpty(arguments.PixelsBase64))
                    {
                        ApplyRawPixels(texture, arguments.PixelsBase64, 0, 0, texture.width, texture.height);
                    }
                }
            }
            else
            {
                texture = LoadEditableTexture(path);
            }

            switch (action)
            {
                case "create":
                case "create_sprite":
                    break;
                case "modify":
                    if (!string.IsNullOrEmpty(arguments.PixelsBase64))
                    {
                        ApplyRawPixels(texture, arguments.PixelsBase64, 0, 0, texture.width, texture.height);
                    }

                    if (arguments.RegionWidth > 0 && arguments.RegionHeight > 0)
                    {
                        if (!string.IsNullOrEmpty(arguments.RegionPixelsBase64))
                        {
                            ApplyRawPixels(
                                texture,
                                arguments.RegionPixelsBase64,
                                arguments.RegionX,
                                arguments.RegionY,
                                arguments.RegionWidth,
                                arguments.RegionHeight);
                        }
                        else
                        {
                            FillRegion(
                                texture,
                                arguments.RegionX,
                                arguments.RegionY,
                                arguments.RegionWidth,
                                arguments.RegionHeight,
                                PatchColor(arguments.RegionColor));
                        }
                    }

                    break;
                case "apply_pattern":
                    ApplyPattern(texture, arguments);
                    break;
                case "apply_gradient":
                    ApplyGradient(texture, arguments);
                    break;
                case "apply_noise":
                    ApplyNoise(texture, arguments);
                    break;
                default:
                    UnityEngine.Object.DestroyImmediate(texture);
                    throw new ArgumentException("Unsupported manage_texture action: " + action);
            }

            texture.Apply(false, false);
            WriteTexture(path, texture);
            bool sprite = action == "create_sprite" || arguments.AsSprite;
            ApplyImportSettings(path, arguments, sprite);
            int width = texture.width;
            int height = texture.height;
            UnityEngine.Object.DestroyImmediate(texture);
            return JsonUtility.ToJson(TextureResult.SuccessResult(action, path, width, height));
        }

        private static Texture2D CreateTexture(int width, int height, Color color)
        {
            width = Math.Max(1, Math.Min(width <= 0 ? 64 : width, 4096));
            height = Math.Max(1, Math.Min(height <= 0 ? 64 : height, 4096));
            Texture2D texture = new Texture2D(width, height, TextureFormat.RGBA32, false, false);
            Color32[] pixels = new Color32[width * height];
            Color32 fill = color;
            for (int index = 0; index < pixels.Length; index++)
            {
                pixels[index] = fill;
            }

            texture.SetPixels32(pixels);
            return texture;
        }

        private static Texture2D LoadEditableTexture(string assetPath)
        {
            string fullPath = FullProjectPath(assetPath);
            if (!File.Exists(fullPath))
            {
                throw new FileNotFoundException("Texture file was not found: " + assetPath);
            }

            Texture2D texture = new Texture2D(2, 2, TextureFormat.RGBA32, false, false);
            if (!texture.LoadImage(File.ReadAllBytes(fullPath), false))
            {
                UnityEngine.Object.DestroyImmediate(texture);
                throw new InvalidOperationException("Unity could not decode texture: " + assetPath);
            }

            return texture;
        }

        private static Texture2D LoadExternalOrAssetImage(string source)
        {
            string normalized = source.Replace('\\', '/');
            string fullPath;
            if (normalized.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
            {
                fullPath = FullProjectPath(normalized);
            }
            else
            {
                fullPath = Path.GetFullPath(source);
                string projectRoot = Directory.GetParent(Application.dataPath).FullName;
                if (!fullPath.StartsWith(projectRoot, StringComparison.OrdinalIgnoreCase))
                {
                    throw new ArgumentException("image_path must stay inside the Unity project.");
                }
            }

            if (!File.Exists(fullPath))
            {
                throw new FileNotFoundException("Source image was not found: " + source);
            }

            Texture2D texture = new Texture2D(2, 2, TextureFormat.RGBA32, false, false);
            if (!texture.LoadImage(File.ReadAllBytes(fullPath), false))
            {
                UnityEngine.Object.DestroyImmediate(texture);
                throw new InvalidOperationException("Unity could not decode source image: " + source);
            }

            return texture;
        }

        private static void ApplyRawPixels(
            Texture2D texture,
            string encoded,
            int x,
            int y,
            int width,
            int height)
        {
            byte[] bytes;
            try { bytes = Convert.FromBase64String(encoded); }
            catch (FormatException error)
            {
                throw new ArgumentException("Pixel payload is not valid base64.", error);
            }

            int expected = checked(width * height * 4);
            if (bytes.Length != expected)
            {
                throw new ArgumentException(
                    "Pixel payload length " + bytes.Length + " does not match RGBA region " +
                    width + "x" + height + " (expected " + expected + ").");
            }

            if (x < 0 || y < 0 || x + width > texture.width || y + height > texture.height)
            {
                throw new ArgumentException("Pixel region is outside the texture bounds.");
            }

            Color32[] pixels = new Color32[width * height];
            for (int index = 0; index < pixels.Length; index++)
            {
                int offset = index * 4;
                pixels[index] = new Color32(
                    bytes[offset], bytes[offset + 1], bytes[offset + 2], bytes[offset + 3]);
            }

            texture.SetPixels32(x, y, width, height, pixels);
        }

        private static void FillRegion(
            Texture2D texture,
            int x,
            int y,
            int width,
            int height,
            Color color)
        {
            if (x < 0 || y < 0 || x + width > texture.width || y + height > texture.height)
            {
                throw new ArgumentException("Fill region is outside the texture bounds.");
            }

            Color[] colors = new Color[width * height];
            for (int index = 0; index < colors.Length; index++) colors[index] = color;
            texture.SetPixels(x, y, width, height, colors);
        }

        private static void ApplyPattern(Texture2D texture, TextureArguments arguments)
        {
            Color first = arguments.Palette != null && arguments.Palette.Length > 0
                ? PatchColor(arguments.Palette[0])
                : Color.white;
            Color second = arguments.Palette != null && arguments.Palette.Length > 1
                ? PatchColor(arguments.Palette[1])
                : Color.black;
            int size = Math.Max(1, arguments.PatternSize <= 0 ? 8 : arguments.PatternSize);
            string pattern = string.IsNullOrEmpty(arguments.Pattern)
                ? "checkerboard"
                : arguments.Pattern.ToLowerInvariant();
            Color[] pixels = new Color[texture.width * texture.height];
            for (int y = 0; y < texture.height; y++)
            {
                for (int x = 0; x < texture.width; x++)
                {
                    bool alternate;
                    switch (pattern)
                    {
                        case "stripes":
                        case "stripes_v":
                            alternate = (x / size) % 2 != 0;
                            break;
                        case "stripes_h":
                            alternate = (y / size) % 2 != 0;
                            break;
                        case "stripes_diag":
                            alternate = ((x + y) / size) % 2 != 0;
                            break;
                        case "dots":
                            float dx = x % size - size * 0.5f;
                            float dy = y % size - size * 0.5f;
                            alternate = dx * dx + dy * dy <= size * size * 0.12f;
                            break;
                        case "grid":
                            alternate = x % size == 0 || y % size == 0;
                            break;
                        case "brick":
                            int shiftedX = x + ((y / size) % 2) * (size / 2);
                            alternate = shiftedX % size == 0 || y % size == 0;
                            break;
                        case "checkerboard":
                            alternate = ((x / size) + (y / size)) % 2 != 0;
                            break;
                        default:
                            throw new ArgumentException("Unsupported texture pattern: " + pattern);
                    }

                    pixels[y * texture.width + x] = alternate ? second : first;
                }
            }

            texture.SetPixels(pixels);
        }

        private static void ApplyGradient(Texture2D texture, TextureArguments arguments)
        {
            Color first = arguments.Palette != null && arguments.Palette.Length > 0
                ? PatchColor(arguments.Palette[0])
                : Color.black;
            Color second = arguments.Palette != null && arguments.Palette.Length > 1
                ? PatchColor(arguments.Palette[1])
                : Color.white;
            bool radial = string.Equals(
                arguments.GradientType,
                "radial",
                StringComparison.OrdinalIgnoreCase);
            float radians = arguments.GradientAngle * Mathf.Deg2Rad;
            Vector2 direction = new Vector2(Mathf.Cos(radians), Mathf.Sin(radians));
            Color[] pixels = new Color[texture.width * texture.height];
            for (int y = 0; y < texture.height; y++)
            {
                for (int x = 0; x < texture.width; x++)
                {
                    float nx = texture.width <= 1 ? 0f : x / (float)(texture.width - 1);
                    float ny = texture.height <= 1 ? 0f : y / (float)(texture.height - 1);
                    float t = radial
                        ? Mathf.Clamp01(Vector2.Distance(new Vector2(nx, ny), new Vector2(0.5f, 0.5f)) * 2f)
                        : Mathf.Clamp01(Vector2.Dot(new Vector2(nx - 0.5f, ny - 0.5f), direction) + 0.5f);
                    pixels[y * texture.width + x] = Color.Lerp(first, second, t);
                }
            }

            texture.SetPixels(pixels);
        }

        private static void ApplyNoise(Texture2D texture, TextureArguments arguments)
        {
            Color first = arguments.Palette != null && arguments.Palette.Length > 0
                ? PatchColor(arguments.Palette[0])
                : Color.black;
            Color second = arguments.Palette != null && arguments.Palette.Length > 1
                ? PatchColor(arguments.Palette[1])
                : Color.white;
            float scale = Mathf.Max(0.0001f, arguments.NoiseScale <= 0f ? 0.1f : arguments.NoiseScale);
            int octaves = Mathf.Clamp(arguments.Octaves <= 0 ? 1 : arguments.Octaves, 1, 8);
            Color[] pixels = new Color[texture.width * texture.height];
            for (int y = 0; y < texture.height; y++)
            {
                for (int x = 0; x < texture.width; x++)
                {
                    float amplitude = 1f;
                    float frequency = 1f;
                    float total = 0f;
                    float weight = 0f;
                    for (int octave = 0; octave < octaves; octave++)
                    {
                        total += Mathf.PerlinNoise(x * scale * frequency, y * scale * frequency) * amplitude;
                        weight += amplitude;
                        amplitude *= 0.5f;
                        frequency *= 2f;
                    }

                    pixels[y * texture.width + x] = Color.Lerp(first, second, total / weight);
                }
            }

            texture.SetPixels(pixels);
        }

        private static void WriteTexture(string assetPath, Texture2D texture)
        {
            string extension = Path.GetExtension(assetPath).ToLowerInvariant();
            if (extension != ".png")
            {
                throw new ArgumentException("Unity 2019 procedural texture output must use .png.");
            }

            EnsureFolder(Path.GetDirectoryName(assetPath).Replace('\\', '/'));
            File.WriteAllBytes(FullProjectPath(assetPath), texture.EncodeToPNG());
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport);
        }

        private static void ApplyImportSettings(
            string assetPath,
            TextureArguments arguments,
            bool forceSprite)
        {
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport);
            TextureImporter importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
            if (importer == null)
            {
                throw new InvalidOperationException("TextureImporter was not found: " + assetPath);
            }

            if (forceSprite || arguments.AsSprite)
            {
                importer.textureType = TextureImporterType.Sprite;
                importer.spriteImportMode = SpriteImportMode.Single;
                importer.spritePixelsPerUnit = arguments.SpritePixelsPerUnit <= 0f
                    ? 100f
                    : arguments.SpritePixelsPerUnit;
                Vector4 pivot = PatchVectorOrDefault(arguments.SpritePivot, new Vector4(0.5f, 0.5f));
                importer.spritePivot = new Vector2(pivot.x, pivot.y);
            }

            SerializedPatch[] settings = arguments.ImportSettings ?? new SerializedPatch[0];
            for (int index = 0; index < settings.Length; index++)
            {
                SerializedPatch patch = settings[index];
                string key = (patch.Path ?? string.Empty).ToLowerInvariant();
                switch (key)
                {
                    case "texture_type":
                        TextureImporterType textureType;
                        if (!Enum.TryParse(patch.StringValue, true, out textureType))
                            throw new ArgumentException("Unknown texture_type: " + patch.StringValue);
                        importer.textureType = textureType;
                        break;
                    case "srgb": importer.sRGBTexture = patch.BoolValue; break;
                    case "alpha_is_transparency": importer.alphaIsTransparency = patch.BoolValue; break;
                    case "readable": importer.isReadable = patch.BoolValue; break;
                    case "generate_mipmaps": importer.mipmapEnabled = patch.BoolValue; break;
                    case "aniso_level": importer.anisoLevel = patch.IntValue; break;
                    case "max_texture_size": importer.maxTextureSize = patch.IntValue; break;
                    case "wrap_mode":
                        TextureWrapMode wrap;
                        if (!Enum.TryParse(patch.StringValue, true, out wrap))
                            throw new ArgumentException("Unknown wrap_mode: " + patch.StringValue);
                        importer.wrapMode = wrap;
                        break;
                    case "filter_mode":
                        FilterMode filter;
                        if (!Enum.TryParse(patch.StringValue, true, out filter))
                            throw new ArgumentException("Unknown filter_mode: " + patch.StringValue);
                        importer.filterMode = filter;
                        break;
                    case "sprite_pixels_per_unit": importer.spritePixelsPerUnit = patch.FloatValue; break;
                    default:
                        throw new ArgumentException("Unsupported texture import setting: " + patch.Path);
                }
            }

            importer.SaveAndReimport();
        }

        private static Color PatchColor(SerializedPatch patch)
        {
            Vector4 value = PatchVectorOrDefault(patch, Vector4.one);
            float maximum = Mathf.Max(value.x, value.y, value.z, value.w);
            if (maximum > 1f)
            {
                value /= 255f;
            }

            return new Color(value.x, value.y, value.z, patch != null && patch.VectorLength >= 4 ? value.w : 1f);
        }

        private static Vector4 PatchVectorOrDefault(SerializedPatch patch, Vector4 fallback)
        {
            if (patch == null || patch.FloatValues == null || patch.FloatValues.Length < 2)
            {
                return fallback;
            }

            return new Vector4(
                patch.FloatValues[0], patch.FloatValues[1],
                patch.FloatValues.Length > 2 ? patch.FloatValues[2] : fallback.z,
                patch.FloatValues.Length > 3 ? patch.FloatValues[3] : fallback.w);
        }

        private static string NormalizeTexturePath(string value)
        {
            string path = Require(value, "path").Replace('\\', '/').Trim();
            if (!path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
            {
                path = "Assets/" + path.TrimStart('/');
            }

            if (path.Contains("../") || path.IndexOf(':') >= 0)
            {
                throw new ArgumentException("Texture path must stay under Assets/.");
            }

            return path;
        }

        private static string FullProjectPath(string assetPath)
        {
            string projectRoot = Directory.GetParent(Application.dataPath).FullName;
            return Path.GetFullPath(Path.Combine(
                projectRoot,
                assetPath.Replace('/', Path.DirectorySeparatorChar)));
        }

        private static void EnsureFolder(string path)
        {
            if (string.IsNullOrEmpty(path) || path == "Assets" || AssetDatabase.IsValidFolder(path)) return;
            string parent = Path.GetDirectoryName(path).Replace('\\', '/');
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
        }

        private static string Require(string value, string field)
        {
            if (string.IsNullOrEmpty(value) || value.Trim().Length == 0)
                throw new ArgumentException(field + " must be a non-empty string.");
            return value.Trim();
        }

        [Serializable] private sealed class TextureArguments
        {
            public string Action;
            public string Path;
            public int Width = 64;
            public int Height = 64;
            public SerializedPatch FillColor;
            public string Pattern;
            public SerializedPatch[] Palette;
            public int PatternSize = 8;
            public string PixelsBase64;
            public string ImagePath;
            public string GradientType;
            public float GradientAngle;
            public float NoiseScale = 0.1f;
            public int Octaves = 1;
            public int RegionX;
            public int RegionY;
            public int RegionWidth;
            public int RegionHeight;
            public SerializedPatch RegionColor;
            public string RegionPixelsBase64;
            public bool AsSprite;
            public float SpritePixelsPerUnit = 100f;
            public SerializedPatch SpritePivot;
            public SerializedPatch[] ImportSettings;
        }

        [Serializable] private sealed class SerializedPatch
        {
            public string Path;
            public string Kind;
            public bool BoolValue;
            public int IntValue;
            public float FloatValue;
            public string StringValue;
            public int VectorLength;
            public float[] FloatValues;
        }

        [Serializable] private sealed class TextureResult
        {
            public bool Success;
            public string Action;
            public string Path;
            public int Width;
            public int Height;
            public string Guid;
            public string Message;

            public static TextureResult SuccessResult(string action, string path, int width, int height)
            {
                return new TextureResult
                {
                    Success = true,
                    Action = action,
                    Path = path,
                    Width = width,
                    Height = height,
                    Guid = AssetDatabase.AssetPathToGUID(path),
                    Message = "Texture action completed."
                };
            }
        }
    }
}
#endif
