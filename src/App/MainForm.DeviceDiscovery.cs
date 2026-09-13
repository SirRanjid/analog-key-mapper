using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Tk75.Diagnostics;

namespace Tk75.App
{
    public sealed partial class MainForm
    {
        readonly Timer discoveryTimer = new Timer { Interval = 500 };
        readonly KeyboardAutoConnect autoConnect = new KeyboardAutoConnect();
        DeviceDiscoveryMetadata[] lastInventory;
        bool discoveryEnabled, discoveryRunning, discoveryPending, discoveryFailed, deviceDetachInProgress, closeAfterDeviceDetach;
        System.Threading.Tasks.Task deviceDetachCleanup;
        Exception deviceDetachCleanupFailure;
        int discoveryGeneration;
        IntPtr deviceNotification;
        System.Threading.SynchronizationContext discoveryContext;
        WindowsFormsSynchronizationContext ownedDiscoveryContext;
        string discoveryStatusDe = "Tastaturen werden gesucht …", discoveryStatusEn = "Looking for keyboards …";
        // DEV_BROADCAST_DEVICEINTERFACE_W includes WCHAR dbcc_name[1], then
        // four-byte structure padding: 32 bytes even for an empty filter name.
        // https://learn.microsoft.com/en-us/windows/win32/api/dbt/ns-dbt-dev_broadcast_deviceinterface_w
        [StructLayout(LayoutKind.Sequential, Pack = 4)] struct DeviceInterfaceFilter { public int Size, Type, Reserved; public Guid ClassGuid; public ushort NameFirst; }
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr RegisterDeviceNotification(IntPtr recipient, ref DeviceInterfaceFilter filter, uint flags);
        [DllImport("user32.dll", SetLastError = true)] static extern bool UnregisterDeviceNotification(IntPtr handle);

        void StartDeviceDiscovery()
        {
            discoveryEnabled = true;
            discoveryContext = System.Threading.SynchronizationContext.Current;
            if (discoveryContext == null) { ownedDiscoveryContext = new WindowsFormsSynchronizationContext(); discoveryContext = ownedDiscoveryContext; }
            discoveryTimer.Tick += delegate { discoveryTimer.Stop(); BeginDeviceScan(); };
            devices.SelectionChangeCommitted += delegate { if (!deviceDetachInProgress) { SetDiscoverySelectionStatus(lastInventory ?? new DeviceDiscoveryMetadata[0]); TryAutoConnectKeyboard(); } };
            HandleCreated += delegate { if (discoveryEnabled) RegisterDeviceChanges(); };
            HandleDestroyed += delegate { UnregisterDeviceChanges(); };
            RegisterDeviceChanges(); Scan();
        }
        void RegisterDeviceChanges()
        {
            if (deviceNotification != IntPtr.Zero || !IsHandleCreated) return;
            var filter = new DeviceInterfaceFilter { Size = Marshal.SizeOf(typeof(DeviceInterfaceFilter)), Type = 5,
                ClassGuid = new Guid("4D1E55B2-F16F-11CF-88CB-001111000030") };
            deviceNotification = RegisterDeviceNotification(Handle, ref filter, 0);
            if (deviceNotification == IntPtr.Zero) store.Event("HID change registration failed: " + Marshal.GetLastWin32Error());
        }
        void UnregisterDeviceChanges()
        { if (deviceNotification != IntPtr.Zero) { UnregisterDeviceNotification(deviceNotification); deviceNotification = IntPtr.Zero; } }
        void StopDeviceDiscovery()
        { discoveryEnabled = false; discoveryGeneration++; discoveryTimer.Stop(); UnregisterDeviceChanges(); }
        void DisposeDeviceDiscovery()
        { StopDeviceDiscovery(); discoveryTimer.Dispose(); if (ownedDiscoveryContext != null) ownedDiscoveryContext.Dispose(); }

