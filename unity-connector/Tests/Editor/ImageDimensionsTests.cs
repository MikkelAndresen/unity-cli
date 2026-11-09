using System.Collections.Generic;
using System.IO;
using NUnit.Framework;

namespace UnityCliConnector.Tests
{
    [TestFixture]
    public class ImageDimensionsTests
    {
        private readonly List<string> m_Created = new();

        [TearDown]
        public void TearDown()
        {
            foreach (var path in m_Created)
                if (File.Exists(path))
                    File.Delete(path);
            m_Created.Clear();
        }

        [Test]
        public void TryReadPng_ReadsSizeFromTheIhdr()
        {
            var path = Write("ihdr", PngHeader(764, 490));

            Assert.IsTrue(ImageDimensions.TryReadPng(path, out var width, out var height));
            Assert.AreEqual(764, width);
            Assert.AreEqual(490, height);
        }

        [Test]
        public void TryReadPng_FailsQuietlyOnAnythingThatIsNotACompletePngHeader()
        {
            // ScreenCapture may still be writing when the tool reads, and a caller may have
            // asked for a path another tool wrote. Neither is an error worth throwing.
            Assert.IsFalse(ImageDimensions.TryReadPng(Write("jpegish", new byte[64]), out _, out _));
            Assert.IsFalse(ImageDimensions.TryReadPng(Write("truncated", PngHeader(64, 64), 12), out _, out _));
            Assert.IsFalse(ImageDimensions.TryReadPng(Write("empty", new byte[0]), out _, out _));
            Assert.IsFalse(ImageDimensions.TryReadPng(Path.Combine(Path.GetTempPath(), "no-such-file.png"),
                out _, out _));
        }

        [Test]
        public void TryReadPng_ReadsWhileAWriterStillHoldsTheFile()
        {
            var path = TempPath("being-written");
            m_Created.Add(path);

            using (var writer = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
            {
                writer.Write(PngHeader(320, 200), 0, 24);
                writer.Flush();

                Assert.IsTrue(ImageDimensions.TryReadPng(path, out var width, out var height));
                Assert.AreEqual(320, width);
                Assert.AreEqual(200, height);
            }
        }

        [Test]
        public void IsCompletePng_TrueOnlyWhenTheFileEndsWithIend()
        {
            // A capture still being written has the header and no IEND, wherever it stopped.
            var complete = CompletePng(64, 64);
            Assert.IsTrue(ImageDimensions.IsCompletePng(Write("complete", complete)));
            Assert.IsFalse(ImageDimensions.IsCompletePng(Write("header-only", PngHeader(64, 64))));
            Assert.IsFalse(ImageDimensions.IsCompletePng(Write("short-trailer", complete, complete.Length - 1)));

            var badCrc = CompletePng(64, 64);
            badCrc[badCrc.Length - 1] ^= 0xFF;
            Assert.IsFalse(ImageDimensions.IsCompletePng(Write("bad-crc", badCrc)));

            Assert.IsFalse(ImageDimensions.IsCompletePng(Path.Combine(Path.GetTempPath(), "no-such-file.png")));
        }

        static byte[] PngHeader(int width, int height)
        {
            var bytes = new byte[24];
            byte[] signature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
            System.Array.Copy(signature, bytes, 8);
            WriteBigEndian(bytes, 8, 13);
            bytes[12] = (byte)'I'; bytes[13] = (byte)'H'; bytes[14] = (byte)'D'; bytes[15] = (byte)'R';
            WriteBigEndian(bytes, 16, width);
            WriteBigEndian(bytes, 20, height);
            return bytes;
        }

        static byte[] CompletePng(int width, int height)
        {
            var bytes = new byte[36];
            System.Array.Copy(PngHeader(width, height), bytes, 24);
            bytes[28] = (byte)'I'; bytes[29] = (byte)'E'; bytes[30] = (byte)'N'; bytes[31] = (byte)'D';
            bytes[32] = 0xAE; bytes[33] = 0x42; bytes[34] = 0x60; bytes[35] = 0x82;
            return bytes;
        }

        static void WriteBigEndian(byte[] bytes, int offset, int value)
        {
            bytes[offset] = (byte)(value >> 24);
            bytes[offset + 1] = (byte)(value >> 16);
            bytes[offset + 2] = (byte)(value >> 8);
            bytes[offset + 3] = (byte)value;
        }

        string Write(string name, byte[] contents, int length = -1)
        {
            var path = TempPath(name);
            m_Created.Add(path);
            if (length < 0) length = contents.Length;
            File.WriteAllBytes(path, length == contents.Length ? contents : Subarray(contents, length));
            return path;
        }

        static byte[] Subarray(byte[] source, int length)
        {
            var copy = new byte[length];
            System.Array.Copy(source, copy, length);
            return copy;
        }

        static string TempPath(string name)
        {
            return Path.Combine(Path.GetTempPath(), $"unity-cli-image-dimensions-{name}.bin");
        }
    }
}
