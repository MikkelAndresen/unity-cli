using System;
using System.Runtime.InteropServices;

namespace UnityCliConnector.Modal
{
    /// <summary>
    /// Objective-C runtime, CoreFoundation and libdispatch bindings used to reach
    /// AppKit from managed code without shipping a native plugin.
    /// </summary>
    /// <remarks>
    /// objc_msgSend gets one DllImport per call signature: on arm64 a message send
    /// must use the exact non-variadic prototype of the target method, so a single
    /// generic declaration would pass arguments in the wrong registers.
    /// ObjC BOOL is one byte, hence the explicit I1 marshaling.
    /// </remarks>
    internal static class MacInterop
    {
        const string OBJC = "/usr/lib/libobjc.A.dylib";
        const string LIB_SYSTEM = "/usr/lib/libSystem.dylib";
        const string CORE_FOUNDATION = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

        // dlfcn.h: #define RTLD_DEFAULT ((void *) -2) on Darwin.
        static readonly IntPtr RTLD_DEFAULT = new IntPtr(-2);

        public delegate void DispatchFunction(IntPtr context);

        [DllImport(OBJC)] static extern IntPtr objc_getClass(string name);
        [DllImport(OBJC)] static extern IntPtr sel_registerName(string name);

        [DllImport(OBJC, EntryPoint = "objc_msgSend")]
        public static extern IntPtr Send(IntPtr receiver, IntPtr selector);

        [DllImport(OBJC, EntryPoint = "objc_msgSend")]
        public static extern IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr arg);

        [DllImport(OBJC, EntryPoint = "objc_msgSend")]
        public static extern IntPtr SendIndex(IntPtr receiver, IntPtr selector, long index);

        [DllImport(OBJC, EntryPoint = "objc_msgSend")]
        public static extern long SendLong(IntPtr receiver, IntPtr selector);

        [DllImport(OBJC, EntryPoint = "objc_msgSend")]
        [return: MarshalAs(UnmanagedType.I1)]
        public static extern bool SendBool(IntPtr receiver, IntPtr selector);

        [DllImport(OBJC, EntryPoint = "objc_msgSend")]
        [return: MarshalAs(UnmanagedType.I1)]
        public static extern bool SendBool(IntPtr receiver, IntPtr selector, IntPtr arg);

        [DllImport(LIB_SYSTEM)] static extern IntPtr dlsym(IntPtr handle, string symbol);
        [DllImport(LIB_SYSTEM)] static extern void dispatch_async_f(IntPtr queue, IntPtr context, IntPtr work);

        [DllImport(CORE_FOUNDATION)] static extern IntPtr CFRunLoopGetMain();
        [DllImport(CORE_FOUNDATION)] static extern IntPtr CFRunLoopCopyCurrentMode(IntPtr runLoop);
        [DllImport(CORE_FOUNDATION)] static extern int CFRunLoopRunInMode(IntPtr mode, double seconds, [MarshalAs(UnmanagedType.I1)] bool returnAfterSourceHandled);
        [DllImport(CORE_FOUNDATION)] static extern void CFRelease(IntPtr obj);

        public static IntPtr Class(string name) => objc_getClass(name);
        public static IntPtr Sel(string name) => sel_registerName(name);

        /// NSString (or toll-free bridged CFStringRef) → managed string. Null-safe.
        public static string ToManagedString(IntPtr nsString)
        {
            if (nsString == IntPtr.Zero) return null;
            return Marshal.PtrToStringUTF8(Send(nsString, Sel("UTF8String")));
        }

        public static string ClassName(IntPtr obj) =>
            ToManagedString(Send(Send(obj, Sel("class")), Sel("description")));

        /// <summary>
        /// Mode the main thread's run loop is currently running in, or null when it
        /// is not inside a run loop. CFRunLoop is thread-safe, so this is the one
        /// probe that may be called from any thread without touching AppKit.
        /// </summary>
        public static string MainRunLoopMode()
        {
            var mode = CFRunLoopCopyCurrentMode(CFRunLoopGetMain());
            if (mode == IntPtr.Zero) return null;
            try { return ToManagedString(mode); }
            finally { CFRelease(mode); }
        }

        /// Enqueues <paramref name="work"/> on the main dispatch queue. The main queue
        /// is serviced in the run loop's common modes, which include the modal-panel
        /// mode AppKit runs while a dialog is up.
        public static void DispatchToMainQueue(IntPtr context, IntPtr work)
        {
            // dispatch_get_main_queue() is a header macro for &_dispatch_main_q.
            dispatch_async_f(dlsym(RTLD_DEFAULT, "_dispatch_main_q"), context, work);
        }

        /// Runs the main run loop in the default mode once. Main thread only.
        public static void PumpMainRunLoop(double seconds)
        {
            var defaultMode = Marshal.ReadIntPtr(dlsym(RTLD_DEFAULT, "kCFRunLoopDefaultMode"));
            CFRunLoopRunInMode(defaultMode, seconds, true);
        }
    }
}
