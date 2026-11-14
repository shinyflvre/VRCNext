using System;
using System.Runtime.InteropServices;
using Valve.VR;

namespace VRCNext.Services
{
    internal static class SteamVrPaths
    {
        private const string InterfaceVersion = "IVRPaths_002";
        private const ulong ChaperoneRoot = 0x0000000600000003UL;
        private const string StandingPath = "/chaperone/standing/transform";
        private const string SeatedPath = "/chaperone/seated/transform";
        private const int WriteTypeSet = 0;

        [StructLayout(LayoutKind.Sequential)]
        private struct PathRead
        {
            public ulong Path;
            public IntPtr Buffer;
            public uint BufferSize;
            public uint Tag;
            public uint RequiredBufferSize;
            public ETrackedPropertyError Error;
            public IntPtr PathName;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PathWrite
        {
            public ulong Path;
            public int WriteType;
            public ETrackedPropertyError SetError;
            public IntPtr Buffer;
            public uint BufferSize;
            public uint Tag;
            public ETrackedPropertyError Error;
            public IntPtr PathName;
            public byte PostEvents;
            public byte ValueChanged;
        }

        [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
        private delegate ETrackedPropertyError ReadPathBatchFn(IntPtr self, ulong root, ref PathRead batch, uint count);

        [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
        private delegate ETrackedPropertyError WritePathBatchFn(IntPtr self, ulong root, ref PathWrite batch, uint count);

        [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
        private delegate ETrackedPropertyError StringToHandleFn(IntPtr self, out ulong handle, [MarshalAs(UnmanagedType.LPStr)] string path);

        private static IntPtr _iface;
        private static ReadPathBatchFn? _read;
        private static WritePathBatchFn? _write;
        private static ulong _standingHandle;
        private static ulong _seatedHandle;
        private static bool _initFailed;

        public static bool Init(Action<string> log)
        {
            if (_iface != IntPtr.Zero) return true;
            if (_initFailed) return false;
            try
            {
                var err = EVRInitError.None;
                var iface = OpenVR.GetGenericInterface(InterfaceVersion, ref err);
                if (iface == IntPtr.Zero || err != EVRInitError.None)
                {
                    _initFailed = true;
                    log($"[SteamVR] {InterfaceVersion} not available: {err}");
                    return false;
                }

                var vtable = Marshal.ReadIntPtr(iface);
                var read = Marshal.GetDelegateForFunctionPointer<ReadPathBatchFn>(Marshal.ReadIntPtr(vtable, 0 * IntPtr.Size));
                var write = Marshal.GetDelegateForFunctionPointer<WritePathBatchFn>(Marshal.ReadIntPtr(vtable, 1 * IntPtr.Size));
                var toHandle = Marshal.GetDelegateForFunctionPointer<StringToHandleFn>(Marshal.ReadIntPtr(vtable, 2 * IntPtr.Size));

                if (toHandle(iface, out var standing, StandingPath) != ETrackedPropertyError.TrackedProp_Success
                    || toHandle(iface, out var seated, SeatedPath) != ETrackedPropertyError.TrackedProp_Success)
                {
                    _initFailed = true;
                    log("[SteamVR] Chaperone transform paths could not be resolved");
                    return false;
                }

                _standingHandle = standing;
                _seatedHandle = seated;
                _read = read;
                _write = write;
                _iface = iface;
                return true;
            }
            catch (Exception ex)
            {
                _initFailed = true;
                log($"[SteamVR] {InterfaceVersion} init failed: {ex.Message}");
                return false;
            }
        }

        public static void Reset()
        {
            _iface = IntPtr.Zero;
            _read = null;
            _write = null;
            _standingHandle = 0;
            _seatedHandle = 0;
            _initFailed = false;
        }

        public static bool TryRead(bool seated, out HmdMatrix34_t transform)
        {
            transform = default;
            if (_iface == IntPtr.Zero || _read == null) return false;

            int size = Marshal.SizeOf<HmdMatrix34_t>();
            var buffer = Marshal.AllocHGlobal(size);
            var name = Marshal.StringToHGlobalAnsi(seated ? SeatedPath : StandingPath);
            try
            {
                var entry = new PathRead
                {
                    Path = seated ? _seatedHandle : _standingHandle,
                    Buffer = buffer,
                    BufferSize = (uint)size,
                    PathName = name,
                };
                var result = _read(_iface, ChaperoneRoot, ref entry, 1);
                if (result != ETrackedPropertyError.TrackedProp_Success
                    || entry.Error != ETrackedPropertyError.TrackedProp_Success
                    || entry.Tag != OpenVR.k_unHmdMatrix34PropertyTag
                    || entry.RequiredBufferSize != size)
                    return false;

                transform = Marshal.PtrToStructure<HmdMatrix34_t>(buffer);
                return true;
            }
            finally
            {
                Marshal.FreeHGlobal(name);
                Marshal.FreeHGlobal(buffer);
            }
        }

        public static bool Write(bool seated, HmdMatrix34_t transform)
        {
            if (_iface == IntPtr.Zero || _write == null) return false;

            int size = Marshal.SizeOf<HmdMatrix34_t>();
            var buffer = Marshal.AllocHGlobal(size);
            var name = Marshal.StringToHGlobalAnsi(seated ? SeatedPath : StandingPath);
            try
            {
                Marshal.StructureToPtr(transform, buffer, false);
                var entry = new PathWrite
                {
                    Path = seated ? _seatedHandle : _standingHandle,
                    WriteType = WriteTypeSet,
                    Buffer = buffer,
                    BufferSize = (uint)size,
                    Tag = OpenVR.k_unHmdMatrix34PropertyTag,
                    PathName = name,
                };
                var result = _write(_iface, ChaperoneRoot, ref entry, 1);
                return result == ETrackedPropertyError.TrackedProp_Success
                    && entry.Error == ETrackedPropertyError.TrackedProp_Success;
            }
            finally
            {
                Marshal.FreeHGlobal(name);
                Marshal.FreeHGlobal(buffer);
            }
        }
    }
}
