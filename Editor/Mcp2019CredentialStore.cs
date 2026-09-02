#if UNITY_EDITOR
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace UnityMcp2019
{
    /// <summary>
    /// Stores asset-provider secrets in Windows Credential Manager. Secrets are
    /// never written to EditorPrefs, project files, MCP payloads, or process args.
    /// </summary>
    internal static class Mcp2019CredentialStore
    {
        private const uint GenericCredential = 1;
        private const uint PersistLocalMachine = 2;
        private const string TargetPrefix = "UnityMcp2019.AssetGen.";

        internal static bool IsAvailable
        {
            get
            {
#if UNITY_EDITOR_WIN
                return true;
#else
                return false;
#endif
            }
        }

        internal static bool Has(string provider)
        {
#if UNITY_EDITOR_WIN
            ValidateProvider(provider);
            IntPtr pointer;
            if (!CredRead(TargetPrefix + provider, GenericCredential, 0, out pointer))
            {
                return false;
            }
            CredFree(pointer);
            return true;
#else
            return false;
#endif
        }

        internal static void Save(string provider, string secret)
        {
#if UNITY_EDITOR_WIN
            ValidateProvider(provider);
            if (string.IsNullOrWhiteSpace(secret))
            {
                throw new ArgumentException("Provider key cannot be empty.");
            }
            byte[] bytes = Encoding.UTF8.GetBytes(secret.Trim());
            IntPtr blob = Marshal.AllocCoTaskMem(bytes.Length);
            try
            {
                Marshal.Copy(bytes, 0, blob, bytes.Length);
                NativeCredential credential = new NativeCredential
                {
                    Type = GenericCredential,
                    TargetName = TargetPrefix + provider,
                    CredentialBlobSize = (uint)bytes.Length,
                    CredentialBlob = blob,
                    Persist = PersistLocalMachine,
                    UserName = "UnityMCP"
                };
                if (!CredWrite(ref credential, 0))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }
            }
            finally
            {
                for (int index = 0; index < bytes.Length; index++) bytes[index] = 0;
                Marshal.FreeCoTaskMem(blob);
            }
#else
            throw new PlatformNotSupportedException(
                "Secure provider-key storage is currently implemented for Windows Editor.");
#endif
        }

        internal static bool Delete(string provider)
        {
#if UNITY_EDITOR_WIN
            ValidateProvider(provider);
            if (CredDelete(TargetPrefix + provider, GenericCredential, 0))
            {
                return true;
            }
            int error = Marshal.GetLastWin32Error();
            if (error == 1168) return false;
            throw new Win32Exception(error);
#else
            return false;
#endif
        }

        private static bool TryRead(string provider, out string secret)
        {
            secret = string.Empty;
#if UNITY_EDITOR_WIN
            ValidateProvider(provider);
            IntPtr pointer;
            if (!CredRead(TargetPrefix + provider, GenericCredential, 0, out pointer))
            {
                return false;
            }
            try
            {
                NativeCredential credential = (NativeCredential)Marshal.PtrToStructure(
                    pointer, typeof(NativeCredential));
                if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0)
                {
                    return false;
                }
                byte[] bytes = new byte[credential.CredentialBlobSize];
                Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
                try
                {
                    secret = Encoding.UTF8.GetString(bytes).Trim();
                    return secret.Length > 0;
                }
                finally
                {
                    for (int index = 0; index < bytes.Length; index++) bytes[index] = 0;
                }
            }
            finally
            {
                CredFree(pointer);
            }
#else
            return false;
#endif
        }

        private static void ValidateProvider(string provider)
        {
            switch (provider)
            {
                case "fal":
                case "openrouter":
                case "tripo":
                case "meshy":
                case "sketchfab":
                    return;
                default:
                    throw new ArgumentException("Unknown asset provider: " + provider + ".");
            }
        }

#if UNITY_EDITOR_WIN
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NativeCredential
        {
            public uint Flags;
            public uint Type;
            [MarshalAs(UnmanagedType.LPWStr)] public string TargetName;
            [MarshalAs(UnmanagedType.LPWStr)] public string Comment;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
            public uint CredentialBlobSize;
            public IntPtr CredentialBlob;
            public uint Persist;
            public uint AttributeCount;
            public IntPtr Attributes;
            [MarshalAs(UnmanagedType.LPWStr)] public string TargetAlias;
            [MarshalAs(UnmanagedType.LPWStr)] public string UserName;
        }

        [DllImport("Advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode,
            SetLastError = true)]
        private static extern bool CredWrite(ref NativeCredential credential, uint flags);

        [DllImport("Advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode,
            SetLastError = true)]
        private static extern bool CredRead(
            string target,
            uint type,
            uint reservedFlag,
            out IntPtr credentialPointer);

        [DllImport("Advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode,
            SetLastError = true)]
        private static extern bool CredDelete(string target, uint type, uint flags);

        [DllImport("Advapi32.dll")]
        private static extern void CredFree(IntPtr buffer);
#endif
    }
}
#endif
