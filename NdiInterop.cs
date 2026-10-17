using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace DecklinkSwitcher
{
    internal static class NdiInterop
    {
        internal const string NdiDll = "Processing.NDI.Lib.x64.dll";

        static NdiInterop()
        {
            try
            {
                NativeLibrary.SetDllImportResolver(typeof(NdiInterop).Assembly, (libraryName, assembly, searchPath) =>
                {
                    if (libraryName.Equals(NdiDll, StringComparison.OrdinalIgnoreCase) ||
                        libraryName.Equals("Processing.NDI.Lib", StringComparison.OrdinalIgnoreCase))
                    {
                        string localPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, NdiDll);
                        if (File.Exists(localPath) && NativeLibrary.TryLoad(localPath, out IntPtr handle))
                        {
                            return handle;
                        }
                    }
                    return IntPtr.Zero;
                });
            }
            catch { }
        }

        [DllImport(NdiDll, EntryPoint = "NDIlib_initialize", CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        public static extern bool NDIlib_initialize();

        [DllImport(NdiDll, EntryPoint = "NDIlib_destroy", CallingConvention = CallingConvention.Cdecl)]
        public static extern void NDIlib_destroy();

        [DllImport(NdiDll, EntryPoint = "NDIlib_version", CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr NDIlib_version();

        [StructLayout(LayoutKind.Sequential)]
        public struct NDIlib_send_create_t
        {
            public IntPtr p_ndi_name;
            public IntPtr p_groups;
            [MarshalAs(UnmanagedType.I1)]
            public bool clock_video;
            [MarshalAs(UnmanagedType.I1)]
            public bool clock_audio;
        }

        [DllImport(NdiDll, EntryPoint = "NDIlib_send_create", CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr NDIlib_send_create(ref NDIlib_send_create_t p_create_settings);

        [DllImport(NdiDll, EntryPoint = "NDIlib_send_destroy", CallingConvention = CallingConvention.Cdecl)]
        public static extern void NDIlib_send_destroy(IntPtr p_instance);

        [StructLayout(LayoutKind.Sequential)]
        public struct NDIlib_video_frame_v2_t
        {
            public int xres;
            public int yres;
            public uint FourCC;
            public int frame_rate_N;
            public int frame_rate_D;
            public float picture_aspect_ratio;
            public int frame_format_type;
            public long timecode;
            public IntPtr p_data;
            public int line_stride_in_bytes;
            public IntPtr p_metadata;
            public long timestamp;
        }

        [DllImport(NdiDll, EntryPoint = "NDIlib_send_send_video_v2", CallingConvention = CallingConvention.Cdecl)]
        public static extern void NDIlib_send_send_video_v2(IntPtr p_instance, ref NDIlib_video_frame_v2_t p_video_data);

        [DllImport(NdiDll, EntryPoint = "NDIlib_send_send_video_async_v2", CallingConvention = CallingConvention.Cdecl)]
        public static extern void NDIlib_send_send_video_async_v2(IntPtr p_instance, ref NDIlib_video_frame_v2_t p_video_data);

        [StructLayout(LayoutKind.Sequential)]
        public struct NDIlib_audio_frame_interleaved_16s_t
        {
            public int sample_rate;
            public int no_channels;
            public int no_samples;
            public long timecode;
            public int reference_level;
            public IntPtr p_data;
        }

        [DllImport(NdiDll, EntryPoint = "NDIlib_util_send_send_audio_interleaved_16s", CallingConvention = CallingConvention.Cdecl)]
        public static extern void NDIlib_util_send_send_audio_interleaved_16s(IntPtr p_instance, ref NDIlib_audio_frame_interleaved_16s_t p_audio_data);

        public const uint FourCC_UYVY = 0x59565955;
        public const long TimecodeSynthesize = 0x7fffffffffffffffL;
    }
}
