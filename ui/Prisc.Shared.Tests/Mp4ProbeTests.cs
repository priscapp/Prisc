using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Text;
using Prisc.VideoSDK;
using Xunit;

namespace Prisc.Shared.Tests
{
    /// <summary>Box layouts the recorder leaves behind: OBS's hybrid mp4 (mp4_output) writes
    /// ftyp + free + init moov (with mvex) + moof/mdat fragments and, on a clean stop, rewrites
    /// it to ftyp + mdat + moov; ffmpeg_muxer writes ftyp + mdat and appends moov on stop.</summary>
    public class Mp4ProbeTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "Mp4ProbeTests_" + Guid.NewGuid().ToString("N"));

        public Mp4ProbeTests() => Directory.CreateDirectory(_dir);

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch { }
        }

        private static byte[] Box(string type, params byte[][] children) => Box(type, children.SelectMany(c => c).ToArray());

        private static byte[] Box(string type, byte[] body)
        {
            var box = new byte[8 + body.Length];
            BinaryPrimitives.WriteUInt32BigEndian(box, (uint)box.Length);
            Encoding.ASCII.GetBytes(type, 0, 4, box, 4);
            body.CopyTo(box, 8);
            return box;
        }

        private static byte[] Payload(int n) => Enumerable.Repeat((byte)0xAB, n).ToArray();

        // the 40-byte header a recorder killed inside its first fragment leaves: OBS patches the
        // ftyp size in after the fact, so on disk it is still 0
        private static byte[] UnpatchedFtyp()
        {
            var ftyp = Box("ftyp", Encoding.ASCII.GetBytes("iso6\0\0\0\0iso6isomobs1iso2avc1mp41"));
            BinaryPrimitives.WriteUInt32BigEndian(ftyp, 0);
            return ftyp;
        }

        private static readonly byte[] Ftyp = Box("ftyp", Encoding.ASCII.GetBytes("isom\0\0\0\0isomiso2avc1mp41"));
        private static readonly byte[] Free = Box("free", new byte[8]);
        private static readonly byte[] InitMoov = Box("moov", Box("mvhd", Payload(100)), Box("trak", Payload(200)), Box("mvex", Box("trex", Payload(24))));
        private static readonly byte[] FullMoov = Box("moov", Box("mvhd", Payload(100)), Box("trak", Payload(200)));
        private static readonly byte[] Moof = Box("moof", Box("mfhd", Payload(8)), Box("traf", Payload(64)));
        private static readonly byte[] Mdat = Box("mdat", Payload(4096));

        private string Write(params byte[][] parts)
        {
            var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".mp4");
            File.WriteAllBytes(path, parts.SelectMany(p => p).ToArray());
            return path;
        }

        [Fact]
        public void FtypOnly_HasNoMedia()
        {
            var path = Write(UnpatchedFtyp());
            Assert.Equal(40, new FileInfo(path).Length);
            Assert.False(Mp4Probe.HasMedia(path));
            Assert.False(Mp4Probe.HasMedia(Write(Ftyp, Free)));
        }

        [Fact]
        public void FragmentedInitSegmentOnly_HasNoMedia()
        {
            Assert.False(Mp4Probe.HasMedia(Write(Ftyp, Free, InitMoov)));
        }

        [Fact]
        public void KilledHybridMp4WithFragments_HasMedia()
        {
            Assert.True(Mp4Probe.HasMedia(Write(Ftyp, Free, InitMoov, Moof, Mdat, Moof, Mdat)));
        }

        [Fact]
        public void TruncatedTail_StillCountsTheCompleteFragments()
        {
            var bytes = new[] { Ftyp, Free, InitMoov, Moof, Mdat, Moof, Mdat }.SelectMany(p => p).ToArray();
            Assert.True(Mp4Probe.HasMedia(Write(bytes[..^1000])));
        }

        [Fact]
        public void TruncatedFirstFragment_HasNoMedia()
        {
            var moof = Moof[..^10]; // the moof box itself is cut short
            Assert.False(Mp4Probe.HasMedia(Write(Ftyp, Free, InitMoov, moof)));
        }

        [Fact]
        public void FinishedMp4_HasMedia()
        {
            Assert.True(Mp4Probe.HasMedia(Write(Ftyp, Mdat, FullMoov))); // hybrid after its remux / plain mp4
            Assert.True(Mp4Probe.HasMedia(Write(Ftyp, FullMoov, Mdat))); // +faststart
        }

        [Fact]
        public void LargesizeMdat_IsWalked()
        {
            var mdat = new byte[16 + 64];
            BinaryPrimitives.WriteUInt32BigEndian(mdat, 1);
            Encoding.ASCII.GetBytes("mdat", 0, 4, mdat, 4);
            BinaryPrimitives.WriteUInt64BigEndian(mdat.AsSpan(8), (ulong)mdat.Length);
            Assert.True(Mp4Probe.HasMedia(Write(Ftyp, mdat, FullMoov)));
        }

        [Fact]
        public void PlainMp4KilledBeforeItsTrailer_HasNoMedia()
        {
            Assert.False(Mp4Probe.HasMedia(Write(Ftyp, Mdat)));
            var openMdat = Box("mdat", Payload(4096));
            BinaryPrimitives.WriteUInt32BigEndian(openMdat, 0); // size 0: runs to the end of the file
            Assert.False(Mp4Probe.HasMedia(Write(Ftyp, openMdat)));
        }

        [Fact]
        public void GarbageAndMissingFiles_DoNotThrow()
        {
            Assert.False(Mp4Probe.HasMedia(Path.Combine(_dir, "missing.mp4")));
            Assert.False(Mp4Probe.HasMedia(Write(new byte[] { 0, 0, 0, 4, 1, 2, 3 })));
            Assert.False(Mp4Probe.HasMedia(Write(Encoding.ASCII.GetBytes("\0\0\0\u0003moovxxxxxxxxxxxx"))));
            Assert.False(Mp4Probe.HasMedia(Write(Array.Empty<byte>())));
        }

        [Fact]
        public void OpenForWriting_IsStillReadable()
        {
            var path = Write(Ftyp, Free, InitMoov, Moof, Mdat);
            using var writer = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            Assert.True(Mp4Probe.HasMedia(path));
        }
    }
}
