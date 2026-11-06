// Animated WebP decoding through the official libwebp demuxer (no video, no Media Foundation).
using System;
using System.IO;
using System.Runtime.InteropServices;

namespace DshLitePet
{
    internal static class LibWebp
    {
        /// <summary>libwebpdemux ABI version understood by the vendored DLL (WebP 1.x).</summary>
        public const int AbiVersion = 0x0107;

        [StructLayout(LayoutKind.Sequential)]
        internal struct WebPData
        {
            public IntPtr Bytes;
            public UIntPtr Size;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct AnimInfo
        {
            public uint CanvasWidth, CanvasHeight, LoopCount, Background, FrameCount;
            public uint Pad0, Pad1, Pad2, Pad3;
        }

        [DllImport("libwebp.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int WebPGetInfo(IntPtr data, UIntPtr size, out int width, out int height);

        [DllImport("libwebpdemux.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr WebPAnimDecoderNewInternal(ref WebPData data, IntPtr options, int abiVersion);

        [DllImport("libwebpdemux.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int WebPAnimDecoderGetInfo(IntPtr decoder, out AnimInfo info);

        [DllImport("libwebpdemux.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int WebPAnimDecoderGetNext(IntPtr decoder, out IntPtr buffer, out int timestamp);

        [DllImport("libwebpdemux.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int WebPAnimDecoderHasMoreFrames(IntPtr decoder);

        [DllImport("libwebpdemux.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void WebPAnimDecoderReset(IntPtr decoder);

        [DllImport("libwebpdemux.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void WebPAnimDecoderDelete(IntPtr decoder);
    }

    /// <summary>One animated WebP file, decoded frame by frame on demand.</summary>
    internal sealed class WebpAnimation : IDisposable
    {
        public readonly CatalogEntry Entry;
        public readonly int Width;
        public readonly int Height;
        public readonly int FrameCount;
        private byte[] _bytes;
        private GCHandle _pin;
        private IntPtr _decoder;

        public WebpAnimation(CatalogEntry entry)
        {
            Entry = entry;
            string path = entry.FilePath;
            _bytes = File.ReadAllBytes(path);
            _pin = GCHandle.Alloc(_bytes, GCHandleType.Pinned);
            var data = new LibWebp.WebPData { Bytes = _pin.AddrOfPinnedObject(), Size = (UIntPtr)_bytes.Length };
            _decoder = LibWebp.WebPAnimDecoderNewInternal(ref data, IntPtr.Zero, LibWebp.AbiVersion);
            if (_decoder == IntPtr.Zero) throw new InvalidOperationException("not an animated WebP: " + path);
            LibWebp.AnimInfo info;
            if (LibWebp.WebPAnimDecoderGetInfo(_decoder, out info) == 0) throw new InvalidOperationException("cannot read WebP info: " + path);
            Width = (int)info.CanvasWidth;
            Height = (int)info.CanvasHeight;
            FrameCount = (int)info.FrameCount;
        }

        /// <summary>Next composited RGBA frame; timestamp is in milliseconds from the animation start.</summary>
        public bool TryNextFrame(out IntPtr buffer, out int timestamp)
        {
            return LibWebp.WebPAnimDecoderGetNext(_decoder, out buffer, out timestamp) != 0 && buffer != IntPtr.Zero;
        }

        public void Reset()
        {
            LibWebp.WebPAnimDecoderReset(_decoder);
        }

        public void Dispose()
        {
            if (_decoder != IntPtr.Zero)
            {
                LibWebp.WebPAnimDecoderDelete(_decoder);
                _decoder = IntPtr.Zero;
            }
            if (_pin.IsAllocated) _pin.Free();
            _bytes = null;
        }
    }
}

