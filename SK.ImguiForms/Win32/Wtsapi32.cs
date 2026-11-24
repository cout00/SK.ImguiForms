namespace SK.ImguiForms.Win32
{
    using System;
    using System.Runtime.InteropServices;

    internal static class Wtsapi32
    {
        public const string LibraryName = "wtsapi32.dll";

        public const int NOTIFY_FOR_THIS_SESSION = 0;

        static readonly IntPtr WTS_CURRENT_SERVER_HANDLE = IntPtr.Zero;
        const int WTS_CURRENT_SESSION = -1;
        const int WTSConnectState = 8;
        const int WTSActive = 0;

        [DllImport(LibraryName, ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool WTSRegisterSessionNotification(IntPtr hWnd, int dwFlags);

        [DllImport(LibraryName, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool WTSUnRegisterSessionNotification(IntPtr hWnd);

        [DllImport(LibraryName, ExactSpelling = true, EntryPoint = "WTSQuerySessionInformationW", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool WTSQuerySessionInformation(IntPtr hServer, int sessionId, int wtsInfoClass, out IntPtr ppBuffer, out int pBytesReturned);

        [DllImport(LibraryName, ExactSpelling = true)]
        static extern void WTSFreeMemory(IntPtr pMemory);

        // True when this process's session is connected to a console or a remote client (WTSActive).
        // A disconnected RDP session is not: nothing it draws reaches a screen. If the state cannot be
        // read, the session counts as connected, which keeps the window drawing as before.
        public static bool IsCurrentSessionActive()
        {
            if(!WTSQuerySessionInformation(WTS_CURRENT_SERVER_HANDLE, WTS_CURRENT_SESSION, WTSConnectState, out var buffer, out var bytes))
            {
                return true;
            }

            try
            {
                return bytes < sizeof(int) || Marshal.ReadInt32(buffer) == WTSActive;
            }
            finally
            {
                WTSFreeMemory(buffer);
            }
        }
    }
}
