using System;
using System.IO;

namespace UnityCliConnector
{
    /// Size of an image Unity just wrote, taken from its header instead of decoding it.
    internal static class ImageDimensions
    {
        const int PngHeaderLength = 24;

        /// PNG IHDR width and height. ScreenCapture only ever writes PNG, so a file with a
        /// different (or not yet written) header reports no size rather than guessing — a
        /// screenshot call still succeeded, and the caller reports the byte count instead.
        internal static bool TryReadPng(string path, out int width, out int height)
        {
            width = 0;
            height = 0;

            var header = new byte[PngHeaderLength];
            int read;

            // ReadWrite share: the writer may still be finishing the file.
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                read = stream.Read(header, 0, PngHeaderLength);
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }

            if (read < PngHeaderLength) return false;
            if (!IsPng(header)) return false;

            width = ReadBigEndianInt32(header, 16);
            height = ReadBigEndianInt32(header, 20);
            return width > 0 && height > 0;
        }

        static bool IsPng(byte[] header)
        {
            if (header[0] != 0x89 || header[1] != (byte)'P' || header[2] != (byte)'N' || header[3] != (byte)'G')
                return false;
            if (header[4] != 0x0D || header[5] != 0x0A || header[6] != 0x1A || header[7] != 0x0A)
                return false;
            // IHDR is always the first chunk, and its length is always 13.
            return header[8] == 0 && header[9] == 0 && header[10] == 0 && header[11] == 13
                && header[12] == (byte)'I' && header[13] == (byte)'H'
                && header[14] == (byte)'D' && header[15] == (byte)'R';
        }

        static int ReadBigEndianInt32(byte[] bytes, int offset)
        {
            return (bytes[offset] << 24) | (bytes[offset + 1] << 16) | (bytes[offset + 2] << 8) | bytes[offset + 3];
        }
    }
}
