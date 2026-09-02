#if UNITY_EDITOR
using System;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace UnityMcp2019
{
    internal static class Mcp2019ProBuilderTools
    {
        private static readonly string[] Actions =
        {
            "ping", "create_shape", "create_poly_shape", "extrude_faces", "extrude_edges",
            "bevel_edges", "subdivide", "delete_faces", "bridge_edges", "connect_elements",
            "detach_faces", "flip_normals", "merge_faces", "combine_meshes", "merge_objects",
            "duplicate_and_flip", "create_polygon", "merge_vertices", "weld_vertices",
            "split_vertices", "move_vertices", "insert_vertex", "append_vertices_to_edge",
            "select_faces", "set_face_material", "set_face_color", "set_face_uvs",
            "get_mesh_info", "convert_to_probuilder", "set_smoothing", "auto_smooth",
            "center_pivot", "freeze_transform", "set_pivot", "validate_mesh", "repair_mesh",
        };

        internal static string Execute(string argumentsJson)
        {
            ProBuilderArguments a = string.IsNullOrEmpty(argumentsJson) || argumentsJson == "{}"
                ? new ProBuilderArguments()
                : JsonUtility.FromJson<ProBuilderArguments>(argumentsJson) ?? new ProBuilderArguments();
            string action = (a.Action ?? string.Empty).Trim().ToLowerInvariant();
            if (!Actions.Contains(action)) return JsonUtility.ToJson(Fail("Unknown manage_probuilder action: " + action));
            Type meshType = FindType("UnityEngine.ProBuilder.ProBuilderMesh");
            if (meshType == null)
            {
                return JsonUtility.ToJson(Fail(
                    "ProBuilder package is not installed. Install com.unity.probuilder via Package Manager."));
            }

            // The public ProBuilder API changed materially across Unity 2019-compatible package
            // releases. Keep package discovery exact and surface the resolved version before an
            // unsupported reflective operation can corrupt mesh topology.
            if (action == "ping")
            {
                return JsonUtility.ToJson(Ok("ProBuilder tool is available.", new ProBuilderData
                {
                    UnityVersion = Application.unityVersion,
                    Available = true,
                    MeshType = meshType.FullName,
                    AssemblyVersion = meshType.Assembly.GetName().Version.ToString(),
                    Actions = Actions,
                }));
            }
            return JsonUtility.ToJson(Fail(
                "The installed ProBuilder API was detected but this Unity 2019 compatibility bridge " +
                "has not validated action '" + action + "' against package version " +
                meshType.Assembly.GetName().Version + "."));
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

        private static ProBuilderResult Ok(string message, ProBuilderData data) { return new ProBuilderResult { Success = true, Message = message, Data = data }; }
        private static ProBuilderResult Fail(string message) { return new ProBuilderResult { Success = false, Message = message, Data = new ProBuilderData { Available = false, Actions = Actions } }; }

        [Serializable] private sealed class ProBuilderArguments { public string Action, Target, SearchMethod; public ProBuilderValue[] Properties; }
        [Serializable] private sealed class ProBuilderValue { public string Name, Kind, StringValue; public bool BoolValue; public int IntValue; public double NumberValue; public float[] Numbers; public ProBuilderValue[] Children, Items; }
        [Serializable] private sealed class ProBuilderResult { public bool Success; public string Message; public ProBuilderData Data; }
        [Serializable] private sealed class ProBuilderData { public string UnityVersion, MeshType, AssemblyVersion; public bool Available; public string[] Actions; }
    }
}
#endif
