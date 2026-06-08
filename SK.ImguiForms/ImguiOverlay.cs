namespace SK.ImguiForms {
    using System;
    using System.Diagnostics;
    using System.Drawing;
    using Hexa.NET.ImGui;
    using SK.ImguiForms.Win32;
    using SKFramework;

    public abstract class ImguiOverlay : ImguiWindow {
        static readonly nint HWND_TOP = new(0);
        static readonly nint HWND_TOPMOST = new(-1);
        static readonly nint HWND_NOTOPMOST = new(-2);

        const long AnchorResolveIntervalMs = 200;

        nint currentOwnerWindowHandle;
        nint currentZOrderTargetWindowHandle;
        nint cachedSiblingAnchor;
        nint lastAnchorBaseTarget;
        long lastAnchorResolveTick;
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
            cachedSiblingAnchor = 0;
            lastAnchorBaseTarget = 0;
            lastAnchorResolveTick = 0;
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

            var baseZOrderTarget = GetZOrderTargetWindowHandle(targetWindowHandle);
            if(targetWindowHandle == 0 || baseZOrderTarget == 0 || targetWindowHandle == Window.Handle || baseZOrderTarget == Window.Handle || bounds.Width <= 0 || bounds.Height <= 0) {
                TargetWindowHandle = 0;
                hasCurrentBounds = false;
                currentBounds = Rectangle.Empty;
                currentZOrderTargetWindowHandle = 0;
                HideOverlayWindow();
                return;
            }

            // In Independent mode the overlay must sit above the game window AND above any sibling
            // overlay attached to it (e.g. the native in-game overlay, which owns the slot directly
            // above the game window). Anchoring our z-order to that top-most sibling instead of the
            // game window itself ends the per-frame z-order ping-pong that causes flicker.
            var zOrderAnchor = baseZOrderTarget;
            if(AttachmentMode == ImguiOverlayAttachmentMode.Independent) {
                zOrderAnchor = ResolveZOrderAnchor(baseZOrderTarget);
            }

            var ownerWindowHandle = GetOwnerWindowHandle(targetWindowHandle);
            if(ownerWindowHandle == Window.Handle) {
                ownerWindowHandle = 0;
            }

            if(ownerWindowHandle != currentOwnerWindowHandle) {
                User32.SetWindowLongHandle(Window.Handle, (int)WindowLongParam.GWLP_HWNDPARENT, ownerWindowHandle);
                currentOwnerWindowHandle = ownerWindowHandle;
            }

            // Mirror the game window's topmost state (not the anchor's): the native overlay already
            // mirrors the game, and reading the game window keeps this correct even mid-transition.
            var targetExStyle = (WindowExStyles)User32.GetWindowLong(baseZOrderTarget, (int)WindowLongParam.GWL_EXSTYLE);
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

            var zOrderNeedsSync = AttachmentMode == ImguiOverlayAttachmentMode.Independent && !IsOverlayImmediatelyAboveTarget(zOrderAnchor);
            if(currentZOrderTargetWindowHandle != zOrderAnchor || !hasCurrentBounds || currentBounds != bounds || !isOverlayWindowVisible || zOrderNeedsSync) {
                var flags = SetWindowPosFlags.SHOWWINDOW
                    | SetWindowPosFlags.NOACTIVATE
                    | SetWindowPosFlags.ASYNCWINDOWPOS;
                var insertAfter = GetOverlayInsertAfterWindow(zOrderAnchor);
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
                currentZOrderTargetWindowHandle = zOrderAnchor;
                currentBounds = bounds;
                hasCurrentBounds = true;
                isOverlayWindowVisible = true;
            }
        }

        // Returns the top-most sibling overlay attached to the game window to anchor our z-order
        // above, falling back to the game window itself when none exists. Throttled so the
        // EnumWindows sweep does not run on every frame; recomputed immediately when the cached
        // anchor dies or the base target changes.
        nint ResolveZOrderAnchor(nint baseTarget) {
            var now = Environment.TickCount64;
            var cachedDead = cachedSiblingAnchor != 0 &&
                (!User32.IsWindow(cachedSiblingAnchor) ||
                 User32.GetWindow(cachedSiblingAnchor, GetWindowCommand.GW_OWNER) != baseTarget);

            if(cachedDead || baseTarget != lastAnchorBaseTarget || now - lastAnchorResolveTick >= AnchorResolveIntervalMs) {
                cachedSiblingAnchor = OverlayZOrderResolver.TryFindTopmostSiblingOverlay(baseTarget, Window.Handle, out var sibling)
                    ? sibling
                    : 0;
                lastAnchorBaseTarget = baseTarget;
                lastAnchorResolveTick = now;
            }

            return cachedSiblingAnchor != 0 ? cachedSiblingAnchor : baseTarget;
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
