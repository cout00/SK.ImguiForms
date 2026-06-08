namespace SK.ImguiForms {
    using System;
    using SK.ImguiForms.Win32;

    /// <summary>
    /// Locates other transparent overlay windows that are attached to the same game window so a
    /// managed overlay can stack itself directly above them instead of fighting for the single
    /// "immediately above the game window" z-order slot (which is permanently held by an
    /// owner-attached overlay such as the native in-game overlay). See plan: clever-jumping-quilt.
    /// </summary>
    static class OverlayZOrderResolver {
        /// <summary>
        /// Finds the top-most sibling overlay window owned by <paramref name="gameWindow"/>.
        /// A sibling overlay is any visible window (other than <paramref name="selfWindow"/>) whose
        /// owner is the game window and that carries the WS_EX_LAYERED style (i.e. a transparent
        /// overlay). Any third-party overlay attached the same way is picked up automatically.
        /// </summary>
        public static bool TryFindTopmostSiblingOverlay(nint gameWindow, nint selfWindow, out nint anchor) {
            anchor = 0;
            if(gameWindow == 0 || !User32.IsWindow(gameWindow)) {
                return false;
            }

            nint found = 0;
            // EnumWindows enumerates top-level windows in z-order (top-most first), so the first
            // match we encounter is the highest sibling overlay.
            User32.EnumWindows((hWnd, _) => {
                if(hWnd == selfWindow || hWnd == gameWindow || !IsSiblingOverlay(hWnd, gameWindow)) {
                    return true;
                }

                found = hWnd;
                return false;
            }, IntPtr.Zero);

            anchor = found;
            return found != 0;
        }

        static bool IsSiblingOverlay(nint hWnd, nint gameWindow) {
            if(!User32.IsWindowVisible(hWnd) || User32.GetWindow(hWnd, GetWindowCommand.GW_OWNER) != gameWindow) {
                return false;
            }

            var exStyle = (WindowExStyles)User32.GetWindowLong(hWnd, (int)WindowLongParam.GWL_EXSTYLE);
            return exStyle.HasFlag(WindowExStyles.WS_EX_LAYERED);
        }
    }
}
