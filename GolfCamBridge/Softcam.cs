using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace GolfCamBridge
{
    /// <summary>
    /// One virtual webcam backed by one softcam DLL. Each DLL variant can host exactly one camera,
    /// so the DLL is loaded dynamically and each instance gets its own function pointers.
    /// Frames must be top-down BGR24, width*3 bytes per row, width and height multiples of 4.
    /// </summary>
    public sealed class Softcam : IDisposable
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr CreateFn(int width, int height, float framerate);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void DeleteFn(IntPtr camera);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void SendFn(IntPtr camera, IntPtr imageBits);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        private delegate bool IsConnectedFn(IntPtr camera);

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadLibrary(string path);
        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Ansi)]
        private static extern IntPtr GetProcAddress(IntPtr module, string name);

        private readonly DeleteFn _delete;
        private readonly SendFn _send;
        private readonly IsConnectedFn _isConnected;
        private IntPtr _camera;

        public int Width { get; }
        public int Height { get; }

        public Softcam(string dllPath, int width, int height, float framerate)
        {
            IntPtr module = LoadLibrary(dllPath);
            if (module == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"LoadLibrary failed: {dllPath}");

            var create = Get<CreateFn>(module, "scCreateCamera");
            _delete = Get<DeleteFn>(module, "scDeleteCamera");
            _send = Get<SendFn>(module, "scSendFrame");
            _isConnected = Get<IsConnectedFn>(module, "scIsConnected");

            _camera = create(width, height, framerate);
            if (_camera == IntPtr.Zero)
                throw new InvalidOperationException(
                    $"scCreateCamera failed for {dllPath} ({width}x{height}). " +
                    "Is another GolfCamBridge already running, or is the size not a multiple of 4?");
            Width = width;
            Height = height;
        }

        public bool IsConnected => _camera != IntPtr.Zero && _isConnected(_camera);

        public void Send(IntPtr bgrTopDown) => _send(_camera, bgrTopDown);

        public void Dispose()
        {
            if (_camera != IntPtr.Zero)
            {
                _delete(_camera);
                _camera = IntPtr.Zero;
            }
        }

        private static T Get<T>(IntPtr module, string name) where T : Delegate
        {
            IntPtr p = GetProcAddress(module, name);
            if (p == IntPtr.Zero) throw new EntryPointNotFoundException(name);
            return Marshal.GetDelegateForFunctionPointer<T>(p);
        }
    }
}
