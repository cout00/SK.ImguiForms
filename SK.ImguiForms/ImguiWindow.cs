namespace SK.ImguiForms {
    using SK.ImguiForms.Win32;
    using SixLabors.ImageSharp;
    using SixLabors.ImageSharp.Formats;
    using SixLabors.ImageSharp.PixelFormats;
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Drawing;
    using System.IO;
    using System.Linq;
    using System.Runtime.CompilerServices;
    using System.Threading;
    using System.Threading.Tasks;
    using ImGuiNET;
    using Vortice.Direct3D;
    using Vortice.Direct3D11;
    using Vortice.DXGI;
    using Vortice.Mathematics;
    using Point = System.Drawing.Point;
    using Rectangle = System.Drawing.Rectangle;
    using Size = System.Drawing.Size;

    public abstract class ImguiWindow : IDisposable, IAsyncDisposable {
        const float BaseUiScaleMultiplier = 1f;
        const float MinimumInterfaceScale = 0.5f;
        static readonly TimeSpan RenderThreadShutdownTimeout = TimeSpan.FromSeconds(5);

        readonly string title;
        readonly bool dpiAware;
        readonly int initialWindowWidth;
        readonly int initialWindowHeight;
        readonly Format format;
        readonly object lifecycleLock = new();
        readonly ConcurrentQueue<FontHelper.FontLoadDelegate> fontUpdates = new();
        readonly Dictionary<string, TextureInfo> loadedTextures = [];

        WNDCLASSEX wndClass;
        IntPtr selfPointer;
        Thread renderThread;
        CancellationTokenSource cancellationTokenSource;
        TaskCompletionSource<bool> startupCompletionSource;
        TaskCompletionSource<bool> shutdownCompletionSource;
        bool disposed;
        int resourcesReleased;
        int fpsLimit;
        int currentClientWidth;
        int currentClientHeight;
        int frameTimingFrameCount;
        long lastRenderTimestamp;
        long frameTimingLastReportTimestamp;
        long frameTimingPreviousFrameTimestamp;
        long frameTimingInputTicks;
        long frameTimingIntervalTicks;
        long frameTimingUpdateTicks;
        long frameTimingDrawTicks;
        long frameTimingPresentTicks;
        long frameTimingTotalTicks;
        long frameTimingMaxIntervalTicks;
        long frameTimingMaxTotalTicks;
        int isRenderingFrame;
        volatile bool acceptsWindowMessages;
        float currentDpiScale;
        float userInterfaceScale;
        float appliedStyleScale;
        FontHelper.FontLoadDelegate activeFontLoadDelegate;
        ImguiInterfaceSize interfaceSize;

        protected ImguiWindow(string windowTitle, bool dpiAware = false, int initialWindowWidth = 800, int initialWindowHeight = 600) {
            title = windowTitle;
            this.dpiAware = dpiAware;
            this.initialWindowWidth = initialWindowWidth;
            this.initialWindowHeight = initialWindowHeight;
            format = Format.R8G8B8A8_UNorm;
            fpsLimit = 60;
            cancellationTokenSource = new();
            startupCompletionSource = CreateCompletionSource();
            shutdownCompletionSource = CreateCompletionSource();
            currentDpiScale = 1f;
            interfaceSize = ImguiInterfaceSize.Medium;
            userInterfaceScale = GetPresetScale(interfaceSize);
            appliedStyleScale = 1f;

            if(dpiAware) {
                if(!User32.SetProcessDpiAwarenessContext(User32.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2)) {
                    User32.SetProcessDPIAware();
                }
            }
        }

        protected Win32Window Window { get; private set; }

        protected Size InitialWindowSize => new(initialWindowWidth, initialWindowHeight);

        protected bool IsReady => startupCompletionSource.Task.IsCompletedSuccessfully && !cancellationTokenSource.IsCancellationRequested;

        protected virtual WindowStyles WindowStyle => WindowStyles.WS_POPUP;

        protected virtual WindowExStyles WindowExStyle => WindowExStyles.WS_EX_ACCEPTFILES | WindowExStyles.WS_EX_TOPMOST;

        protected virtual bool UseClickThrough => false;

        public nint Handle => Window?.Handle ?? 0;

        public float DpiScale => currentDpiScale > 0f ? currentDpiScale : 1f;

        public float EffectiveUiScale => GetEffectiveUiScale();

        public float InterfaceScale => userInterfaceScale;

        public ImguiInterfaceSize InterfaceSize {
            get => interfaceSize;
            set {
                interfaceSize = value;
                ApplyInterfaceScaleCore(GetPresetScale(value));
            }
        }

        public bool VSync;

        public bool SmoothFramePacing { get; set; } = true;

        public bool PreferFlipSwapChain { get; set; }

        public bool FrameTimingDiagnosticsEnabled { get; set; }

        public string FrameTimingDiagnosticsName { get; set; } = string.Empty;

        public Action<string> FrameTimingDiagnosticsSink { get; set; }

        public int FPSLimit {
            get => fpsLimit;
            set {
                if(value == 0) {
                    fpsLimit = 0;
                    _ = Winmm.MM_EndPeriod(1);
                    return;
                }

                if(value > 0) {
                    fpsLimit = value;
                    _ = Winmm.MM_BeginPeriod(1);
                }
            }
        }

        public Point Position {
            get => Window?.Dimensions.Location ?? Point.Empty;
            set {
                if(Window == null || Window.Handle == IntPtr.Zero || Window.Dimensions.Location == value) {
                    return;
                }

                Window.Dimensions = new Rectangle(value, Window.Dimensions.Size);
                User32.MoveWindow(Window.Handle, value.X, value.Y, Window.Dimensions.Width, Window.Dimensions.Height, true);
            }
        }

        public Size Size {
            get => Window?.Dimensions.Size ?? Size.Empty;
            set {
                if(Window == null || Window.Handle == IntPtr.Zero || Window.Dimensions.Size == value) {
                    return;
                }

                Window.Dimensions = new Rectangle(Window.Dimensions.Location, value);
                User32.MoveWindow(Window.Handle, Window.Dimensions.X, Window.Dimensions.Y, value.Width, value.Height, true);
            }
        }

        public async Task Start() {
            ThrowIfDisposed();

            lock(lifecycleLock) {
                if(renderThread == null) {
                    renderThread = new Thread(RenderThreadMain) {
                        IsBackground = true,
                        Name = $"{GetType().Name}.Render"
                    };
                    renderThread.Start();
                }
            }

            await startupCompletionSource.Task.ConfigureAwait(false);
        }

        public virtual async Task Run() {
            await Start().ConfigureAwait(false);
            await shutdownCompletionSource.Task.ConfigureAwait(false);
        }

        public virtual void Close() {
            cancellationTokenSource.Cancel();
        }

        public void Dispose() {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        public async ValueTask DisposeAsync() {
            if(!TryBeginDispose()) {
                return;
            }

            Close();
            await WaitForRenderThreadShutdownAsync().ConfigureAwait(false);
            ReleaseResources();
            GC.SuppressFinalize(this);
        }

        public void ApplyInterfaceScale(float scale) {
            ApplyInterfaceScaleCore(scale);
        }

        public float ScaleFontSize(float size) {
            return MathF.Max(1f, MathF.Round(size * EffectiveUiScale));
        }

        public unsafe bool ReplaceFont(string pathName, int size, FontGlyphRangeType language) {
            if(!File.Exists(pathName)) {
                return false;
            }

            FontHelper.FontLoadDelegate fontLoadDelegate = config => {
                var io = ImGui.GetIO();
                var glyphRange = language switch {
                    FontGlyphRangeType.English => io.Fonts.GetGlyphRangesDefault(),
                    FontGlyphRangeType.ChineseSimplifiedCommon => io.Fonts.GetGlyphRangesChineseSimplifiedCommon(),
                    FontGlyphRangeType.ChineseFull => io.Fonts.GetGlyphRangesChineseFull(),
                    FontGlyphRangeType.Japanese => io.Fonts.GetGlyphRangesJapanese(),
                    FontGlyphRangeType.Korean => io.Fonts.GetGlyphRangesKorean(),
                    FontGlyphRangeType.Thai => io.Fonts.GetGlyphRangesThai(),
                    FontGlyphRangeType.Vietnamese => io.Fonts.GetGlyphRangesVietnamese(),
                    FontGlyphRangeType.Cyrillic => io.Fonts.GetGlyphRangesCyrillic(),
                    _ => throw new InvalidOperationException($"Font glyph range '{language}' is not supported.")
                };

                io.Fonts.AddFontFromFileTTF(pathName, ScaleFontSize(size), config, glyphRange);
                ImGuiNative.igGetIO()->FontDefault = null;
            };

            return QueueFontUpdate(fontLoadDelegate);
        }

        public unsafe bool ReplaceFont(string pathName, int size, ushort[] glyphRange) {
            if(!File.Exists(pathName) || glyphRange == null || glyphRange.Length == 0) {
                return false;
            }

            FontHelper.FontLoadDelegate fontLoadDelegate = config => {
                var io = ImGui.GetIO();
                fixed(ushort* glyphRangePointer = &glyphRange[0]) {
                    io.Fonts.AddFontFromFileTTF(pathName, ScaleFontSize(size), config, new IntPtr(glyphRangePointer));
                    ImGuiNative.igGetIO()->FontDefault = null;
                }
            };

            return QueueFontUpdate(fontLoadDelegate);
        }

        public unsafe bool ReplaceFont() {
            FontHelper.FontLoadDelegate fontLoadDelegate = config => {
                var io = ImGui.GetIO();
                config->SizePixels = ScaleFontSize(13f);
                io.Fonts.AddFontDefault(config);
                ImGuiNative.igGetIO()->FontDefault = null;
            };

            return QueueFontUpdate(fontLoadDelegate);
        }

        public bool ReplaceFont(FontHelper.FontLoadDelegate fontLoadDelegate) {
            return QueueFontUpdate(fontLoadDelegate);
        }

        public void AddOrGetImagePointer(string filePath, bool srgb, out IntPtr handle, out uint width, out uint height) {
            if(loadedTextures.TryGetValue(filePath, out var textureInfo)) {
                handle = textureInfo.Handle;
                width = textureInfo.Width;
                height = textureInfo.Height;
                return;
            }

            var decoderOptions = new DecoderOptions();
            decoderOptions.Configuration.PreferContiguousImageBuffers = true;

            using var image = Image.Load<Rgba32>(decoderOptions, filePath);
            handle = renderer.CreateImageTexture(image, srgb ? Format.R8G8B8A8_UNorm_SRgb : Format.R8G8B8A8_UNorm);
            width = (uint)image.Width;
            height = (uint)image.Height;
            loadedTextures[filePath] = new TextureInfo(handle, width, height);
        }

        public void AddOrGetImagePointer(string name, Image<Rgba32> image, bool srgb, out IntPtr handle) {
            if(loadedTextures.TryGetValue(name, out var textureInfo)) {
                handle = textureInfo.Handle;
                return;
            }

            handle = renderer.CreateImageTexture(image, srgb ? Format.R8G8B8A8_UNorm_SRgb : Format.R8G8B8A8_UNorm);
            loadedTextures[name] = new TextureInfo(handle, (uint)image.Width, (uint)image.Height);
        }

        public bool TryGetImagePointer(string key, out IntPtr handle, out uint width, out uint height) {
            if(loadedTextures.TryGetValue(key, out var textureInfo)) {
                handle = textureInfo.Handle;
                width = textureInfo.Width;
                height = textureInfo.Height;
                return true;
            }

            handle = IntPtr.Zero;
            width = 0;
            height = 0;
            return false;
        }

        public bool RemoveImage(string key) {
            if(!loadedTextures.Remove(key, out var textureInfo)) {
                return false;
            }

            return renderer != null && renderer.RemoveImageTexture(textureInfo.Handle);
        }

        protected virtual void Dispose(bool disposing) {
            if(!TryBeginDispose()) {
                return;
            }

            if(disposing) {
                ThrowIfSynchronousDisposeWouldDeadlock();
                Close();

                WaitForRenderThreadShutdown();
                ReleaseResources();
            }

            ReleaseResources();
        }

        protected virtual Task PostInitialized() {
            return Task.CompletedTask;
        }

        protected virtual void OnBeforeRender() {
        }

        protected virtual void OnAfterRender() {
        }

        protected virtual bool TryHandleWindowMessage(WindowMessage message, UIntPtr wParam, IntPtr lParam, out IntPtr result) {
            result = IntPtr.Zero;
            return false;
        }

        protected virtual bool TryHandleInitializationWindowMessage(WindowMessage message, UIntPtr wParam, IntPtr lParam, out IntPtr result) {
            result = IntPtr.Zero;
            return false;
        }

        protected virtual void HandleWindowMessage(WindowMessage message, UIntPtr wParam, IntPtr lParam) {
            switch(message) {
                case WindowMessage.ShowWindow:
                    EnsureRenderSizeMatchesClientRect();
                    break;

                case WindowMessage.Size:
                    if((SizeMessage)wParam is SizeMessage.SIZE_RESTORED or SizeMessage.SIZE_MAXIMIZED) {
                        var packedSize = (int)lParam;
                        OnResize(Utils.Loword(packedSize), Utils.Hiword(packedSize));
                        UpdateWindowBoundsFromHandle();
                    }

                    break;

                case WindowMessage.DpiChanged:
                    UpdateDpiScale();
                    ReplaceFontIfRequired();
                    EnsureRenderSizeMatchesClientRect();
                    break;

                case WindowMessage.Move:
                    UpdateWindowBoundsFromHandle();
                    break;

                case WindowMessage.Destroy:
                    Close();
                    break;
            }
        }

        protected virtual bool ShouldRenderCurrentFrame() {
            return true;
        }

        protected virtual int GetActiveFpsLimit() {
            return FPSLimit;
        }

        protected virtual void OnWindowCreated() {
        }

        protected virtual void OnWindowShown() {
        }

        protected void ApplyCurrentDpiScaleToStyle() {
            UpdateDpiScale(styleIsUnscaled: true);
        }

        protected bool EnqueueFontUpdate(FontHelper.FontLoadDelegate fontLoadDelegate) {
            return QueueFontUpdate(fontLoadDelegate);
        }

        protected Size GetNativeWindowClientSize() {
            if(!TryGetClientRect(out var clientRect)) {
                return Size.Empty;
            }

            return new Size(clientRect.Width, clientRect.Height);
        }

        protected void SetNativeWindowClientSize(Size clientSize) {
            if(Window == null || Window.Handle == IntPtr.Zero || clientSize.Width <= 0 || clientSize.Height <= 0) {
                return;
            }

            var outerSize = GetNativeWindowOuterSizeForClientSize(clientSize);
            var x = Window.Dimensions.X;
            var y = Window.Dimensions.Y;
            Window.Dimensions = new Rectangle(x, y, outerSize.Width, outerSize.Height);

            User32.SetWindowPos(
                Window.Handle,
                IntPtr.Zero,
                x,
                y,
                outerSize.Width,
                outerSize.Height,
                SetWindowPosFlags.NOZORDER
                | SetWindowPosFlags.NOOWNERZORDER
                | SetWindowPosFlags.FRAMECHANGED);
        }

        protected Size GetNativeWindowOuterSizeForClientSize(Size clientSize) {
            if(Window == null || Window.Handle == IntPtr.Zero) {
                return clientSize;
            }

            var rect = new RECT {
                Left = 0,
                Top = 0,
                Right = clientSize.Width,
                Bottom = clientSize.Height
            };

            var style = (int)User32.GetWindowLong(Window.Handle, (int)WindowLongParam.GWL_STYLE);
            var exStyle = (int)User32.GetWindowLong(Window.Handle, (int)WindowLongParam.GWL_EXSTYLE);
            if(!dpiAware || !User32.AdjustWindowRectExForDpi(ref rect, style, false, exStyle, User32.GetDpiForWindow(Window.Handle))) {
                User32.AdjustWindowRectEx(ref rect, style, false, exStyle);
            }

            return new Size(rect.Width, rect.Height);
        }

        protected bool TryGetClientRect(out RECT clientRect) {
            clientRect = default;
            return Window != null && Window.Handle != IntPtr.Zero && User32.GetClientRect(Window.Handle, out clientRect);
        }

        private protected bool TryGetClientPoint(IntPtr lParam, out POINT point) {
            point = new POINT(
                unchecked((short)((long)lParam & 0xFFFF)),
                unchecked((short)(((long)lParam >> 16) & 0xFFFF)));

            return Window != null && Window.Handle != IntPtr.Zero && User32.ScreenToClient(Window.Handle, ref point);
        }

        protected bool IsWindowMinimized() {
            return Window != null && Window.Handle != IntPtr.Zero && User32.IsIconic(Window.Handle);
        }

        protected bool IsWindowForeground() {
            return Window != null && Window.Handle != IntPtr.Zero && User32.GetForegroundWindow() == Window.Handle;
        }

        protected void EnsureRenderSizeMatchesClientRect() {
            if(!TryGetClientRect(out var clientRect) || clientRect.Width <= 0 || clientRect.Height <= 0) {
                return;
            }

            if(clientRect.Width != currentClientWidth || clientRect.Height != currentClientHeight) {
                OnResize(clientRect.Width, clientRect.Height);
            }
        }

        protected void RenderFrameImmediately() {
            if(renderView == null) {
                return;
            }

            var now = Stopwatch.GetTimestamp();
            var deltaTime = lastRenderTimestamp == 0
                ? 1f / 60f
                : (now - lastRenderTimestamp) / (float)Stopwatch.Frequency;
            lastRenderTimestamp = now;
            RenderFrame(Math.Max(deltaTime, 1f / 1000f), new Color4(0.0f));
        }

        protected abstract void Render();

        ID3D11Device device;
        ID3D11DeviceContext deviceContext;
        IDXGISwapChain swapChain;
        ID3D11Texture2D backBuffer;
        ID3D11RenderTargetView renderView;
        ImGuiRenderer renderer;
        ImGuiInputHandler inputHandler;
        int swapChainBufferCount = 1;

        static TaskCompletionSource<bool> CreateCompletionSource() {
            return new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        static float GetPresetScale(ImguiInterfaceSize size) {
            return size switch {
                ImguiInterfaceSize.Small => 0.7f,
                ImguiInterfaceSize.Large => 1f,
                _ => 0.8f
            };
        }

        bool TryBeginDispose() {
            lock(lifecycleLock) {
                if(disposed) {
                    return false;
                }

                disposed = true;
                acceptsWindowMessages = false;
                return true;
            }
        }

        void ThrowIfSynchronousDisposeWouldDeadlock() {
            if(Monitor.IsEntered(ImGuiContextSync.SyncRoot)) {
                throw new InvalidOperationException(
                    $"{GetType().Name} cannot be disposed synchronously while holding ImGuiContextSync.SyncRoot. "
                    + "Call Close() and await DisposeAsync() outside the render callback.");
            }
        }

        void ThrowIfDisposed() {
            ObjectDisposedException.ThrowIf(disposed, GetType().Name);
        }

        void RenderThreadMain() {
            Exception failure = null;
            var startupCancelled = false;

            try {
                InitializeResources().GetAwaiter().GetResult();
                startupCompletionSource.TrySetResult(true);
                RunRenderLoop(cancellationTokenSource.Token);
            }
            catch(OperationCanceledException) {
                startupCancelled = true;
            }
            catch(Exception ex) {
                failure = ex;
            }
            finally {
                try {
                    ReleaseResources();
                }
                catch(Exception cleanupException) {
                    failure = failure == null
                        ? cleanupException
                        : new AggregateException(failure, cleanupException);
                }

                lock(lifecycleLock) {
                    if(ReferenceEquals(renderThread, Thread.CurrentThread)) {
                        renderThread = null;
                    }
                }

                if(failure != null) {
                    startupCompletionSource.TrySetException(failure);
                    shutdownCompletionSource.TrySetException(failure);
                }
                else {
                    if(startupCancelled) {
                        startupCompletionSource.TrySetCanceled();
                    }

                    shutdownCompletionSource.TrySetResult(true);
                }
            }
        }

        async Task InitializeResources() {
            D3D11.D3D11CreateDevice(
                null,
                DriverType.Hardware,
                DeviceCreationFlags.None,
                new[] { FeatureLevel.Level_10_0 },
                out device,
                out deviceContext);

            selfPointer = Kernel32.GetModuleHandle(null);
            wndClass = new WNDCLASSEX {
                Size = Unsafe.SizeOf<WNDCLASSEX>(),
                Styles = WindowClassStyles.CS_HREDRAW | WindowClassStyles.CS_VREDRAW | WindowClassStyles.CS_PARENTDC,
                WindowProc = WndProc,
                InstanceHandle = selfPointer,
                CursorHandle = User32.LoadCursor(IntPtr.Zero, SystemCursor.IDC_ARROW),
                BackgroundBrushHandle = IntPtr.Zero,
                IconHandle = IntPtr.Zero,
                MenuName = string.Empty,
                ClassName = $"{GetType().FullName}.{Guid.NewGuid():N}",
                SmallIconHandle = IntPtr.Zero,
                ClassExtraBytes = 0,
                WindowExtraBytes = 0
            };

            if(User32.RegisterClassEx(ref wndClass) == 0) {
                throw new InvalidOperationException($"Failed to register window class '{wndClass.ClassName}'.");
            }

            Window = new Win32Window(
                wndClass.ClassName,
                initialWindowWidth,
                initialWindowHeight,
                0,
                0,
                title,
                WindowStyle,
                WindowExStyle);

            renderer = new ImGuiRenderer(device, deviceContext, initialWindowWidth, initialWindowHeight);
            inputHandler = new ImGuiInputHandler(Window.Handle, () => renderer != null ? renderer.Context : IntPtr.Zero);

            OnWindowCreated();
            UpdateDpiScale(styleIsUnscaled: true);
            await PostInitialized().ConfigureAwait(false);
            ReplaceFontIfRequired();

            User32.ShowWindow(Window.Handle, ShowWindowCommand.Show);
            EnsureRenderSizeMatchesClientRect();
            OnWindowShown();
            acceptsWindowMessages = true;
        }

        void RunRenderLoop(CancellationToken token) {
            var stopwatch = Stopwatch.StartNew();
            var clearColor = new Color4(0.0f);

            while(!token.IsCancellationRequested) {
                var frameStartTimestamp = Stopwatch.GetTimestamp();
                var deltaTime = stopwatch.ElapsedTicks / (float)Stopwatch.Frequency;
                stopwatch.Restart();

                Window?.PumpEvents();

                if(ShouldRenderCurrentFrame()) {
                    EnsureRenderSizeMatchesClientRect();
                    RenderFrame(Math.Max(deltaTime, 1f / 1000f), clearColor);
                }

                ReplaceFontIfRequired();

                if(VSync) {
                    continue;
                }

                var effectiveFpsLimit = GetActiveFpsLimit();
                if(effectiveFpsLimit > 0) {
                    WaitForFrameBudget(frameStartTimestamp, effectiveFpsLimit);
                }
            }
        }

        void WaitForFrameBudget(long frameStartTimestamp, int effectiveFpsLimit) {
            var frameBudgetTicks = Stopwatch.Frequency / effectiveFpsLimit;
            var targetTimestamp = frameStartTimestamp + frameBudgetTicks;

            if(!SmoothFramePacing) {
                var remainingMs = (int)((targetTimestamp - Stopwatch.GetTimestamp()) * 1000 / Stopwatch.Frequency);
                if(remainingMs > 0) {
                    Thread.Sleep(remainingMs);
                }

                return;
            }

            while(true) {
                var remainingTicks = targetTimestamp - Stopwatch.GetTimestamp();
                if(remainingTicks <= 0) {
                    return;
                }

                var remainingMs = remainingTicks * 1000.0 / Stopwatch.Frequency;
                if(remainingMs > 2.0) {
                    Thread.Sleep(1);
                }
                else if(remainingMs > 0.5) {
                    Thread.Yield();
                }
                else {
                    Thread.SpinWait(10);
                }
            }
        }

        void RenderFrame(float deltaTime, Color4 clearColor) {
            if(Interlocked.Exchange(ref isRenderingFrame, 1) != 0) {
                return;
            }

            var measureFrameTiming = FrameTimingDiagnosticsEnabled;
            var frameStartTimestamp = measureFrameTiming ? Stopwatch.GetTimestamp() : 0;
            var frameIntervalTicks = measureFrameTiming && frameTimingPreviousFrameTimestamp != 0
                ? frameStartTimestamp - frameTimingPreviousFrameTimestamp
                : 0;
            if(measureFrameTiming) {
                frameTimingPreviousFrameTimestamp = frameStartTimestamp;
            }

            long inputEndTimestamp = 0;
            long updateEndTimestamp = 0;
            long drawEndTimestamp = 0;
            long presentEndTimestamp = 0;

            try {
                if(renderer == null || deviceContext == null || renderView == null || swapChain == null) {
                    return;
                }

                var wantsMouseCapture = inputHandler.Update();
                if(UseClickThrough && Window != null) {
                    Utils.SetOverlayClickable(Window.Handle, wantsMouseCapture);
                }

                if(measureFrameTiming) {
                    inputEndTimestamp = Stopwatch.GetTimestamp();
                }

                renderer.Update(deltaTime, () => {
                    OnBeforeRender();
                    Render();
                    OnAfterRender();
                });

                if(measureFrameTiming) {
                    updateEndTimestamp = Stopwatch.GetTimestamp();
                }

                deviceContext.OMSetRenderTargets(renderView);
                deviceContext.ClearRenderTargetView(renderView, clearColor);
                renderer.Render();

                if(measureFrameTiming) {
                    drawEndTimestamp = Stopwatch.GetTimestamp();
                }

                swapChain.Present(VSync ? 1 : 0, PresentFlags.None);

                if(measureFrameTiming) {
                    presentEndTimestamp = Stopwatch.GetTimestamp();
                    RecordFrameTiming(
                        frameIntervalTicks,
                        inputEndTimestamp - frameStartTimestamp,
                        updateEndTimestamp - inputEndTimestamp,
                        drawEndTimestamp - updateEndTimestamp,
                        presentEndTimestamp - drawEndTimestamp,
                        presentEndTimestamp - frameStartTimestamp);
                }
            }
            finally {
                Interlocked.Exchange(ref isRenderingFrame, 0);
            }
        }

        void RecordFrameTiming(long intervalTicks, long inputTicks, long updateTicks, long drawTicks, long presentTicks, long totalTicks) {
            var now = Stopwatch.GetTimestamp();
            if(frameTimingLastReportTimestamp == 0) {
                frameTimingLastReportTimestamp = now;
            }

            frameTimingFrameCount++;
            frameTimingIntervalTicks += intervalTicks;
            frameTimingInputTicks += inputTicks;
            frameTimingUpdateTicks += updateTicks;
            frameTimingDrawTicks += drawTicks;
            frameTimingPresentTicks += presentTicks;
            frameTimingTotalTicks += totalTicks;
            frameTimingMaxIntervalTicks = Math.Max(frameTimingMaxIntervalTicks, intervalTicks);
            frameTimingMaxTotalTicks = Math.Max(frameTimingMaxTotalTicks, totalTicks);

            var reportElapsedTicks = now - frameTimingLastReportTimestamp;
            if(reportElapsedTicks < Stopwatch.Frequency) {
                return;
            }

            var frameCount = frameTimingFrameCount;
            var elapsedSeconds = reportElapsedTicks / (double)Stopwatch.Frequency;
            var diagnosticsName = string.IsNullOrWhiteSpace(FrameTimingDiagnosticsName)
                ? GetType().Name
                : FrameTimingDiagnosticsName;
            var diagnostics = new ImguiFrameTimingDiagnostics(
                diagnosticsName,
                frameCount / elapsedSeconds,
                TicksToMilliseconds(frameTimingIntervalTicks / frameCount),
                TicksToMilliseconds(frameTimingMaxIntervalTicks),
                TicksToMilliseconds(frameTimingTotalTicks / frameCount),
                TicksToMilliseconds(frameTimingMaxTotalTicks),
                TicksToMilliseconds(frameTimingInputTicks / frameCount),
                TicksToMilliseconds(frameTimingUpdateTicks / frameCount),
                TicksToMilliseconds(frameTimingDrawTicks / frameCount),
                TicksToMilliseconds(frameTimingPresentTicks / frameCount),
                VSync,
                FPSLimit);
            ImguiFrameTimingDiagnosticsRegistry.Set(diagnostics);

            var message = string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"{diagnosticsName}: fps={diagnostics.FramesPerSecond:0.0}, interval={diagnostics.AverageIntervalMilliseconds:0.###}ms, intervalMax={diagnostics.MaximumIntervalMilliseconds:0.###}ms, render={diagnostics.AverageRenderMilliseconds:0.###}ms, renderMax={diagnostics.MaximumRenderMilliseconds:0.###}ms, input={diagnostics.AverageInputMilliseconds:0.###}ms, imgui={diagnostics.AverageImguiMilliseconds:0.###}ms, d3d={diagnostics.AverageD3DMilliseconds:0.###}ms, present={diagnostics.AveragePresentMilliseconds:0.###}ms, vsync={VSync}, fpsLimit={FPSLimit}");

            var sink = FrameTimingDiagnosticsSink;
            if(sink != null) {
                sink(message);
            }
            else {
                Debug.WriteLine(message);
            }

            frameTimingLastReportTimestamp = now;
            frameTimingFrameCount = 0;
            frameTimingIntervalTicks = 0;
            frameTimingInputTicks = 0;
            frameTimingUpdateTicks = 0;
            frameTimingDrawTicks = 0;
            frameTimingPresentTicks = 0;
            frameTimingTotalTicks = 0;
            frameTimingMaxIntervalTicks = 0;
            frameTimingMaxTotalTicks = 0;
        }

        static double TicksToMilliseconds(long ticks) {
            return ticks * 1000.0 / Stopwatch.Frequency;
        }

        void OnResize(int width, int height) {
            if(width <= 0 || height <= 0) {
                return;
            }

            currentClientWidth = width;
            currentClientHeight = height;

            if(renderView == null) {
                using var dxgiFactory = device.QueryInterface<IDXGIDevice>().GetParent<IDXGIAdapter>().GetParent<IDXGIFactory>();
                swapChain = CreateSwapChain(dxgiFactory, width, height);
                dxgiFactory.MakeWindowAssociation(Window.Handle, WindowAssociationFlags.IgnoreAll);
                backBuffer = swapChain.GetBuffer<ID3D11Texture2D>(0);
                renderView = device.CreateRenderTargetView(backBuffer);
            }
            else {
                deviceContext.OMSetRenderTargets((ID3D11RenderTargetView)null);
                deviceContext.Flush();
                renderView.Dispose();
                backBuffer.Dispose();
                swapChain.ResizeBuffers(swapChainBufferCount, width, height, format, SwapChainFlags.None);
                backBuffer = swapChain.GetBuffer<ID3D11Texture2D>(0);
                renderView = device.CreateRenderTargetView(backBuffer);
            }

            renderer.Resize(width, height);
        }

        IDXGISwapChain CreateSwapChain(IDXGIFactory dxgiFactory, int width, int height) {
            if(PreferFlipSwapChain) {
                try {
                    swapChainBufferCount = 2;
                    return dxgiFactory.CreateSwapChain(device, CreateSwapChainDescription(width, height, 2, SwapEffect.FlipDiscard));
                }
                catch {
                    swapChainBufferCount = 1;
                    PreferFlipSwapChain = false;
                }
            }

            swapChainBufferCount = 1;
            return dxgiFactory.CreateSwapChain(device, CreateSwapChainDescription(width, height, 1, SwapEffect.Discard));
        }

        SwapChainDescription CreateSwapChainDescription(int width, int height, int bufferCount, SwapEffect swapEffect) {
            return new SwapChainDescription {
                BufferCount = bufferCount,
                BufferDescription = new ModeDescription(width, height, format),
                Windowed = true,
                OutputWindow = Window.Handle,
                SampleDescription = new SampleDescription(1, 0),
                SwapEffect = swapEffect,
                BufferUsage = Usage.RenderTargetOutput
            };
        }

        void UpdateWindowBoundsFromHandle() {
            if(Window == null || Window.Handle == IntPtr.Zero || !User32.GetWindowRect(Window.Handle, out var windowRect)) {
                return;
            }

            Window.Dimensions = new Rectangle(windowRect.Left, windowRect.Top, windowRect.Width, windowRect.Height);
        }

        float GetWindowDpiScale() {
            if(!dpiAware || Window == null || Window.Handle == IntPtr.Zero) {
                return 1f;
            }

            var dpi = User32.GetDpiForWindow(Window.Handle);
            return dpi == 0 ? 1f : dpi / 96f;
        }

        float GetEffectiveUiScale() {
            return MathF.Max(MinimumInterfaceScale, DpiScale * userInterfaceScale * BaseUiScaleMultiplier);
        }

        void ApplyInterfaceScaleCore(float scale) {
            var normalizedScale = MathF.Max(MinimumInterfaceScale, scale);
            if(MathF.Abs(normalizedScale - userInterfaceScale) <= 0.001f) {
                return;
            }

            userInterfaceScale = normalizedScale;
            if(renderer != null) {
                UpdateDpiScale();
                if(IsReady) {
                    QueueActiveFontReloadForDpi();
                }
            }
        }

        void UpdateDpiScale(bool styleIsUnscaled = false) {
            lock(ImGuiContextSync.SyncRoot) {
                if(renderer == null) {
                    return;
                }

                var previousContext = ImGui.GetCurrentContext();
                var dpiScale = GetWindowDpiScale();
                var effectiveScale = MathF.Max(MinimumInterfaceScale, dpiScale * userInterfaceScale * BaseUiScaleMultiplier);
                var effectiveScaleChanged = MathF.Abs(effectiveScale - appliedStyleScale) > 0.001f;
                if(!effectiveScaleChanged && !styleIsUnscaled) {
                    return;
                }

                currentDpiScale = dpiScale;
                renderer.MakeCurrent();

                var io = ImGui.GetIO();
                io.FontGlobalScale = 1f;

                var style = ImGui.GetStyle();
                var styleScaleFactor = styleIsUnscaled
                    ? effectiveScale
                    : appliedStyleScale > 0f
                        ? effectiveScale / appliedStyleScale
                        : effectiveScale;
                if(MathF.Abs(styleScaleFactor - 1f) > 0.001f) {
                    style.ScaleAllSizes(styleScaleFactor);
                }

                appliedStyleScale = effectiveScale;
                if(IsReady) {
                    QueueActiveFontReloadForDpi();
                }

                if(previousContext != IntPtr.Zero && previousContext != renderer.Context) {
                    ImGui.SetCurrentContext(previousContext);
                }
            }
        }

        bool QueueFontUpdate(FontHelper.FontLoadDelegate fontLoadDelegate) {
            if(fontLoadDelegate == null) {
                return false;
            }

            activeFontLoadDelegate = fontLoadDelegate;
            fontUpdates.Enqueue(fontLoadDelegate);
            return true;
        }

        void WaitForRenderThreadShutdown() {
            Thread threadToWait;
            lock(lifecycleLock) {
                threadToWait = renderThread;
            }

            if(threadToWait == null || Thread.CurrentThread == threadToWait) {
                return;
            }

            var shutdownTask = shutdownCompletionSource.Task;
            if(!shutdownTask.Wait(RenderThreadShutdownTimeout)) {
                throw new TimeoutException($"{GetType().Name} render thread did not stop within {RenderThreadShutdownTimeout.TotalSeconds:0} seconds.");
            }

            shutdownTask.GetAwaiter().GetResult();
        }

        async Task WaitForRenderThreadShutdownAsync() {
            Thread threadToWait;
            lock(lifecycleLock) {
                threadToWait = renderThread;
            }

            if(threadToWait == null || Thread.CurrentThread == threadToWait) {
                return;
            }

            var shutdownTask = shutdownCompletionSource.Task;
            try {
                await shutdownTask.WaitAsync(RenderThreadShutdownTimeout).ConfigureAwait(false);
            }
            catch(TimeoutException ex) {
                throw new TimeoutException($"{GetType().Name} render thread did not stop within {RenderThreadShutdownTimeout.TotalSeconds:0} seconds.", ex);
            }
        }

        void ReleaseResources() {
            if(Interlocked.Exchange(ref resourcesReleased, 1) != 0) {
                return;
            }

            if(FPSLimit > 0) {
                _ = Winmm.MM_EndPeriod(1);
            }

            foreach(var key in loadedTextures.Keys.ToArray()) {
                RemoveImage(key);
            }

            loadedTextures.Clear();
            fontUpdates.Clear();

            renderView?.Release();
            backBuffer?.Release();
            swapChain?.Release();
            renderer?.Dispose();
            Window?.Dispose();
            deviceContext?.Release();
            device?.Release();
            cancellationTokenSource.Dispose();

            if(selfPointer != IntPtr.Zero && !User32.UnregisterClass(wndClass.ClassName, selfPointer)) {
                throw new InvalidOperationException($"Failed to unregister window class '{wndClass.ClassName}'.");
            }

            selfPointer = IntPtr.Zero;
        }

        void ReplaceFontIfRequired() {
            if(renderer == null) {
                return;
            }

            while(fontUpdates.TryDequeue(out var fontUpdate)) {
                renderer.UpdateFontTexture(fontUpdate);
            }
        }

        void QueueActiveFontReloadForDpi() {
            if(activeFontLoadDelegate != null) {
                fontUpdates.Enqueue(activeFontLoadDelegate);
            }
        }

        bool TryHandleFrameworkMessage(WindowMessage message, UIntPtr wParam, IntPtr lParam, out IntPtr result) {
            if(message == WindowMessage.Paint && Window != null) {
                User32.BeginPaint(Window.Handle, out var paintStruct);
                User32.EndPaint(Window.Handle, ref paintStruct);
                result = IntPtr.Zero;
                return true;
            }

            result = IntPtr.Zero;
            return false;
        }

        IntPtr WndProc(IntPtr hWnd, uint messageValue, UIntPtr wParam, IntPtr lParam) {
            var message = (WindowMessage)messageValue;

            if(!acceptsWindowMessages || Window == null || Window.Handle != hWnd) {
                if(TryHandleInitializationWindowMessage(message, wParam, lParam, out var initializationResult)) {
                    return initializationResult;
                }

                return User32.DefWindowProc(hWnd, messageValue, wParam, lParam);
            }

            if(TryHandleFrameworkMessage(message, wParam, lParam, out var frameworkResult)) {
                return frameworkResult;
            }

            if(TryHandleWindowMessage(message, wParam, lParam, out var customResult)) {
                return customResult;
            }

            if(inputHandler != null && inputHandler.ProcessMessage(message, wParam, lParam)) {
                return IntPtr.Zero;
            }

            HandleWindowMessage(message, wParam, lParam);
            return User32.DefWindowProc(hWnd, messageValue, wParam, lParam);
        }

        readonly record struct TextureInfo(IntPtr Handle, uint Width, uint Height);
    }
}
