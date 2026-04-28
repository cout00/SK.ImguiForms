using System.Diagnostics;
using System.Numerics;
using ImGuiNET;
using SKFramework;

namespace SK.ImguiForms.Example;

sealed class ExampleForm : ImguiForm {
    readonly string assetsDirectory;
    NotepadOverlayWindow? overlayWindow;
    Process? notepadProcess;
    string statusMessage = "Press the play button to start Notepad and attach the overlay.";
    bool isLaunchingNotepad;
    Task? overlayDisposalTask;

    public ExampleForm() : base("SK.ImguiForms Example", true, 900, 560) {
        assetsDirectory = Path.Combine(AppContext.BaseDirectory, "Assets");
        InterfaceSize = ImguiInterfaceSize.Medium;
        FPSLimit = 60;
    }

    protected override float RenderTitleBarLeadingContent(int reservedButtonsWidth) {
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("SK.ImguiForms example");
        return ImGui.GetItemRectMax().X;
    }

    protected override void OnBeforeRender() {
        base.OnBeforeRender();
        if(overlayWindow != null) {
            overlayWindow.InterfaceSize = InterfaceSize;
        }

        if(notepadProcess == null) {
            return;
        }

        try {
            if(notepadProcess.HasExited) {
                _ = DisposeOverlayAsync();
                notepadProcess.Dispose();
                notepadProcess = null;
                statusMessage = "Notepad exited. Launch it again to restore the overlay.";
            }
        }
        catch {
            _ = DisposeOverlayAsync();
            notepadProcess = null;
            statusMessage = "Notepad state is no longer available.";
        }
    }

    protected override void RenderFormContent() {
        ImGui.TextWrapped("Minimal sample for SK.ImguiForms. This window launches Notepad and starts a transparent overlay that follows it.");
        ImGui.Spacing();

        if(Graphics.IconButton("launch-notepad-icon", 0xf04b, IconSize.Medium, S(new Vector2(42f, 34f)), "Launch or attach Notepad")) {
            _ = LaunchOrAttachNotepadAsync();
        }

        ImGui.SameLine();
        if(ImGui.Button("Launch or attach Notepad", S(new Vector2(0f, 34f)))) {
            _ = LaunchOrAttachNotepadAsync();
        }

        ImGui.SameLine();
        if(Graphics.IconButton("focus-notepad-icon", 0xf08e, IconSize.Medium, S(new Vector2(42f, 34f)), "Bring Notepad to foreground")) {
            FocusNotepad();
        }

        ImGui.SameLine();
        if(Graphics.IconButton("stop-overlay-icon", 0xf00d, IconSize.Medium, S(new Vector2(42f, 34f)), "Stop overlay")) {
            _ = DisposeOverlayAsync();
            statusMessage = "Overlay stopped. Notepad can stay open.";
        }

        ImGui.SameLine();
        if(Graphics.IconButton("cycle-ui-scale-icon", 0xf065, IconSize.Medium, S(new Vector2(42f, 34f)), "Change UI scale")) {
            CycleInterfaceSize();
        }

        ImGui.SameLine();
        RenderInterfaceSizeCombo();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextColored(new Vector4(0.85f, 0.9f, 1f, 1f), "Form-side icon buttons");
        ImGui.SameLine();
        Graphics.DrawIcon(0xf06e, IconSize.Small, new Vector4(1f, 0.85f, 0.35f, 1f));
        ImGui.SameLine();
        ImGui.TextUnformatted("launch / focus / stop");

        ImGui.Spacing();
        ImGui.TextWrapped(statusMessage);

        if(notepadProcess != null) {
            ImGui.Text($"Process ID: {notepadProcess.Id}");
            ImGui.Text($"Window handle: 0x{(overlayWindow?.TargetWindowHandle ?? 0).ToInt64():X}");
        }

        ImGui.Spacing();
        ImGui.TextDisabled("Overlay content:");
        ImGui.BulletText("DrawImage with two sample textures");
        ImGui.BulletText("DrawIconGlyph with Font Awesome icons");
        ImGui.BulletText("DrawLine, DrawRect, DrawRectFilled");
        ImGui.BulletText("DrawCircle, DrawCircleFilled");
        ImGui.BulletText("DrawTriangle, DrawTriangleFilled");
        ImGui.BulletText("DrawQuad, DrawQuadFilled");
        ImGui.BulletText("DrawText and DrawTextInBox");
    }

