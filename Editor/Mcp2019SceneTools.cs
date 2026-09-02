#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace UnityMcp2019
{
    /// <summary>
    /// Focused scene-object operations inspired by CoplayDev/unity-mcp.
    /// Targets are instance-id only and every mutation participates in Unity Undo.
    /// </summary>
    internal static class Mcp2019SceneTools
    {
        internal static string Execute(string method, string argumentsJson)
        {
            switch (method)
            {
                case "get_gameobject":
                    return JsonUtility.ToJson(GetGameObject(Parse<GetGameObjectArguments>(argumentsJson)));
                case "manage_gameobject":
                    return JsonUtility.ToJson(ManageGameObject(Parse<ManageGameObjectArguments>(argumentsJson)));
                case "manage_component":
                    return JsonUtility.ToJson(ManageComponent(Parse<ManageComponentArguments>(argumentsJson)));
                default:
                    throw new ArgumentException("Unknown scene method: " + method);
            }
        }

        private static T Parse<T>(string json) where T : class, new()
        {
            if (string.IsNullOrEmpty(json) || json == "{}")
            {
                return new T();
            }

            T value = JsonUtility.FromJson<T>(json);
            return value ?? new T();
        }

        private static GameObjectDetail GetGameObject(GetGameObjectArguments arguments)
        {
            return CreateDetail(ResolveSceneGameObject(arguments.InstanceId));
        }

        private static MutationResult ManageGameObject(ManageGameObjectArguments arguments)
        {
            EnsureSceneEditingAllowed();
            string action = NormalizeAction(arguments.Action);

            switch (action)
            {
                case "create":
                    return CreateGameObject(arguments);
                case "modify":
                    return ModifyGameObject(arguments);
                case "delete":
                    return DeleteGameObject(arguments);
                case "duplicate":
                    return DuplicateGameObject(arguments);
                case "move_relative":
                    return MoveRelative(arguments);
                case "look_at":
                    return LookAt(arguments);
                default:
                    throw new ArgumentException(
                        "action must be create, modify, delete, duplicate, move_relative, or look_at.");
            }
        }

        private static MutationResult CreateGameObject(ManageGameObjectArguments arguments)
        {
            string name = ValidateName(arguments.Name);
            Transform parent = ResolveOptionalTransform(
                arguments.HasParent,
                arguments.ParentInstanceId,
                arguments.ParentTarget,
                string.Empty);

            Undo.IncrementCurrentGroup();
            int undoGroup = Undo.GetCurrentGroup();
            string undoLabel = "MCP Create GameObject";
            Undo.SetCurrentGroupName(undoLabel);

            GameObject gameObject = CreatePrimitive(arguments.Primitive);
            gameObject.name = name;
            Undo.RegisterCreatedObjectUndo(gameObject, undoLabel);

            if (parent != null)
            {
                Undo.SetTransformParent(gameObject.transform, parent, undoLabel);
            }

            ApplyTransformChanges(gameObject.transform, arguments, undoLabel);
            if (arguments.HasActive)
            {
                Undo.RecordObject(gameObject, undoLabel);
                gameObject.SetActive(arguments.Active);
            }

            ApplyGameObjectSettings(gameObject, arguments, undoLabel);
            ApplyComponentAdds(gameObject, arguments.ComponentsToAdd, undoLabel);
            ApplyComponentProperties(gameObject, arguments.ComponentProperties, undoLabel);

            string prefabPath = string.Empty;
            if (arguments.SaveAsPrefab)
            {
                prefabPath = SaveCreatedPrefab(gameObject, arguments);
            }

            MarkSceneDirty(gameObject);
            Undo.CollapseUndoOperations(undoGroup);
            MutationResult result = MutationResult.From(
                action: "create", detail: CreateDetail(gameObject), undoLabel: undoLabel);
            result.PrefabPath = prefabPath;
            return result;
        }

        private static MutationResult ModifyGameObject(ManageGameObjectArguments arguments)
        {
            if (!arguments.HasName && !arguments.HasParent && !arguments.HasActive &&
                !arguments.HasPosition && !arguments.HasRotation && !arguments.HasScale &&
                !arguments.HasTag && !arguments.HasLayer && !arguments.HasStatic &&
                IsEmpty(arguments.ComponentsToAdd) && IsEmpty(arguments.ComponentsToRemove) &&
                IsEmpty(arguments.ComponentProperties))
            {
                throw new ArgumentException("modify requires at least one changed field.");
            }

            GameObject gameObject = ResolveSceneGameObject(
                arguments.TargetInstanceId != 0 ? arguments.TargetInstanceId : arguments.InstanceId,
                arguments.Target,
                arguments.SearchMethod);
            string validatedName = arguments.HasName ? ValidateName(arguments.Name) : string.Empty;
            Transform requestedParent = ResolveOptionalTransform(
                arguments.HasParent,
                arguments.ParentInstanceId,
                arguments.ParentTarget,
                string.Empty);
            if (requestedParent != null)
            {
                if (requestedParent == gameObject.transform ||
                    requestedParent.IsChildOf(gameObject.transform))
                {
                    throw new ArgumentException("A GameObject cannot be parented to itself or its child.");
                }
            }

            string undoLabel = "MCP Modify GameObject";
            Undo.IncrementCurrentGroup();
            int undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName(undoLabel);

            if (arguments.HasName)
            {
                Undo.RecordObject(gameObject, undoLabel);
                gameObject.name = validatedName;
            }

            if (arguments.HasActive)
            {
                Undo.RecordObject(gameObject, undoLabel);
                gameObject.SetActive(arguments.Active);
            }

            if (arguments.HasParent)
            {
                Undo.SetTransformParent(gameObject.transform, requestedParent, undoLabel);
            }

            ApplyTransformChanges(gameObject.transform, arguments, undoLabel);
            ApplyGameObjectSettings(gameObject, arguments, undoLabel);
            ApplyComponentAdds(gameObject, arguments.ComponentsToAdd, undoLabel);
            ApplyComponentRemovals(gameObject, arguments.ComponentsToRemove, undoLabel);
            ApplyComponentProperties(gameObject, arguments.ComponentProperties, undoLabel);
            MarkSceneDirty(gameObject);
            Undo.CollapseUndoOperations(undoGroup);
            return MutationResult.From(action: "modify", detail: CreateDetail(gameObject), undoLabel: undoLabel);
        }

        private static MutationResult DeleteGameObject(ManageGameObjectArguments arguments)
        {
            if (!arguments.Confirm)
            {
                throw new ArgumentException("delete requires confirm=true.");
            }

            GameObject gameObject = ResolveSceneGameObject(
                arguments.TargetInstanceId != 0 ? arguments.TargetInstanceId : arguments.InstanceId,
                arguments.Target,
                arguments.SearchMethod);
            int instanceId = gameObject.GetInstanceID();
            string path = GetPath(gameObject.transform);
            UnityEngine.SceneManagement.Scene scene = gameObject.scene;
            string undoLabel = "MCP Delete GameObject";
            Undo.IncrementCurrentGroup();
            int undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName(undoLabel);
            Undo.DestroyObjectImmediate(gameObject);
            if (scene.IsValid())
            {
                EditorSceneManager.MarkSceneDirty(scene);
            }
            Undo.CollapseUndoOperations(undoGroup);
            return new MutationResult
            {
                Ok = true,
                Action = "delete",
                InstanceId = instanceId,
                Path = path,
                UndoLabel = undoLabel
            };
        }

        private static MutationResult DuplicateGameObject(ManageGameObjectArguments arguments)
        {
            GameObject source = ResolveSceneGameObject(
                arguments.TargetInstanceId != 0 ? arguments.TargetInstanceId : arguments.InstanceId,
                arguments.Target,
                arguments.SearchMethod);
            string undoLabel = "MCP Duplicate GameObject";
            GameObject duplicate = UnityEngine.Object.Instantiate(source, source.transform.parent);
            duplicate.name = string.IsNullOrWhiteSpace(arguments.NewName)
                ? source.name + "_Copy"
                : ValidateName(arguments.NewName);
            duplicate.transform.position = source.transform.position +
                (arguments.HasOffset ? arguments.Offset : Vector3.zero);
            Undo.RegisterCreatedObjectUndo(duplicate, undoLabel);
            MarkSceneDirty(duplicate);
            return MutationResult.From("duplicate", CreateDetail(duplicate), undoLabel);
        }

        private static MutationResult MoveRelative(ManageGameObjectArguments arguments)
        {
            GameObject target = ResolveSceneGameObject(
                arguments.TargetInstanceId != 0 ? arguments.TargetInstanceId : arguments.InstanceId,
                arguments.Target,
                arguments.SearchMethod);
            GameObject reference = ResolveSceneGameObject(
                arguments.ReferenceInstanceId,
                arguments.ReferenceTarget,
                string.Empty);
            Vector3 direction = DirectionVector(arguments.Direction);
            if (!arguments.WorldSpace)
            {
                direction = reference.transform.TransformDirection(direction);
            }

            float distance = arguments.HasDistance ? arguments.Distance : 1f;
            string undoLabel = "MCP Move GameObject Relative";
            Undo.RecordObject(target.transform, undoLabel);
            target.transform.position = reference.transform.position + direction.normalized * distance;
            MarkSceneDirty(target);
            return MutationResult.From("move_relative", CreateDetail(target), undoLabel);
        }

        private static MutationResult LookAt(ManageGameObjectArguments arguments)
        {
            GameObject target = ResolveSceneGameObject(
                arguments.TargetInstanceId != 0 ? arguments.TargetInstanceId : arguments.InstanceId,
                arguments.Target,
                arguments.SearchMethod);
            Vector3 destination;
            if (arguments.HasLookAtPosition)
            {
                destination = arguments.LookAtPosition;
            }
            else
            {
                destination = ResolveSceneGameObject(
                    arguments.LookAtInstanceId,
                    arguments.LookAtTarget,
                    string.Empty).transform.position;
            }

            Vector3 up = arguments.HasLookAtUp ? arguments.LookAtUp : Vector3.up;
            if (up.sqrMagnitude < 0.000001f)
            {
                throw new ArgumentException("look_at_up cannot be a zero vector.");
            }

            string undoLabel = "MCP Look At";
            Undo.RecordObject(target.transform, undoLabel);
            target.transform.LookAt(destination, up.normalized);
            MarkSceneDirty(target);
            return MutationResult.From("look_at", CreateDetail(target), undoLabel);
        }

        private static MutationResult ManageComponent(ManageComponentArguments arguments)
        {
            EnsureSceneEditingAllowed();
            string action = NormalizeAction(arguments.Action);
            GameObject gameObject = ResolveSceneGameObject(
                arguments.InstanceId,
                arguments.Target,
                arguments.SearchMethod);
            Type componentType = ResolveComponentType(arguments.ComponentType);

            switch (action)
            {
                case "add":
                    return AddComponent(gameObject, componentType, arguments.Properties);
                case "remove":
                    return RemoveComponent(gameObject, componentType, arguments);
                case "set_property":
                    return SetComponentProperty(gameObject, componentType, arguments);
                default:
                    throw new ArgumentException("action must be add, remove, or set_property.");
            }
        }

        private static MutationResult AddComponent(
            GameObject gameObject,
            Type componentType,
            SerializedPatch[] properties)
        {
            if (componentType.IsAbstract || componentType.IsGenericTypeDefinition)
            {
                throw new ArgumentException("component_type must be a concrete component type.");
            }

            if (typeof(Transform).IsAssignableFrom(componentType))
            {
                throw new ArgumentException("Transform components cannot be added explicitly.");
            }

            string undoLabel = "MCP Add Component";
            Undo.IncrementCurrentGroup();
            int undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName(undoLabel);
            Component component = Undo.AddComponent(gameObject, componentType);
            if (component == null)
            {
                throw new InvalidOperationException("Unity did not add component " + componentType.FullName + ".");
            }

            ApplySerializedPatches(component, properties);
            EditorUtility.SetDirty(component);

            MarkSceneDirty(gameObject);
            Undo.CollapseUndoOperations(undoGroup);
            return MutationResult.From(
                action: "add",
                detail: CreateDetail(gameObject),
                undoLabel: undoLabel,
                component: CreateComponentRecord(component));
        }

        private static MutationResult RemoveComponent(
            GameObject gameObject,
            Type componentType,
            ManageComponentArguments arguments)
        {
            if (!arguments.Confirm)
            {
                throw new ArgumentException("remove requires confirm=true.");
            }

            if (typeof(Transform).IsAssignableFrom(componentType))
            {
                throw new ArgumentException("Transform components cannot be removed.");
            }

            Component component = ResolveComponent(gameObject, componentType, arguments.ComponentIndex);
            ComponentRecord removed = CreateComponentRecord(component);
            string undoLabel = "MCP Remove Component";
            Undo.IncrementCurrentGroup();
            int undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName(undoLabel);
            Undo.DestroyObjectImmediate(component);
            MarkSceneDirty(gameObject);
            Undo.CollapseUndoOperations(undoGroup);
            return MutationResult.From(
                action: "remove",
                detail: CreateDetail(gameObject),
                undoLabel: undoLabel,
                component: removed);
        }

        private static MutationResult SetComponentProperty(
            GameObject gameObject,
            Type componentType,
            ManageComponentArguments arguments)
        {
            SerializedPatch[] patches = arguments.Properties;
            bool hasStructuredPatches = patches != null && patches.Length > 0;
            string propertyPath = arguments.PropertyPath == null
                ? string.Empty
                : arguments.PropertyPath.Trim();
            if (!hasStructuredPatches && (propertyPath.Length == 0 || propertyPath == "m_Script"))
            {
                throw new ArgumentException("property_path is required and cannot be m_Script.");
            }

            Component component = ResolveComponent(gameObject, componentType, arguments.ComponentIndex);
            string undoLabel = "MCP Set Component Property";
            Undo.IncrementCurrentGroup();
            int undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName(undoLabel);
            Undo.RecordObject(component, undoLabel);
            if (hasStructuredPatches)
            {
                ApplySerializedPatches(component, patches);
                propertyPath = patches.Length == 1 ? patches[0].Path : "<multiple>";
            }
            else
            {
                SerializedObject serializedObject = new SerializedObject(component);
                SerializedProperty property = serializedObject.FindProperty(propertyPath);
                if (property == null)
                {
                    throw new ArgumentException(
                        "Serialized property was not found: " + propertyPath + ".");
                }
                serializedObject.Update();
                SetSerializedValue(property, arguments);
                serializedObject.ApplyModifiedProperties();
            }
            EditorUtility.SetDirty(component);
            MarkSceneDirty(gameObject);
            Undo.CollapseUndoOperations(undoGroup);

            return MutationResult.From(
                action: "set_property",
                detail: CreateDetail(gameObject),
                undoLabel: undoLabel,
                component: CreateComponentRecord(component),
                propertyPath: propertyPath);
        }

        private static void SetSerializedValue(
            SerializedProperty property,
            ManageComponentArguments arguments)
        {
            string kind = arguments.ValueKind == null
                ? string.Empty
                : arguments.ValueKind.Trim().ToLowerInvariant();

            switch (property.propertyType)
            {
                case SerializedPropertyType.Integer:
                case SerializedPropertyType.LayerMask:
                    RequireValueKind(kind, "integer");
                    property.intValue = arguments.IntValue;
                    return;
                case SerializedPropertyType.Boolean:
                    RequireValueKind(kind, "boolean");
                    property.boolValue = arguments.BoolValue;
                    return;
                case SerializedPropertyType.Float:
                    if (kind != "number" && kind != "integer")
                    {
                        throw ValueKindError("number", kind);
                    }

                    property.floatValue = kind == "integer"
                        ? arguments.IntValue
                        : arguments.FloatValue;
                    return;
                case SerializedPropertyType.String:
                    RequireValueKind(kind, "string");
                    property.stringValue = arguments.StringValue ?? string.Empty;
                    return;
                case SerializedPropertyType.Enum:
                    SetEnumValue(property, arguments, kind);
                    return;
                case SerializedPropertyType.Color:
                    RequireVectorLength(arguments, 3, 4);
                    property.colorValue = new Color(
                        arguments.VectorValue.x,
                        arguments.VectorValue.y,
                        arguments.VectorValue.z,
                        arguments.VectorLength == 4 ? arguments.VectorValue.w : 1f);
                    return;
                case SerializedPropertyType.Vector2:
                    RequireVectorLength(arguments, 2, 2);
                    property.vector2Value = new Vector2(
                        arguments.VectorValue.x,
                        arguments.VectorValue.y);
                    return;
                case SerializedPropertyType.Vector3:
                    RequireVectorLength(arguments, 3, 3);
                    property.vector3Value = new Vector3(
                        arguments.VectorValue.x,
                        arguments.VectorValue.y,
                        arguments.VectorValue.z);
                    return;
                case SerializedPropertyType.Vector4:
                    RequireVectorLength(arguments, 4, 4);
                    property.vector4Value = arguments.VectorValue;
                    return;
                default:
                    throw new ArgumentException(
                        "Unsupported serialized property type: " + property.propertyType +
                        ". Supported: integer, layer mask, boolean, float, string, enum, " +
                        "Color, Vector2, Vector3, and Vector4.");
            }
        }

        private static void SetEnumValue(
            SerializedProperty property,
            ManageComponentArguments arguments,
            string kind)
        {
            if (kind == "integer")
            {
                if (arguments.IntValue < 0 || arguments.IntValue >= property.enumNames.Length)
                {
                    throw new ArgumentException("Enum index is out of range.");
                }

                property.enumValueIndex = arguments.IntValue;
                return;
            }

            RequireValueKind(kind, "string");
            int index = Array.IndexOf(property.enumNames, arguments.StringValue);
            if (index < 0)
            {
                throw new ArgumentException(
                    "Unknown enum name. Expected one of: " +
                    string.Join(", ", property.enumNames));
            }

            property.enumValueIndex = index;
        }

        private static void RequireValueKind(string actual, string expected)
        {
            if (actual != expected)
            {
                throw ValueKindError(expected, actual);
            }
        }

        private static ArgumentException ValueKindError(string expected, string actual)
        {
            return new ArgumentException(
                "Property requires a " + expected + " value, received " +
                (actual.Length == 0 ? "no value" : actual) + ".");
        }

        private static void RequireVectorLength(
            ManageComponentArguments arguments,
            int minimum,
            int maximum)
        {
            if (arguments.ValueKind != "vector" ||
                arguments.VectorLength < minimum ||
                arguments.VectorLength > maximum)
            {
                throw new ArgumentException(
                    minimum == maximum
                        ? "Property requires a numeric array with " + minimum + " elements."
                        : "Property requires a numeric array with " + minimum + " or " + maximum + " elements.");
            }
        }

        private static void ApplySerializedPatches(
            UnityEngine.Object target,
            SerializedPatch[] patches)
        {
            if (patches == null || patches.Length == 0)
            {
                return;
            }

            SerializedObject serializedObject = new SerializedObject(target);
            serializedObject.Update();
            for (int index = 0; index < patches.Length; index++)
            {
                SerializedPatch patch = patches[index];
                if (patch == null || string.IsNullOrWhiteSpace(patch.Path) || patch.Path == "m_Script")
                {
                    throw new ArgumentException("Component property path is required and cannot be m_Script.");
                }

                SerializedProperty property = serializedObject.FindProperty(patch.Path);
                if (property == null)
                {
                    if (!TrySetReflectedPath(target, patch))
                    {
                        throw new ArgumentException(
                            "Serialized property or writable C# member was not found: " +
                            patch.Path + ".");
                    }
                    continue;
                }

                SetSerializedPatch(property, patch);
            }
            serializedObject.ApplyModifiedProperties();
        }

        private static bool TrySetReflectedPath(UnityEngine.Object root, SerializedPatch patch)
        {
            string[] parts = patch.Path.Split('.');
            object current = root;
            for (int index = 0; index < parts.Length - 1; index++)
            {
                if (current == null)
                {
                    return false;
                }
                MemberInfo member = FindInstanceMember(current.GetType(), parts[index], false);
                if (member == null)
                {
                    return false;
                }
                current = ReadMember(current, member);
            }
            if (current == null)
            {
                return false;
            }

            MemberInfo finalMember = FindInstanceMember(
                current.GetType(), parts[parts.Length - 1], true);
            if (finalMember == null)
            {
                return false;
            }
            Type valueType = finalMember is PropertyInfo
                ? ((PropertyInfo)finalMember).PropertyType
                : ((FieldInfo)finalMember).FieldType;
            object value = ConvertPatchValue(patch, valueType);
            PropertyInfo property = finalMember as PropertyInfo;
            if (property != null)
            {
                property.SetValue(current, value, null);
            }
            else
            {
                ((FieldInfo)finalMember).SetValue(current, value);
            }
            return true;
        }

        private static MemberInfo FindInstanceMember(Type type, string name, bool requireWritable)
        {
            const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public |
                BindingFlags.NonPublic;
            PropertyInfo property = type.GetProperties(Flags).FirstOrDefault(candidate =>
                string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase) &&
                candidate.GetIndexParameters().Length == 0 &&
                candidate.CanRead && (!requireWritable || candidate.CanWrite));
            if (property != null)
            {
                return property;
            }
            return type.GetFields(Flags).FirstOrDefault(candidate =>
                string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase) &&
                (!requireWritable || !candidate.IsInitOnly));
        }

        private static object ReadMember(object target, MemberInfo member)
        {
            PropertyInfo property = member as PropertyInfo;
            return property != null
                ? property.GetValue(target, null)
                : ((FieldInfo)member).GetValue(target);
        }

        private static object ConvertPatchValue(SerializedPatch patch, Type requestedType)
        {
            Type type = Nullable.GetUnderlyingType(requestedType) ?? requestedType;
            string kind = (patch.Kind ?? string.Empty).ToLowerInvariant();
            if (typeof(UnityEngine.Object).IsAssignableFrom(type))
            {
                UnityEngine.Object reference = ResolveObjectReference(patch);
                if (reference != null && !type.IsInstanceOfType(reference))
                {
                    GameObject gameObject = reference as GameObject;
                    if (gameObject != null && typeof(Component).IsAssignableFrom(type))
                    {
                        reference = gameObject.GetComponent(type);
                    }
                }
                if (reference != null && !type.IsInstanceOfType(reference))
                {
                    throw new ArgumentException(
                        "Object reference is not assignable to " + type.FullName + ".");
                }
                return reference;
            }
            if (type == typeof(string)) return patch.StringValue ?? string.Empty;
            if (type == typeof(bool)) return kind == "bool" ? patch.BoolValue : patch.IntValue != 0;
            if (type == typeof(int)) return kind == "int" ? patch.IntValue : Mathf.RoundToInt(patch.FloatValue);
            if (type == typeof(float)) return kind == "int" ? patch.IntValue : patch.FloatValue;
            if (type == typeof(double)) return kind == "int" ? (double)patch.IntValue : patch.FloatValue;
            if (type == typeof(long)) return kind == "int" ? (long)patch.IntValue : (long)patch.FloatValue;
            if (type.IsEnum)
            {
                return kind == "int"
                    ? Enum.ToObject(type, patch.IntValue)
                    : Enum.Parse(type, patch.StringValue, true);
            }
            Vector4 vector = PatchVector(patch);
            if (type == typeof(Vector2)) return new Vector2(vector.x, vector.y);
            if (type == typeof(Vector3)) return new Vector3(vector.x, vector.y, vector.z);
            if (type == typeof(Vector4)) return vector;
            if (type == typeof(Color))
            {
                return new Color(
                    vector.x, vector.y, vector.z, patch.VectorLength >= 4 ? vector.w : 1f);
            }
            throw new ArgumentException("Unsupported reflected member type: " + type.FullName + ".");
        }

        private static void SetSerializedPatch(SerializedProperty property, SerializedPatch patch)
        {
            string kind = (patch.Kind ?? string.Empty).ToLowerInvariant();
            switch (property.propertyType)
            {
                case SerializedPropertyType.Boolean:
                    property.boolValue = kind == "bool" ? patch.BoolValue : patch.IntValue != 0;
                    return;
                case SerializedPropertyType.Integer:
                case SerializedPropertyType.LayerMask:
                case SerializedPropertyType.Character:
                    property.intValue = kind == "int"
                        ? patch.IntValue
                        : Mathf.RoundToInt(patch.FloatValue);
                    return;
                case SerializedPropertyType.Float:
                    property.floatValue = kind == "int" ? patch.IntValue : patch.FloatValue;
                    return;
                case SerializedPropertyType.String:
                    property.stringValue = patch.StringValue ?? string.Empty;
                    return;
                case SerializedPropertyType.Enum:
                    if (kind == "int")
                    {
                        if (patch.IntValue < 0 || patch.IntValue >= property.enumNames.Length)
                        {
                            throw new ArgumentException("Enum index is out of range for " + patch.Path + ".");
                        }
                        property.enumValueIndex = patch.IntValue;
                        return;
                    }
                    int enumIndex = Array.FindIndex(
                        property.enumNames,
                        value => string.Equals(value, patch.StringValue, StringComparison.OrdinalIgnoreCase));
                    if (enumIndex < 0)
                    {
                        throw new ArgumentException("Enum value was not found: " + patch.StringValue + ".");
                    }
                    property.enumValueIndex = enumIndex;
                    return;
                case SerializedPropertyType.Color:
                    Vector4 color = PatchVector(patch);
                    property.colorValue = new Color(
                        color.x, color.y, color.z, patch.VectorLength >= 4 ? color.w : 1f);
                    return;
                case SerializedPropertyType.Vector2:
                    Vector4 vector2 = PatchVector(patch);
                    property.vector2Value = new Vector2(vector2.x, vector2.y);
                    return;
                case SerializedPropertyType.Vector3:
                    Vector4 vector3 = PatchVector(patch);
                    property.vector3Value = new Vector3(vector3.x, vector3.y, vector3.z);
                    return;
                case SerializedPropertyType.Vector4:
                    property.vector4Value = PatchVector(patch);
                    return;
                case SerializedPropertyType.ObjectReference:
                    property.objectReferenceValue = ResolveObjectReference(patch);
                    return;
                default:
                    throw new ArgumentException(
                        "Unsupported serialized property type for " + patch.Path + ": " +
                        property.propertyType + ".");
            }
        }

        private static Vector4 PatchVector(SerializedPatch patch)
        {
            if (patch.FloatValues == null || patch.FloatValues.Length < 2)
            {
                throw new ArgumentException("Property " + patch.Path + " requires a numeric vector.");
            }
            return new Vector4(
                patch.FloatValues.Length > 0 ? patch.FloatValues[0] : 0f,
                patch.FloatValues.Length > 1 ? patch.FloatValues[1] : 0f,
                patch.FloatValues.Length > 2 ? patch.FloatValues[2] : 0f,
                patch.FloatValues.Length > 3 ? patch.FloatValues[3] : 0f);
        }

        private static UnityEngine.Object ResolveObjectReference(SerializedPatch patch)
        {
            string kind = (patch.Kind ?? string.Empty).ToLowerInvariant();
            if (kind == "null")
            {
                return null;
            }
            if (!string.IsNullOrEmpty(patch.ReferenceGuid))
            {
                string guidPath = AssetDatabase.GUIDToAssetPath(patch.ReferenceGuid);
                return AssetDatabase.LoadMainAssetAtPath(guidPath);
            }
            if (!string.IsNullOrEmpty(patch.ReferencePath))
            {
                return AssetDatabase.LoadMainAssetAtPath(patch.ReferencePath);
            }
            if (kind == "int")
            {
                return EditorUtility.InstanceIDToObject(patch.IntValue);
            }
            if (kind == "string" && !string.IsNullOrWhiteSpace(patch.StringValue))
            {
                if (patch.StringValue.StartsWith("Assets/", StringComparison.Ordinal))
                {
                    return AssetDatabase.LoadMainAssetAtPath(patch.StringValue);
                }
                return ResolveSceneGameObject(0, patch.StringValue, string.Empty);
            }
            throw new ArgumentException("Unsupported object reference for property " + patch.Path + ".");
        }

        private static void ApplyGameObjectSettings(
            GameObject gameObject,
            ManageGameObjectArguments arguments,
            string undoLabel)
        {
            if (!arguments.HasTag && !arguments.HasLayer && !arguments.HasStatic)
            {
                return;
            }
            Undo.RecordObject(gameObject, undoLabel);
            if (arguments.HasTag)
            {
                gameObject.tag = arguments.Tag;
            }
            if (arguments.HasLayer)
            {
                int layer = LayerMask.NameToLayer(arguments.Layer);
                if (layer < 0 && !int.TryParse(arguments.Layer, out layer))
                {
                    throw new ArgumentException("Layer was not found: " + arguments.Layer + ".");
                }
                if (layer < 0 || layer > 31)
                {
                    throw new ArgumentException("Layer must resolve to an index from 0 to 31.");
                }
                gameObject.layer = layer;
            }
            if (arguments.HasStatic)
            {
                GameObjectUtility.SetStaticEditorFlags(
                    gameObject,
                    arguments.IsStatic ? (StaticEditorFlags)(-1) : 0);
            }
        }

        private static void ApplyComponentAdds(
            GameObject gameObject,
            ComponentMutation[] mutations,
            string undoLabel)
        {
            if (mutations == null)
            {
                return;
            }
            for (int index = 0; index < mutations.Length; index++)
            {
                ComponentMutation mutation = mutations[index];
                Type type = ResolveComponentType(mutation.TypeName);
                if (typeof(Transform).IsAssignableFrom(type))
                {
                    throw new ArgumentException("Transform components cannot be added explicitly.");
                }
                Component component = Undo.AddComponent(gameObject, type);
                ApplySerializedPatches(component, mutation.Properties);
                EditorUtility.SetDirty(component);
            }
        }

        private static void ApplyComponentRemovals(
            GameObject gameObject,
            string[] typeNames,
            string undoLabel)
        {
            if (typeNames == null)
            {
                return;
            }
            for (int index = 0; index < typeNames.Length; index++)
            {
                Type type = ResolveComponentType(typeNames[index]);
                if (typeof(Transform).IsAssignableFrom(type))
                {
                    throw new ArgumentException("Transform components cannot be removed.");
                }
                Component component = gameObject.GetComponent(type);
                if (component == null)
                {
                    throw new ArgumentException("Component was not found: " + typeNames[index] + ".");
                }
                Undo.DestroyObjectImmediate(component);
            }
        }

        private static void ApplyComponentProperties(
            GameObject gameObject,
            ComponentMutation[] mutations,
            string undoLabel)
        {
            if (mutations == null)
            {
                return;
            }
            for (int index = 0; index < mutations.Length; index++)
            {
                ComponentMutation mutation = mutations[index];
                Type type = ResolveComponentType(mutation.TypeName);
                Component component = gameObject.GetComponent(type);
                if (component == null)
                {
                    throw new ArgumentException("Component was not found: " + mutation.TypeName + ".");
                }
                Undo.RecordObject(component, undoLabel);
                ApplySerializedPatches(component, mutation.Properties);
                EditorUtility.SetDirty(component);
            }
        }

        private static string SaveCreatedPrefab(
            GameObject gameObject,
            ManageGameObjectArguments arguments)
        {
            string path = arguments.PrefabPath;
            if (string.IsNullOrWhiteSpace(path))
            {
                string folder = string.IsNullOrWhiteSpace(arguments.PrefabFolder)
                    ? "Assets/Prefabs"
                    : arguments.PrefabFolder.Replace('\\', '/').TrimEnd('/');
                path = folder + "/" + gameObject.name + ".prefab";
            }
            path = path.Replace('\\', '/');
            if (!path.StartsWith("Assets/", StringComparison.Ordinal) || path.Contains("../"))
            {
                throw new ArgumentException("prefab_path must stay below Assets/.");
            }
            if (!path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
            {
                path += ".prefab";
            }
            EnsureAssetFolder(Path.GetDirectoryName(path).Replace('\\', '/'));
            GameObject prefab = PrefabUtility.SaveAsPrefabAsset(gameObject, path);
            if (prefab == null)
            {
                throw new InvalidOperationException("Unity failed to save prefab: " + path + ".");
            }
            AssetDatabase.SaveAssets();
            return path;
        }

        private static void EnsureAssetFolder(string folder)
        {
            string normalized = string.IsNullOrEmpty(folder) ? "Assets" : folder;
            if (AssetDatabase.IsValidFolder(normalized))
            {
                return;
            }
            string parent = Path.GetDirectoryName(normalized).Replace('\\', '/');
            EnsureAssetFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(normalized));
        }

        private static Vector3 DirectionVector(string value)
        {
            switch ((value ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "left": return Vector3.left;
                case "right": return Vector3.right;
                case "up": return Vector3.up;
                case "down": return Vector3.down;
                case "forward":
                case "front": return Vector3.forward;
                case "back":
                case "backward":
                case "behind": return Vector3.back;
                default:
                    throw new ArgumentException(
                        "direction must be left, right, up, down, forward/front, or back/backward/behind.");
            }
        }

        private static Transform ResolveOptionalTransform(
            bool hasValue,
            int instanceId,
            string target,
            string searchMethod)
        {
            if (!hasValue || (instanceId == 0 && string.IsNullOrWhiteSpace(target)))
            {
                return null;
            }
            return ResolveSceneGameObject(instanceId, target, searchMethod).transform;
        }

        private static bool IsEmpty<T>(T[] values)
        {
            return values == null || values.Length == 0;
        }

        private static void ApplyTransformChanges(
            Transform transform,
            ManageGameObjectArguments arguments,
            string undoLabel)
        {
            if (!arguments.HasPosition && !arguments.HasRotation && !arguments.HasScale)
            {
                return;
            }

            Undo.RecordObject(transform, undoLabel);
            if (arguments.HasPosition)
            {
                transform.position = arguments.Position;
            }

            if (arguments.HasRotation)
            {
                transform.eulerAngles = arguments.RotationEuler;
            }

            if (arguments.HasScale)
            {
                transform.localScale = arguments.Scale;
            }
        }

        private static GameObject CreatePrimitive(string primitive)
        {
            string normalized = primitive == null
                ? string.Empty
                : primitive.Trim();
            if (normalized.Length == 0 ||
                string.Equals(normalized, "empty", StringComparison.OrdinalIgnoreCase))
            {
                return new GameObject();
            }

            PrimitiveType primitiveType;
            if (!Enum.TryParse(normalized, true, out primitiveType))
            {
                throw new ArgumentException(
                    "primitive must be empty, Cube, Sphere, Capsule, Cylinder, Plane, or Quad.");
            }

            if (!Enum.IsDefined(typeof(PrimitiveType), primitiveType))
            {
                throw new ArgumentException(
                    "primitive must be empty, Cube, Sphere, Capsule, Cylinder, Plane, or Quad.");
            }

            return GameObject.CreatePrimitive(primitiveType);
        }

        private static Type ResolveComponentType(string requestedName)
        {
            string name = requestedName == null ? string.Empty : requestedName.Trim();
            if (name.Length == 0)
            {
                throw new ArgumentException("component_type is required.");
            }

            List<Type> simpleMatches = new List<Type>();
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int assemblyIndex = 0; assemblyIndex < assemblies.Length; assemblyIndex++)
            {
                Type[] types;
                try
                {
                    types = assemblies[assemblyIndex].GetTypes();
                }
                catch (ReflectionTypeLoadException exception)
                {
                    types = exception.Types;
                }

                for (int typeIndex = 0; typeIndex < types.Length; typeIndex++)
                {
                    Type type = types[typeIndex];
                    if (type == null || !typeof(Component).IsAssignableFrom(type))
                    {
                        continue;
                    }

                    if (string.Equals(type.FullName, name, StringComparison.Ordinal))
                    {
                        return type;
                    }

                    if (string.Equals(type.Name, name, StringComparison.Ordinal))
                    {
                        simpleMatches.Add(type);
                    }
                }
            }

            if (simpleMatches.Count == 1)
            {
                return simpleMatches[0];
            }

            if (simpleMatches.Count > 1)
            {
                throw new ArgumentException(
                    "component_type is ambiguous; use the namespace-qualified type name.");
            }

            throw new ArgumentException("Component type was not found: " + name + ".");
        }

        private static Component ResolveComponent(GameObject gameObject, Type type, int index)
        {
            Component[] components = gameObject.GetComponents(type);
            if (index < 0 || index >= components.Length)
            {
                throw new ArgumentException(
                    "component_index " + index + " is out of range for " + type.FullName +
                    " (count=" + components.Length + ").");
            }

            return components[index];
        }

        private static GameObject ResolveSceneGameObject(int instanceId)
        {
            return ResolveSceneGameObject(instanceId, null, null);
        }

        private static GameObject ResolveSceneGameObject(
            int instanceId,
            string target,
            string searchMethod)
        {
            if (instanceId != 0)
            {
                GameObject byId = EditorUtility.InstanceIDToObject(instanceId) as GameObject;
                if (IsLoadedSceneObject(byId))
                {
                    return byId;
                }
                throw new ArgumentException(
                    "Loaded scene GameObject was not found for instance ID " + instanceId + ".");
            }

            string value = target == null ? string.Empty : target.Trim();
            if (value.Length == 0)
            {
                throw new ArgumentException("target must identify a loaded scene GameObject.");
            }
            int parsedId;
            if (int.TryParse(value, out parsedId) && parsedId != 0)
            {
                return ResolveSceneGameObject(parsedId, null, null);
            }

            string method = (searchMethod ?? string.Empty).Trim().ToLowerInvariant();
            if (method.Length == 0)
            {
                method = value.Contains("/") ? "by_path" : "by_name";
            }
            List<GameObject> matches = new List<GameObject>();
            GameObject[] all = Resources.FindObjectsOfTypeAll<GameObject>();
            for (int index = 0; index < all.Length; index++)
            {
                GameObject candidate = all[index];
                if (!IsLoadedSceneObject(candidate))
                {
                    continue;
                }

                bool matched;
                switch (method)
                {
                    case "by_name":
                        matched = string.Equals(candidate.name, value, StringComparison.Ordinal);
                        break;
                    case "by_path":
                        matched = string.Equals(GetPath(candidate.transform), value, StringComparison.Ordinal);
                        break;
                    case "by_tag":
                        matched = string.Equals(SafeGetTag(candidate), value, StringComparison.Ordinal);
                        break;
                    case "by_layer":
                        int layer = LayerMask.NameToLayer(value);
                        matched = layer >= 0 && candidate.layer == layer;
                        break;
                    case "by_component":
                        matched = candidate.GetComponent(ResolveComponentType(value)) != null;
                        break;
                    case "by_id":
                        matched = candidate.GetInstanceID().ToString() == value;
                        break;
                    default:
                        throw new ArgumentException("Unknown search_method: " + searchMethod + ".");
                }
                if (matched)
                {
                    matches.Add(candidate);
                }
            }

            if (matches.Count == 0)
            {
                throw new ArgumentException(
                    "Loaded scene GameObject was not found for target '" + value + "'.");
            }
            if (matches.Count > 1 && method != "by_path" && method != "by_id")
            {
                throw new ArgumentException(
                    "Target '" + value + "' is ambiguous (" + matches.Count +
                    " matches); use an instance ID or hierarchy path.");
            }
            return matches[0];
        }

        private static bool IsLoadedSceneObject(GameObject gameObject)
        {
            return gameObject != null && !EditorUtility.IsPersistent(gameObject) &&
                gameObject.scene.IsValid() && gameObject.scene.isLoaded;
        }

        private static void EnsureSceneEditingAllowed()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                throw new InvalidOperationException(
                    "Scene edits are unavailable while Unity is compiling or updating assets.");
            }

            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                throw new InvalidOperationException(
                    "Scene edits are disabled while entering or running Play Mode.");
            }
        }

        private static string NormalizeAction(string action)
        {
            return action == null ? string.Empty : action.Trim().ToLowerInvariant();
        }

        private static string ValidateName(string name)
        {
            string value = name == null ? string.Empty : name.Trim();
            if (value.Length == 0 || value.Length > 200)
            {
                throw new ArgumentException("name must contain 1 to 200 characters.");
            }

            return value;
        }

        private static void MarkSceneDirty(GameObject gameObject)
        {
            if (gameObject != null && gameObject.scene.IsValid())
            {
                EditorSceneManager.MarkSceneDirty(gameObject.scene);
            }
        }

        private static GameObjectDetail CreateDetail(GameObject gameObject)
        {
            Transform transform = gameObject.transform;
            Component[] components = gameObject.GetComponents<Component>();
            List<ComponentRecord> componentRecords = new List<ComponentRecord>();
            for (int index = 0; index < components.Length; index++)
            {
                componentRecords.Add(CreateComponentRecord(components[index]));
            }

            int[] children = new int[transform.childCount];
            for (int index = 0; index < transform.childCount; index++)
            {
                children[index] = transform.GetChild(index).gameObject.GetInstanceID();
            }

            return new GameObjectDetail
            {
                InstanceId = gameObject.GetInstanceID(),
                Name = gameObject.name,
                Path = GetPath(transform),
                Scene = gameObject.scene.name,
                ParentInstanceId = transform.parent == null
                    ? 0
                    : transform.parent.gameObject.GetInstanceID(),
                ChildInstanceIds = children,
                ActiveSelf = gameObject.activeSelf,
                ActiveInHierarchy = gameObject.activeInHierarchy,
                Layer = gameObject.layer,
                Tag = SafeGetTag(gameObject),
                WorldPosition = transform.position,
                WorldRotationEuler = transform.eulerAngles,
                LocalPosition = transform.localPosition,
                LocalRotationEuler = transform.localEulerAngles,
                LocalScale = transform.localScale,
                Components = componentRecords.ToArray()
            };
        }

        private static ComponentRecord CreateComponentRecord(Component component)
        {
            if (component == null)
            {
                return new ComponentRecord
                {
                    InstanceId = 0,
                    Type = "<Missing Script>",
                    EnabledSupported = false
                };
            }

            Behaviour behaviour = component as Behaviour;
            Renderer renderer = component as Renderer;
            Collider collider = component as Collider;
            bool enabledSupported = behaviour != null || renderer != null || collider != null;
            bool enabled = behaviour != null
                ? behaviour.enabled
                : renderer != null
                    ? renderer.enabled
                    : collider != null && collider.enabled;

            return new ComponentRecord
            {
                InstanceId = component.GetInstanceID(),
                Type = component.GetType().FullName,
                EnabledSupported = enabledSupported,
                Enabled = enabled
            };
        }

        private static string SafeGetTag(GameObject gameObject)
        {
            try
            {
                return gameObject.tag;
            }
            catch (UnityException)
            {
                return string.Empty;
            }
        }

        private static string GetPath(Transform transform)
        {
            List<string> parts = new List<string>();
            Transform current = transform;
            while (current != null)
            {
                parts.Add(current.name);
                current = current.parent;
            }

            parts.Reverse();
            return string.Join("/", parts.ToArray());
        }

        [Serializable]
        private sealed class GetGameObjectArguments
        {
            public int InstanceId;
        }

        [Serializable]
        private sealed class ManageGameObjectArguments
        {
            public string Action;
            public int InstanceId;
            public int TargetInstanceId;
            public string Target;
            public string SearchMethod;
            public string Name;
            public string Primitive;
            public bool Confirm;
            public bool HasName;
            public bool HasParent;
            public int ParentInstanceId;
            public string ParentTarget;
            public bool HasActive;
            public bool Active;
            public bool HasTag;
            public string Tag;
            public bool HasLayer;
            public string Layer;
            public bool HasStatic;
            public bool IsStatic;
            public bool HasPosition;
            public Vector3 Position;
            public bool HasRotation;
            public Vector3 RotationEuler;
            public bool HasScale;
            public Vector3 Scale;
            public ComponentMutation[] ComponentsToAdd;
            public string[] ComponentsToRemove;
            public ComponentMutation[] ComponentProperties;
            public bool SaveAsPrefab;
            public string PrefabPath;
            public string PrefabFolder;
            public string NewName;
            public bool HasOffset;
            public Vector3 Offset;
            public int ReferenceInstanceId;
            public string ReferenceTarget;
            public string Direction;
            public bool HasDistance;
            public float Distance;
            public bool WorldSpace;
            public bool HasLookAtPosition;
            public Vector3 LookAtPosition;
            public int LookAtInstanceId;
            public string LookAtTarget;
            public bool HasLookAtUp;
            public Vector3 LookAtUp;
        }

        [Serializable]
        private sealed class ManageComponentArguments
        {
            public string Action;
            public int InstanceId;
            public string Target;
            public string SearchMethod;
            public string ComponentType;
            public int ComponentIndex;
            public string PropertyPath;
            public bool Confirm;
            public string ValueKind;
            public int IntValue;
            public float FloatValue;
            public bool BoolValue;
            public string StringValue;
            public int VectorLength;
            public Vector4 VectorValue;
            public SerializedPatch[] Properties;
        }

        [Serializable]
        private sealed class ComponentMutation
        {
            public string TypeName;
            public SerializedPatch[] Properties;
        }

        [Serializable]
        private sealed class SerializedPatch
        {
            public string Path;
            public string Kind;
            public bool BoolValue;
            public int IntValue;
            public float FloatValue;
            public string StringValue;
            public int VectorLength;
            public float[] FloatValues;
            public string ReferencePath;
            public string ReferenceGuid;
        }

        [Serializable]
        private sealed class GameObjectDetail
        {
            public int InstanceId;
            public string Name;
            public string Path;
            public string Scene;
            public int ParentInstanceId;
            public int[] ChildInstanceIds;
            public bool ActiveSelf;
            public bool ActiveInHierarchy;
            public int Layer;
            public string Tag;
            public Vector3 WorldPosition;
            public Vector3 WorldRotationEuler;
            public Vector3 LocalPosition;
            public Vector3 LocalRotationEuler;
            public Vector3 LocalScale;
            public ComponentRecord[] Components;
        }

        [Serializable]
        private sealed class ComponentRecord
        {
            public int InstanceId;
            public string Type;
            public bool EnabledSupported;
            public bool Enabled;
        }

        [Serializable]
        private sealed class MutationResult
        {
            public bool Ok;
            public string Action;
            public int InstanceId;
            public string Path;
            public string UndoLabel;
            public string PropertyPath;
            public string PrefabPath;
            public ComponentRecord Component;
            public GameObjectDetail GameObject;

            public static MutationResult From(
                string action,
                GameObjectDetail detail,
                string undoLabel,
                ComponentRecord component = null,
                string propertyPath = "")
            {
                return new MutationResult
                {
                    Ok = true,
                    Action = action,
                    InstanceId = detail.InstanceId,
                    Path = detail.Path,
                    UndoLabel = undoLabel,
                    PropertyPath = propertyPath,
                    Component = component,
                    GameObject = detail
                };
            }
        }
    }
}
#endif
