using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Tk75.Diagnostics;
using Tk75.Mapping;

namespace Tk75.App
{
    public sealed partial class MainForm
    {
        readonly Label keyTitle = new Label(), keyHint = new Label(), pressureText = new LiveValueLabel(), keyboardTitle = new Label();
        readonly Label mappingEmpty = new Label();
        readonly Label targetHeader = new Label { Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
        readonly LinkLabel inputDetails = new LinkLabel { Text = "Warum kommen keine Druckwerte?", AutoSize = true };
        readonly SleekComboBox layoutMode = new SleekComboBox();
        readonly QuietToolTip keyCardTips = new QuietToolTip();
        readonly ProgressBar pressure = new SleekProgressBar { Maximum = 1000, Style = ProgressBarStyle.Continuous };
        readonly Panel pageHost = new Panel { Dock = DockStyle.Fill };
        readonly Dictionary<string, Control> pages = new Dictionary<string, Control>();
        readonly TableLayoutPanel keyboardArea = new TableLayoutPanel();
        readonly Panel advancedPanel = new SleekCard();
        readonly Panel curveEditorScroll = new CurveEditorScrollPanel { Dock = DockStyle.Fill, AutoScroll = true, Margin = Padding.Empty };
        Button addTargetButton, removeTargetButton, toggleTargetButton, advancedToggle;
        bool advancedVisible;
        bool automaticIso = true, automaticLayoutAvailable = true, changingLayout;

        static Label Caption(string text, int height)
        { return new Label { Text = text, Dock = DockStyle.Fill, AutoEllipsis = true, Height = height, TextAlign = ContentAlignment.MiddleLeft }; }

        void BuildUi()
        {
            var shell = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, Padding = new Padding(24, 10, 24, 12) };
            shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 82)); shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 68 + SystemInformation.HorizontalScrollBarHeight));
            Controls.Add(shell);

            var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 5, RowCount = 2, Margin = Padding.Empty, Padding = new Padding(0, 1, 0, 16) };
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 142)); header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42)); header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58)); header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130)); header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 58));
            header.RowStyles.Add(new RowStyle(SizeType.Absolute, 20)); header.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var brandPanel = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
            var brand = Caption("analog", 40); brand.Font = new Font("Segoe UI", 23, FontStyle.Bold); brand.Dock = DockStyle.Top; brand.Height = 39;
            var brandCaption = Caption("Keyboard Mapper", 19); brandCaption.Tag = "muted"; brandCaption.Dock = DockStyle.Bottom;
            brandPanel.Controls.Add(brand); brandPanel.Controls.Add(brandCaption); header.Controls.Add(brandPanel, 0, 0); header.SetRowSpan(brandPanel, 2);
            var profileCaption = Caption("PROFIL", 20); profileCaption.Tag = "muted"; profileCaption.Margin = new Padding(10, 0, 0, 0); header.Controls.Add(profileCaption, 1, 0);
            var deviceCaption = Caption("TASTATUR", 20); deviceCaption.Tag = "muted"; deviceCaption.Margin = new Padding(8, 0, 0, 0); header.Controls.Add(deviceCaption, 2, 0);
            Combo(profiles, 240, null); profiles.Dock = DockStyle.Fill; profiles.Margin = new Padding(8, 4, 16, 0); header.Controls.Add(profiles, 1, 1);
            profiles.SelectedIndexChanged += delegate { if (!updating && profiles.SelectedItem != null) Attempt(delegate { SaveProfile(); LoadProfile(((ProfileItem)profiles.SelectedItem).Path); }); };
            Combo(devices, 300, null); devices.Dock = DockStyle.Fill; devices.Margin = new Padding(6, 4, 12, 0); header.Controls.Add(devices, 2, 1);
            var connect = new SleekButton { Text = "Verbinden", Dock = DockStyle.Fill, Tag = "primary" }; connect.Click += delegate { Attempt(Connect); }; header.Controls.Add(connect, 3, 1);
            var menuButton = new SleekButton { Text = "•••", Dock = DockStyle.Fill }; header.Controls.Add(menuButton, 4, 1);
            var menu = BuildMainMenu(); menuButton.Click += delegate { menu.Show(menuButton, new Point(menuButton.Width - menu.Width, menuButton.Height)); };
            shell.Controls.Add(header, 0, 0); shell.Controls.Add(pageHost, 0, 1);

            var mapping = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
            // Both work areas keep their golden-ratio shares in every context. Switching
            // editors must never resize the keyboard beneath a click or drag.
            mapping.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 61.8f)); mapping.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 38.2f)); mapping.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            pages.Add("mapping", mapping); pageHost.Controls.Add(mapping);
            keyboardArea.Dock = DockStyle.Fill; keyboardArea.ColumnCount = 1; keyboardArea.RowCount = 3; keyboardArea.Padding = new Padding(8, 8, 16, 8);
            keyboardArea.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            keyboardArea.RowStyles.Add(new RowStyle(SizeType.Absolute, 108)); keyboardArea.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); keyboardArea.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
            mapping.Controls.Add(keyboardArea, 0, 0);
            var introductionRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
            introductionRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); introductionRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 224));
            introductionRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var introduction = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
            keyboardTitle.Text = "Deine Tastatur"; keyboardTitle.Font = new Font("Segoe UI", 21, FontStyle.Bold); keyboardTitle.Dock = DockStyle.Top; keyboardTitle.Height = 40;
            introduction.Controls.Add(keyboardTitle); introduction.Controls.Add(BuildInputModeControl()); introductionRow.Controls.Add(introduction, 0, 0);
            var player = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = new Padding(12, 0, 0, 8) };
            player.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); player.RowStyles.Add(new RowStyle(SizeType.Absolute, 22)); player.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var playerCaption = Caption(Tr("Spieler / Controller", "Player / controller"), 22); playerCaption.Tag = "muted"; playerCaption.Margin = Padding.Empty; player.Controls.Add(playerCaption, 0, 0);
            Combo(mainControllerSlotPicker, 208, new object[0]); mainControllerSlotPicker.Dock = DockStyle.Fill; mainControllerSlotPicker.Margin = new Padding(0, 3, 0, 0);
            player.Controls.Add(mainControllerSlotPicker, 0, 1); introductionRow.Controls.Add(player, 1, 0); keyboardArea.Controls.Add(introductionRow, 0, 0);
            keyboard.Dock = DockStyle.Fill; keyboard.LayoutModel = KeyboardLayout.Tk75Iso(); keyboard.LegendStyle = KeyboardLegendStyle.Qwertz;
            keyboard.SelectionChanged += delegate { if (!updating) SelectKeyboardKeys(); }; keyboardArea.Controls.Add(keyboard, 0, 1);
            keyboard.KeyClicked += delegate(KeyboardKeyDefinition key) { if (!updating && activeMappingDrag == null && !TryCompleteSocdCapture(key)) SetDetailMode(null, true, false); };
            var legend = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty, Padding = new Padding(0, 4, 0, 0) };
            var mappingHint = new Label { Text = Tr("Drag & Drop: Taste ↔ Controller", "Drag & drop: key ↔ controller"), Width = 270, Height = 29, TextAlign = ContentAlignment.MiddleLeft, Tag = "muted" };
            keyCardTips.SetToolTip(mappingHint, Tr("Taste auf einen Controller-Button ziehen – oder den Controller-Button auf eine Taste. Strg + Klick wählt mehrere Tasten aus.", "Drag a key onto a controller button, or drag the controller button onto a key. Ctrl + click selects multiple keys."));
            mappingHint.MouseEnter += delegate { keyCardTips.SetToolTip(mappingHint, Tr("Taste auf einen Controller-Button ziehen – oder den Controller-Button auf eine Taste. Strg + Klick wählt mehrere Tasten aus.", "Drag a key onto a controller button, or drag the controller button onto a key. Ctrl + click selects multiple keys.")); };
            legend.Controls.Add(mappingHint);
            Combo(layoutMode, 185, new object[] { "ISO · voreingestellt", "Manuell · ANSI", "Manuell · ISO" }); layoutMode.SelectedIndex = 0;
            layoutMode.SelectedIndexChanged += delegate { if (!changingLayout) ChangeLayoutFromPicker(); }; legend.Controls.Add(layoutMode); keyboardArea.Controls.Add(legend, 0, 2);
            advancedPanel.Dock = DockStyle.Fill; advancedPanel.Padding = new Padding(12); advancedPanel.Visible = false; BuildAdvancedEditor(); BuildDetailAreas();
            var sidebar = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = Padding.Empty, Padding = new Padding(8, 8, 8, 8) };
            sidebar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); sidebar.RowStyles.Add(new RowStyle(SizeType.Absolute, 44)); sidebar.RowStyles.Add(new RowStyle(SizeType.Absolute, 30)); sidebar.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var detailHeading = new Panel { Dock = DockStyle.Fill, Margin = new Padding(6, 0, 6, 0) }; detailHeading.Controls.Add(detailsTitle); detailHeading.Controls.Add(controllerHeading);
            sidebar.Controls.Add(DetailButtonBar(), 0, 0); sidebar.Controls.Add(detailHeading, 0, 1); sidebar.Controls.Add(detailsHost, 0, 2); mapping.Controls.Add(sidebar, 1, 0);

            keys.Columns.Add("index", "Index"); keys.Columns.Add("label", "Taste"); keys.Columns.Add("raw", "Rohwert"); keys.Columns.Add("mapping", "Ziele");
            foreach (DataGridViewColumn column in keys.Columns) column.SortMode = DataGridViewColumnSortMode.NotSortable;
            keys.SelectionChanged += delegate { if (!updating) { RefreshBindings(); HighlightKeys(); } };
            var rawPage = SecondaryPage("keys", "Erkannte Tasten"); rawPage.Controls.Add(keys);
            var rawBar = Bar(); Add(rawBar, "Taste anders benennen / anlernen", LearnKey); rawBar.Controls.Add(showAll); showAll.CheckedChanged += delegate { if (!updating) RefreshKeys(); }; rawPage.Controls.Add(rawBar);

            var context = new ContextMenuStrip(); context.Items.Add("Einstellungen kopieren", null, delegate { Attempt(CopySettings); }); context.Items.Add("Einstellungen einfügen (gewählte Gruppe)", null, delegate { Attempt(PasteSettings); }); StyleMenu(context); keys.ContextMenuStrip = context; keyboard.ContextMenuStrip = context;
            var monitorPage = SecondaryPage("monitor", Tr("Live-Monitor · berechnete Vorschau", "Live monitor · calculated preview")); monitorPage.Controls.Add(monitor);
            foreach (string column in new[] { "Taste / Ziel", "Rohwert", "Hub mm¹", "Normalisiert", "Nach Deadzone", "Nach Kurve", "Final", "Status" }) monitor.Columns.Add(column, column);
            liveStatus.Dock = DockStyle.Bottom; liveStatus.Height = 64; monitorPage.Controls.Add(liveStatus);
            BuildBehavior(SecondaryPage("behavior", "Profil & Programmregeln"));
            var helpPage = SecondaryPage("help", "Verbindung & Entwicklungsstand");
            helpPage.Controls.Add(new TextBox { Dock = DockStyle.Fill, ReadOnly = true, Tag = "localized", Multiline = true, ScrollBars = ScrollBars.Vertical, BackColor = ModernTheme.Surface, Text = Tr(
                "Tastatur\r\n\r\nEine eindeutig erkannte Tastatur verbindet sich automatisch. Alle Tasten starten mit dem Druckbereich 0–385. Der Min/Max-Regler gilt für die ausgewählten Tasten; die Skala ist für die Tastatur gemeinsam. Zum Kalibrieren eine ausgewählte Taste einmal ganz drücken und loslassen. Die Füllung im Tastenhintergrund steigt mit dem Druck von unten nach oben.\r\n\r\n" +
                "Zuordnen und einstellen\r\n\r\nWähle eine Taste und füge rechts ein Ziel hinzu. Unter Controller wählst du den Spieler. Tasten und Controller-Ziele lassen sich in beide Richtungen ziehen. Rechts wechseln die Ansichten Tasten, Kurve und Controller. Die Tastatur bleibt dabei gleich groß. Unter Kurve stellst du Auslösen und Rapid Trigger mit senkrechten Reglern ein. Kalibrieren übernimmt beim Loslassen den gemessenen Wert. Die Kurve zeigt die Antwort und gewählte Einstellungen. Wähle einen Modus zum Bearbeiten, um Kurvenpunkte oder Bereichsgrenzen direkt zu ziehen. Glatt (Bézier) verbindet sie weich: Wähle einen Punkt und ziehe seine Griffe. Rechtsklick auf einen Griff stellt die automatische Rundung wieder her; Rechtsklick auf einen inneren Punkt löscht ihn. Strg+Z macht Änderungen rückgängig.\r\n\r\n" +
                "Mehrere Controller\r\n\r\nJeder Controller hat eigene Zuordnungen und einen eigenen USB-Anschluss. Klicke auf Stecker oder Buchse oder ziehe den Stecker hinein. Die kleinen Anschlüsse in der Fußleiste bedienen jeden Controller direkt; dort ziehst du vertikal. „Alle aus“ trennt alle Controller; eine zusätzliche Tastenkombination ist optional. Bis zu 32 Controllerplätze können Xbox- und DualSense-Ausgaben mischen. Windows unterstützt insgesamt höchstens vier XInput-Controller, einschließlich physischer Geräte. DualSense belegt keine XInput-Plätze. Spiele können weniger Controller unterstützen. Eigene Namen gelten innerhalb dieser App.\r\n\r\n" +
                "Aktueller Stand\r\n\r\nDie Druckeingabe wurde an der angeschlossenen TK75 geprüft. Xbox- und DualSense-Ausgabe unterstützen die üblichen Tasten, Sticks und Trigger. Die Erkennung in einem Spiel hängt von dessen Controller-Unterstützung ab. Der Anschluss zeigt daher erst dann Verbunden, wenn der Ausgabeweg die Verbindung bestätigt. Verbindungsfehler stehen unten. Profile und eigene Kalibrierungen werden getrennt im Unterordner data gespeichert.",
                "Keyboard\r\n\r\nAn unambiguously recognized keyboard connects automatically. All keys start with a pressure range of 0–385. The min/max slider applies to the selected keys; the scale maximum is shared by the keyboard. To calibrate, press a selected key fully once, then release. The background of each key fills from bottom to top as you press.\r\n\r\n" +
                "Mapping and settings\r\n\r\nChoose a key and add a target on the right. Choose the player under Controller. Drag keys and controller targets in either direction. The right-hand panel switches between Keys, Curve and Controller while the keyboard keeps its size. Under Curve, vertical sliders configure actuation and Rapid Trigger. Calibrate records a setting and applies it when the key is released. The graph shows the response and selected settings. Choose an editing mode to drag curve points or range limits directly. Smooth (Bézier) joins them smoothly: select a point and drag its handles. Right-click a handle to restore automatic smoothing; right-click an interior point to delete it. Ctrl+Z undoes changes.\r\n\r\n" +
                "Multiple controllers\r\n\r\nEach controller has its own mappings and USB connection. Click the plug or socket, or slide the plug in. The small footer connectors address each controller directly; drag vertically there. “All off” disconnects every controller; an additional shortcut is optional. Up to 32 controller slots can mix Xbox and DualSense output. Windows supports at most four XInput controllers in total, including physical devices. DualSense does not use XInput slots. Games may support fewer controllers. Custom names apply within this app.\r\n\r\n" +
                "Current status\r\n\r\nPressure input has been checked on the connected TK75. Xbox and DualSense output support common buttons, sticks and triggers. Recognition in a game depends on its controller support. The connector shows Connected only after the output backend confirms a connection. Connection errors appear at the bottom. Profiles and custom calibrations are stored separately in the data subfolder.") });

            shell.Controls.Add(BuildControllerFooter(), 0, 2);
        }

        ContextMenuStrip BuildMainMenu()
        {
            var menu = new ContextMenuStrip();
            var profileMenu = new ToolStripMenuItem("Profil"); menu.Items.Add(profileMenu);
            profileMenu.DropDownItems.Add("Speichern  ·  Strg+S", null, delegate { Attempt(SaveProfile); });
            profileMenu.DropDownItems.Add("Neu", null, delegate { Attempt(delegate { NewProfile(false); }); }); profileMenu.DropDownItems.Add("Duplizieren", null, delegate { Attempt(delegate { NewProfile(true); }); });
            profileMenu.DropDownItems.Add("Umbenennen", null, delegate { Attempt(RenameProfile); }); profileMenu.DropDownItems.Add("Löschen", null, delegate { Attempt(DeleteProfile); });
            profileMenu.DropDownItems.Add(new ToolStripSeparator()); profileMenu.DropDownItems.Add("Importieren", null, delegate { Attempt(ImportProfile); }); profileMenu.DropDownItems.Add("Exportieren", null, delegate { Attempt(ExportProfile); });
            menu.Items.Add("Rückgängig  ·  Strg+Z", null, delegate { Attempt(Undo); }); menu.Items.Add("Wiederholen  ·  Strg+Y", null, delegate { Attempt(Redo); });
            menu.Items.Add(new ToolStripSeparator()); menu.Items.Add("Geräte neu suchen", null, delegate { Attempt(Scan); });
            var layoutMenu = new ToolStripMenuItem("Tastaturabbildung"); menu.Items.Add(layoutMenu);
            layoutMenu.DropDownItems.Add("Automatisch erkennen", null, delegate { layoutMode.SelectedIndex = 0; });
            layoutMenu.DropDownItems.Add("TK75 · ANSI", null, delegate { layoutMode.SelectedIndex = 1; });
            layoutMenu.DropDownItems.Add("TK75 · ISO", null, delegate { layoutMode.SelectedIndex = 2; });
            layoutMenu.DropDownItems.Add(new ToolStripSeparator()); layoutMenu.DropDownItems.Add(Tr("Beschriftung automatisch (ANSI: US, ISO: Deutsch)", "Automatic legends (ANSI: US, ISO: German)"), null, delegate { SetLegendOverride(null); });
            layoutMenu.DropDownItems.Add(Tr("Beschriftung Deutsch · QWERTZ", "German legends · QWERTZ"), null, delegate { SetLegendOverride(1); });
            layoutMenu.DropDownItems.Add(Tr("Beschriftung US · QWERTY", "US legends · QWERTY"), null, delegate { SetLegendOverride(0); });
            menu.Items.Add("Live-Monitor", null, delegate { ShowPage("monitor"); }); menu.Items.Add("Erkannte Tasten / Anlernen", null, delegate { ShowPage("keys"); });
            menu.Items.Add(Tr("Tastatur-Eingaben abfangen…", "Suppress keyboard input…"), null, delegate { Attempt(ShowKeyboardSuppression); });
            menu.Items.Add(Tr("Controller-Eingaben & Tastenkombinationen…", "Controller input & shortcuts…"), null, delegate { Attempt(ShowModeShortcut); });
            menu.Items.Add("Profil & Programmregeln", null, delegate { ShowPage("behavior"); }); menu.Items.Add("Verbindung & Entwicklungsstand", null, delegate { ShowPage("help"); }); AddStartupMenu(menu); menu.Items.Add(new ToolStripSeparator()); AddLanguageMenu(menu); StyleMenu(menu); return menu;
        }
        void StyleMenu(ContextMenuStrip menu)
        { ModernTheme.Apply(menu); UiText.Apply(menu); languageMenus.Add(menu); Disposed += delegate { menu.Dispose(); }; }

        Control SecondaryPage(string id, string title)
        {
            var outer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Visible = false, Padding = new Padding(8) };
            outer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); outer.RowStyles.Add(new RowStyle(SizeType.Absolute, 52)); outer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var heading = Bar(); heading.Dock = DockStyle.Fill; heading.AutoSize = false; Add(heading, "← Tastatur", delegate { ShowPage("mapping"); }); heading.Controls.Add(new Label { Text = title, AutoSize = true, Padding = new Padding(8, 10, 0, 0) }); outer.Controls.Add(heading, 0, 0);
            var content = new Panel { Dock = DockStyle.Fill }; outer.Controls.Add(content, 0, 1); pages.Add(id, outer); pageHost.Controls.Add(outer); return content;
        }
        void ShowPage(string name)
        { foreach (var page in pages) page.Value.Visible = page.Key == name; pages[name].BringToFront(); }

        Control BuildKeyCard()
        {
            UiText.PreserveText(keyTitle);
            var surface = new SleekCard { Dock = DockStyle.Fill, Padding = new Padding(12, 6, 12, 6), Margin = Padding.Empty };
            var card = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, RowCount = 13, Padding = Padding.Empty, Margin = Padding.Empty };
            surface.Controls.Add(keyCardScroll); keyCardScroll.Controls.Add(card);
            card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            // The frequently adjusted controls remain in the first viewport. Lower
            // sections scroll within this fixed card, never moving the keyboard.
            foreach (int height in new[] { 38, 28, 24, 0, 80, 0, 24, 34, 40, 140, 40, 20, 138 }) card.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            card.Controls.Add(keySocdPanel, 0, 12);
            keyTitle.Text = "Taste auswählen"; keyTitle.Font = new Font("Segoe UI", 20, FontStyle.Bold); keyTitle.AutoSize = false; keyTitle.AutoEllipsis = true; keyTitle.Dock = DockStyle.Fill; card.Controls.Add(keyTitle, 0, 0);
            keyHint.AutoSize = false; keyHint.AutoEllipsis = true; keyHint.UseMnemonic = false; keyHint.TextAlign = ContentAlignment.TopLeft; keyHint.Margin = new Padding(3, 0, 3, 3); keyHint.Dock = DockStyle.Fill; keyHint.Tag = "muted"; keyHint.Text = "Klicke auf eine Taste in der Abbildung."; card.Controls.Add(keyHint, 0, 1);
            EventHandler resizeHint = delegate { FitKeyHint(card); };
            keyHint.TextChanged += resizeHint; keyHint.FontChanged += resizeHint; keyHint.SizeChanged += resizeHint;
            keyCardTips.SetToolTip(keyHint, keyHint.Text); Disposed += delegate { keyCardTips.Dispose(); };
            pressureText.AutoSize = false; pressureText.AutoEllipsis = true; pressureText.UseMnemonic = false; pressureText.Dock = DockStyle.Fill; pressureText.Text = "Druck: —"; pressureText.Tag = "muted"; pressureText.TextAlign = ContentAlignment.MiddleLeft; pressureText.TextChanged += delegate { keyCardTips.SetToolTip(pressureText, pressureText.Text); }; card.Controls.Add(pressureText, 0, 2);
            // The range slider's live marker replaces the old separate bar.
            pressure.Visible = false; pressure.Dock = DockStyle.Fill; pressure.Margin = Padding.Empty; card.Controls.Add(pressure, 0, 3);
            card.Controls.Add(BuildPressureRangeEditor(), 0, 4);
            inputDetails.Click += delegate { if (reader != null) MessageBox.Show(this, UiText.Get(reader.Status), Tr("Druckwert-Verbindung", "Pressure input connection"), MessageBoxButtons.OK, MessageBoxIcon.Information); }; card.Controls.Add(inputDetails, 0, 5);
            UiText.PreserveText(targetHeader); targetHeader.Text = "Controller-Ziele"; targetHeader.Font = new Font("Segoe UI", 12, FontStyle.Bold); card.Controls.Add(targetHeader, 0, 6);
            Combo(targets, 260, Enum.GetValues(typeof(OutputTarget)).Cast<OutputTarget>().Select(t => (object)new TargetItem(t, delegate { return CurrentControllerStyle; })).ToArray()); targets.IgnoreClosedTextInput = true; targets.DrawMode = DrawMode.OwnerDrawFixed; targets.FlatStyle = FlatStyle.Flat; targets.DropDownWidth = 360; targets.SelectedIndex = 0; targets.Dock = DockStyle.Fill; targets.Margin = new Padding(3, 4, 3, 3); card.Controls.Add(targets, 0, 7);
            addTargetButton = new SleekButton { Text = "+ Ziel hinzufügen", Dock = DockStyle.Fill }; addTargetButton.Click += delegate { Attempt(AddBinding); }; card.Controls.Add(addTargetButton, 0, 8);
            var bindingsPanel = new Panel { Dock = DockStyle.Fill }; bindingsPanel.Controls.Add(bindings);
            bindings.Columns.Add("key", "Taste"); bindings.Columns.Add("target", "Controller-Ziel"); bindings.Columns.Add("enabled", "An"); bindings.Columns.Add("value", "Wert"); bindings.Columns[0].Visible = false; bindings.Columns[2].MinimumWidth = 40; bindings.Columns[2].FillWeight = 18; bindings.Columns[3].MinimumWidth = 54; bindings.Columns[3].FillWeight = 24;
            InitializeMappingSummaryGrid();
            bindings.SelectionChanged += delegate { if (!updating) { RefreshSettings(); UpdateKeyCard(); } };
            bindings.CellDoubleClick += delegate(object sender, DataGridViewCellEventArgs e) { if (e.RowIndex >= 0) Attempt(delegate { SetDetailMode("advanced", true); }); };
            mappingEmpty.Text = "Noch keine Zuordnung\nWähle oben dein erstes Ziel."; mappingEmpty.TextAlign = ContentAlignment.MiddleCenter; mappingEmpty.Tag = "muted"; mappingEmpty.Dock = DockStyle.Fill; mappingEmpty.BackColor = ModernTheme.Surface; bindingsPanel.Controls.Add(mappingEmpty); card.Controls.Add(bindingsPanel, 0, 9);
            var actions = Bar(); actions.Dock = DockStyle.Fill; actions.AutoSize = false; actions.Padding = Padding.Empty; actions.Margin = Padding.Empty;
            removeTargetButton = Add(actions, "Entfernen", RemoveBinding); toggleTargetButton = Add(actions, "Ein / aus", ToggleBindings);
            keyBehaviorToggle = Add(actions, Tr("Gegentasten", "Opposite keys"), delegate { SetDetailMode("input", true); ScrollKeySettingsTo(keySocdPanel); }); card.Controls.Add(actions, 0, 10);
            actions.WrapContents = false;
            actions.Layout += delegate {
                foreach (Control action in actions.Controls)
                {
                    action.AutoSize = false;
                    action.Width = Math.Max(1, actions.ClientSize.Width / 3 - action.Margin.Horizontal);
                    action.Height = Math.Max(action.MinimumSize.Height, actions.ClientSize.Height - action.Margin.Vertical);
                }
            };
            selectionStatus.Dock = DockStyle.Fill; selectionStatus.Tag = "muted"; selectionStatus.TextAlign = ContentAlignment.MiddleLeft; selectionStatus.AutoEllipsis = true; UiText.PreserveText(selectionStatus); card.Controls.Add(selectionStatus, 0, 11); return surface;
        }

        void FitKeyHint(TableLayoutPanel card)
        {
            if (keyHint.IsDisposed || card.IsDisposed || keyHint.ClientSize.Width <= 0) return;
            int width = Math.Max(1, keyHint.ClientSize.Width - keyHint.Padding.Horizontal);
            Size measured = TextRenderer.MeasureText(keyHint.Text, keyHint.Font, new Size(width, Int32.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
            int height = Math.Max(28, Math.Min(64, measured.Height + keyHint.Padding.Vertical + keyHint.Margin.Vertical + 4));
            int row = card.GetRow(keyHint);
            if (Math.Abs(card.RowStyles[row].Height - height) > 0.5f) card.RowStyles[row].Height = height;
            keyCardTips.SetToolTip(keyHint, keyHint.Text);
        }

        void BuildAdvancedEditor()
        {
            var editorArea = new Panel { Dock = DockStyle.Top, Margin = Padding.Empty };
            advancedPanel.Controls.Add(curveEditorScroll); curveEditorScroll.Controls.Add(editorArea);
            Control tools = BuildCurveTools(); tools.Dock = DockStyle.None; editorArea.Controls.Add(tools);
            var responseArea = new Panel { Margin = Padding.Empty };
            editorArea.Controls.Add(responseArea);
            keyBehaviorPanel.Dock = DockStyle.None; responseArea.Controls.Add(keyBehaviorPanel);
            curve.Dock = DockStyle.None; curve.EditCompleted += ApplyCurve; responseArea.Controls.Add(curve);
            settings.MultiSelect = false; settings.SelectionMode = DataGridViewSelectionMode.CellSelect; settings.ReadOnly = false; settings.Columns.Add("property", "Parameter"); settings.Columns.Add("value", "Wert"); settings.Columns[0].ReadOnly = true;
            BuildCurveSettingsSliders();
            settings.CellEndEdit += delegate(object sender, DataGridViewCellEventArgs e) { if (!updating && e.ColumnIndex == 1) Attempt(delegate { EditProperty(e.RowIndex); }); };
            settings.Dock = DockStyle.None; editorArea.Controls.Add(settings);
            settings.CellFormatting += delegate(object sender, DataGridViewCellFormattingEventArgs e) { if (e.ColumnIndex == 1 && e.Value as string == "Gemischt") { e.Value = UiText.Get("Gemischt"); e.FormattingApplied = true; } };
            bool arranging = false, arrangeAgain = false;
            EventHandler arrange = delegate {
                if (arranging) { arrangeAgain = true; return; }
                arranging = true;
                try { do {
                arrangeAgain = false;
                const int toolsHeight = 78;
                // This borderless viewport's outer bounds remain stable while
                // Windows reserves or releases its native scrollbar space.
                var layout = CurveEditorGeometry.Create(curveEditorScroll.Width, curveEditorScroll.Height, toolsHeight, SystemInformation.VerticalScrollBarWidth);
                tools.Bounds = new Rectangle(0, 0, layout.Width, toolsHeight);
                responseArea.Bounds = new Rectangle(0, toolsHeight, layout.Width, layout.ResponseHeight);
                settings.Bounds = new Rectangle(3, toolsHeight + layout.ResponseHeight + 3, Math.Max(1, layout.Width - 6), layout.SettingsHeight - 6);
                keyBehaviorPanel.Bounds = layout.Beside ? new Rectangle(0, 0, 184, layout.ResponseHeight) : new Rectangle(0, layout.Side + 8, layout.Width, 264);
                curve.Bounds = layout.Beside ? new Rectangle(196, 0, layout.Side, layout.Side) : new Rectangle(0, 0, layout.Side, layout.Side);
                if (editorArea.Height != layout.TotalHeight) editorArea.Height = layout.TotalHeight;
                } while (arrangeAgain); }
                finally { arranging = false; }
            };
            editorArea.Layout += delegate { arrange(null, EventArgs.Empty); };
            responseArea.SizeChanged += arrange; curveEditorScroll.SizeChanged += arrange;
        }
        void ScrollCurveSettingsTo(Control target)
        { if (target == null || target.IsDisposed || !advancedPanel.Visible) return; curveEditorScroll.ScrollControlIntoView(target); }

        void SetAdvancedVisible(bool visible)
        { SetDetailMode(visible ? "advanced" : null, true); }
        void SelectKeyboardKeys()
        {
            int[] chosen = keyboard.SelectedKeyIndices; updating = true;
            try { keys.ClearSelection(); foreach (int index in chosen) { keys.Rows[index].Visible = true; keys.Rows[index].Selected = true; } }
            finally { updating = false; } RefreshBindings(); HighlightKeys();
        }
        IEnumerable<int> LayoutIndices()
        { return keyboard.LayoutModel == null ? Enumerable.Empty<int>() : keyboard.LayoutModel.Keys.Where(k => k.KeyIndex.HasValue).Select(k => k.KeyIndex.Value); }
        string LayoutLabel(int index)
        { var definition = keyboard.LayoutModel == null ? null : keyboard.LayoutModel.Keys.FirstOrDefault(k => k.KeyIndex == index); return definition == null ? "Index " + index : definition.GetLegend(keyboard.LegendStyle).Replace("\n", " "); }
        void UpdateKeyCard()
        {
            int[] selected = SelectedKeys(); bool one = selected.Length == 1; bool any = selected.Length != 0;
            keyTitle.Text = one ? string.Format(Tr("Taste {0}", "Key {0}"), Label(selected[0])) : any ? string.Format(Tr("{0} Tasten", "{0} keys"), selected.Length) : Tr("Taste auswählen", "Choose a key");
            keyHint.Text = !any ? Tr("Klicke auf eine Taste in der Abbildung.", "Click a key on the keyboard.") : one ? Tr("Druckbereich · diese Taste", "Pressure range · this key") : Tr("Druckbereich · ausgewählte Tasten", "Pressure range · selected keys");
            RefreshPressureRangeEditor();
            RefreshInputThresholdCaptureButtons();
            if (one && reader != null && !reader.IsReading) keyHint.Text = Tr("Keine Druckwerte.\nVerbindungsdetails stehen unten.", "No pressure data.\nSee connection details below.");
            inputDetails.Visible = reader != null && (!reader.IsReading || !reader.HasReceivedSamples);
            var keyRows = (TableLayoutPanel)inputDetails.Parent; keyRows.RowStyles[keyRows.GetRow(inputDetails)].Height = inputDetails.Visible ? 22 : 0;
            targets.Enabled = addTargetButton.Enabled = any; removeTargetButton.Enabled = toggleTargetButton.Enabled = SelectedBindings().Length != 0;
            targetHeader.Text = string.Format(Tr("Ziele · {0}", "Targets · {0}"), ControllerDisplayName); keyCardTips.SetToolTip(targetHeader, targetHeader.Text);
            mappingEmpty.Visible = bindings.Rows.Count == 0; if (mappingEmpty.Visible) mappingEmpty.BringToFront();
            bindings.Columns[0].Visible = selected.Length > 1;
            RefreshMappingSummaries(false);
            UpdateDetailsTitle();
            UpdatePressure(reader == null ? new KeyStateSnapshot[0] : reader.GetUiSnapshot(MappingSession.MaximumInputAgeMilliseconds));
        }
        void UpdatePressure(KeyStateSnapshot[] snapshot)
        {
            int[] selected = SelectedKeys();
            if (selected.Length != 1) { if (pressureCaptureReader == null) pressureRange.MeasuredValue = null; SetPressureDisplay(0, Tr("Druck: —", "Pressure: —")); return; }
            var samples = snapshot.Where(s => s.KeyIndex == selected[0] && s.Known).ToArray();
            if (samples.Length == 0) { if (pressureCaptureReader == null) pressureRange.MeasuredValue = null; SetPressureDisplay(0, reader == null ? Tr("Zum Messen oben verbinden", "Connect above to measure") : !reader.IsReading ? Tr("Druckzugriff noch nicht bereit", "Pressure input is not ready") : Tr("Warte auf Druckdaten …", "Waiting for pressure data …")); return; }
            var sample = samples[0];
            if (sample.Stale) { if (pressureCaptureReader == null) pressureRange.MeasuredValue = null; SetPressureDisplay(0, string.Format(Tr("Letzter Wert: {0} · veraltet", "Last value: {0} · stale"), sample.RawValue)); return; }
            double amount = sharedPressureRange.Depth(selected[0], sample.RawValue);
            SetPressureDisplay((int)Math.Round(amount * 1000), string.Format(Tr("Druck · {0} · Rohwert {1}", "Pressure · {0} · raw {1}"), amount.ToString("P0"), sample.RawValue));
            if (pressureCaptureReader == null) pressureRange.MeasuredValue = sample.RawValue;
        }
        void SetPressureDisplay(int value, string text)
        { if (pressure.Value != value) pressure.Value = value; if (pressureText.Text != text) pressureText.Text = text; }

        void ChangeLayoutFromPicker()
        {
            bool iso = layoutMode.SelectedIndex == 2 || layoutMode.SelectedIndex == 0 && automaticIso;
            keyboard.LayoutModel = layoutMode.SelectedIndex == 0 && !automaticLayoutAvailable ? null : iso ? KeyboardLayout.Tk75Iso() : KeyboardLayout.Tk75Ansi();
            ApplyLegendForLayout();
            keyboardTitle.Text = UiText.Get(keyboard.LayoutModel == null ? "Tastatur einrichten" : "Deine Tastatur"); RefreshKeys(); RefreshBindings();
        }
        void ConfigureKeyboardForDevice(CollectionInfo device)
        {
            bool tk75 = device != null && device.vendorId == 0x3151 && device.productId == 0x5030 && (device.product ?? "").IndexOf("TK75", StringComparison.OrdinalIgnoreCase) >= 0;
            automaticLayoutAvailable = tk75;
            automaticIso = true; changingLayout = true; try { layoutMode.Items[0] = tk75 ? "ISO · voreingestellt" : "Automatisch · unbekannt"; } finally { changingLayout = false; }
            keyboard.ClearKeyStates();
            if (layoutMode.SelectedIndex != 0) return;
            keyboard.LayoutModel = tk75 ? automaticIso ? KeyboardLayout.Tk75Iso() : KeyboardLayout.Tk75Ansi() : null;
            ApplyLegendForLayout();
            keyboardTitle.Text = UiText.Get(tk75 ? "Deine Tastatur" : "Tastatur einrichten");
        }
        void UpdateDetectedLayout()
        {
            ApplyDetectedModel(reader == null ? (uint?)null : reader.DeviceModelId);
        }
        void ApplyDetectedModel(uint? detected)
        {
            if (!detected.HasValue) return;
            uint model = detected.Value; if (model != 3590 && model != 3591) return;
            automaticLayoutAvailable = true;
            bool iso = model == 3591; string caption = "Automatisch · " + (iso ? "ISO" : "ANSI");
            if (automaticIso == iso && (string)layoutMode.Items[0] == caption) return;
            automaticIso = iso; changingLayout = true; try { layoutMode.Items[0] = caption; } finally { changingLayout = false; }
            if (layoutMode.SelectedIndex == 0) ChangeLayoutFromPicker();
        }
        void UpdateKeyboardValues(KeyStateSnapshot[] snapshot)
        {
            var current = snapshot.ToDictionary(s => s.KeyIndex); var mapped = new HashSet<int>(CurrentControllerBindings(UiReadProfile).Select(b => b.KeyIndex));
            foreach (int index in LayoutIndices())
            {
                KeyStateSnapshot sample; bool valid = current.TryGetValue(index, out sample) && sample.Known && !sample.Stale;
                double? depth = valid ? (double?)sharedPressureRange.Depth(index, sample.RawValue) : null; bool estimated = sharedPressureRange.IsDefaultForKey(index);
                keyboard.UpdateKeyState(index, mapped.Contains(index), valid && depth.HasValue && depth.Value > 0, depth, estimated);
            }
        }
    }
}
