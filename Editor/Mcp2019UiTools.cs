#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace UnityMcp2019
{
    internal static class Mcp2019UiTools
    {
        private const BindingFlags AnyMember = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        private static string pendingCapturePath;

        internal static string Execute(string argumentsJson)
        {
            UiArguments a = string.IsNullOrEmpty(argumentsJson) || argumentsJson == "{}"
                ? new UiArguments()
                : JsonUtility.FromJson<UiArguments>(argumentsJson) ?? new UiArguments();
            string action = (a.Action ?? string.Empty).Trim().ToLowerInvariant();
            try
            {
                UiResult result;
                switch (action)
                {
                    case "ping": result = Ping(); break;
                    case "create": result = CreateFile(a); break;
                    case "read": result = ReadFile(a); break;
                    case "update": result = UpdateFile(a); break;
                    case "delete": result = DeleteFile(a); break;
                    case "attach_ui_document": result = AttachDocument(a); break;
                    case "detach_ui_document": result = DetachDocument(a); break;
                    case "create_panel_settings": result = CreatePanelSettings(a); break;
                    case "update_panel_settings": result = UpdatePanelSettings(a); break;
                    case "get_visual_tree": result = GetVisualTree(a); break;
                    case "render_ui": result = RenderUi(a); break;
                    case "link_stylesheet": result = LinkStylesheet(a); break;
                    case "list": result = ListAssets(a); break;
                    case "modify_visual_element": result = ModifyVisualElement(a); break;
                    default: return JsonUtility.ToJson(Fail("Unknown manage_ui action: " + action));
                }
                return JsonUtility.ToJson(result);
            }
            catch (Exception ex)
            {
                return JsonUtility.ToJson(Fail(ex.GetBaseException().Message));
            }
        }

        private static UiResult Ping()
        {
            Type document = FindType("UnityEngine.UIElements.UIDocument");
            Type settings = FindType("UnityEngine.UIElements.PanelSettings");
            return Ok("manage_ui is available.", new UiData
            {
                UnityVersion = Application.unityVersion,
                HasVisualTreeAssets = typeof(VisualTreeAsset) != null,
                HasRuntimeUiDocument = document != null,
                HasPanelSettings = settings != null,
                CompatibilityMode = document == null ? "Unity2019EditorAssets" : "RuntimeUIToolkit",
            });
        }

        private static UiResult CreateFile(UiArguments a)
        {
            string path = ValidateUiPath(a.Path);
            if (File.Exists(FullAssetPath(path))) throw new InvalidOperationException("UI asset already exists: " + path);
            string contents = DecodeContents(a);
            ValidateContents(path, contents);
            EnsureFolder(Path.GetDirectoryName(path).Replace('\\', '/'));
            File.WriteAllText(FullAssetPath(path), contents, new UTF8Encoding(false));
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
            return Ok("Created UI asset.", FileData(path, contents));
        }

        private static UiResult ReadFile(UiArguments a)
        {
            string path = ValidateUiPath(a.Path);
            string full = FullAssetPath(path);
            if (!File.Exists(full)) throw new FileNotFoundException("UI asset was not found", path);
            string contents = File.ReadAllText(full, Encoding.UTF8);
            return Ok("Read UI asset.", FileData(path, contents));
        }

        private static UiResult UpdateFile(UiArguments a)
        {
            string path = ValidateUiPath(a.Path);
            string full = FullAssetPath(path);
            if (!File.Exists(full)) throw new FileNotFoundException("UI asset was not found", path);
            string contents = DecodeContents(a);
            ValidateContents(path, contents);
            File.WriteAllText(full, contents, new UTF8Encoding(false));
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
            return Ok("Updated UI asset.", FileData(path, contents));
        }

        private static UiResult DeleteFile(UiArguments a)
        {
            string path = ValidateUiPath(a.Path);
            if (!File.Exists(FullAssetPath(path))) throw new FileNotFoundException("UI asset was not found", path);
            if (!AssetDatabase.DeleteAsset(path)) throw new InvalidOperationException("AssetDatabase failed to delete: " + path);
            return Ok("Deleted UI asset.", new UiData { Path = path });
        }

        private static UiResult LinkStylesheet(UiArguments a)
        {
            string path = ValidateUiPath(a.Path);
            if (!path.EndsWith(".uxml", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("path must point to a .uxml file.");
            string stylesheet = ValidateUiPath(a.Stylesheet);
            if (!stylesheet.EndsWith(".uss", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("stylesheet must point to a .uss file.");
            string full = FullAssetPath(path);
            if (!File.Exists(full)) throw new FileNotFoundException("UXML file was not found", path);
            if (!File.Exists(FullAssetPath(stylesheet))) throw new FileNotFoundException("USS file was not found", stylesheet);
            string contents = File.ReadAllText(full, Encoding.UTF8);
            if (contents.Contains("src=\"" + stylesheet + "\"") || contents.Contains("src=\"project://database/" + stylesheet + "\""))
                return Ok("Stylesheet already linked.", new UiData { Path = path, Stylesheet = stylesheet, AlreadyLinked = true });
            int open = FindUxmlOpeningTagEnd(contents);
            if (open < 0) throw new InvalidOperationException("Could not find an opening <ui:UXML> or <UXML> element.");
            contents = contents.Insert(open, "\n    <ui:Style src=\"project://database/" + stylesheet + "\" />");
            File.WriteAllText(full, contents, new UTF8Encoding(false));
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
            return Ok("Stylesheet linked.", new UiData { Path = path, Stylesheet = stylesheet });
        }

        private static UiResult ListAssets(UiArguments a)
        {
            string scope = string.IsNullOrEmpty(a.Path) ? "Assets" : ValidateAssetScope(a.Path);
            string filter = (a.FilterType ?? string.Empty).Trim().ToLowerInvariant();
            List<UiAssetRecord> assets = new List<UiAssetRecord>();
            if (string.IsNullOrEmpty(filter) || filter == "uxml")
            {
                foreach (string guid in AssetDatabase.FindAssets("t:VisualTreeAsset", new[] { scope }))
                {
                    string path = AssetDatabase.GUIDToAssetPath(guid);
                    if (path.EndsWith(".uxml", StringComparison.OrdinalIgnoreCase)) assets.Add(AssetRecord(path, "uxml"));
                }
            }
            if (string.IsNullOrEmpty(filter) || filter == "uss")
            {
                foreach (string guid in AssetDatabase.FindAssets("t:StyleSheet", new[] { scope }))
                {
                    string path = AssetDatabase.GUIDToAssetPath(guid);
                    if (path.EndsWith(".uss", StringComparison.OrdinalIgnoreCase)) assets.Add(AssetRecord(path, "uss"));
                }
            }
            Type panelType = FindType("UnityEngine.UIElements.PanelSettings");
            if ((string.IsNullOrEmpty(filter) || filter == "panelsettings") && panelType != null)
            {
                foreach (string guid in AssetDatabase.FindAssets("t:PanelSettings", new[] { scope })) assets.Add(AssetRecord(AssetDatabase.GUIDToAssetPath(guid), "PanelSettings"));
            }
            assets = assets.GroupBy(x => x.Path, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).OrderBy(x => x.Path, StringComparer.OrdinalIgnoreCase).ToList();
            int pageSize = a.HasPageSize ? Mathf.Clamp(a.PageSize, 1, 500) : 50;
            int pageNumber = a.HasPageNumber ? Math.Max(1, a.PageNumber) : 1;
            int start = Math.Min(assets.Count, (pageNumber - 1) * pageSize);
            UiAssetRecord[] page = assets.Skip(start).Take(pageSize).ToArray();
            return Ok("UI assets listed.", new UiData
            {
                Assets = page, Count = page.Length, Total = assets.Count, PageSize = pageSize, PageNumber = pageNumber,
                HasMore = start + page.Length < assets.Count,
            });
        }

        private static UiResult AttachDocument(UiArguments a)
        {
            Type documentType = RequireRuntimeType("UnityEngine.UIElements.UIDocument", "UIDocument is not present in Unity 2019. UXML/USS asset operations remain available, but runtime UI Toolkit attachment requires Unity 2021.1 or a compatible package.");
            GameObject go = FindTarget(a.Target);
            Component document = go.GetComponent(documentType) ?? Undo.AddComponent(go, documentType);
            if (!string.IsNullOrEmpty(a.SourceAsset))
            {
                VisualTreeAsset asset = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(a.SourceAsset);
                if (asset == null) throw new InvalidOperationException("VisualTreeAsset was not found: " + a.SourceAsset);
                SetMember(document, "visualTreeAsset", asset);
            }
            if (!string.IsNullOrEmpty(a.PanelSettings))
            {
                UnityEngine.Object panel = AssetDatabase.LoadMainAssetAtPath(a.PanelSettings);
                if (panel == null) throw new InvalidOperationException("PanelSettings asset was not found: " + a.PanelSettings);
                SetMember(document, "panelSettings", panel);
            }
            if (a.HasSortOrder) SetMember(document, "sortingOrder", a.SortOrder);
            EditorUtility.SetDirty(document);
            return Ok("UIDocument attached.", DocumentData(go, document));
        }

        private static UiResult DetachDocument(UiArguments a)
        {
            Type documentType = RequireRuntimeType("UnityEngine.UIElements.UIDocument", "UIDocument is not present in Unity 2019.");
            GameObject go = FindTarget(a.Target);
            Component document = go.GetComponent(documentType);
            if (document == null) throw new InvalidOperationException("Target has no UIDocument component.");
            Undo.DestroyObjectImmediate(document);
            return Ok("UIDocument detached.", new UiData { Target = FullPath(go), InstanceId = go.GetInstanceID() });
        }

        private static UiResult CreatePanelSettings(UiArguments a)
        {
            Type type = RequireRuntimeType("UnityEngine.UIElements.PanelSettings", "PanelSettings is not present in Unity 2019; this asset type requires Unity 2021.1 or a compatible package.");
            string path = NormalizeAssetPath(string.IsNullOrEmpty(a.Path) ? "Assets/UI/PanelSettings.asset" : a.Path, ".asset");
            if (AssetDatabase.LoadMainAssetAtPath(path) != null) throw new InvalidOperationException("Asset already exists: " + path);
            EnsureFolder(Path.GetDirectoryName(path).Replace('\\', '/'));
            ScriptableObject settings = ScriptableObject.CreateInstance(type);
            ApplyPanelSettings(settings, a);
            AssetDatabase.CreateAsset(settings, path);
            AssetDatabase.SaveAssets();
            return Ok("PanelSettings asset created.", new UiData { Path = path, Name = settings.name, InstanceId = settings.GetInstanceID() });
        }

        private static UiResult UpdatePanelSettings(UiArguments a)
        {
            Type type = RequireRuntimeType("UnityEngine.UIElements.PanelSettings", "PanelSettings is not present in Unity 2019.");
            string path = NormalizeAssetPath(a.Path, ".asset");
            UnityEngine.Object settings = AssetDatabase.LoadAssetAtPath(path, type);
            if (settings == null) throw new InvalidOperationException("PanelSettings asset was not found: " + path);
            Undo.RecordObject(settings, "MCP update PanelSettings");
            ApplyPanelSettings(settings, a);
            EditorUtility.SetDirty(settings);
            AssetDatabase.SaveAssets();
            return Ok("PanelSettings asset updated.", new UiData { Path = path, Name = settings.name, InstanceId = settings.GetInstanceID() });
        }

        private static void ApplyPanelSettings(UnityEngine.Object settings, UiArguments a)
        {
            if (!string.IsNullOrEmpty(a.ScaleMode)) TrySetMember(settings, "scaleMode", a.ScaleMode);
            if (a.HasReferenceResolution) TrySetMember(settings, "referenceResolution", new Vector2(Value(a.ReferenceResolution, 0), Value(a.ReferenceResolution, 1)));
            ApplyPatches(settings, a.Settings);
        }

        private static UiResult GetVisualTree(UiArguments a)
        {
            VisualElement root;
            string source;
            string target;
            ResolveVisualRoot(a, out root, out source, out target);
            int maxDepth = a.HasMaxDepth ? Mathf.Clamp(a.MaxDepth, 0, 64) : 10;
            UiElementRecord tree = SerializeElement(root, 0, maxDepth);
            return Ok("Visual tree read.", new UiData { Target = target, SourceAsset = source, Tree = tree });
        }

        private static UiResult ModifyVisualElement(UiArguments a)
        {
            if (string.IsNullOrEmpty(a.ElementName)) throw new InvalidOperationException("element_name is required.");
            VisualElement root;
            string source;
            string target;
            ResolveVisualRoot(a, out root, out source, out target);
            VisualElement element = FindElement(root, a.ElementName);
            if (element == null) throw new InvalidOperationException("Visual element was not found: " + a.ElementName);
            List<string> changes = new List<string>();
            if (a.Text != null)
            {
                PropertyInfo text = element.GetType().GetProperty("text", AnyMember);
                if (text == null || !text.CanWrite) throw new InvalidOperationException("Element does not support text: " + a.ElementName);
                text.SetValue(element, a.Text, null); changes.Add("text");
            }
            foreach (string cls in a.AddClasses ?? new string[0]) { if (!element.ClassListContains(cls)) element.AddToClassList(cls); changes.Add("+class:" + cls); }
            foreach (string cls in a.RemoveClasses ?? new string[0]) { if (element.ClassListContains(cls)) element.RemoveFromClassList(cls); changes.Add("-class:" + cls); }
            foreach (string cls in a.ToggleClasses ?? new string[0]) { element.ToggleInClassList(cls); changes.Add("~class:" + cls); }
            if (a.HasEnabled) { element.SetEnabled(a.Enabled); changes.Add("enabled"); }
            if (a.HasVisible) { SetStyle(element, "display", a.Visible ? "Flex" : "None"); changes.Add("visible"); }
            if (a.Tooltip != null) { TrySetMember(element, "tooltip", a.Tooltip); changes.Add("tooltip"); }
            if (a.Style != null)
                foreach (SerializedPatch patch in a.Style) { SetStyle(element, patch.Path, PatchValue(patch)); changes.Add("style:" + patch.Path); }
            if (changes.Count == 0) throw new InvalidOperationException("No visual element modifications were specified.");
            return Ok("Visual element modified in the live/preview tree.", new UiData
            {
                Target = target, SourceAsset = source, ElementName = element.name, ElementType = element.GetType().Name,
                Modifications = changes.ToArray(), Classes = Classes(element),
                Note = string.IsNullOrEmpty(target) ? "Unity 2019 preview-tree changes are transient; update the UXML file to persist them." : string.Empty,
            });
        }

        private static UiResult RenderUi(UiArguments a)
        {
            if (!EditorApplication.isPlaying)
                throw new InvalidOperationException("Unity 2019 has no runtime PanelSettings renderer in Edit mode. Enter Play mode to capture the Game view, or use UXML asset inspection with get_visual_tree.");
            string folder = string.IsNullOrEmpty(a.OutputFolder) ? "Assets/Screenshots" : a.OutputFolder;
            string absoluteFolder = ResolveProjectOutput(folder);
            Directory.CreateDirectory(absoluteFolder);
            string file = string.IsNullOrEmpty(a.FileName) ? "ui_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".png" : a.FileName;
            if (!file.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) file += ".png";
            string full = Path.Combine(absoluteFolder, Path.GetFileName(file));
            if (string.Equals(pendingCapturePath, full, StringComparison.OrdinalIgnoreCase) && File.Exists(full))
            {
                byte[] bytes = File.ReadAllBytes(full);
                pendingCapturePath = null;
                return Ok("UI capture completed.", new UiData { Path = ToProjectPath(full), HasContent = true, ImageBase64 = a.HasIncludeImage && a.IncludeImage ? Convert.ToBase64String(bytes) : string.Empty, MimeType = "image/png" });
            }
            ScreenCapture.CaptureScreenshot(full, 1);
            pendingCapturePath = full;
            return Ok("UI capture queued; call render_ui again after a frame to retrieve it.", new UiData { Path = ToProjectPath(full), Pending = true, HasContent = false });
        }

        private static void ResolveVisualRoot(UiArguments a, out VisualElement root, out string source, out string target)
        {
            root = null; source = string.Empty; target = string.Empty;
            string assetPath = !string.IsNullOrEmpty(a.SourceAsset) ? a.SourceAsset : (!string.IsNullOrEmpty(a.Path) && a.Path.EndsWith(".uxml", StringComparison.OrdinalIgnoreCase) ? a.Path : null);
            if (!string.IsNullOrEmpty(a.Target))
            {
                Type documentType = RequireRuntimeType("UnityEngine.UIElements.UIDocument", "UIDocument is not present in Unity 2019. Pass path/source_asset to inspect a UXML preview tree instead.");
                GameObject go = FindTarget(a.Target);
                Component document = go.GetComponent(documentType);
                if (document == null) throw new InvalidOperationException("Target has no UIDocument component.");
                root = GetMember(document, "rootVisualElement") as VisualElement;
                VisualTreeAsset sourceAsset = GetMember(document, "visualTreeAsset") as VisualTreeAsset;
                source = sourceAsset == null ? string.Empty : AssetDatabase.GetAssetPath(sourceAsset);
                target = FullPath(go);
            }
            else if (!string.IsNullOrEmpty(assetPath))
            {
                VisualTreeAsset asset = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(assetPath);
                if (asset == null) throw new InvalidOperationException("VisualTreeAsset was not found: " + assetPath);
                root = new VisualElement { name = "UXMLPreviewRoot" };
                MethodInfo clone = typeof(VisualTreeAsset).GetMethods(AnyMember).FirstOrDefault(m => m.Name == "CloneTree" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(VisualElement));
                if (clone == null) throw new InvalidOperationException("VisualTreeAsset.CloneTree is unavailable in this Unity 2019 build.");
                clone.Invoke(asset, new object[] { root });
                source = assetPath;
            }
            else throw new InvalidOperationException("target or a UXML path/source_asset is required.");
            if (root == null) throw new InvalidOperationException("The visual tree has not been built.");
        }

        private static UiElementRecord SerializeElement(VisualElement element, int depth, int maxDepth)
        {
            List<UiElementRecord> children = new List<UiElementRecord>();
            if (depth < maxDepth)
                for (int i = 0; i < element.hierarchy.childCount; i++) children.Add(SerializeElement(element.hierarchy[i], depth + 1, maxDepth));
            PropertyInfo textProperty = element.GetType().GetProperty("text", AnyMember);
            string text = textProperty == null ? string.Empty : Convert.ToString(textProperty.GetValue(element, null));
            return new UiElementRecord
            {
                Type = element.GetType().Name, Name = element.name ?? string.Empty, Classes = Classes(element),
                Text = text, Enabled = element.enabledSelf, ChildCount = element.hierarchy.childCount, Children = children.ToArray(),
            };
        }

        private static VisualElement FindElement(VisualElement root, string name)
        {
            if (string.Equals(root.name, name, StringComparison.Ordinal)) return root;
            for (int i = 0; i < root.hierarchy.childCount; i++)
            {
                VisualElement found = FindElement(root.hierarchy[i], name);
                if (found != null) return found;
            }
            return null;
        }

        private static string[] Classes(VisualElement element)
        {
            MethodInfo method = typeof(VisualElement).GetMethod("GetClasses", AnyMember);
            IEnumerable values = method == null ? null : method.Invoke(element, null) as IEnumerable;
            if (values == null) return new string[0];
            List<string> classes = new List<string>();
            foreach (object value in values) classes.Add(Convert.ToString(value));
            return classes.ToArray();
        }

        private static void SetStyle(VisualElement element, string requestedName, object value)
        {
            if (string.IsNullOrEmpty(requestedName)) return;
            object style = element.style;
            string name = NormalizeStyleName(requestedName);
            PropertyInfo property = style.GetType().GetProperty(name, AnyMember);
            if (property == null || !property.CanWrite) throw new InvalidOperationException("Unsupported inline style in Unity 2019: " + requestedName);
            object converted = ConvertStyleValue(value, property.PropertyType);
            property.SetValue(style, converted, null);
        }

        private static object ConvertStyleValue(object value, Type styleType)
        {
            Type inner = styleType.IsGenericType ? styleType.GetGenericArguments().FirstOrDefault() : null;
            Type target = inner ?? styleType;
            object typed = value;
            if (target == typeof(float)) typed = Convert.ToSingle(value);
            else if (target == typeof(int)) typed = Convert.ToInt32(value);
            else if (target == typeof(Color)) typed = ParseColor(Convert.ToString(value));
            else if (target.IsEnum) typed = Enum.Parse(target, Convert.ToString(value), true);
            else if (target.Name == "Length") typed = Activator.CreateInstance(target, new object[] { Convert.ToSingle(value) });
            if (styleType.IsInstanceOfType(typed)) return typed;
            ConstructorInfo constructor = styleType.GetConstructor(new[] { target });
            if (constructor != null) return constructor.Invoke(new[] { typed });
            MethodInfo implicitMethod = styleType.GetMethods(AnyMember).FirstOrDefault(m => m.Name == "op_Implicit" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType.IsInstanceOfType(typed));
            if (implicitMethod != null) return implicitMethod.Invoke(null, new[] { typed });
            throw new InvalidOperationException("Cannot convert style value to " + styleType.Name);
        }

        private static string NormalizeStyleName(string name)
        {
            string[] parts = name.Replace("_", "-").Split('-');
            string result = parts[0].ToLowerInvariant();
            for (int i = 1; i < parts.Length; i++) if (parts[i].Length > 0) result += char.ToUpperInvariant(parts[i][0]) + parts[i].Substring(1);
            return result;
        }

        private static void ApplyPatches(UnityEngine.Object target, SerializedPatch[] patches)
        {
            if (patches == null) return;
            SerializedObject serialized = new SerializedObject(target);
            foreach (SerializedPatch patch in patches)
            {
                SerializedProperty property = serialized.FindProperty(patch.Path);
                if (property == null) { TrySetMember(target, patch.Path, PatchValue(patch)); continue; }
                switch (property.propertyType)
                {
                    case SerializedPropertyType.Boolean: property.boolValue = patch.BoolValue; break;
                    case SerializedPropertyType.Integer: property.intValue = patch.IntValue; break;
                    case SerializedPropertyType.Float: property.floatValue = patch.FloatValue; break;
                    case SerializedPropertyType.String: property.stringValue = patch.StringValue ?? string.Empty; break;
                    case SerializedPropertyType.Color: property.colorValue = patch.VectorValue != null ? new Color(Value(patch.VectorValue, 0), Value(patch.VectorValue, 1), Value(patch.VectorValue, 2), patch.VectorValue.Length > 3 ? patch.VectorValue[3] : 1f) : ParseColor(patch.StringValue); break;
                    case SerializedPropertyType.Vector2: property.vector2Value = new Vector2(Value(patch.VectorValue, 0), Value(patch.VectorValue, 1)); break;
                    case SerializedPropertyType.Enum: property.enumValueIndex = Math.Max(0, Array.FindIndex(property.enumNames, x => string.Equals(x, patch.StringValue, StringComparison.OrdinalIgnoreCase))); break;
                    case SerializedPropertyType.ObjectReference: property.objectReferenceValue = string.IsNullOrEmpty(patch.StringValue) ? null : AssetDatabase.LoadMainAssetAtPath(patch.StringValue); break;
                    default: throw new InvalidOperationException("Unsupported PanelSettings property: " + patch.Path);
                }
            }
            serialized.ApplyModifiedProperties();
        }

        private static object PatchValue(SerializedPatch patch)
        {
            string kind = (patch.Kind ?? string.Empty).ToLowerInvariant();
            if (kind == "bool") return patch.BoolValue;
            if (kind == "int") return patch.IntValue;
            if (kind == "float" || kind == "number") return patch.FloatValue;
            if (kind == "color") return patch.VectorValue != null && patch.VectorValue.Length > 0 ? (object)new Color(Value(patch.VectorValue, 0), Value(patch.VectorValue, 1), Value(patch.VectorValue, 2), patch.VectorValue.Length > 3 ? patch.VectorValue[3] : 1f) : ParseColor(patch.StringValue);
            return patch.StringValue;
        }

        private static object GetMember(object target, string name)
        {
            if (target == null) return null;
            FieldInfo field = target.GetType().GetField(name, AnyMember);
            if (field != null) return field.GetValue(target);
            PropertyInfo property = target.GetType().GetProperty(name, AnyMember);
            return property == null ? null : property.GetValue(target, null);
        }

        private static void SetMember(object target, string name, object value)
        {
            if (!TrySetMember(target, name, value)) throw new InvalidOperationException("Member was not found or cannot be set: " + name);
        }

        private static bool TrySetMember(object target, string name, object value)
        {
            if (target == null || string.IsNullOrEmpty(name)) return false;
            FieldInfo field = target.GetType().GetField(name, AnyMember);
            if (field != null) { field.SetValue(target, ConvertValue(value, field.FieldType)); return true; }
            PropertyInfo property = target.GetType().GetProperty(name, AnyMember);
            if (property != null && property.CanWrite) { property.SetValue(target, ConvertValue(value, property.PropertyType), null); return true; }
            return false;
        }

        private static object ConvertValue(object value, Type type)
        {
            if (value == null || type.IsInstanceOfType(value)) return value;
            if (type.IsEnum) return Enum.Parse(type, Convert.ToString(value), true);
            if (typeof(UnityEngine.Object).IsAssignableFrom(type) && value is string) return AssetDatabase.LoadAssetAtPath((string)value, type);
            return Convert.ChangeType(value, type);
        }

        private static UiData DocumentData(GameObject go, Component document)
        {
            UnityEngine.Object source = GetMember(document, "visualTreeAsset") as UnityEngine.Object;
            UnityEngine.Object panel = GetMember(document, "panelSettings") as UnityEngine.Object;
            object order = GetMember(document, "sortingOrder");
            return new UiData { Target = FullPath(go), InstanceId = go.GetInstanceID(), SourceAsset = source == null ? string.Empty : AssetDatabase.GetAssetPath(source), PanelSettings = panel == null ? string.Empty : AssetDatabase.GetAssetPath(panel), SortOrder = order == null ? 0 : Convert.ToInt32(order) };
        }

        private static UiData FileData(string path, string contents)
        {
            FileInfo info = new FileInfo(FullAssetPath(path));
            return new UiData { Path = path, Contents = contents, Extension = Path.GetExtension(path).TrimStart('.').ToLowerInvariant(), SizeBytes = info.Length };
        }

        private static UiAssetRecord AssetRecord(string path, string type)
        {
            FileInfo info = new FileInfo(FullAssetPath(path));
            return new UiAssetRecord { Path = path, Name = Path.GetFileNameWithoutExtension(path), Type = type, Guid = AssetDatabase.AssetPathToGUID(path), SizeBytes = info.Exists ? info.Length : 0 };
        }

        private static string DecodeContents(UiArguments a)
        {
            if (!a.ContentsEncoded || string.IsNullOrEmpty(a.EncodedContents)) throw new InvalidOperationException("contents is required.");
            try { return Encoding.UTF8.GetString(Convert.FromBase64String(a.EncodedContents)); }
            catch (FormatException) { throw new InvalidOperationException("contents was not valid base64 transport data."); }
        }

        private static void ValidateContents(string path, string contents)
        {
            if (string.IsNullOrWhiteSpace(contents)) throw new InvalidOperationException("contents cannot be empty.");
            if (path.EndsWith(".uxml", StringComparison.OrdinalIgnoreCase) && contents.IndexOf("<ui:UXML", StringComparison.OrdinalIgnoreCase) < 0 && contents.IndexOf("<UXML", StringComparison.OrdinalIgnoreCase) < 0)
                throw new InvalidOperationException("UXML contents require a <ui:UXML> or <UXML> root element.");
        }

        private static int FindUxmlOpeningTagEnd(string contents)
        {
            int start = contents.IndexOf("<ui:UXML", StringComparison.OrdinalIgnoreCase);
            if (start < 0) start = contents.IndexOf("<UXML", StringComparison.OrdinalIgnoreCase);
            return start < 0 ? -1 : contents.IndexOf('>', start) + 1;
        }

        private static string ValidateUiPath(string path)
        {
            string normalized = NormalizeAssetPath(path, string.Empty);
            string extension = Path.GetExtension(normalized).ToLowerInvariant();
            if (extension != ".uxml" && extension != ".uss") throw new InvalidOperationException("UI file path must use .uxml or .uss.");
            return normalized;
        }

        private static string ValidateAssetScope(string path)
        {
            string normalized = (path ?? string.Empty).Replace('\\', '/').TrimEnd('/');
            if (normalized.Equals("Assets", StringComparison.OrdinalIgnoreCase)) return "Assets";
            if (!normalized.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) || normalized.Split('/').Contains("..")) throw new InvalidOperationException("path must be under Assets.");
            return normalized;
        }

        private static string NormalizeAssetPath(string path, string extension)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new InvalidOperationException("path is required.");
            string normalized = path.Replace('\\', '/').Trim();
            if (!normalized.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("path must be under Assets.");
            if (normalized.Split('/').Contains("..")) throw new InvalidOperationException("path cannot contain traversal sequences.");
            if (!string.IsNullOrEmpty(extension) && !normalized.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) normalized += extension;
            return normalized;
        }

        private static string FullAssetPath(string path) { return Path.Combine(Application.dataPath, path.Substring("Assets/".Length)).Replace('/', Path.DirectorySeparatorChar); }

        private static void EnsureFolder(string assetFolder)
        {
            if (string.IsNullOrEmpty(assetFolder) || assetFolder == "Assets") return;
            string current = "Assets";
            foreach (string part in assetFolder.Substring("Assets".Length).Trim('/').Split('/'))
            {
                if (string.IsNullOrEmpty(part)) continue;
                string next = current + "/" + part;
                if (!AssetDatabase.IsValidFolder(next)) AssetDatabase.CreateFolder(current, part);
                current = next;
            }
        }

        private static string ResolveProjectOutput(string requested)
        {
            string root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string path = Path.IsPathRooted(requested) ? Path.GetFullPath(requested) : Path.GetFullPath(Path.Combine(root, requested));
            if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("output_folder must stay inside the Unity project.");
            return path;
        }

        private static string ToProjectPath(string absolute)
        {
            string root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            if (!absolute.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return absolute;
            return absolute.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Replace('\\', '/');
        }

        private static GameObject FindTarget(string value)
        {
            if (string.IsNullOrEmpty(value)) throw new InvalidOperationException("target is required.");
            if (int.TryParse(value, out int id))
            {
                GameObject byId = EditorUtility.InstanceIDToObject(id) as GameObject;
                if (byId != null) return byId;
            }
            GameObject direct = GameObject.Find(value);
            if (direct != null) return direct;
            string normalized = value.Trim('/');
            foreach (GameObject go in Resources.FindObjectsOfTypeAll<GameObject>()) if (go.scene.IsValid() && (go.name == value || FullPath(go) == normalized)) return go;
            throw new InvalidOperationException("GameObject was not found: " + value);
        }

        private static string FullPath(GameObject go)
        {
            string path = go.name;
            for (Transform parent = go.transform.parent; parent != null; parent = parent.parent) path = parent.name + "/" + path;
            return path;
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

        private static Type RequireRuntimeType(string name, string message) { Type type = FindType(name); if (type == null) throw new InvalidOperationException(message); return type; }

        private static Color ParseColor(string text)
        {
            if (!ColorUtility.TryParseHtmlString(text ?? string.Empty, out Color color)) throw new InvalidOperationException("Invalid color value: " + text);
            return color;
        }

        private static float Value(float[] values, int index) { return values != null && values.Length > index ? values[index] : 0f; }
        private static UiResult Ok(string message, UiData data) { return new UiResult { Success = true, Message = message, Data = data ?? new UiData() }; }
        private static UiResult Fail(string message) { return new UiResult { Success = false, Message = message, Data = new UiData() }; }

        [Serializable] private sealed class UiArguments
        {
            public string Action, Path, EncodedContents, Target, SourceAsset, PanelSettings, ScaleMode, FileName, OutputFolder, Stylesheet, FilterType, ElementName, Text, Tooltip;
            public bool ContentsEncoded, IncludeImage, Enabled, Visible;
            public bool HasIncludeImage, HasEnabled, HasVisible, HasSortOrder, HasMaxDepth, HasWidth, HasHeight, HasMaxResolution, HasPageSize, HasPageNumber, HasReferenceResolution;
            public int SortOrder, MaxDepth, Width, Height, MaxResolution, PageSize, PageNumber;
            public float[] ReferenceResolution;
            public string[] AddClasses, RemoveClasses, ToggleClasses;
            public SerializedPatch[] Settings, Style;
        }
        [Serializable] private sealed class SerializedPatch { public string Path, Kind, StringValue; public bool BoolValue; public int IntValue; public float FloatValue; public float[] VectorValue; }
        [Serializable] private sealed class UiResult { public bool Success; public string Message; public UiData Data; }
        [Serializable] private sealed class UiData
        {
            public string UnityVersion, CompatibilityMode, Path, Name, Contents, Extension, Target, SourceAsset, PanelSettings, Stylesheet, ElementName, ElementType, Note, ImageBase64, MimeType;
            public bool HasVisualTreeAssets, HasRuntimeUiDocument, HasPanelSettings, AlreadyLinked, HasMore, Pending, HasContent;
            public int InstanceId, SortOrder, Count, Total, PageSize, PageNumber;
            public long SizeBytes;
            public UiAssetRecord[] Assets;
            public UiElementRecord Tree;
            public string[] Modifications, Classes;
        }
        [Serializable] private sealed class UiAssetRecord { public string Path, Name, Type, Guid; public long SizeBytes; }
        [Serializable] private sealed class UiElementRecord { public string Type, Name, Text; public bool Enabled; public int ChildCount; public string[] Classes; public UiElementRecord[] Children; }
    }
}
#endif
