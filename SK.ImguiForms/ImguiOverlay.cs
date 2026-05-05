namespace SK.ImguiForms {
    using System;
    using System.Diagnostics;
    using System.Drawing;
    using ImGuiNET;
    using SK.ImguiForms.Win32;
    using SKFramework;

    public abstract class ImguiOverlay : ImguiWindow {
        static readonly nint HWND_TOP = new(0);
        static readonly nint HWND_TOPMOST = new(-1);
        static readonly nint HWND_NOTOPMOST = new(-2);

        nint currentOwnerWindowHandle;
        nint currentZOrderTargetWindowHandle;
        bool? currentTopMostState;
        bool isOverlayWindowVisible;
        Rectangle currentBounds;
        bool hasCurrentBounds;
        ImguiOverlayAttachmentMode attachmentMode;
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

        public ImguiOverlayAttachmentMode AttachmentMode {
            get => attachmentMode;
            set {
                if(attachmentMode == value) {
                    return;
                }

                attachmentMode = value;
                currentZOrderTargetWindowHandle = 0;
            }
        }

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
            return AttachmentMode == ImguiOverlayAttachmentMode.OwnedByTarget
                ? GetZOrderTargetWindowHandle(targetWindowHandle)
                : 0;
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
            if(ownerWindowHandle == Window.Handle) {
                ownerWindowHandle = 0;
            }

            if(ownerWindowHandle != currentOwnerWindowHandle) {
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

            var zOrderNeedsSync = AttachmentMode == ImguiOverlayAttachmentMode.Independent && !IsOverlayImmediatelyAboveTarget(zOrderTargetWindowHandle);
            if(currentZOrderTargetWindowHandle != zOrderTargetWindowHandle || !hasCurrentBounds || currentBounds != bounds || !isOverlayWindowVisible || zOrderNeedsSync) {
                var flags = SetWindowPosFlags.SHOWWINDOW
                    | SetWindowPosFlags.NOACTIVATE
                    | SetWindowPosFlags.ASYNCWINDOWPOS;
                var insertAfter = GetOverlayInsertAfterWindow(zOrderTargetWindowHandle);
                if(insertAfter == 0) {
                    flags |= SetWindowPosFlags.NOZORDER;
                }

                User32.SetWindowPos(
                    Window.Handle,
                    insertAfter,
                    bounds.X,
                    bounds.Y,
                    bounds.Width,
                    bounds.Height,
                    flags);
                currentZOrderTargetWindowHandle = zOrderTargetWindowHandle;
                currentBounds = bounds;
                hasCurrentBounds = true;
                isOverlayWindowVisible = true;
            }
        }

        bool IsOverlayImmediatelyAboveTarget(nint zOrderTargetWindowHandle) {
            return AttachmentMode != ImguiOverlayAttachmentMode.Independent
                || User32.GetWindow(zOrderTargetWindowHandle, GetWindowCommand.GW_HWNDPREV) == Window.Handle;
        }

        nint GetOverlayInsertAfterWindow(nint zOrderTargetWindowHandle) {
            if(AttachmentMode == ImguiOverlayAttachmentMode.OwnedByTarget) {
                return zOrderTargetWindowHandle;
            }

            var windowAboveTarget = User32.GetWindow(zOrderTargetWindowHandle, GetWindowCommand.GW_HWNDPREV);
            if(windowAboveTarget == Window.Handle) {
                return 0;
            }

            return windowAboveTarget != 0 ? windowAboveTarget : HWND_TOP;
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
