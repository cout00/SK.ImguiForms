namespace SK.ImguiForms {
    using System;
    using System.Threading.Tasks;

    public static class ImguiApplication {
        public static async Task Start(ImguiWindow window) {
            ArgumentNullException.ThrowIfNull(window);
            using(window) {
                await window.Run().ConfigureAwait(false);
            }
        }
    }
}