        void OnDeviceChange(Message message)
        {
            if (!discoveryEnabled || closing) return;
            int kind = message.WParam.ToInt32();
            if (kind != 0x8000 && kind != 0x8004 && kind != 7) return;
            // The notification's exact collection path can disarm immediately;
            // inventory fallback also handles ordinary DEVNODES_CHANGED events.
            if (kind == 0x8004 && message.LParam != IntPtr.Zero)
            {
                int size = Marshal.ReadInt32(message.LParam), type = Marshal.ReadInt32(message.LParam, 4);
                if (type == 5 && size >= 30 && size <= 65536 && (size & 1) == 0)
                {
                    string path = Marshal.PtrToStringUni(IntPtr.Add(message.LParam, 28), (size - 28) / 2).TrimEnd('\0');
                    autoConnect.ObserveRemoval(path);
                    var active = reader;
                    if (active != null && string.Equals(active.Device.devicePath, path, StringComparison.OrdinalIgnoreCase)) DetachRemovedReader(active);
                }
            }
            discoveryGeneration++; discoveryPending = true; discoveryTimer.Stop(); discoveryTimer.Start();
        }
        void Scan()
        {
            if (!discoveryEnabled || closing) return;
            discoveryGeneration++; discoveryPending = true; discoveryTimer.Stop(); BeginDeviceScan();
        }
        void BeginDeviceScan()
        {
            if (!discoveryEnabled || closing || !discoveryPending || discoveryRunning) return;
            discoveryPending = false; discoveryRunning = true; int generation = discoveryGeneration;
            System.Threading.ThreadPool.QueueUserWorkItem(delegate {
                CollectionInfo[] inventory = null; DeviceDiscoveryMetadata[] metadata = null; Exception error = null;
                try { inventory = HidInventory.Enumerate().ToArray(); metadata = inventory.Select(DiscoveryMetadata).ToArray(); }
                catch (Exception ex) { error = ex; }
                PostDiscovery(delegate {
                    discoveryRunning = false;
                    if (!discoveryEnabled || closing) return;
                    if (generation == discoveryGeneration)
                    {
                        if (error == null) ApplyDeviceInventory(inventory, metadata);
                        else { discoveryFailed = true; store.Event("Keyboard inventory failed: " + error.Message); SetDiscoveryStatus("Tastatursuche fehlgeschlagen · über das Menü erneut suchen", "Keyboard search failed · search again from the menu"); }
                    }
                    if (discoveryPending && !discoveryTimer.Enabled) discoveryTimer.Start();
                });
            });
        }
        void PostDiscovery(Action action)
        {
            // The UI synchronization context owns a separate marshaling handle;
            // MainForm handle recreation must not discard a scan completion.
            try { if (discoveryContext != null) discoveryContext.Post(delegate { if (!IsDisposed) action(); }, null); }
            catch (InvalidOperationException) { } // UI thread/context has ended
        }
        static DeviceDiscoveryMetadata DiscoveryMetadata(CollectionInfo value)
        {
            return new DeviceDiscoveryMetadata { Path = value.devicePath, Product = value.product, VendorId = value.vendorId, ProductId = value.productId,
                UsagePage = value.usagePage, Usage = value.usage, InputLength = value.inputReportLength, FeatureLength = value.featureReportLength,
                Failed = value.error != null, HasTravelDecoder = TravelProtocols.Find(value) != null,
                Signature = value.manufacturer + "\n" + value.serial + "\n" + value.error + "\n" + ProtocolFingerprint.Calculate(value, "inventory-v1") };
        }
        void ApplyDeviceInventory(CollectionInfo[] inventory, DeviceDiscoveryMetadata[] metadata)
        {
            var active = reader;
            if (active != null && !DeviceDiscoveryPolicy.ContainsPath(metadata, active.Device.devicePath)) DetachRemovedReader(active);
            bool recovered = discoveryFailed; discoveryFailed = false;
            if (lastInventory != null && DeviceDiscoveryPolicy.SameInventory(lastInventory, metadata))
            { if (recovered) SetDiscoverySelectionStatus(metadata); TryAutoConnectKeyboard(); return; }
            var previous = devices.SelectedItem as DeviceItem;
            string remembered = previous == null ? active == null ? null : active.Device.devicePath : previous.Device.devicePath;
            string selectedPath = DeviceDiscoveryPolicy.SelectPath(metadata, remembered);
            // A still-present manual selection survives a transient metadata error.
            var visible = inventory.Where(d => TravelProtocols.IsVendorInput(d) || string.Equals(d.devicePath, selectedPath, StringComparison.OrdinalIgnoreCase))
                .OrderBy(d => d.devicePath, StringComparer.OrdinalIgnoreCase).ToArray();
            bool wasUpdating = updating; updating = true; devices.BeginUpdate();
            try
            {
                devices.Items.Clear();
                var names = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var device in visible)
                {
                    string name = device.product ?? ""; int ordinal; names.TryGetValue(name, out ordinal); names[name] = ++ordinal;
                    devices.Items.Add(new DeviceItem(device, visible.Count(d => string.Equals(d.product ?? "", name, StringComparison.OrdinalIgnoreCase)) > 1 ? ordinal : 0));
                }
                devices.SelectedIndex = Array.FindIndex(visible, d => string.Equals(d.devicePath, selectedPath, StringComparison.OrdinalIgnoreCase));
            }
            finally { devices.EndUpdate(); updating = wasUpdating; }
            lastInventory = metadata; SetDiscoverySelectionStatus(metadata);
            TryAutoConnectKeyboard();
        }
        void TryAutoConnectKeyboard()
        {
            var selected = devices.SelectedItem as DeviceItem;
            string path = autoConnect.TryBegin(lastInventory ?? new DeviceDiscoveryMetadata[0], selected == null ? null : selected.Device.devicePath,
                !discoveryEnabled || closing || reader != null || deviceDetachInProgress);
            if (path == null) return;
            SetDiscoveryStatus("Tastatur wird automatisch verbunden …", "Connecting keyboard automatically …");
            try { Connect(); }
            catch (Exception ex)
            {
                store.Event("Automatic keyboard connection failed: " + ex.Message);
                SetDiscoveryStatus("Automatische Verbindung fehlgeschlagen · mit Verbinden erneut versuchen: " + UiText.Get(ex.Message),
                    "Automatic connection failed · use Connect to retry: " + UiText.Get(ex.Message));
            }
        }
        void SetDiscoverySelectionStatus(DeviceDiscoveryMetadata[] inventory)
        {
            if (deviceDetachInProgress) return;
            if (devices.SelectedItem != null) SetDiscoveryStatus("Tastatur ausgewählt · zum Lesen verbinden", "Keyboard selected · connect to read input");
            else if (inventory.Count(d => d.KnownInput) > 1 || inventory.Count(d => d.KnownConfiguration) > 1)
                SetDiscoveryStatus("Mehrere Tastaturen erkannt · oben auswählen", "Multiple keyboards detected · choose one above");
            else if (inventory.Any(d => d.KnownInput)) SetDiscoveryStatus("Tastatur erkannt · Anschluss noch nicht eindeutig, oben auswählen", "Keyboard detected · connection is not yet unambiguous, choose above");
            else SetDiscoveryStatus("Keine unterstützte Tastatur erkannt · USB-Anschluss prüfen", "No supported keyboard detected · check the USB connection");
        }
        void SetDiscoveryStatus(string german, string english)
        { discoveryStatusDe = german; discoveryStatusEn = english; if (reader == null) deviceStatus.Text = UiText.Get(german, english); }
        string DiscoveryStatus() { return UiText.Get(discoveryStatusDe, discoveryStatusEn); }

