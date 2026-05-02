namespace SK.ImguiForms {
    using System;
    using System.Collections.Generic;
    using System.ComponentModel;
    using System.Drawing;
    using System.IO;
    using System.Numerics;
    using System.Text.Json;
    using ImGuiNET;
    using SK.ImguiForms.Win32;
    using SKFramework;

    public sealed class CloseEventArgs : CancelEventArgs {
    }

    public abstract class ImguiForm : ImguiWindow {
        readonly List<RECT> dragRegions = [];
        readonly List<TitleBarButton> titleBarButtons = [];
        static readonly Action NoOp = static () => { };
        bool isInNativeSizeMove;
        bool hasInitializedShell;
        bool isWindowMaximized;
        bool appliedPersistedWindowState;
        Size restoredClientSize;

        protected unsafe ImguiForm(string windowTitle, bool dpiAware = true, int initialWindowWidth = 800, int initialWindowHeight = 600)
            : base(windowTitle, dpiAware, initialWindowWidth, initialWindowHeight) {
            Graphics = new SKImguiGraphics(this);
            EnqueueFontUpdate(Graphics.LoadFonts);
        }

        public SKImguiGraphics Graphics { get; }

        protected override WindowStyles WindowStyle => WindowStyles.WS_OVERLAPPED | WindowStyles.WS_THICKFRAME;

        protected override WindowExStyles WindowExStyle => WindowExStyles.WS_EX_ACCEPTFILES | WindowExStyles.WS_EX_APPWINDOW;

        protected virtual string WindowStateFilePath => Path.Combine(AppContext.BaseDirectory, "window_state.json");

        protected virtual bool PersistWindowState => true;

        protected virtual int BackgroundFpsLimit => FPSLimit <= 0 ? 15 : Math.Min(FPSLimit, 15);

        protected virtual int MinimizedFpsLimit => 2;

        protected virtual Vector2 DefaultWindowPosition => new(100f, 100f);

        protected virtual Vector2 DefaultWindowSize => new(InitialWindowSize.Width, InitialWindowSize.Height);

        protected virtual Vector2 WindowPadding => new(5f, 5f);

        protected virtual float TitleBarBottomSpacing => 6f;

        protected virtual string FormWindowId => " ";

        protected virtual string FormContentId => "Content";

        protected virtual ImGuiWindowFlags FormWindowFlags => ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoCollapse;

        protected virtual bool ShowMinimizeButton => true;

        protected virtual bool ShowMaximizeButton => true;

        protected virtual bool ShowCloseButton => true;

        protected bool IsWindowMaximized => isWindowMaximized;

        public override void Close() {
            SaveWindowStateIfNeeded();
            base.Close();
        }

        protected override void OnBeforeRender() {
            dragRegions.Clear();
            titleBarButtons.Clear();
        }

        protected override void OnWindowCreated() {
            ApplyPersistedWindowStateIfAvailable();
        }

        protected override void OnWindowShown() {
            var initialClientSize = appliedPersistedWindowState
                ? restoredClientSize
                : InitialWindowSize;

            if(initialClientSize.Width > 0 && initialClientSize.Height > 0) {
                SetNativeWindowClientSize(initialClientSize);
            }

            EnsureRenderSizeMatchesClientRect();
            RenderFrameImmediately();
        }

        protected override int GetActiveFpsLimit() {
            if(IsWindowMinimized()) {
                return MinimizedFpsLimit;
            }

            if(!IsWindowForeground()) {
                return BackgroundFpsLimit;
            }

            return base.GetActiveFpsLimit();
        }

        protected override bool ShouldRenderCurrentFrame() {
            return !IsWindowMinimized();
        }

        protected sealed override void Render() {
            RenderUiCore();
        }

        protected override bool TryHandleWindowMessage(WindowMessage message, UIntPtr wParam, IntPtr lParam, out IntPtr result) {
            switch(message) {
                case WindowMessage.Close:
                    RequestClose();
                    result = IntPtr.Zero;
                    return true;

                case WindowMessage.NcCalcSize:
                    result = IntPtr.Zero;
                    return true;

                case WindowMessage.NcHitTest:
                    var hitTest = HitTestWindowFrame(lParam);
                    if(hitTest != HitTestResult.HTCLIENT) {
                        result = new IntPtr((int)hitTest);
                        return true;
                    }

                    break;

                case WindowMessage.EraseBackground:
                    result = new IntPtr(1);
                    return true;
            }

            result = IntPtr.Zero;
            return false;
        }

        protected override bool TryHandleInitializationWindowMessage(WindowMessage message, UIntPtr wParam, IntPtr lParam, out IntPtr result) {
            switch(message) {
                case WindowMessage.NcCalcSize:
                    result = IntPtr.Zero;
                    return true;

                case WindowMessage.EraseBackground:
                    result = new IntPtr(1);
                    return true;
            }

            result = IntPtr.Zero;
            return false;
        }

        protected override void HandleWindowMessage(WindowMessage message, UIntPtr wParam, IntPtr lParam) {
            switch(message) {
                case WindowMessage.ShowWindow:
                    base.HandleWindowMessage(message, wParam, lParam);
                    RenderFrameImmediately();
                    return;

                case WindowMessage.Size:
                    base.HandleWindowMessage(message, wParam, lParam);
                    isWindowMaximized = (SizeMessage)wParam == SizeMessage.SIZE_MAXIMIZED;
                    if((SizeMessage)wParam is SizeMessage.SIZE_RESTORED or SizeMessage.SIZE_MAXIMIZED) {
                        SaveWindowStateIfNeeded();
                        RenderFrameImmediately();
                    }

                    return;

                case WindowMessage.DpiChanged:
                    ApplySuggestedBounds(lParam);
                    base.HandleWindowMessage(message, wParam, lParam);
                    RenderFrameImmediately();
                    return;

                case WindowMessage.EnterSizeMove:
                    isInNativeSizeMove = true;
                    return;

                case WindowMessage.Sizing:
                    EnsureRenderSizeMatchesClientRect();
                    RenderFrameImmediately();
                    return;

                case WindowMessage.ExitSizeMove:
                    isInNativeSizeMove = false;
                    SaveWindowStateIfNeeded();
                    EnsureRenderSizeMatchesClientRect();
                    RenderFrameImmediately();
                    return;

                case WindowMessage.WindowPositionChanged:
                    EnsureRenderSizeMatchesClientRect();
                    if(isInNativeSizeMove) {
                        RenderFrameImmediately();
                    }

                    return;

                case WindowMessage.Move:
                    base.HandleWindowMessage(message, wParam, lParam);
                    SaveWindowStateIfNeeded();
                    return;

                case WindowMessage.Destroy:
                    SaveWindowStateIfNeeded();
                    base.HandleWindowMessage(message, wParam, lParam);
                    return;
            }

            base.HandleWindowMessage(message, wParam, lParam);
        }

        protected void UseCurrentClientAreaForNextWindow() {
            var clientSize = GetNativeWindowClientSize();
            if(clientSize.Width <= 0 || clientSize.Height <= 0) {
                return;
            }

            ImGui.SetNextWindowPos(Vector2.Zero, ImGuiCond.Always);
            ImGui.SetNextWindowSize(new Vector2(clientSize.Width, clientSize.Height), ImGuiCond.Always);
        }

        protected void SetWindowMinimized(bool minimized) {
            if(Window == null || Window.Handle == IntPtr.Zero) {
                return;
            }

            User32.ShowWindow(Window.Handle, minimized ? ShowWindowCommand.Minimize : ShowWindowCommand.Restore);
        }

        protected void SetWindowMaximized(bool maximized) {
            if(Window == null || Window.Handle == IntPtr.Zero) {
                return;
            }

            isWindowMaximized = maximized;
            User32.ShowWindow(Window.Handle, maximized ? ShowWindowCommand.Maximize : ShowWindowCommand.Restore);
        }

        protected void ApplyWindowPlacement(Vector2 defaultPosition, Vector2 defaultSize) {
            ImGui.SetNextWindowPos(defaultPosition, ImGuiCond.FirstUseEver);
            ImGui.SetNextWindowSize(defaultSize, ImGuiCond.FirstUseEver);
        }

        protected void AddTitleBarButton(string id, string text, Action onClick, string tooltip = null, bool enabled = true) {
            titleBarButtons.Add(new TitleBarButton(id, text, null, tooltip, enabled, onClick ?? NoOp));
        }

        protected void AddTitleBarIconButton(string id, int iconNumber, Action onClick, string tooltip = null, bool enabled = true) {
            titleBarButtons.Add(new TitleBarButton(id, null, iconNumber, tooltip, enabled, onClick ?? NoOp));
        }

        protected void SyncHostWithCurrentImGuiWindow() {
            if(Window == null || Window.Handle == IntPtr.Zero) {
                return;
            }

            var imguiPos = ImGui.GetWindowPos();
            var imguiSize = ImGui.GetWindowSize();
            var clientSize = GetNativeWindowClientSize();
            if(clientSize.Width <= 0 || clientSize.Height <= 0) {
                return;
            }

            var targetClientWidth = Math.Max(1, (int)MathF.Round(imguiSize.X));
            var targetClientHeight = Math.Max(1, (int)MathF.Round(imguiSize.Y));
            var deltaX = (int)MathF.Round(imguiPos.X);
            var deltaY = (int)MathF.Round(imguiPos.Y);
            var moved = deltaX != 0 || deltaY != 0;
            var resized = clientSize.Width != targetClientWidth || clientSize.Height != targetClientHeight;

            if(moved || resized) {
                if(User32.GetWindowRect(Window.Handle, out var windowRect)) {
                    var outerSize = GetNativeWindowOuterSizeForClientSize(new Size(targetClientWidth, targetClientHeight));
                    var x = windowRect.Left + deltaX;
                    var y = windowRect.Top + deltaY;
                    Window.Dimensions = new Rectangle(x, y, outerSize.Width, outerSize.Height);
                    User32.MoveWindow(Window.Handle, x, y, outerSize.Width, outerSize.Height, true);
                    clientSize = GetNativeWindowClientSize();
                }
            }

            ImGui.SetWindowPos(Vector2.Zero, ImGuiCond.Always);
            ImGui.SetWindowSize(new Vector2(clientSize.Width, clientSize.Height), ImGuiCond.Always);
        }

        protected bool DragArea(string id, Vector2 size) {
            var pressed = ImGui.InvisibleButton(id, size);
            RegisterDragRegionForLastItem();
            return pressed;
        }

        protected void RegisterDragRegionForLastItem() {
            RegisterDragRegion(ImGui.GetItemRectMin(), ImGui.GetItemRectMax());
        }

        protected void RegisterDragRegion(Vector2 screenMin, Vector2 screenMax) {
            var windowPosition = ImGui.GetWindowPos();
            RegisterDragRegion(Rectangle.FromLTRB(
                (int)MathF.Floor(screenMin.X - windowPosition.X),
                (int)MathF.Floor(screenMin.Y - windowPosition.Y),
                (int)MathF.Ceiling(screenMax.X - windowPosition.X),
                (int)MathF.Ceiling(screenMax.Y - windowPosition.Y)));
        }

        protected void RegisterDragRegion(Rectangle dragRegion) {
            if(dragRegion.Width <= 0 || dragRegion.Height <= 0) {
                return;
            }

            dragRegions.Add(new RECT {
                Left = dragRegion.Left,
                Top = dragRegion.Top,
                Right = dragRegion.Right,
                Bottom = dragRegion.Bottom
            });
        }

        void ApplySuggestedBounds(IntPtr lParam) {
            if(Window == null || Window.Handle == IntPtr.Zero || lParam == IntPtr.Zero) {
                return;
            }

            var suggestedRect = System.Runtime.InteropServices.Marshal.PtrToStructure<RECT>(lParam);
            Window.Dimensions = new Rectangle(
                suggestedRect.Left,
                suggestedRect.Top,
                suggestedRect.Width,
                suggestedRect.Height);

            User32.SetWindowPos(
                Window.Handle,
                IntPtr.Zero,
                suggestedRect.Left,
                suggestedRect.Top,
                suggestedRect.Width,
                suggestedRect.Height,
                SetWindowPosFlags.NOZORDER
                | SetWindowPosFlags.NOOWNERZORDER
                | SetWindowPosFlags.NOACTIVATE);
        }

        void RenderUiCore() {
            EnsureShellInitialized();

            ApplyWindowPlacement(DefaultWindowPosition, DefaultWindowSize);
            UseCurrentClientAreaForNextWindow();

            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, WindowPadding);
            try {
                ImGui.Begin(FormWindowId, FormWindowFlags);
                SyncHostWithCurrentImGuiWindow();
                RenderTitleBar();

                ImGui.BeginChild(FormContentId, new Vector2(0, 0), ImGuiChildFlags.None, ImGuiWindowFlags.HorizontalScrollbar);
                try {
                    RenderFormContent();
                }
                finally {
                    ImGui.EndChild();
                }

                PersistFormState();
                ImGui.End();
            }
            finally {
                ImGui.PopStyleVar();
            }

            RenderAfterFormWindow();
        }

        void EnsureShellInitialized() {
            if(hasInitializedShell || Handle == 0) {
                return;
            }

            OnShellInitialized();
            hasInitializedShell = true;
        }

        void RenderTitleBar() {
            var style = ImGui.GetStyle();
            var headerRowHeight = Math.Max(ImGui.GetTextLineHeight(), ImGui.GetFrameHeight()) + style.FramePadding.Y + TitleBarBottomSpacing;
            var iconButtonSize = new Vector2(ImGui.GetFrameHeight(), ImGui.GetFrameHeight());

            ConfigureTitleBarButtons();
            var customButtonsWidth = MeasureTitleBarButtonsWidth(iconButtonSize);
            var systemButtonsWidth = MeasureSystemButtonsWidth(iconButtonSize);
            var reservedButtonsWidth = customButtonsWidth + systemButtonsWidth;

            ImGui.BeginGroup();
            var headerStart = ImGui.GetCursorScreenPos();
            var headerBgMin = new Vector2(headerStart.X - style.WindowPadding.X, headerStart.Y - style.FramePadding.Y * 0.5f);
            var headerBgMax = new Vector2(headerStart.X + ImGui.GetContentRegionAvail().X + style.WindowPadding.X, headerBgMin.Y + headerRowHeight);
            var headerDrawList = ImGui.GetWindowDrawList();
            headerDrawList.AddRectFilled(headerBgMin, headerBgMax, ImGui.ColorConvertFloat4ToU32(style.Colors[(int)ImGuiCol.MenuBarBg]), style.FrameRounding);
            headerDrawList.AddLine(
                new Vector2(headerBgMin.X, headerBgMax.Y),
                new Vector2(headerBgMax.X, headerBgMax.Y),
                ImGui.ColorConvertFloat4ToU32(style.Colors[(int)ImGuiCol.Separator]),
                1f);

            var headerInteractiveEndX = RenderTitleBarLeadingContent((int)MathF.Ceiling(reservedButtonsWidth));
            var buttonsStartX = ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X - reservedButtonsWidth - style.FramePadding.X;
            ImGui.SameLine(Math.Max(ImGui.GetCursorPosX(), buttonsStartX));

            RenderConfiguredTitleBarButtons(iconButtonSize);
            RenderSystemTitleBarButtons(iconButtonSize);

            var windowPos = ImGui.GetWindowPos();
            var dragStartScreenX = headerInteractiveEndX > 0f
                ? MathF.Max(headerBgMin.X, headerInteractiveEndX + style.ItemSpacing.X)
                : headerBgMin.X;
            var dragLeft = Math.Max(0f, dragStartScreenX - windowPos.X);
            var dragTop = Math.Max(0f, headerBgMin.Y - windowPos.Y);
            var dragRight = Math.Max(dragLeft, buttonsStartX - style.ItemSpacing.X);
            var dragBottom = Math.Max(dragTop, headerBgMax.Y - windowPos.Y);
            RegisterDragRegion(Rectangle.FromLTRB(
                (int)MathF.Floor(dragLeft),
                (int)MathF.Floor(dragTop),
                (int)MathF.Ceiling(dragRight),
                (int)MathF.Ceiling(dragBottom)));

            ImGui.Dummy(new Vector2(0, TitleBarBottomSpacing));
            ImGui.EndGroup();
        }

        float MeasureTitleBarButtonsWidth(Vector2 iconButtonSize) {
            if(titleBarButtons.Count == 0) {
                return 0f;
            }

            var style = ImGui.GetStyle();
            var width = 0f;
            for(var i = 0; i < titleBarButtons.Count; i++) {
                if(i > 0) {
                    width += style.ItemSpacing.X;
                }

                width += titleBarButtons[i].IconNumber.HasValue
                    ? iconButtonSize.X
                    : ImGui.CalcTextSize(titleBarButtons[i].Text ?? string.Empty).X + style.FramePadding.X * 2f;
            }

            return width;
        }

        float MeasureSystemButtonsWidth(Vector2 iconButtonSize) {
            var count = 0;
            if(ShowMinimizeButton) {
                count++;
            }
            if(ShowMaximizeButton) {
                count++;
            }
            if(ShowCloseButton) {
                count++;
            }

            if(count == 0) {
                return 0f;
            }

            var style = ImGui.GetStyle();
            return iconButtonSize.X * count + style.ItemSpacing.X * count;
        }

        void RenderConfiguredTitleBarButtons(Vector2 iconButtonSize) {
            for(var i = 0; i < titleBarButtons.Count; i++) {
                if(i > 0) {
                    ImGui.SameLine();
                }

                RenderTitleBarButton(titleBarButtons[i], iconButtonSize);
            }

            if(titleBarButtons.Count > 0 && (ShowMinimizeButton || ShowMaximizeButton || ShowCloseButton)) {
                ImGui.SameLine();
            }
        }

        void RenderSystemTitleBarButtons(Vector2 iconButtonSize) {
            if(ShowMinimizeButton) {
                if(Graphics.IconButton("window-minimize", 0x2d1, IconSize.Small, iconButtonSize, "Minimize")) {
                    OnMinimizeRequested();
                }
                if(ShowMaximizeButton || ShowCloseButton) {
                    ImGui.SameLine();
                }
            }

            if(ShowMaximizeButton) {
                var maximizeIcon = isWindowMaximized ? 0x2d2 : 0x2d0;
                var maximizeTooltip = isWindowMaximized ? "Restore" : "Maximize";
                if(Graphics.IconButton("window-maximize", maximizeIcon, IconSize.Small, iconButtonSize, maximizeTooltip)) {
                    OnMaximizeRequested();
                }
                if(ShowCloseButton) {
                    ImGui.SameLine();
                }
            }

            if(ShowCloseButton && Graphics.IconButton("window-close", 0x00d, IconSize.Small, iconButtonSize, "Close")) {
                RequestClose();
            }
        }

        void RequestClose() {
            var eventArgs = new CloseEventArgs();
            OnCloseRequested(eventArgs);
            if(!eventArgs.Cancel) {
                Close();
            }
        }

        void RenderTitleBarButton(TitleBarButton button, Vector2 iconButtonSize) {
            ImGui.BeginDisabled(!button.Enabled);
            try {
                bool pressed;
                if(button.IconNumber.HasValue) {
                    pressed = Graphics.IconButton(button.Id, button.IconNumber.Value, IconSize.Small, iconButtonSize, button.Tooltip);
                }
                else {
                    var width = ImGui.CalcTextSize(button.Text ?? string.Empty).X + ImGui.GetStyle().FramePadding.X * 2f;
                    pressed = ImGui.Button(button.Text ?? string.Empty, new Vector2(width, iconButtonSize.Y));
                    if(!string.IsNullOrWhiteSpace(button.Tooltip) && ImGui.IsItemHovered()) {
                        ImGui.SetTooltip(button.Tooltip);
                    }
                }

                if(pressed) {
                    button.OnClick();
                }
            }
            finally {
                ImGui.EndDisabled();
            }
        }

        HitTestResult HitTestWindowFrame(IntPtr lParam) {
            if(!TryGetClientPoint(lParam, out var point) || !TryGetClientRect(out var clientRect)) {
                return HitTestResult.HTCLIENT;
            }

            var resizeBorderX = Math.Max(8, User32.GetSystemMetrics(32) + User32.GetSystemMetrics(92));
            var resizeBorderY = Math.Max(8, User32.GetSystemMetrics(33) + User32.GetSystemMetrics(92));
            var onLeft = point.X < resizeBorderX;
            var onRight = point.X >= clientRect.Width - resizeBorderX;
            var onTop = point.Y < resizeBorderY;
            var onBottom = point.Y >= clientRect.Height - resizeBorderY;

            if(onTop && onLeft) return HitTestResult.HTTOPLEFT;
            if(onTop && onRight) return HitTestResult.HTTOPRIGHT;
            if(onBottom && onLeft) return HitTestResult.HTBOTTOMLEFT;
            if(onBottom && onRight) return HitTestResult.HTBOTTOMRIGHT;
            if(onLeft) return HitTestResult.HTLEFT;
            if(onRight) return HitTestResult.HTRIGHT;
            if(onTop) return HitTestResult.HTTOP;
            if(onBottom) return HitTestResult.HTBOTTOM;

            foreach(var dragRegion in dragRegions) {
                if(point.X >= dragRegion.Left &&
                    point.X < dragRegion.Right &&
                    point.Y >= dragRegion.Top &&
                    point.Y < dragRegion.Bottom) {
                    return HitTestResult.HTCAPTION;
                }
            }

            return HitTestResult.HTCLIENT;
        }

        void ApplyPersistedWindowStateIfAvailable() {
            if(!PersistWindowState || Window == null || Window.Handle == IntPtr.Zero) {
                return;
            }

            if(!File.Exists(WindowStateFilePath)) {
                return;
            }

            WindowState state;
            try {
                var json = File.ReadAllText(WindowStateFilePath);
                state = JsonSerializer.Deserialize<WindowState>(json);
            }
            catch(Exception ex) {
                throw new InvalidOperationException($"Failed to read window state from '{WindowStateFilePath}'.", ex);
            }

            if(state == null || state.Width <= 0 || state.Height <= 0) {
                return;
            }

            Position = new Point(state.X, state.Y);
            restoredClientSize = new Size(state.Width, state.Height);
            appliedPersistedWindowState = true;
        }

        void SaveWindowStateIfNeeded() {
            if(!PersistWindowState || Window == null || Window.Handle == IntPtr.Zero || !IsReady) {
                return;
            }

            var size = GetNativeWindowClientSize();
            if(size.Width <= 0 || size.Height <= 0) {
                return;
            }

            try {
                var state = new WindowState {
                    X = Window.Dimensions.X,
                    Y = Window.Dimensions.Y,
                    Width = size.Width,
                    Height = size.Height
                };

                var json = JsonSerializer.Serialize(state, new JsonSerializerOptions {
                    WriteIndented = true
                });
                File.WriteAllText(WindowStateFilePath, json);
            }
            catch(Exception ex) {
                throw new InvalidOperationException($"Failed to write window state to '{WindowStateFilePath}'.", ex);
            }
        }

        protected virtual float RenderTitleBarLeadingContent(int reservedButtonsWidth) {
            return 0f;
        }

        protected virtual void ConfigureTitleBarButtons() {
        }

        protected virtual void PersistFormState() {
        }

        protected virtual void RenderAfterFormWindow() {
        }

        protected virtual void OnShellInitialized() {
        }

        protected virtual void OnCloseRequested(CloseEventArgs e) {
        }

        protected virtual void OnMinimizeRequested() {
            SetWindowMinimized(true);
        }

        protected virtual void OnMaximizeRequested() {
            SetWindowMaximized(!isWindowMaximized);
        }

        protected abstract void RenderFormContent();

        sealed class WindowState {
            public int X { get; set; }

            public int Y { get; set; }

            public int Width { get; set; }

            public int Height { get; set; }
        }

        readonly record struct TitleBarButton(string Id, string Text, int? IconNumber, string Tooltip, bool Enabled, Action OnClick);
    }
}
