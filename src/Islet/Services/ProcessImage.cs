using System.Runtime.InteropServices;
using System.Text;

namespace Islet
{
    /// <summary>
    /// The exe a running process was started from, cheaply. Process.MainModule lists every module
    /// loaded in the other process to find it, which is slow for browsers and call apps and fails
    /// for elevated ones; this asks Windows for the path directly, and works for elevated ones too.
    /// </summary>
    public static class ProcessImage
    {
        const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        /// <summary>The full path of the process's exe, or null if it ended or can't be looked at.</summary>
        public static string? PathOf(int pid)
        {
            var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);

            if (handle == IntPtr.Zero)
                return null;

            try
            {
                var path = new StringBuilder(1024);
                int length = path.Capacity;

                return QueryFullProcessImageName(handle, 0, path, ref length) ? path.ToString(0, length) : null;
            }
            finally
            {
                CloseHandle(handle);
            }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr OpenProcess(uint access, bool inheritHandle, int pid);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder path, ref int size);

        [DllImport("kernel32.dll")]
        static extern bool CloseHandle(IntPtr handle);
    }
}
