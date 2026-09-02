#if UNITY_EDITOR
using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CSharp;
using UnityEngine;

namespace UnityMcp2019
{
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    public sealed class Mcp2019CustomToolAttribute : Attribute
    {
        public Mcp2019CustomToolAttribute(string name)
        {
            Name = name;
        }

        public string Name { get; private set; }
        public string Description { get; set; }
        public bool ReadOnly { get; set; }
    }

    internal static class Mcp2019ExecutionTools
    {
        private const int HistoryCapacity = 50;
        private static readonly List<HistoryRecord> History = new List<HistoryRecord>();

        internal static string Execute(string method, string argumentsJson)
        {
            if (method == "execute_code")
            {
                ExecuteCodeArguments arguments = Parse<ExecuteCodeArguments>(argumentsJson);
                return JsonUtility.ToJson(ExecuteCode(arguments));
            }

            if (method == "execute_custom_tool")
            {
                CustomToolArguments arguments = Parse<CustomToolArguments>(argumentsJson);
                return JsonUtility.ToJson(ExecuteCustomTool(arguments));
            }

            throw new ArgumentException("Unknown execution tool method: " + method);
        }

        internal static string ListCustomToolsJson()
        {
            return JsonUtility.ToJson(new CustomToolListResult
            {
                Success = true,
                Tools = DiscoverCustomTools()
                    .Select(item => new CustomToolInfo
                    {
                        Name = item.Attribute.Name,
                        Description = item.Attribute.Description ?? string.Empty,
                        ReadOnly = item.Attribute.ReadOnly,
                        DeclaringType = item.Method.DeclaringType == null
                            ? string.Empty
                            : item.Method.DeclaringType.FullName,
                        Method = item.Method.Name
                    })
                    .OrderBy(item => item.Name)
                    .ToArray()
            });
        }

        internal static string ValidateScriptJson(string argumentsJson)
        {
            ScriptValidationArguments arguments = Parse<ScriptValidationArguments>(argumentsJson);
            return JsonUtility.ToJson(ValidateScript(arguments));
        }

        private static T Parse<T>(string json) where T : class, new()
        {
            if (string.IsNullOrEmpty(json) || json == "{}")
            {
                return new T();
            }

            return JsonUtility.FromJson<T>(json) ?? new T();
        }

        private static ExecuteCodeResult ExecuteCode(ExecuteCodeArguments arguments)
        {
            string action = Require(arguments.Action, "action").ToLowerInvariant();
            if (action == "get_history")
            {
                int limit = Math.Max(1, Math.Min(arguments.Limit <= 0 ? 10 : arguments.Limit, 50));
                HistoryRecord[] records = History
                    .Skip(Math.Max(0, History.Count - limit))
                    .Reverse()
                    .ToArray();
                return new ExecuteCodeResult
                {
                    Success = true,
                    Action = action,
                    History = records,
                    Message = "Execution history returned."
                };
            }

            if (action == "clear_history")
            {
                int count = History.Count;
                History.Clear();
                return new ExecuteCodeResult
                {
                    Success = true,
                    Action = action,
                    Message = "Cleared " + count + " execution history entries."
                };
            }

            string code;
            if (action == "replay")
            {
                if (arguments.Index < 0 || arguments.Index >= History.Count)
                {
                    throw new ArgumentException(
                        "replay index must reference an existing chronological history entry.");
                }

                code = History[arguments.Index].Code;
            }
            else if (action == "execute")
            {
                code = Require(arguments.Code, "code");
            }
            else
            {
                throw new ArgumentException(
                    "execute_code action must be execute, get_history, replay, or clear_history.");
            }

            if (arguments.SafetyChecks)
            {
                CheckCodeSafety(code);
            }

            ExecutionValue value;
            try
            {
                value = CompileAndExecute(code);
                AddHistory(code, true, value.Value, string.Empty);
            }
            catch (Exception exception)
            {
                AddHistory(code, false, string.Empty, exception.Message);
                throw;
            }

            return new ExecuteCodeResult
            {
                Success = true,
                Action = action,
                Compiler = "codedom",
                Value = value.Value,
                ValueType = value.ValueType,
                UnityObjectInstanceId = value.UnityObjectInstanceId,
                HistoryIndex = History.Count - 1,
                Message = "C# code executed in memory."
            };
        }

