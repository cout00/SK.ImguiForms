namespace SK.ImguiForms {
    internal static class ImGuiContextSync {
        public static readonly object SyncRoot = new();
    }
}
