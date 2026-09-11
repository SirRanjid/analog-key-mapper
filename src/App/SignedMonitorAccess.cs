using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;

namespace Tk75.App
{
    // Read-only trust check. This class never executes or loads the subject file.
    // The caller chooses its fixed bundled helper path and must still use normal
    // Process.Start; Windows application control remains the final start authority.
    public static class SignedMonitorAccess
    {
        static readonly Guid GenericVerifyV2 = new Guid("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");
        const uint TrustNoSignature = 0x800B0100;
        const uint TrustBadDigest = 0x80096010;

        // ABI: Microsoft wintrust.h, including pSignatureSettings (Windows 8+).
        // https://learn.microsoft.com/windows/win32/api/wintrust/ns-wintrust-wintrust_file_info
        [StructLayout(LayoutKind.Sequential)]
        struct WinTrustFileInfo
        {
            public uint Size;
            public IntPtr FilePath;
            public IntPtr FileHandle;
            public IntPtr KnownSubject;
        }

        // https://learn.microsoft.com/windows/win32/api/wintrust/ns-wintrust-wintrust_data
        [StructLayout(LayoutKind.Sequential)]
        struct WinTrustData
        {
            public uint Size;
            public IntPtr PolicyCallbackData;
            public IntPtr SipClientData;
            public uint UiChoice;
            public uint RevocationChecks;
            public uint UnionChoice;
            public IntPtr FileInfo;
            public uint StateAction;
            public IntPtr StateData;
            public IntPtr UrlReference;
            public uint ProviderFlags;
            public uint UiContext;
            public IntPtr SignatureSettings;
        }

        // WinVerifyTrust returns a signed 32-bit LONG, not an HRESULT success range.
        // Only exactly zero is a successful trust decision.
        // https://learn.microsoft.com/windows/win32/api/wintrust/nf-wintrust-winverifytrust
        [DllImport("wintrust.dll", ExactSpelling = true, CallingConvention = CallingConvention.Winapi)]
        static extern int WinVerifyTrust(IntPtr window, ref Guid action, ref WinTrustData data);

        public static string AvailabilityMessage(string path)
        {
            try { RequireTrusted(path); return null; }
            catch (SecurityException ex) { return ex.Message; }
            catch (IOException) { return "Der signierte Tastatur-Zugriff fehlt oder kann nicht gelesen werden."; }
            catch (UnauthorizedAccessException) { return "Windows erlaubt keinen Lesezugriff auf den Tastatur-Helfer."; }
            catch (ArgumentException) { return "Der Pfad zum Tastatur-Helfer ist ungültig."; }
            catch (NotSupportedException) { return "Der Pfad zum Tastatur-Helfer wird nicht unterstützt."; }
            catch (DllNotFoundException) { return "Die Windows-Signaturprüfung ist auf diesem System nicht verfügbar."; }
            catch (EntryPointNotFoundException) { return "Die Windows-Signaturprüfung ist auf diesem System nicht verfügbar."; }
        }

        public static void RequireTrusted(string path)
        {
            using (AcquireTrustedFile(path)) { }
        }

        // Keep this read lease alive through the caller's normal Process.Start to
        // prevent replacement/write of the verified file between check and start.
        // Windows trust may retrieve certificate/revocation data; call off the UI thread.
        public static IDisposable AcquireTrustedFile(string path)
        {
            return AcquireFile(path, false);
        }

        // Explicit local-build option: allow only a missing signature. A present
        // but invalid or untrusted signature still fails, and Windows still decides
        // whether the executable may run through the normal process-start path.
        public static IDisposable AcquireFile(string path, bool allowUnsigned)
        {
            string fullPath = GetLocalExecutablePath(path);
            FileStream file = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            try
            {
                Verify(fullPath, file, allowUnsigned);
                return file;
            }
            catch { file.Dispose(); throw; }
        }

        static string GetLocalExecutablePath(string path)
        {
            if (String.IsNullOrWhiteSpace(path) || path.Length < 3 || !Char.IsLetter(path[0]) || path[1] != ':' ||
                (path[2] != '\\' && path[2] != '/') || path.IndexOf(':', 2) >= 0)
                throw new ArgumentException("Ein vollständiger lokaler Dateipfad ist erforderlich.", "path");
            string fullPath = Path.GetFullPath(path);
            if (!String.Equals(Path.GetExtension(fullPath), ".exe", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Der Tastatur-Helfer muss eine EXE-Datei sein.", "path");
            return fullPath;
        }

        static void Verify(string path, FileStream file, bool allowUnsigned)
        {
            IntPtr pathBuffer = IntPtr.Zero, fileBuffer = IntPtr.Zero;
            WinTrustData data = new WinTrustData();
            Guid action = GenericVerifyV2;
            bool verifyAttempted = false;
            try
            {
                pathBuffer = Marshal.StringToCoTaskMemUni(path);
                WinTrustFileInfo info = new WinTrustFileInfo();
                info.Size = (uint)Marshal.SizeOf(typeof(WinTrustFileInfo));
                info.FilePath = pathBuffer;
                // The FileStream stays alive for the entire native call and trust cleanup.
                info.FileHandle = file.SafeFileHandle.DangerousGetHandle();
                fileBuffer = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(WinTrustFileInfo)));
                Marshal.StructureToPtr(info, fileBuffer, false);
                data.Size = (uint)Marshal.SizeOf(typeof(WinTrustData));
                data.UiChoice = 2; // WTD_UI_NONE
                data.RevocationChecks = 0; // Specific revocation policy is set below.
                data.UnionChoice = 1; // WTD_CHOICE_FILE
                data.FileInfo = fileBuffer;
                data.StateAction = 1; // WTD_STATEACTION_VERIFY
                // Check the chain except its root; reject obsolete MD2/MD4 signatures.
                // No hash-only, usage bypass, revocation bypass, or custom trust store.
                data.ProviderFlags = 0x80 | 0x2000;
                verifyAttempted = true;
                int status = WinVerifyTrust(new IntPtr(-1), ref action, ref data);
                if (status != 0)
                {
                    uint code = unchecked((uint)status);
                    if (allowUnsigned && code == TrustNoSignature) return;
                    string reason = code == TrustNoSignature
                        ? "Der Tastatur-Helfer ist noch nicht digital signiert. Die automatische Druckwert-Erfassung kann deshalb nicht starten."
                        : code == TrustBadDigest
                            ? "Die Signatur des Tastatur-Helfers passt nicht zu seiner Datei. Die automatische Druckwert-Erfassung bleibt aus."
                            : "Windows konnte die Signatur des Tastatur-Helfers nicht als vertrauenswürdig bestätigen. Die automatische Druckwert-Erfassung bleibt aus.";
                    throw new SecurityException(reason + " (Prüfcode 0x" + code.ToString("X8") + ")");
                }
            }
            finally
            {
                try
                {
                    if (verifyAttempted)
                    {
                        // CLOSE is required after every VERIFY, including rejected files.
                        data.StateAction = 2;
                        WinVerifyTrust(new IntPtr(-1), ref action, ref data);
                    }
                }
                finally
                {
                    if (fileBuffer != IntPtr.Zero) Marshal.FreeHGlobal(fileBuffer);
                    if (pathBuffer != IntPtr.Zero) Marshal.FreeCoTaskMem(pathBuffer);
                    GC.KeepAlive(file);
                }
            }
        }
    }
}
