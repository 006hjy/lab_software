using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace alicat_mfc
{
    /// <summary>
    /// Thin wrapper around the Windows INI file APIs, used to persist
    /// application settings in an "alicat_mfc.ini" file located in the
    /// same directory as the executable.
    /// </summary>
    public static class IniConfig
    {
        private const int BufferSize = 4096;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetPrivateProfileString(
            string lpAppName,
            string lpKeyName,
            string lpDefault,
            StringBuilder lpReturnedString,
            int nSize,
            string lpFileName);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool WritePrivateProfileString(
            string lpAppName,
            string lpKeyName,
            string lpString,
            string lpFileName);

        /// <summary>Full path to the INI file, next to the executable.</summary>
        public static string FilePath =>
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "alicat_mfc.ini");

        /// <summary>
        /// Read a string value from the INI file. Returns <paramref name="defaultValue"/>
        /// if the section, key, or file is missing.
        /// </summary>
        public static string Read(string section, string key, string defaultValue = "")
        {
            var sb = new StringBuilder(BufferSize);
            int n = GetPrivateProfileString(section, key, defaultValue, sb, sb.Capacity, FilePath);
            return n > 0 ? sb.ToString() : defaultValue;
        }

        /// <summary>Write a string value to the INI file.</summary>
        public static void Write(string section, string key, string value)
        {
            WritePrivateProfileString(section, key, value ?? "", FilePath);
        }
    }
}
