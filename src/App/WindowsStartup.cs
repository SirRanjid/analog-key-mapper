using System;
using System.IO;
using Microsoft.Win32;

namespace Tk75.App
{
    internal interface IStartupRegistrationStore
    {
        string Read();
        void Write(string command);
        void Delete();
    }

    // Registration is changed only by an explicit click. Reading the menu or
    // starting the application never installs or repairs a startup entry.
    internal sealed class WindowsStartup
    {
        readonly IStartupRegistrationStore store;
        readonly string command;

        internal WindowsStartup(string executablePath, IStartupRegistrationStore store)
        { if (store == null) throw new ArgumentNullException("store"); command = BuildCommand(executablePath); this.store = store; }

        internal static WindowsStartup Current()
        { return new WindowsStartup(System.Windows.Forms.Application.ExecutablePath, new RegistryStartupRegistrationStore()); }

        internal static string BuildCommand(string executablePath)
        {
            if (String.IsNullOrWhiteSpace(executablePath) || !Path.IsPathRooted(executablePath) ||
                executablePath.IndexOfAny(new[] { '"', '\r', '\n' }) >= 0)
                throw new ArgumentException("The startup executable must have an absolute path.");
            return "\"" + Path.GetFullPath(executablePath) + "\" --background";
        }

        internal static bool IsBackgroundArgument(string[] args)
        { return args != null && args.Length == 1 && String.Equals(args[0], "--background", StringComparison.Ordinal); }

        internal bool IsRegistered
        { get { return String.Equals(store.Read(), command, StringComparison.OrdinalIgnoreCase); } }

        internal void SetRegistered(bool enabled)
        {
            if (enabled) store.Write(command);
            else if (IsRegistered) store.Delete(); // Do not remove another installation's entry.
            if (IsRegistered != enabled) throw new IOException(UiText.Get("Der Autostart konnte nicht aktualisiert werden.", "Windows startup could not be updated."));
        }

        sealed class RegistryStartupRegistrationStore : IStartupRegistrationStore
        {
            const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
            const string Name = "AnalogKeyMapper";
            public string Read()
            { using (RegistryKey key = Registry.CurrentUser.OpenSubKey(Key, false)) return key == null ? null : key.GetValue(Name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string; }
            public void Write(string command)
            { using (RegistryKey key = Registry.CurrentUser.CreateSubKey(Key)) key.SetValue(Name, command, RegistryValueKind.String); }
            public void Delete()
            { using (RegistryKey key = Registry.CurrentUser.OpenSubKey(Key, true)) { if (key != null) key.DeleteValue(Name, false); } }
        }
    }
}