        void DetachRemovedReader(ReaderSession removed)
        {
            if (deviceDetachInProgress || !object.ReferenceEquals(reader, removed)) return;
            CancelMappingDrag();
            deviceDetachInProgress = true; reader = null;
            SetDiscoveryStatus("Tastatur entfernt · Verbindung wird beendet …", "Keyboard removed · closing connection …");
            outputStatus.Text = Tr("Controller wird ausgeschaltet …", "Turning controller off …");
            controllerPreview.SetFrame(null, false, false); keyboard.ClearKeyStates();
            // Runtime getters share the output lock. UpdateLive and UI actions are
            // suspended while the worker disarms and drains the detached reader.
            var interactive = Controls.Cast<Control>().Where(c => c.Enabled).ToArray();
            foreach (Control control in interactive) control.Enabled = false;
            var cleanup = new System.Threading.Tasks.TaskCompletionSource<object>();
            deviceDetachCleanupFailure = null; deviceDetachCleanup = cleanup.Task;
            System.Threading.ThreadPool.QueueUserWorkItem(delegate {
                try { runtime.SetReader(null); }
                catch (Exception ex) { deviceDetachCleanupFailure = ex; store.Event("Disconnect runtime: " + ex.Message); }
                finally {
                    try { RestoreRgbBeforeDisconnect(removed, RgbCloseTimeoutMilliseconds); } catch (Exception ex) { deviceDetachCleanupFailure = ex; store.Event("Disconnect lighting: " + ex.Message); }
                    try { removed.Dispose(); } catch (Exception ex) { deviceDetachCleanupFailure = ex; store.Event("Disconnect reader: " + ex.Message); }
                    finally { cleanup.TrySetResult(null); }
                }
                PostDiscovery(delegate {
                    deviceDetachInProgress = false;
                    if (closing || IsDisposed) return;
                    foreach (Control control in interactive) if (!control.IsDisposed) control.Enabled = true;
                    SetDiscoverySelectionStatus(lastInventory ?? new DeviceDiscoveryMetadata[0]);
                    RefreshKeys(); RefreshBindings(); UpdateLive();
                    bool requestedClose = closeAfterDeviceDetach; closeAfterDeviceDetach = false;
                    if (requestedClose) Close(); else TryAutoConnectKeyboard();
                });
            });
        }
    }
}
