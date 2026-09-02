#if UNITY_EDITOR
using System;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor.PackageManager;
using UnityEngine;

namespace UnityMcp2019.Tests
{
    public sealed class PackageLayoutTests
    {
        private const string PackageName = "com.ibelongedtoyou.unity-mcp-2019";

        [Test]
        public void PackageIsRegisteredAndContainsBundledServer()
        {
            PackageInfo package = FindPackage();
            Assert.IsNotNull(package, "The UPM package is not registered.");
            Assert.IsTrue(File.Exists(Path.Combine(package.resolvedPath, "package.json")));
            Assert.IsTrue(File.Exists(Path.Combine(package.resolvedPath, "Tools~", "server.py")));
        }

        [Test]
        public void ManifestTargetsUnity2019AndMatchesImplementationVersion()
        {
            PackageInfo package = FindPackage();
            Assert.IsNotNull(package);
            PackageManifest manifest = JsonUtility.FromJson<PackageManifest>(
                File.ReadAllText(Path.Combine(package.resolvedPath, "package.json")));
            Assert.AreEqual(PackageName, manifest.name);
            Assert.AreEqual("0.3.0", manifest.version);
            Assert.AreEqual("2019.4", manifest.unity);
            Assert.AreEqual("0.3.0", Mcp2019Bridge.BridgeVersion);
        }

        [Test]
        public void ServerSourceContainsNoLocalWorkspacePath()
        {
            PackageInfo package = FindPackage();
            Assert.IsNotNull(package);
            string source = File.ReadAllText(Path.Combine(package.resolvedPath, "Tools~", "server.py"));
            Assert.IsFalse(
                Regex.IsMatch(
                    source,
                    @"(?im)(?:^|[\s""'(])(?:[A-Z]:[\\/]|/(?:Users|home)/)"),
                "The bundled server must not contain local absolute paths.");
        }

        private static PackageInfo FindPackage()
        {
            PackageInfo package = PackageInfo.FindForAssetPath("Packages/" + PackageName);
            return package != null &&
                string.Equals(package.name, PackageName, StringComparison.Ordinal)
                    ? package
                    : null;
        }

        [Serializable]
        private sealed class PackageManifest
        {
            public string name;
            public string version;
            public string unity;
        }
    }
}
#endif
