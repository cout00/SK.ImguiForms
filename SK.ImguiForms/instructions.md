# SK.ImguiForms integration instructions

This document is for AI agents that need to integrate `SK.ImguiForms` into another Windows .NET project.

## What this library provides

`SK.ImguiForms` is a Win32 + Direct3D11 ImGui host with two explicit app models:

- `ImguiForm`: a normal native window with a custom ImGui-drawn title bar, resize handling, drag zones, and persisted window state.
- `ImguiOverlay`: a transparent overlay that follows another native window and renders into an ImGui background draw list.

## Project requirements

- Target Windows only.
- Prefer `net10.0-windows`, which is what the library currently targets.
- Add a project reference to `SK.ImguiForms`.

If you reference the project directly, the default fonts are copied automatically:

- `Fonts\NotoMono-Regular.ttf`
- `Fonts\Font Awesome 7 Free-Solid-900.otf`

Those files are resolved from `AppContext.BaseDirectory\Fonts`.

## Choose the correct base class

Use `ImguiForm` when you need:

- a standalone desktop window
- built-in title bar buttons
- window dragging and resizing
- automatic persistence to `window_state.json`

Use `ImguiOverlay` when you need:

- a transparent overlay above another window
- automatic z-order syncing with a target window
- click-through behavior
- rendering via `ImDrawListPtr`

## Minimal standalone form

Use `ImguiApplication.Start(...)` for the main window of a standalone app.

```csharp
using SK.ImguiForms;

[STAThread]
static void Main()
{
    ImguiApplication.Start(new MyForm()).GetAwaiter().GetResult();
}

sealed class MyForm : ImguiForm
{
    public MyForm() : base("My Tool", dpiAware: true, initialWindowWidth: 1280, initialWindowHeight: 720)
    {
    }

    protected override void RenderFormContent()
    {
        ImGuiNET.ImGui.Text("Hello from SK.ImguiForms");
    }
}
```

## Minimal overlay

Use `Start()` when the overlay is secondary to another host process or form.

If you already have a launched `Process`, prefer the built-in process-targeting constructor. `ImguiOverlay` will wait for the real top-level window, track it automatically, and size itself to the target client area by default.

```csharp
using System.Diagnostics;
using ImGuiNET;
using SK.ImguiForms;

sealed class MyOverlay : ImguiOverlay
{
    public MyOverlay(Process targetProcess) : base("My Overlay", targetProcess, dpiAware: true)
    {
    }

    protected override void RenderOverlay(ImDrawListPtr drawList)
    {
        drawList.AddText(new System.Numerics.Vector2(20, 20), 0xFFFFFFFF, "Overlay");
    }
}
```

## `ImguiWindow` APIs you will usually use

- `await Start()` - create the native window and start rendering.
- `await Run()` - start and then wait until the window closes.
- `Close()` - request shutdown.
- `InterfaceSize` - preset UI scale via `ImguiInterfaceSize.Small`, `.Medium`, or `.Large`.
- `ApplyInterfaceScale(float scale)` - custom UI scale when presets are not enough.
- `DpiScale`, `EffectiveUiScale`, `InterfaceScale` - inspect current scaling.
- `ReplaceFont(...)` - replace the default text font.
- `Position`, `Size`, `Handle` - native window interop and placement.
- `FPSLimit`, `VSync` - basic render pacing.

If you change theme metrics after DPI or interface-size changes, call `ApplyCurrentDpiScaleToStyle()` from your subclass.

## `ImguiForm` hooks

`ImguiForm` already owns the shell. Your subclass should usually only provide content and behavior.

Required:

- `RenderFormContent()` - render the main client area.

Common optional hooks:

- `OnBeforeRender()` - per-frame prep before the form shell is rendered.
- `RenderTitleBarLeadingContent(int reservedButtonsWidth)` - render custom content on the left side of the title bar.
- `ConfigureTitleBarButtons()` - add custom title bar buttons.
- `PersistFormState()` - persist app-specific state that is not native window geometry.
- `RenderAfterFormWindow()` - render extra floating windows after the main form.
- `OnShellInitialized()` - one-time shell setup after the native window exists.
- `OnCloseRequested()`, `OnMinimizeRequested()`, `OnMaximizeRequested()` - override system button behavior.

Built-in title bar helpers:

- `AddTitleBarButton(...)`
- `AddTitleBarIconButton(...)`

Built-in drag helpers:

- `DragArea(...)`
- `RegisterDragRegionForLastItem()`
- `RegisterDragRegion(...)`

These exist so you do not have to reimplement Win32 hit-testing or custom dragging logic in consumers.

## Form window state

By default, `ImguiForm` stores native window placement in:

- `AppContext.BaseDirectory\window_state.json`

This is automatic. You do not need to save `LastWindowPosition` or `LastWindowSize` in your own settings model.

