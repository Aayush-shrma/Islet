using System.Runtime.InteropServices;

namespace Islet
{
    /// <summary>
    /// Whether some program has asked Windows to keep the screen on: what video players, browsers
    /// playing a video and call apps do for as long as they're showing something. The screensaver
    /// stays away while that's so, as it would over a film.
    /// </summary>
    public static class DisplayRequest
    {
        const int SystemExecutionState = 16;
        const uint DisplayRequired = 0x2;

        /// <summary>True while any program is holding the display on. False when it can't be told.</summary>
        public static bool IsHeld()
        {
            try
            {
                return CallNtPowerInformation(SystemExecutionState, IntPtr.Zero, 0, out uint state, sizeof(uint)) == 0
                    && (state & DisplayRequired) != 0;
            }
            catch
            {
                return false;
            }
        }

        [DllImport("powrprof.dll")]
        static extern uint CallNtPowerInformation(int level, IntPtr input, int inputLength, out uint output, int outputLength);
    }
}
