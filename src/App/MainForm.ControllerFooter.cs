using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using Tk75.Mapping;
using Tk75.Output;

namespace Tk75.App
{
    public sealed partial class MainForm
    {
        readonly FlowLayoutPanel controllerFooterSlots = new FlowLayoutPanel {
            Name = "controllerFooterSlots", Dock = DockStyle.Fill, AutoScroll = true,
            FlowDirection = FlowDirection.LeftToRight, WrapContents = false,
            Margin = Padding.Empty, Padding = Padding.Empty
        };
        readonly Dictionary<string, ControllerConnector> footerConnectors = new Dictionary<string, ControllerConnector>(StringComparer.Ordinal);
        readonly List<ControllerDefinition> footerDefinitions = new List<ControllerDefinition>();
        EditHistory footerHistory;
        object footerSnapshotToken;
        string footerLanguage;

        Control BuildControllerFooter()
        {
            var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = Padding.Empty, Padding = new Padding(8, 4, 0, 0) };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 38));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 62));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            footer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            var status = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = new Padding(0, 0, 12, 0) };
            status.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            status.RowStyles.Add(new RowStyle(SizeType.Percent, 50)); status.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            deviceStatus.Dock = outputStatus.Dock = DockStyle.Fill;
            deviceStatus.TextAlign = outputStatus.TextAlign = ContentAlignment.MiddleLeft;
            deviceStatus.Tag = outputStatus.Tag = "muted"; deviceStatus.AutoEllipsis = outputStatus.AutoEllipsis = true;
            status.Controls.Add(deviceStatus, 0, 0); status.Controls.Add(outputStatus, 0, 1);
            footer.Controls.Add(status, 0, 0); footer.Controls.Add(controllerFooterSlots, 1, 0);

            allOffButton = new SleekButton { Text = Tr("Alle aus", "All off"), Anchor = AnchorStyles.Right,
                AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(96, 32), Tag = "danger" };
            UiText.PreserveText(allOffButton);
            allOffButton.Click += delegate
            {
                try { Attempt(delegate { runtime.Disable("Manuell deaktiviert"); }); }
                finally { RefreshKeyboardSuppression(false); UpdateControllerConnectionUi(); }
            };
            footer.Controls.Add(allOffButton, 2, 0);
            RefreshControllerFooter();
            return footer;
        }

        void RefreshControllerFooter()
        {
            // Snapshot identity makes the steady-state timer path constant-time.
            // Mapping edits may change the snapshot without replacing any controls.
            object token = history.SnapshotToken;
            string language = UiText.Language;
            if (Object.ReferenceEquals(footerHistory, history) && Object.ReferenceEquals(footerSnapshotToken, token) && footerLanguage == language) return;
            Profile profile = UiReadProfile;
            List<ControllerDefinition> definitions = ControllerRouting.EffectiveControllers(profile);
            var ids = new HashSet<string>(definitions.Select(d => d.Id), StringComparer.Ordinal);
            controllerFooterSlots.SuspendLayout();
            try
            {
                foreach (string removed in footerConnectors.Keys.Where(id => !ids.Contains(id)).ToArray())
                {
                    ControllerConnector connector = footerConnectors[removed];
                    footerConnectors.Remove(removed); controllerFooterSlots.Controls.Remove(connector); connector.Dispose();
                }
                for (int i = 0; i < definitions.Count; i++)
                {
                    ControllerDefinition definition = definitions[i];
                    ControllerConnector connector;
                    if (!footerConnectors.TryGetValue(definition.Id, out connector))
                    {
                        string id = definition.Id;
                        connector = new ControllerConnector { Vertical = true, Compact = true, Width = 56, Height = 64,
                            Margin = new Padding(3, 0, 3, 0), Tag = id };
                        connector.ConnectionRequested += delegate(bool connect) { SetControllerConnectionForId(id, connect); };
                        footerConnectors.Add(id, connector); controllerFooterSlots.Controls.Add(connector);
                    }
                    connector.ControllerLabel = (i + 1).ToString(CultureInfo.InvariantCulture);
                    connector.ControllerName = definition.Name;
                    connector.AccentColor = RgbColor(RgbOverridePlan.GetControllerColor(profile, definition.Id));
                    if (controllerFooterSlots.Controls.GetChildIndex(connector) != i) controllerFooterSlots.Controls.SetChildIndex(connector, i);
                    if (footerLanguage != language) connector.RefreshLanguage();
                }
                footerDefinitions.Clear(); footerDefinitions.AddRange(definitions);
                footerHistory = history; footerSnapshotToken = token; footerLanguage = language;
            }
            finally { controllerFooterSlots.ResumeLayout(); }
        }

        void UpdateControllerFooterConnections(bool enabled, string selectedAvailability)
        {
            if (!enabled)
            {
                foreach (ControllerConnector connector in footerConnectors.Values) if (connector.Enabled) connector.Enabled = false;
                return;
            }
            RefreshControllerFooter();
            // Resolve each output kind at most once, rather than probing the
            // helper file separately for all 32 possible profile slots.
            string xboxError = selectedControllerKind == ControllerKind.Xbox360 ? selectedAvailability : null;
            string ps5Error = selectedControllerKind == ControllerKind.DualSense ? selectedAvailability : null;
            bool haveXbox = selectedControllerKind == ControllerKind.Xbox360, havePs5 = selectedControllerKind == ControllerKind.DualSense;
            foreach (ControllerDefinition definition in footerDefinitions)
            {
                string error;
                if (definition.Kind == ControllerKind.Xbox360)
                {
                    if (!haveXbox) { xboxError = UiText.Get(ControllerOutputs.AvailabilityError(definition.Kind)); haveXbox = true; }
                    error = xboxError;
                }
                else
                {
                    if (!havePs5) { ps5Error = UiText.Get(ControllerOutputs.AvailabilityError(definition.Kind)); havePs5 = true; }
                    error = ps5Error;
                }
                ControllerConnector connector = footerConnectors[definition.Id];
                if (!connector.Enabled) connector.Enabled = true;
                connector.SetConnection(runtime.IsControllerEnabled(definition.Id), error);
            }
        }
    }
}
