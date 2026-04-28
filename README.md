# SK.ImguiForms

`SK.ImguiForms` is a Windows-only .NET library for hosting ImGui UI in native Win32 windows and transparent overlays backed by Direct3D11.

The project provides two main app models:

- `ImguiForm` for a standalone native window with a custom ImGui-drawn title bar, resize and drag handling, and automatic window-state persistence.
- `ImguiOverlay` for a transparent overlay that follows a target native window and renders through an ImGui background draw list.

The library targets `net10.0-windows` and uses `ImGui.NET`, `Vortice.Direct3D11`, `Vortice.D3DCompiler`, and `SixLabors.ImageSharp`.

## What this repository contains

- `SK.ImguiForms/` - the reusable library code.
- `SK.ImguiForms.Example/` - a sample app that launches Notepad and attaches an overlay to it.
- `SK.ImguiForms/instructions.md` - the integration guide for AI agents and consumers of the library.

## How the library is intended to be used

Use `ImguiForm` when you need:

- a standalone desktop window
- built-in title bar buttons
- window dragging and resizing
- automatic persistence to `window_state.json`

Use `ImguiOverlay` when you need:

- a transparent overlay above another window
- automatic z-order syncing with a target window
- click-through behavior
- rendering through `ImDrawListPtr`

The base classes also manage:

- native window creation and rendering lifecycle
- DPI and interface scaling
- default font loading from `AppContext.BaseDirectory\Fonts`
- overlay targeting, bounds sync, and host-window tracking

## Example app

`SK.ImguiForms.Example` shows the recommended integration pattern:

1. Start the app with `ImguiApplication.Start(...)`.
2. Derive a form from `ImguiForm` for the main UI.
3. Derive an overlay from `ImguiOverlay` when you need to draw over another process.
4. Keep app-specific drawing and behavior inside `RenderFormContent()` and `RenderOverlay(...)`.

The sample form launches `notepad.exe`, attaches a transparent overlay, and demonstrates image, icon, line, rectangle, circle, triangle, quad, and text drawing APIs.

## For AI agents

If you are working on integration or extension tasks, read [`SK.ImguiForms/instructions.md`](SK.ImguiForms/instructions.md) first. That file is the authoritative guide for how to use this library.

If you add a separate `agents.md` for agent-specific workflow notes, add a reference to it from `SK.ImguiForms/instructions.md` so AI agents can discover it from the main integration guide.

## Build

```powershell
dotnet build SK.ImguiForms.sln
```

The example project references the library directly and copies its sample assets to the output directory.
