// One animation clip = one H.264 file whose frame is the color image stacked on top of
// its grayscale alpha mask (2× body height). A single decoder yields both planes.
using System;
using System.Runtime.InteropServices;

namespace DshLitePet
{
    internal sealed class AnimationClip : IDisposable
    {
        public readonly CatalogEntry Entry;
        public int Width;
        public int Height;
        public int BodyHeight;
        public byte[] Frame;
        private IMFSourceReader _reader;

        public AnimationClip(CatalogEntry entry)
        {
            Entry = entry;
            _reader = Mf.Open(entry.FilePath, out Width, out Height);
            BodyHeight = Height / 2;
            if (BodyHeight <= 0 || BodyHeight * 2 != Height) throw new InvalidOperationException("clip frame is not color+alpha stacked: " + entry.Id);
            Frame = new byte[Width * Height * 4];
        }

        /// <summary>Decodes the next frame (color on top, alpha mask below). False at end of clip.</summary>
        public bool TryReadFrame()
        {
            long stamp;
            return Mf.Read(_reader, Frame, Width, Height, out stamp);
        }

        public void Dispose()
        {
            if (_reader != null) Marshal.ReleaseComObject(_reader);
            _reader = null;
        }
    }
}


