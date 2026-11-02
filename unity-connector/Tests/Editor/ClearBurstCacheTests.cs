using System;
using System.IO;
using NUnit.Framework;
using UnityCliConnector.Tools;

namespace UnityCliConnector.Tests
{
    [TestFixture]
    public class ClearBurstCacheTests
    {
        string _projectRoot;
        string _cacheRoot;
        string _jitRoot;

        [SetUp]
        public void SetUp()
        {
            _projectRoot = Path.Combine(Path.GetTempPath(), "unity-cli-burst-cache-" + Guid.NewGuid().ToString("N"));
            _cacheRoot = Path.Combine(_projectRoot, "Library", "BurstCache");
            _jitRoot = Path.Combine(_cacheRoot, "JIT");
            Directory.CreateDirectory(Path.Combine(_jitRoot, "cache"));
            File.WriteAllText(Path.Combine(_jitRoot, "DeleteCache.txt"), "marker");
            File.WriteAllBytes(Path.Combine(_jitRoot, "cache", "lib1.bin"), new byte[2048]);
            File.WriteAllText(Path.Combine(_cacheRoot, "AotSettings.hash"), "hash");
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_projectRoot))
                Directory.Delete(_projectRoot, true);
        }

        [Test]
        public void Clear_RemovesWholeCacheTree()
        {
            var report = ClearBurstCache.Clear(_projectRoot, false, false);

            Assert.IsTrue(report.Removed);
            Assert.IsFalse(Directory.Exists(_cacheRoot));
            Assert.AreEqual(3, report.Files);
            Assert.GreaterOrEqual(report.Bytes, 2048);
        }

        [Test]
        public void DryRun_CountsButLeavesEverythingOnDisk()
        {
            var report = ClearBurstCache.Clear(_projectRoot, false, true);

            Assert.AreEqual(3, report.Files);
            Assert.IsFalse(report.Removed);
            Assert.IsTrue(File.Exists(Path.Combine(_jitRoot, "cache", "lib1.bin")));
            Assert.IsTrue(File.Exists(Path.Combine(_cacheRoot, "AotSettings.hash")));
        }

        [Test]
        public void JitScope_KeepsAotSettingsHash()
        {
            var report = ClearBurstCache.Clear(_projectRoot, true, false);

            Assert.IsTrue(report.Removed);
            Assert.IsFalse(Directory.Exists(_jitRoot));
            Assert.IsTrue(File.Exists(Path.Combine(_cacheRoot, "AotSettings.hash")));
        }

        [Test]
        public void MissingCache_IsReportedAsNothingToClear()
        {
            Directory.Delete(_cacheRoot, true);

            var report = ClearBurstCache.Clear(_projectRoot, false, false);

            Assert.IsFalse(report.Existed);
            Assert.AreEqual(0, report.Files);
            Assert.IsTrue(Directory.Exists(_projectRoot));
        }

        [Test]
        public void FormatBytes_ScalesUnits()
        {
            Assert.AreEqual("512 B", ClearBurstCache.FormatBytes(512));
            Assert.AreEqual("2 KB", ClearBurstCache.FormatBytes(2048));
            Assert.AreEqual("1.5 MB", ClearBurstCache.FormatBytes(1024 * 1024 + 512 * 1024));
        }
    }
}
