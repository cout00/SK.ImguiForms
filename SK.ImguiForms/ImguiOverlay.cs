namespace SK.ImguiForms {
    using System;
    using System.Diagnostics;
    using System.Drawing;
    using ImGuiNET;
    using SK.ImguiForms.Win32;
    using SKFramework;

    public abstract class ImguiOverlay : ImguiWindow {
        static readonly nint HWND_TOPMOST = new(-1);
        static readonly nint HWND_NOTOPMOST = new(-2);

        nint currentOwnerWindowHandle;
        nint currentZOrderTargetWindowHandle;
        bool? currentTopMostState;
        bool isOverlayWindowVisible;
        Rectangle currentBounds;
        bool hasCurrentBounds;
        Process targetProcess;
        string targetProcessName = string.Empty;
        DateTime targetProcessSearchStartUtc;
        int? lockedTargetWindowProcessId;

        protected unsafe ImguiOverlay(string windowTitle, bool dpiAware = true, int initialWindowWidth = 800, int initialWindowHeight = 600)
            : base(windowTitle, dpiAware, initialWindowWidth, initialWindowHeight) {
            VSync = false;
            FPSLimit = 60;
            SmoothFramePacing = true;
            PreferFlipSwapChain = false;
            Graphics = new SKOverlayGraphics(this);
            EnqueueFontUpdate(Graphics.LoadFonts);
        }

        protected unsafe ImguiOverlay(string windowTitle, Process targetProcess, bool dpiAware = true, int initialWindowWidth = 800, int initialWindowHeight = 600)
            : this(windowTitle, dpiAware, initialWindowWidth, initialWindowHeight) {
            AttachToProcess(targetProcess);
        }

        public SKOverlayGraphics Graphics { get; }

        public nint TargetWindowHandle { get; private set; }

        public Rectangle OverlayBounds => currentBounds;

        protected override WindowStyles WindowStyle => WindowStyles.WS_POPUP;

        protected override WindowExStyles WindowExStyle => WindowExStyles.WS_EX_ACCEPTFILES;

        protected override bool UseClickThrough => true;

        protected virtual bool UseTargetClientBounds => true;

        public void AttachToProcess(Process process) {
            ArgumentNullException.ThrowIfNull(process);

            targetProcess = process;
            targetProcessName = OverlayTargetWindowResolver.GetProcessName(process);
            targetProcessSearchStartUtc = OverlayTargetWindowResolver.GetProcessSearchStartUtc(process);
            lockedTargetWindowProcessId = null;
            TargetWindowHandle = 0;
            currentZOrderTargetWindowHandle = 0;
            currentOwnerWindowHandle = 0;
            currentBounds = Rectangle.Empty;
            hasCurrentBounds = false;
        }

        public void ActivateTargetWindow() {
            var targetWindowHandle = GetTargetWindowHandle();
            if(targetWindowHandle != 0) {
                OverlayTargetWindowResolver.BringToFront(targetWindowHandle);
            }
        }

        protected override void OnWindowShown() {
            if(Window != null && Window.Handle != 0) {
                Utils.InitTransparency(Window.Handle);
                HideOverlayWindow();
            }
        }

        protected sealed override void Render() {
            var targetWindowHandle = GetTargetWindowHandle();
            var bounds = GetOverlayBounds(targetWindowHandle);
            TargetWindowHandle = targetWindowHandle;
            SyncOverlayWindow(targetWindowHandle, bounds);
            if(targetWindowHandle == 0 || bounds.Width <= 0 || bounds.Height <= 0) {
                return;
            }

            RenderOverlay(ImGui.GetBackgroundDrawList());
        }

        protected virtual Process GetTargetProcess() {
            return targetProcess;
        }

        protected virtual string GetTargetProcessName(Process process) {
            if(!string.IsNullOrWhiteSpace(targetProcessName)) {
                return targetProcessName;
            }

            return OverlayTargetWindowResolver.GetProcessName(process);
        }

        protected virtual nint GetTargetWindowHandle() {
            var process = GetTargetProcess();
            if(process == null) {
                return 0;
            }

            var processName = GetTargetProcessName(process);
            var searchStartUtc = GetTargetProcessSearchStartUtc(process);
            if(!OverlayTargetWindowResolver.TryResolveProcessWindow(process, processName, searchStartUtc, lockedTargetWindowProcessId, out var ownerProcessId, out var handle)) {
                return 0;
            }

            if(!lockedTargetWindowProcessId.HasValue && ownerProcessId > 0) {
                lockedTargetWindowProcessId = ownerProcessId;
            }

            return handle;
        }

        protected virtual DateTime GetTargetProcessSearchStartUtc(Process process) {
            if(ReferenceEquals(process, targetProcess) && targetProcessSearchStartUtc != default) {
                return targetProcessSearchStartUtc;
            }

            return OverlayTargetWindowResolver.GetProcessSearchStartUtc(process);
        }

        protected virtual Rectangle GetOverlayBounds(nint targetWindowHandle) {
            if(targetWindowHandle == 0) {
                return Rectangle.Empty;
            }

            return UseTargetClientBounds
                ? OverlayTargetWindowResolver.TryGetClientBounds(targetWindowHandle, out var clientBounds) ? clientBounds : Rectangle.Empty
                : OverlayTargetWindowResolver.TryGetWindowBounds(targetWindowHandle, out var windowBounds) ? windowBounds : Rectangle.Empty;
        }

        protected abstract void RenderOverlay(ImDrawListPtr drawList);

        protected virtual nint GetZOrderTargetWindowHandle(nint targetWindowHandle) {
            return targetWindowHandle;
        }

        protected virtual nint GetOwnerWindowHandle(nint targetWindowHandle) {
            return GetZOrderTargetWindowHandle(targetWindowHandle);
        }

        void SyncOverlayWindow(nint targetWindowHandle, Rectangle bounds) {
            if(Window == null || Window.Handle == 0) {
                return;
            }

            var zOrderTargetWindowHandle = GetZOrderTargetWindowHandle(targetWindowHandle);
            if(targetWindowHandle == 0 || zOrderTargetWindowHandle == 0 || targetWindowHandle == Window.Handle || zOrderTargetWindowHandle == Window.Handle || bounds.Width <= 0 || bounds.Height <= 0) {
                TargetWindowHandle = 0;
                hasCurrentBounds = false;
                currentBounds = Rectangle.Empty;
                currentZOrderTargetWindowHandle = 0;
                HideOverlayWindow();
                return;
            }

            var ownerWindowHandle = GetOwnerWindowHandle(targetWindowHandle);
            if(ownerWindowHandle != 0 && ownerWindowHandle != Window.Handle && ownerWindowHandle != currentOwnerWindowHandle) {
                User32.SetWindowLongHandle(Window.Handle, (int)WindowLongParam.GWLP_HWNDPARENT, ownerWindowHandle);
                currentOwnerWindowHandle = ownerWindowHandle;
            }

            var targetExStyle = (WindowExStyles)User32.GetWindowLong(zOrderTargetWindowHandle, (int)WindowLongParam.GWL_EXSTYLE);
            var shouldBeTopMost = targetExStyle.HasFlag(WindowExStyles.WS_EX_TOPMOST);
            if(currentTopMostState != shouldBeTopMost) {
                User32.SetWindowPos(
                    Window.Handle,
                    shouldBeTopMost ? HWND_TOPMOST : HWND_NOTOPMOST,
                    0,
                    0,
                    0,
                    0,
                    SetWindowPosFlags.NOMOVE
                    | SetWindowPosFlags.NOSIZE
                    | SetWindowPosFlags.NOACTIVATE
                    | SetWindowPosFlags.ASYNCWINDOWPOS);
                currentTopMostState = shouldBeTopMost;
            }

            if(Window.Dimensions.Location != bounds.Location || Window.Dimensions.Size != bounds.Size) {
                Window.Dimensions = bounds;
            }

            if(currentZOrderTargetWindowHandle != zOrderTargetWindowHandle || !hasCurrentBounds || currentBounds != bounds || !isOverlayWindowVisible) {
                User32.SetWindowPos(
                    Window.Handle,
                    zOrderTargetWindowHandle,
                    bounds.X,
                    bounds.Y,
                    bounds.Width,
                    bounds.Height,
                    SetWindowPosFlags.SHOWWINDOW
                    | SetWindowPosFlags.NOACTIVATE
                    | SetWindowPosFlags.ASYNCWINDOWPOS);
                currentZOrderTargetWindowHandle = zOrderTargetWindowHandle;
                currentBounds = bounds;
                hasCurrentBounds = true;
                isOverlayWindowVisible = true;
            }
        }

        void HideOverlayWindow() {
            if(Window == null || Window.Handle == 0 || !isOverlayWindowVisible) {
                return;
            }

            User32.SetWindowPos(
                Window.Handle,
                IntPtr.Zero,
                0,
                0,
                0,
                0,
                SetWindowPosFlags.NOMOVE
                | SetWindowPosFlags.NOSIZE
                | SetWindowPosFlags.NOACTIVATE
                | SetWindowPosFlags.NOZORDER
                | SetWindowPosFlags.HIDEWINDOW);
            isOverlayWindowVisible = false;
        }
    }
}
