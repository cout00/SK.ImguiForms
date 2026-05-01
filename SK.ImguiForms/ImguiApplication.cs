namespace SK.ImguiForms {
    using System;
    using System.Threading.Tasks;

    public static class ImguiApplication {
        public static async Task Start(ImguiWindow window) {
            ArgumentNullException.ThrowIfNull(window);

            Exception startupFailure = null;
            try {
                await window.Run().ConfigureAwait(false);
            }
            catch(Exception ex) {
                startupFailure = ex;
            }

            Exception disposeFailure = null;
            try {
                window.Dispose();
            }
            catch(Exception ex) {
                disposeFailure = ex;
            }

            if(startupFailure != null) {
                if(disposeFailure == null) {
                    throw startupFailure;
                }

                throw new AggregateException(startupFailure, disposeFailure);
            }

            if(disposeFailure != null) {
                throw disposeFailure;
            }
        }
    }
}