    Vector2 S(Vector2 value) {
        return Graphics.Scale(value);
    }

    void CycleInterfaceSize() {
        InterfaceSize = InterfaceSize switch {
            ImguiInterfaceSize.Small => ImguiInterfaceSize.Medium,
            ImguiInterfaceSize.Medium => ImguiInterfaceSize.Large,
            _ => ImguiInterfaceSize.Small,
        };
    }

    void RenderInterfaceSizeCombo() {
        ImGui.SetNextItemWidth(S(new Vector2(130f, 0f)).X);
        if(!ImGui.BeginCombo("##interface-size", InterfaceSize.ToString())) {
            return;
        }

        foreach(var size in Enum.GetValues<ImguiInterfaceSize>()) {
            var selected = InterfaceSize == size;
            if(ImGui.Selectable(size.ToString(), selected)) {
                InterfaceSize = size;
            }

            if(selected) {
                ImGui.SetItemDefaultFocus();
            }
        }

        ImGui.EndCombo();
    }

    protected override void Dispose(bool disposing) {
        if(disposing) {
            DisposeOverlayAsync().GetAwaiter().GetResult();
            notepadProcess?.Dispose();
            notepadProcess = null;
        }

        base.Dispose(disposing);
    }

    async Task LaunchOrAttachNotepadAsync() {
        if(isLaunchingNotepad) {
            return;
        }

        isLaunchingNotepad = true;
        statusMessage = "Opening Notepad...";
        try {
            if(notepadProcess == null || notepadProcess.HasExited) {
                notepadProcess?.Dispose();
                notepadProcess = Process.Start(new ProcessStartInfo("notepad.exe") {
                    UseShellExecute = true
                }) ?? throw new InvalidOperationException("Failed to start notepad.exe.");
            }

            await EnsureOverlayAsync(notepadProcess).ConfigureAwait(false);
            overlayWindow?.ActivateTargetWindow();
            statusMessage = "Overlay started. It will attach to Notepad automatically as soon as the target window becomes available.";
        }
        catch(Exception ex) {
            statusMessage = ex.Message;
        }
        finally {
            isLaunchingNotepad = false;
        }
    }

    async Task EnsureOverlayAsync(Process process) {
        if(overlayWindow != null && overlayWindow.TargetProcessId == process.Id) {
            return;
        }

        await DisposeOverlayAsync().ConfigureAwait(false);

        var overlay = new NotepadOverlayWindow(process, assetsDirectory) {
            InterfaceSize = InterfaceSize
        };

        try {
            await overlay.Start().ConfigureAwait(false);
            overlayWindow = overlay;
        }
        catch {
            await overlay.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    void FocusNotepad() {
        if(overlayWindow == null) {
            statusMessage = "Notepad is not running yet.";
            return;
        }

        overlayWindow.ActivateTargetWindow();
        statusMessage = "Notepad brought to foreground.";
    }

    Task DisposeOverlayAsync() {
        var overlay = overlayWindow;
        overlayWindow = null;

        var previousDisposalTask = overlayDisposalTask;
        if(overlay == null) {
            return previousDisposalTask ?? Task.CompletedTask;
        }

        async Task DisposeAsync() {
            if(previousDisposalTask != null) {
                await previousDisposalTask.ConfigureAwait(false);
            }

            await overlay.DisposeAsync().ConfigureAwait(false);
        }

        var disposalTask = DisposeAsync();
        overlayDisposalTask = disposalTask;
        return ClearOverlayDisposalTaskWhenCompleteAsync(disposalTask);
    }

    async Task ClearOverlayDisposalTaskWhenCompleteAsync(Task disposalTask) {
        try {
            await disposalTask.ConfigureAwait(false);
        }
        finally {
            if(ReferenceEquals(overlayDisposalTask, disposalTask)) {
                overlayDisposalTask = null;
            }
        }
    }
}