        private static void CheckCodeSafety(string code)
        {
            string compact = new string(code.Where(character => !char.IsWhiteSpace(character)).ToArray());
            string[] blocked =
            {
                "System.IO.File.Delete", "File.Delete(", "Directory.Delete(",
                "Process.Start(", "Environment.Exit(", "EditorApplication.Exit(",
                "while(true)", "for(;;)", "Thread.Abort(", "GC.Collect("
            };
            for (int index = 0; index < blocked.Length; index++)
            {
                string pattern = new string(blocked[index]
                    .Where(character => !char.IsWhiteSpace(character)).ToArray());
                if (compact.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    throw new InvalidOperationException(
                        "Safety checks blocked C# pattern: " + blocked[index]);
                }
            }
        }

        private static ExecutionValue CompileAndExecute(string code)
        {
            string source =
                "using System;\n" +
                "using System.Collections.Generic;\n" +
                "using UnityEngine;\n" +
                "using UnityEditor;\n" +
                "public static class __Mcp2019DynamicCode\n" +
                "{\n" +
                "    public static object Run()\n" +
                "    {\n" + code + "\n        return null;\n    }\n}";

            CompilerParameters parameters = new CompilerParameters
            {
                GenerateExecutable = false,
                GenerateInMemory = true,
                IncludeDebugInformation = false,
                TreatWarningsAsErrors = false,
                CompilerOptions = "/optimize"
            };
            HashSet<string> references = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int index = 0; index < assemblies.Length; index++)
            {
                string location;
                try
                {
                    location = assemblies[index].Location;
                }
                catch
                {
                    continue;
                }

                if (!string.IsNullOrEmpty(location) && File.Exists(location) && references.Add(location))
                {
                    parameters.ReferencedAssemblies.Add(location);
                }
            }

            using (CSharpCodeProvider provider = new CSharpCodeProvider())
            {
                CompilerResults results = provider.CompileAssemblyFromSource(parameters, source);
                List<string> diagnostics = new List<string>();
                foreach (CompilerError error in results.Errors)
                {
                    if (!error.IsWarning)
                    {
                        diagnostics.Add(
                            "line " + Math.Max(1, error.Line - 8) + ": " + error.ErrorText);
                    }
                }

                if (diagnostics.Count > 0)
                {
                    throw new InvalidOperationException(
                        "C# compilation failed: " + string.Join(" | ", diagnostics.ToArray()));
                }

                Type generatedType = results.CompiledAssembly.GetType("__Mcp2019DynamicCode");
                MethodInfo run = generatedType == null
                    ? null
                    : generatedType.GetMethod("Run", BindingFlags.Public | BindingFlags.Static);
                if (run == null)
                {
                    throw new InvalidOperationException("Compiled C# entry point was not found.");
                }

                object raw;
                try
                {
                    raw = run.Invoke(null, null);
                }
                catch (TargetInvocationException exception)
                {
                    Exception inner = exception.InnerException ?? exception;
                    throw new InvalidOperationException(
                        "Executed C# code threw " + inner.GetType().Name + ": " + inner.Message,
                        inner);
                }

                UnityEngine.Object unityObject = raw as UnityEngine.Object;
                return new ExecutionValue
                {
                    Value = raw == null ? "null" : raw.ToString(),
                    ValueType = raw == null ? "null" : raw.GetType().FullName,
                    UnityObjectInstanceId = unityObject == null ? 0 : unityObject.GetInstanceID()
                };
            }
        }

