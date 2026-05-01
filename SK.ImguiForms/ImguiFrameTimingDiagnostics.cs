namespace SK.ImguiForms {
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Linq;

    public readonly record struct ImguiFrameTimingDiagnostics(
        string Name,
        double FramesPerSecond,
        double AverageIntervalMilliseconds,
        double MaximumIntervalMilliseconds,
        double AverageRenderMilliseconds,
        double MaximumRenderMilliseconds,
        double AverageInputMilliseconds,
        double AverageImguiMilliseconds,
        double AverageD3DMilliseconds,
        double AveragePresentMilliseconds,
        bool VSync,
        int FPSLimit);

    public static class ImguiFrameTimingDiagnosticsRegistry {
        static readonly ConcurrentDictionary<string, ImguiFrameTimingDiagnostics> items = new();

        public static IReadOnlyCollection<ImguiFrameTimingDiagnostics> Items => items.Values.ToArray();

        internal static void Set(ImguiFrameTimingDiagnostics diagnostics) {
            if(string.IsNullOrWhiteSpace(diagnostics.Name)) {
                return;
            }

            items[diagnostics.Name] = diagnostics;
        }
    }
}
