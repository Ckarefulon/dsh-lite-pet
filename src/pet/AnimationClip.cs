// One animation clip = a color video plus its alpha video, decoded together.
using System;
using System.Runtime.InteropServices;

namespace DshLitePet
{
    internal sealed class AnimationClip : IDisposable
    {
        public readonly CatalogEntry Entry;
        public int Width;
        public int Height;
        public byte[] Color;
        public byte[] Alpha;
        private IMFSourceReader _colorReader;
        private IMFSourceReader _alphaReader;

        public AnimationClip(CatalogEntry entry)
        {
            Entry = entry;
            _colorReader = Mf.Open(entry.ColorPath, out Width, out Height);
            int aw;
            int ah;
            _alphaReader = Mf.Open(entry.AlphaPath, out aw, out ah);
            if (aw != Width || ah != Height) throw new InvalidOperationException("alpha size mismatch for " + entry.Id);
            Color = new byte[Width * Height * 4];
            Alpha = new byte[Width * Height * 4];
        }

        /// <summary>Decodes the next frame pair. Returns false at end of clip (the caller decides whether to loop).</summary>
        public bool TryReadFrame()
        {
            long pts;
            if (!Mf.Read(_colorReader, Color, Width, Height, out pts)) return false;
            if (!Mf.Read(_alphaReader, Alpha, Width, Height, out pts)) return false;
            return true;
        }

        public void Rewind()
        {
            Mf.Rewind(_colorReader);
            Mf.Rewind(_alphaReader);
        }

        public void Dispose()
        {
            if (_colorReader != null) { Marshal.ReleaseComObject(_colorReader); _colorReader = null; }
            if (_alphaReader != null) { Marshal.ReleaseComObject(_alphaReader); _alphaReader = null; }
        }
    }
}