        private static ScriptValidationResult ValidateScript(ScriptValidationArguments arguments)
        {
            string level = string.IsNullOrEmpty(arguments.Level)
                ? "standard"
                : arguments.Level.ToLowerInvariant();
            if (level != "standard")
            {
                throw new ArgumentException("Unity compiler validation requires level=standard.");
            }

            CompilerParameters parameters = new CompilerParameters
            {
                GenerateExecutable = false,
                GenerateInMemory = true,
                IncludeDebugInformation = false,
                TreatWarningsAsErrors = false,
                CompilerOptions = "/optimize"
            };
            HashSet<string> references = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int index = 0; index < assemblies.Length; index++)
            {
                string location;
                try
                {
                    location = assemblies[index].Location;
                }
                catch
                {
                    continue;
                }

                if (!string.IsNullOrEmpty(location) && File.Exists(location) && references.Add(location))
                {
                    parameters.ReferencedAssemblies.Add(location);
                }
            }

            List<ScriptDiagnostic> diagnostics = new List<ScriptDiagnostic>();
            using (CSharpCodeProvider provider = new CSharpCodeProvider())
            {
                CompilerResults results = provider.CompileAssemblyFromSource(
                    parameters,
                    arguments.Contents ?? string.Empty);
                foreach (CompilerError error in results.Errors)
                {
                    diagnostics.Add(new ScriptDiagnostic
                    {
                        Severity = error.IsWarning ? "warning" : "error",
                        Code = error.ErrorNumber ?? string.Empty,
                        Line = Math.Max(0, error.Line),
                        Column = Math.Max(0, error.Column),
                        Message = error.ErrorText ?? string.Empty
                    });
                }
            }

