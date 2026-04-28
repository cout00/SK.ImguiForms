namespace SK.ImguiForms {
    using System;
    using System.Diagnostics;
    using System.Drawing;
    using System.IO;
    using SK.ImguiForms.Win32;

    static class OverlayTargetWindowResolver {
        public static bool TryResolveProcessWindow(Process process, string processName, DateTime launchedAfterUtc, int? lockedOwnerProcessId, out int ownerProcessId, out nint handle) {
            ownerProcessId = 0;
            handle = 0;

            if(lockedOwnerProcessId.HasValue) {
                if(TryFindBestWindowForProcessId(lockedOwnerProcessId.Value, out handle, out _)) {
                    ownerProcessId = lockedOwnerProcessId.Value;
                    return true;
                }

                return false;
            }

            if(process != null) {
                try {
                    if(!process.HasExited && TryFindBestWindowForProcessId(process.Id, out handle, out _)) {
                        ownerProcessId = process.Id;
                        return true;
                    }
                }
                catch {
                }
            }

            return TryFindNewestWindowForProcessName(processName, launchedAfterUtc, out ownerProcessId, out handle);
        }

        public static DateTime GetProcessSearchStartUtc(Process process) {
            try {
                return process.StartTime.ToUniversalTime().AddSeconds(-1);
            }
            catch {
                return DateTime.UtcNow;
            }
        }

        public static string GetProcessName(Process process) {
            if(process == null) {
                return string.Empty;
            }

            try {
                var fileName = process.StartInfo?.FileName;
                if(!string.IsNullOrWhiteSpace(fileName)) {
                    return NormalizeProcessName(Path.GetFileNameWithoutExtension(fileName));
                }
            }
            catch {
            }

            try {
                return NormalizeProcessName(process.ProcessName);
            }
            catch {
                return string.Empty;
            }
        }

        public static bool TryGetWindowBounds(nint handle, out Rectangle bounds) {
            bounds = Rectangle.Empty;
            if(handle == 0 || !User32.IsWindow(handle) || !User32.GetWindowRect(handle, out var rect)) {
                return false;
            }

            bounds = Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
            return bounds.Width > 0 && bounds.Height > 0;
        }

        public static bool TryGetClientBounds(nint handle, out Rectangle bounds) {
            bounds = Rectangle.Empty;
            if(handle == 0 || !User32.IsWindow(handle) || !User32.GetClientRect(handle, out var clientRect)) {
                return false;
            }

            var origin = new POINT { X = 0, Y = 0 };
            if(!User32.ClientToScreen(handle, ref origin)) {
                return false;
            }

            bounds = new Rectangle(origin.X, origin.Y, clientRect.Width, clientRect.Height);
            return bounds.Width > 0 && bounds.Height > 0;
        }

        public static void BringToFront(nint handle) {
            if(handle == 0 || !User32.IsWindow(handle)) {
                return;
            }

            if(User32.IsIconic(handle)) {
                User32.ShowWindow(handle, ShowWindowCommand.Restore);
            }

            User32.SetForegroundWindow(handle);
        }

        static bool TryFindNewestWindowForProcessName(string processName, DateTime launchedAfterUtc, out int ownerProcessId, out nint handle) {
            ownerProcessId = 0;
            handle = 0;
            processName = NormalizeProcessName(processName);
            if(string.IsNullOrWhiteSpace(processName)) {
                return false;
            }

            var bestArea = 0;
            var bestStartTimeUtc = DateTime.MinValue;
            var foundCandidateStartedAfterLaunch = false;

            foreach(var candidateProcess in Process.GetProcessesByName(processName)) {
                try {
                    if(candidateProcess.HasExited || !TryFindBestWindowForProcessId(candidateProcess.Id, out var candidateHandle, out var candidateArea)) {
                        continue;
                    }

                    var candidateStartTimeUtc = GetProcessSearchStartUtc(candidateProcess).AddSeconds(1);
                    var candidateStartedAfterLaunch = candidateStartTimeUtc >= launchedAfterUtc;

                    if(ownerProcessId != 0) {
                        if(candidateStartedAfterLaunch != foundCandidateStartedAfterLaunch) {
                            if(!candidateStartedAfterLaunch) {
                                continue;
                            }
                        }
                        else if(candidateStartTimeUtc < bestStartTimeUtc) {
                            continue;
                        }
                        else if(candidateStartTimeUtc == bestStartTimeUtc && candidateArea <= bestArea) {
                            continue;
                        }
                    }

                    ownerProcessId = candidateProcess.Id;
                    handle = candidateHandle;
                    bestArea = candidateArea;
                    bestStartTimeUtc = candidateStartTimeUtc;
                    foundCandidateStartedAfterLaunch = candidateStartedAfterLaunch;
                }
                catch {
                }
                finally {
                    candidateProcess.Dispose();
                }
            }

            return ownerProcessId != 0 && handle != 0;
        }

        static bool TryFindBestWindowForProcessId(int processId, out nint handle, out int area) {
            handle = 0;
            area = 0;
            if(processId <= 0) {
                return false;
            }

            var processIdUInt = (uint)processId;
            var bestHandle = IntPtr.Zero;
            var bestArea = 0;
            User32.EnumWindows((hWnd, _) => {
                User32.GetWindowThreadProcessId(hWnd, out var ownerProcessId);
                if(ownerProcessId != processIdUInt || !TryGetCandidateWindowArea(hWnd, out var candidateArea)) {
                    return true;
                }

                if(candidateArea > bestArea) {
                    bestArea = candidateArea;
                    bestHandle = hWnd;
                }

                return true;
            }, IntPtr.Zero);

            handle = bestHandle;
            area = bestArea;
            return handle != 0;
        }

        static bool TryGetCandidateWindowArea(nint handle, out int area) {
            area = 0;
            if(handle == 0 || !User32.IsWindow(handle) || !User32.IsWindowVisible(handle) || !User32.GetWindowRect(handle, out var rect)) {
                return false;
            }

            var width = rect.Right - rect.Left;
            var height = rect.Bottom - rect.Top;
            if(width <= 0 || height <= 0) {
                return false;
            }

            area = width * height;
            return true;
        }

        static string NormalizeProcessName(string processName) {
            if(string.IsNullOrWhiteSpace(processName)) {
                return string.Empty;
            }

            processName = processName.Trim();
            if(processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) {
                processName = Path.GetFileNameWithoutExtension(processName);
            }

            return processName.Equals("Idle", StringComparison.OrdinalIgnoreCase) ||
                   processName.Equals("System Idle Process", StringComparison.OrdinalIgnoreCase)
                ? string.Empty
                : processName;
        }
    }
}
