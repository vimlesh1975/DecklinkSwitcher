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
        public const uint FourCC_BGRA = 0x41524742;
        public const uint FourCC_BGRX = 0x58524742;
        public const long TimecodeSynthesize = 0x7fffffffffffffffL;

        // ---------------- Finder ----------------
        [StructLayout(LayoutKind.Sequential)]
        public struct NDIlib_find_create_t
        {
            [MarshalAs(UnmanagedType.I1)]
            public bool show_local_sources;
            public IntPtr p_groups;
            public IntPtr p_extra_ips;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct NDIlib_source_t
        {
            public IntPtr p_ndi_name;
            public IntPtr p_url_address;
        }

        [DllImport(NdiDll, EntryPoint = "NDIlib_find_create_v2", CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr NDIlib_find_create_v2(ref NDIlib_find_create_t p_create_settings);

        [DllImport(NdiDll, EntryPoint = "NDIlib_find_destroy", CallingConvention = CallingConvention.Cdecl)]
        public static extern void NDIlib_find_destroy(IntPtr p_instance);

        [DllImport(NdiDll, EntryPoint = "NDIlib_find_wait_for_sources", CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        public static extern bool NDIlib_find_wait_for_sources(IntPtr p_instance, uint timeout_in_ms);

        [DllImport(NdiDll, EntryPoint = "NDIlib_find_get_current_sources", CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr NDIlib_find_get_current_sources(IntPtr p_instance, out uint p_no_sources);

        // ---------------- Receiver ----------------
        public const int RecvColorFormat_UYVY_BGRA = 1;
        public const int RecvBandwidth_Highest = 100;

        [StructLayout(LayoutKind.Sequential)]
        public struct NDIlib_recv_create_v3_t
        {
            public NDIlib_source_t source_to_connect_to;
            public int color_format;
            public int bandwidth;
            [MarshalAs(UnmanagedType.I1)]
            public bool allow_video_fields;
            public IntPtr p_ndi_recv_name;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct NDIlib_audio_frame_v2_t
        {
            public int sample_rate;
            public int no_channels;
            public int no_samples;
            public long timecode;
            public IntPtr p_data; // 32-bit float planar
            public int channel_stride_in_bytes;
            public IntPtr p_metadata;
            public long timestamp;
        }

        public const int FrameType_None = 0;
        public const int FrameType_Video = 1;
        public const int FrameType_Audio = 2;

        [DllImport(NdiDll, EntryPoint = "NDIlib_recv_create_v3", CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr NDIlib_recv_create_v3(ref NDIlib_recv_create_v3_t p_create_settings);

        [DllImport(NdiDll, EntryPoint = "NDIlib_recv_destroy", CallingConvention = CallingConvention.Cdecl)]
        public static extern void NDIlib_recv_destroy(IntPtr p_instance);

        [DllImport(NdiDll, EntryPoint = "NDIlib_recv_capture_v2", CallingConvention = CallingConvention.Cdecl)]
        public static extern int NDIlib_recv_capture_v2(IntPtr p_instance, ref NDIlib_video_frame_v2_t p_video_data, ref NDIlib_audio_frame_v2_t p_audio_data, IntPtr p_metadata, uint timeout_in_ms);

        [DllImport(NdiDll, EntryPoint = "NDIlib_recv_free_video_v2", CallingConvention = CallingConvention.Cdecl)]
        public static extern void NDIlib_recv_free_video_v2(IntPtr p_instance, ref NDIlib_video_frame_v2_t p_video_data);

        [DllImport(NdiDll, EntryPoint = "NDIlib_recv_free_audio_v2", CallingConvention = CallingConvention.Cdecl)]
        public static extern void NDIlib_recv_free_audio_v2(IntPtr p_instance, ref NDIlib_audio_frame_v2_t p_audio_data);

        [DllImport(NdiDll, EntryPoint = "NDIlib_recv_get_no_connections", CallingConvention = CallingConvention.Cdecl)]
        public static extern int NDIlib_recv_get_no_connections(IntPtr p_instance);
    }
}
