using System;
using System.Buffers.Binary;
using System.IO;

namespace Prisc.VideoSDK
{
    /// <summary>
    /// Reads just enough of an MP4's top-level box layout to tell whether it holds any media,
    /// for a recording whose recorder died before it could finish the file.
    /// </summary>
    public static class Mp4Probe
    {
        /// <summary>
        /// True when the file holds media a player can find: a sample index (a <c>moov</c> box
        /// that is not just a fragmented file's init segment) or at least one complete
        /// <c>moof</c> fragment. A recorder killed before its first fragment leaves only the
        /// <c>ftyp</c> header (and perhaps the empty init <c>moov</c>), and a plain mp4 killed
        /// before its trailer is an <c>mdat</c> with no <c>moov</c>; both are unplayable and
        /// return false. A box cut short by the end of the file is ignored. Never throws.
        /// </summary>
        public static bool HasMedia(string path)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                long length = fs.Length;
                long pos = 0;
                while (TryReadBox(fs, pos, length, out var type, out long body, out long end))
                {
                    if (type == "moof")
                        return true;
                    if (type == "moov" && !HasChild(fs, body, end, "mvex"))
                        return true;
                    pos = end;
                }
                return false;
            }
            catch
            {
                return false;
            }
        }

        private static bool HasChild(FileStream fs, long pos, long end, string wanted)
        {
            while (TryReadBox(fs, pos, end, out var type, out _, out long next))
            {
                if (type == wanted)
                    return true;
                pos = next;
            }
            return false;
        }

        /// <summary>Reads the box header at <paramref name="pos"/>. False when there is no
        /// complete box there: fewer than 8 bytes left, a size below the header, or a box that
        /// runs past <paramref name="limit"/> (a truncated tail).</summary>
        private static bool TryReadBox(FileStream fs, long pos, long limit, out string type, out long body, out long end)
        {
            type = null;
            body = end = 0;
            if (limit - pos < 8)
                return false;

            Span<byte> header = stackalloc byte[16];
            fs.Position = pos;
            fs.ReadExactly(header[..8]);
            long size = BinaryPrimitives.ReadUInt32BigEndian(header);
            type = System.Text.Encoding.ASCII.GetString(header[4..8]);
            body = pos + 8;

            if (size == 1)
            {
                // 64-bit largesize follows the type
                if (limit - pos < 16)
                    return false;
                fs.ReadExactly(header[8..16]);
                ulong large = BinaryPrimitives.ReadUInt64BigEndian(header[8..]);
                if (large > long.MaxValue)
                    return false;
                size = (long)large;
                body = pos + 16;
            }
            else if (size == 0)
            {
                // the box runs to the end of its container
                size = limit - pos;
            }

            if (size < body - pos || size > limit - pos)
                return false;
            end = pos + size;
            return true;
        }
    }
}