            int errorCount = diagnostics.Count(item => item.Severity == "error");
            int warningCount = diagnostics.Count - errorCount;
            bool valid = errorCount == 0;
            return new ScriptValidationResult
            {
                Success = valid,
                Message = valid ? "Script validation passed." : "Script validation failed.",
                Data = new ScriptValidationData
                {
                    Path = arguments.AssetPath ?? string.Empty,
                    Level = level,
                    IsValid = valid,
                    Compiler = "Unity CodeDom",
                    ErrorCount = errorCount,
                    WarningCount = warningCount,
                    Diagnostics = arguments.IncludeDiagnostics
                        ? diagnostics.ToArray()
                        : new ScriptDiagnostic[0]
                }
            };
        }

        private static CustomToolResult ExecuteCustomTool(CustomToolArguments arguments)
        {
            string requestedName = Require(arguments.ToolName, "tool_name");
            List<CustomToolRegistration> matches = DiscoverCustomTools()
                .Where(item => string.Equals(
                    item.Attribute.Name,
                    requestedName,
                    StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (matches.Count != 1)
            {
                throw new ArgumentException(
                    "Custom tool must be registered exactly once; found " + matches.Count +
                    " registrations for " + requestedName + ".");
            }

            MethodInfo method = matches[0].Method;
            ParameterInfo[] parameters = method.GetParameters();
            object[] invocationArguments;
            if (parameters.Length == 0)
            {
                invocationArguments = null;
            }
            else if (parameters.Length == 1 && parameters[0].ParameterType == typeof(string))
            {
                invocationArguments = new object[] { arguments.ParametersJson ?? "{}" };
            }
            else
            {
                throw new InvalidOperationException(
                    "Custom tool methods must take no arguments or one JSON string argument.");
            }

            object value;
            try
            {
                value = method.Invoke(null, invocationArguments);
            }
            catch (TargetInvocationException exception)
            {
                Exception inner = exception.InnerException ?? exception;
                throw new InvalidOperationException(
                    "Custom tool threw " + inner.GetType().Name + ": " + inner.Message,
                    inner);
            }

            return new CustomToolResult
            {
                Success = true,
                ToolName = matches[0].Attribute.Name,
                Value = value == null ? "null" : value.ToString(),
                ValueType = value == null ? "null" : value.GetType().FullName
            };
        }

        private static List<CustomToolRegistration> DiscoverCustomTools()
        {
            List<CustomToolRegistration> tools = new List<CustomToolRegistration>();
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
                    types = exception.Types.Where(type => type != null).ToArray();
                }
                catch
                {
                    continue;
                }

                for (int typeIndex = 0; typeIndex < types.Length; typeIndex++)
                {
                    MethodInfo[] methods = types[typeIndex].GetMethods(
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                    for (int methodIndex = 0; methodIndex < methods.Length; methodIndex++)
                    {
                        Mcp2019CustomToolAttribute attribute = methods[methodIndex]
                            .GetCustomAttributes(typeof(Mcp2019CustomToolAttribute), false)
                            .FirstOrDefault() as Mcp2019CustomToolAttribute;
                        if (attribute == null || string.IsNullOrEmpty(attribute.Name))
                        {
                            continue;
                        }

                        tools.Add(new CustomToolRegistration
                        {
                            Attribute = attribute,
                            Method = methods[methodIndex]
                        });
                    }
                }
            }

            return tools;
        }

        private static void AddHistory(string code, bool success, string value, string error)
        {
            History.Add(new HistoryRecord
            {
                Index = History.Count,
                Utc = DateTime.UtcNow.ToString("o"),
                Code = code,
                Success = success,
                Value = value ?? string.Empty,
                Error = error ?? string.Empty
            });
            if (History.Count > HistoryCapacity)
            {
                History.RemoveAt(0);
                for (int index = 0; index < History.Count; index++)
                {
                    History[index].Index = index;
                }
            }
        }

        private static string Require(string value, string field)
        {
            if (string.IsNullOrEmpty(value) || value.Trim().Length == 0)
            {
                throw new ArgumentException(field + " must be a non-empty string.");
            }

            return value.Trim();
        }

        private sealed class CustomToolRegistration
        {
            public Mcp2019CustomToolAttribute Attribute;
            public MethodInfo Method;
        }

        private sealed class ExecutionValue
        {
            public string Value;
            public string ValueType;
            public int UnityObjectInstanceId;
        }

        [Serializable] private sealed class ExecuteCodeArguments
        {
            public string Action;
            public string Code;
            public bool SafetyChecks = true;
            public int Index = -1;
            public int Limit = 10;
            public string Compiler;
        }

        [Serializable] private sealed class CustomToolArguments
        {
            public string ToolName;
            public string ParametersJson;
        }

        [Serializable] private sealed class ScriptValidationArguments
        {
            public string AssetPath;
            public string Contents;
            public string Level;
            public bool IncludeDiagnostics = true;
        }

        [Serializable] private sealed class ExecuteCodeResult
        {
            public bool Success;
            public string Action;
            public string Compiler;
            public string Value;
            public string ValueType;
            public int UnityObjectInstanceId;
            public int HistoryIndex;
            public string Message;
            public HistoryRecord[] History;
        }

        [Serializable] private sealed class HistoryRecord
        {
            public int Index;
            public string Utc;
            public string Code;
            public bool Success;
            public string Value;
            public string Error;
        }

        [Serializable] private sealed class CustomToolResult
        {
            public bool Success;
            public string ToolName;
            public string Value;
            public string ValueType;
        }

        [Serializable] private sealed class CustomToolListResult
        {
            public bool Success;
            public CustomToolInfo[] Tools;
        }

        [Serializable] private sealed class ScriptValidationResult
        {
            public bool Success;
            public string Message;
            public ScriptValidationData Data;
        }

        [Serializable] private sealed class ScriptValidationData
        {
            public string Path;
            public string Level;
            public bool IsValid;
            public string Compiler;
            public int ErrorCount;
            public int WarningCount;
            public ScriptDiagnostic[] Diagnostics;
        }

        [Serializable] private sealed class ScriptDiagnostic
        {
            public string Severity;
            public string Code;
            public int Line;
            public int Column;
            public string Message;
        }

        [Serializable] private sealed class CustomToolInfo
        {
            public string Name;
            public string Description;
            public bool ReadOnly;
            public string DeclaringType;
            public string Method;
        }
    }
}
#endif
