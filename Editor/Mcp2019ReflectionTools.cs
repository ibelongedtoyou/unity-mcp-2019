#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace UnityMcp2019
{
    internal static class Mcp2019ReflectionTools
    {
        private const BindingFlags PublicMembers =
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;

        internal static string Execute(string argumentsJson)
        {
            ReflectArguments arguments = string.IsNullOrEmpty(argumentsJson) || argumentsJson == "{}"
                ? new ReflectArguments()
                : JsonUtility.FromJson<ReflectArguments>(argumentsJson) ?? new ReflectArguments();
            string action = Require(arguments.Action, "action").ToLowerInvariant();
            switch (action)
            {
                case "get_type":
                    return JsonUtility.ToJson(GetTypeSummary(Require(arguments.ClassName, "class_name")));
                case "get_member":
                    return JsonUtility.ToJson(GetMember(
                        Require(arguments.ClassName, "class_name"),
                        Require(arguments.MemberName, "member_name")));
                case "search":
                    return JsonUtility.ToJson(Search(
                        Require(arguments.Query, "query"),
                        string.IsNullOrEmpty(arguments.Scope) ? "all" : arguments.Scope));
                default:
                    throw new ArgumentException(
                        "unity_reflect action must be get_type, get_member, or search.");
            }
        }

        private static TypeSummaryResult GetTypeSummary(string requestedName)
        {
            Type type = ResolveType(requestedName);
            string[] constructors = type.GetConstructors(PublicMembers)
                .Select(FormatMethodBase).Distinct().OrderBy(value => value).ToArray();
            string[] methods = type.GetMethods(PublicMembers)
                .Where(method => !method.IsSpecialName)
                .Select(method => method.Name).Distinct().OrderBy(value => value).ToArray();
            string[] properties = type.GetProperties(PublicMembers)
                .Select(property => property.Name).Distinct().OrderBy(value => value).ToArray();
            string[] fields = type.GetFields(PublicMembers)
                .Select(field => field.Name).Distinct().OrderBy(value => value).ToArray();
            string[] events = type.GetEvents(PublicMembers)
                .Select(item => item.Name).Distinct().OrderBy(value => value).ToArray();
            return new TypeSummaryResult
            {
                Success = true,
                Type = TypeRecord.From(type),
                Constructors = constructors,
                Methods = methods,
                Properties = properties,
                Fields = fields,
                Events = events
            };
        }

        private static MemberResult GetMember(string requestedName, string memberName)
        {
            Type type = ResolveType(requestedName);
            List<MemberRecord> records = new List<MemberRecord>();
            MemberInfo[] members = type.GetMember(memberName, PublicMembers);
            for (int index = 0; index < members.Length; index++)
            {
                records.Add(MemberRecord.From(members[index]));
            }

            if (records.Count == 0)
            {
                throw new ArgumentException(
                    "Public member was not found: " + type.FullName + "." + memberName);
            }

            return new MemberResult
            {
                Success = true,
                Type = TypeRecord.From(type),
                MemberName = memberName,
                Members = records.ToArray()
            };
        }

        private static SearchResult Search(string query, string scope)
        {
            string normalizedScope = scope.Trim().ToLowerInvariant();
            if (normalizedScope != "all" && normalizedScope != "unity" &&
                normalizedScope != "packages" && normalizedScope != "project")
            {
                throw new ArgumentException("scope must be unity, packages, project, or all.");
            }

            List<TypeRecord> records = new List<TypeRecord>();
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int assemblyIndex = 0; assemblyIndex < assemblies.Length; assemblyIndex++)
            {
                Assembly assembly = assemblies[assemblyIndex];
                if (!AssemblyMatchesScope(assembly, normalizedScope))
                {
                    continue;
                }

                Type[] types = SafeGetTypes(assembly);
                for (int typeIndex = 0; typeIndex < types.Length; typeIndex++)
                {
                    Type type = types[typeIndex];
                    if (type == null || type.FullName == null ||
                        type.FullName.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        continue;
                    }

                    records.Add(TypeRecord.From(type));
                    if (records.Count >= 200)
                    {
                        break;
                    }
                }

                if (records.Count >= 200)
                {
                    break;
                }
            }

            records.Sort(delegate(TypeRecord left, TypeRecord right)
            {
                return string.Compare(left.FullName, right.FullName, StringComparison.Ordinal);
            });
            return new SearchResult
            {
                Success = true,
                Query = query,
                Scope = normalizedScope,
                Count = records.Count,
                Truncated = records.Count >= 200,
                Types = records.ToArray()
            };
        }

        private static Type ResolveType(string requestedName)
        {
            Type direct = Type.GetType(requestedName, false, true);
            if (direct != null)
            {
                return direct;
            }

            List<Type> matches = new List<Type>();
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int assemblyIndex = 0; assemblyIndex < assemblies.Length; assemblyIndex++)
            {
                Type exact = assemblies[assemblyIndex].GetType(requestedName, false, true);
                if (exact != null)
                {
                    return exact;
                }

                Type[] types = SafeGetTypes(assemblies[assemblyIndex]);
                for (int typeIndex = 0; typeIndex < types.Length; typeIndex++)
                {
                    Type type = types[typeIndex];
                    if (type != null && string.Equals(
                        type.Name, requestedName, StringComparison.OrdinalIgnoreCase))
                    {
                        matches.Add(type);
                    }
                }
            }

            if (matches.Count == 1)
            {
                return matches[0];
            }

            if (matches.Count > 1)
            {
                string names = string.Join(", ", matches.Take(20)
                    .Select(type => type.FullName).ToArray());
                throw new ArgumentException(
                    "Type name is ambiguous; use a fully qualified name. Matches: " + names);
            }

            throw new ArgumentException("Loaded C# type was not found: " + requestedName);
        }

        private static Type[] SafeGetTypes(Assembly assembly)
        {
            try
            {
                return assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException exception)
            {
                return exception.Types.Where(type => type != null).ToArray();
            }
            catch
            {
                return new Type[0];
            }
        }

        private static bool AssemblyMatchesScope(Assembly assembly, string scope)
        {
            if (scope == "all")
            {
                return true;
            }

            string name = assembly.GetName().Name ?? string.Empty;
            if (scope == "unity")
            {
                return name.StartsWith("UnityEngine", StringComparison.Ordinal) ||
                       name.StartsWith("UnityEditor", StringComparison.Ordinal);
            }

            if (scope == "project")
            {
                return name.StartsWith("Assembly-CSharp", StringComparison.Ordinal) ||
                       name.StartsWith("Assembly-CSharp-Editor", StringComparison.Ordinal);
            }

            string location;
            try
            {
                location = assembly.Location ?? string.Empty;
            }
            catch
            {
                location = string.Empty;
            }

            return location.IndexOf("PackageCache", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   location.IndexOf("Packages", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string FormatMethodBase(MethodBase method)
        {
            string parameters = string.Join(", ", method.GetParameters()
                .Select(parameter => FormatTypeName(parameter.ParameterType) + " " + parameter.Name)
                .ToArray());
            return method.Name + "(" + parameters + ")";
        }

        private static string FormatMember(MemberInfo member)
        {
            MethodInfo method = member as MethodInfo;
            if (method != null)
            {
                return FormatTypeName(method.ReturnType) + " " + FormatMethodBase(method);
            }

            ConstructorInfo constructor = member as ConstructorInfo;
            if (constructor != null)
            {
                return FormatMethodBase(constructor);
            }

            PropertyInfo property = member as PropertyInfo;
            if (property != null)
            {
                return FormatTypeName(property.PropertyType) + " " + property.Name +
                       " { " + (property.CanRead ? "get; " : string.Empty) +
                       (property.CanWrite ? "set; " : string.Empty) + "}";
            }

            FieldInfo field = member as FieldInfo;
            if (field != null)
            {
                return FormatTypeName(field.FieldType) + " " + field.Name;
            }

            EventInfo eventInfo = member as EventInfo;
            if (eventInfo != null)
            {
                return "event " + FormatTypeName(eventInfo.EventHandlerType) + " " + eventInfo.Name;
            }

            return member.ToString();
        }

        private static string FormatTypeName(Type type)
        {
            if (!type.IsGenericType)
            {
                return type.FullName ?? type.Name;
            }

            string name = type.GetGenericTypeDefinition().FullName ?? type.Name;
            int marker = name.IndexOf('`');
            if (marker >= 0)
            {
                name = name.Substring(0, marker);
            }

            return name + "<" + string.Join(", ", type.GetGenericArguments()
                .Select(FormatTypeName).ToArray()) + ">";
        }

        private static string Require(string value, string field)
        {
            if (string.IsNullOrEmpty(value) || value.Trim().Length == 0)
            {
                throw new ArgumentException(field + " must be a non-empty string.");
            }

            return value.Trim();
        }

        [Serializable]
        private sealed class ReflectArguments
        {
            public string Action;
            public string ClassName;
            public string MemberName;
            public string Query;
            public string Scope;
        }

        [Serializable]
        private sealed class TypeRecord
        {
            public string Name;
            public string FullName;
            public string Namespace;
            public string Assembly;
            public string BaseType;
            public bool IsClass;
            public bool IsValueType;
            public bool IsEnum;
            public bool IsInterface;

            public static TypeRecord From(Type type)
            {
                return new TypeRecord
                {
                    Name = type.Name,
                    FullName = type.FullName ?? type.Name,
                    Namespace = type.Namespace ?? string.Empty,
                    Assembly = type.Assembly.GetName().Name,
                    BaseType = type.BaseType == null ? string.Empty : FormatTypeName(type.BaseType),
                    IsClass = type.IsClass,
                    IsValueType = type.IsValueType,
                    IsEnum = type.IsEnum,
                    IsInterface = type.IsInterface
                };
            }
        }

        [Serializable]
        private sealed class MemberRecord
        {
            public string Name;
            public string Kind;
            public string Signature;
            public bool IsStatic;

            public static MemberRecord From(MemberInfo member)
            {
                bool isStatic = false;
                MethodBase method = member as MethodBase;
                if (method != null)
                {
                    isStatic = method.IsStatic;
                }
                else
                {
                    FieldInfo field = member as FieldInfo;
                    if (field != null)
                    {
                        isStatic = field.IsStatic;
                    }
                    else
                    {
                        PropertyInfo property = member as PropertyInfo;
                        MethodInfo accessor = property == null
                            ? null
                            : property.GetGetMethod() ?? property.GetSetMethod();
                        isStatic = accessor != null && accessor.IsStatic;
                    }
                }

                return new MemberRecord
                {
                    Name = member.Name,
                    Kind = member.MemberType.ToString(),
                    Signature = FormatMember(member),
                    IsStatic = isStatic
                };
            }
        }

        [Serializable]
        private sealed class TypeSummaryResult
        {
            public bool Success;
            public TypeRecord Type;
            public string[] Constructors;
            public string[] Methods;
            public string[] Properties;
            public string[] Fields;
            public string[] Events;
        }

        [Serializable]
        private sealed class MemberResult
        {
            public bool Success;
            public TypeRecord Type;
            public string MemberName;
            public MemberRecord[] Members;
        }

        [Serializable]
        private sealed class SearchResult
        {
            public bool Success;
            public string Query;
            public string Scope;
            public int Count;
            public bool Truncated;
            public TypeRecord[] Types;
        }
    }
}
#endif