Relevant customization points:

- `PersistWindowState` - override and return `false` to disable geometry persistence.
- `WindowStateFilePath` - override to change the file path or file name.
- `DefaultWindowPosition`
- `DefaultWindowSize`

## `ImguiOverlay` hooks

Required:

- `RenderOverlay(ImDrawListPtr drawList)`

Common optional hooks:

- `GetTargetProcess()` - provide the target process dynamically instead of passing it in the constructor.
- `GetTargetWindowHandle()` - override target-window discovery if process-based lookup is not enough.
- `GetOverlayBounds(nint targetWindowHandle)` - override automatic bounds if you do not want the default target client area.
- `GetZOrderTargetWindowHandle(nint targetWindowHandle)` - override the window that controls overlay z-order.
- `GetOwnerWindowHandle(nint targetWindowHandle)` - override the owner/parent window handle if different from the z-order target.
- `UseTargetClientBounds` - switch the automatic bounds source between client area and full window bounds.

Convenience APIs:

- `AttachToProcess(Process process)` - assign or replace the target process after construction.
- `ActivateTargetWindow()` - bring the resolved target window to the foreground.
- `TargetWindowHandle` - inspect the currently resolved native target window handle.

`ImguiOverlay` already handles:

- waiting for a target window to appear
- top-level target window discovery for the supplied process
- click-through transparency
- owner window sync
- topmost state sync
- bounds sync and auto-sizing

Do not duplicate that logic in consumers unless you are intentionally replacing the default behavior.

## Fonts and graphics

`ImguiForm` creates `SKImguiGraphics` automatically.

`ImguiOverlay` creates `SKOverlayGraphics` automatically.

Both types enqueue default font loading automatically from the `Fonts` directory next to the executable. In a normal integration, you should not create those graphics objects manually.

For draw-list coordinates, sizes, radii, line thicknesses, ImGui fixed button sizes, combo widths, and every other fixed design-time value, always use `Graphics.Scale(...)`. This is critical: unscaled base sizes will break on non-100% DPI and when `InterfaceSize` changes. The method applies the current effective UI scale from the owning `ImguiWindow` and has overloads for `float`, `Vector2`, and `RectangleF`.

Use logical design values in code and scale them at the API boundary:

```csharp
var margin = Graphics.Scale(18f);
var buttonSize = Graphics.Scale(new Vector2(42f, 34f));
var panel = new RectangleF(margin, margin, width - margin * 2f, Graphics.Scale(120f));

if(Graphics.IconButton("run", 0xf04b, IconSize.Medium, buttonSize, "Run")) {
    Start();
}

ImGui.SetNextItemWidth(Graphics.Scale(130f));
ImGui.BeginCombo("##interface-size", InterfaceSize.ToString());

Graphics.DrawRectFilled(panel, Color.FromArgb(180, 42, 62, 116), Graphics.Scale(12f));
```

Do not leave base sizes unscaled:

```csharp
// Wrong: these constants ignore DPI and InterfaceSize.
ImGui.Button("Run", new Vector2(42f, 34f));
ImGui.SetNextItemWidth(130f);
Graphics.DrawRectFilled(panel, Color.FromArgb(180, 42, 62, 116), 12f);
```

Do not pass `Graphics.Scale(...)` into `DrawText(..., float fontSize)` or other explicit font-size parameters. Font sizes are already resolved through the window's `EffectiveUiScale`.

## Recommended integration pattern

1. Add a reference to `SK.ImguiForms`.
2. Create one subclass of `ImguiForm` for your main app window.
3. Put application-specific UI in `RenderFormContent()`.
4. Use `ConfigureTitleBarButtons()` and `RenderTitleBarLeadingContent(...)` instead of custom shell code.
5. Use `InterfaceSize` for preset scaling; use `ApplyInterfaceScale(float)` only for custom values.
6. In overlay draw-list rendering, scale fixed layout values with `Graphics.Scale(...)`.
7. If you need an overlay, create a separate `ImguiOverlay` subclass and implement only bounds + drawing.
8. Let the base classes own window persistence, host synchronization, and drag behavior.

## Anti-patterns

Avoid these when integrating:

- do not manually persist window geometry in app settings unless you intentionally disable base persistence
- do not manually create `SKImguiGraphics` or `SKOverlayGraphics` in consumers
- do not reimplement native drag zones, host syncing, or title bar system buttons in app code
- do not double-scale font sizes with `Graphics.Scale(...)`

## Reference example in this repository

Use these files as the current reference integration:

- `SK.ImguiForms.Example\Program.cs`
- `SK.ImguiForms.Example\ExampleForm.cs`
- `SK.ImguiForms.Example\NotepadOverlayWindow.cs`

They show the intended split between base library responsibilities and app-specific behavior.
