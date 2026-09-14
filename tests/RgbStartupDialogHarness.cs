using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using Tk75.App;

public static class RgbStartupDialogHarness
{
    static int checks;
    static void Check(bool value, string reason) { checks++; if (!value) throw new Exception(reason); }
    static FieldInfo Field(Type type, string name)
    { return type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic); }
    static void ReplacedWorkClosesOldDialog(string output)
    {
        string data = Path.Combine(output, "dialog-switch-state-" + Guid.NewGuid().ToString("N"));
        using (var form = new MainForm(data, true))
        using (var dialog = new RgbStartupConfirmationDialog("F9"))
        {
            Type formType = typeof(MainForm), workType = formType.GetNestedType("RgbBackupWork", BindingFlags.NonPublic);
            object oldWork = Activator.CreateInstance(workType, BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] { null, (uint)3591, data }, null);
            object newWork = Activator.CreateInstance(workType, BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] { null, (uint)3591, data }, null);
            Field(workType, "StartupApprovalPending").SetValue(oldWork, true);
            Field(workType, "StartupApprovalPending").SetValue(newWork, true);
            Field(formType, "rgbStartupDialog").SetValue(form, dialog);
            Field(formType, "rgbStartupDialogWork").SetValue(form, oldWork);
            Field(formType, "rgbBackupWork").SetValue(form, newWork);
            int closed = 0;
            dialog.FormClosed += delegate
            {
                closed++;
                Field(formType, "rgbStartupDialog").SetValue(form, null);
                Field(formType, "rgbStartupDialogWork").SetValue(form, null);
                formType.GetMethod("CompleteRgbStartupApproval", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(form,
                    new object[] { oldWork, dialog.DialogResult == DialogResult.OK, dialog.AutoRepairChecked });
            };
            try
            {
                // The owner is preview-only and remains unshown, so its real
                // startup callback cannot open inputs or start an RGB worker.
                dialog.StartPosition = FormStartPosition.Manual; dialog.Location = new Point(-32000, -32000);
                dialog.Show(form);
                formType.GetMethod("RefreshRgbStartupConfirmation", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(form, null);
                Check(closed == 1 && dialog.IsDisposed, "Replacing pending work closes its obsolete modeless dialog");
                Check(dialog.DialogResult == DialogResult.Cancel, "Obsolete dialog is canceled rather than approved");
                Check((int)Field(workType, "StartupApproval").GetValue(oldWork) == -1, "Obsolete decision declines only its original request");
                Check((int)Field(workType, "StartupApproval").GetValue(newWork) == 0 &&
                    (bool)Field(workType, "StartupApprovalPending").GetValue(newWork), "New pending request cannot inherit an old dialog decision");
                formType.GetMethod("RefreshRgbStartupConfirmation", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(form, null);
                Check(Field(formType, "rgbStartupDialog").GetValue(form) == null, "Preview mode never opens a replacement prompt");
            }
            finally
            {
                // Synthetic work has no reader or worker to stop at disposal.
                Field(formType, "rgbBackupWork").SetValue(form, null);
                Field(formType, "rgbStartupDialog").SetValue(form, null);
                Field(formType, "rgbStartupDialogWork").SetValue(form, null);
            }
        }
    }
    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            Application.EnableVisualStyles();
            string output = Path.GetFullPath(args[0]); Directory.CreateDirectory(output);
            foreach (string language in new string[] { "de", "en" })
            {
                UiText.SetLanguage(language);
                using (var dialog = new RgbStartupConfirmationDialog(UiText.Get(
                    "F9 · Umschalttaste: Gelb\r\nW, A, S, D · Controller: Blau",
                    "F9 · Mode-switch key: Yellow\r\nW, A, S, D · Controller: Blue")))
                {
                    dialog.StartPosition = FormStartPosition.Manual; dialog.Location = new Point(-32000, -32000);
                    dialog.Show(); Application.DoEvents();
                    dialog.PerformLayout();
                    Check(!dialog.AutoRepairChecked, "Opening does not grant automatic consent");
                    var automatic = (CheckBox)dialog.Controls.Find("automaticStartupRepair", true)[0];
                    automatic.Checked = true; dialog.DialogResult = DialogResult.Cancel;
                    Check(!dialog.AutoRepairChecked, "Cancel suppresses a checked automatic option");
                    dialog.DialogResult = DialogResult.OK;
                    Check(dialog.AutoRepairChecked, "Affirmative choice returns explicit opt-in");
                    dialog.DialogResult = DialogResult.None; automatic.Checked = false;
                    using (var bitmap = new Bitmap(dialog.Width, dialog.Height))
                    {
                        dialog.DrawToBitmap(bitmap, new Rectangle(Point.Empty, dialog.Size));
                        bitmap.Save(Path.Combine(output, "confirmation-" + language + ".png"));
                    }
                    Control details = dialog.Controls.Find("startupMarkerSummary", true)[0];
                    Check(details.Height >= 48, "Summary retains two visible lines");
                    Check(automatic.Bounds.Bottom <= automatic.Parent.ClientSize.Height, "Automatic choice remains within dialog layout");
                }
            }
            foreach (bool accept in new bool[] { false, true })
            {
                using (var dialog = new RgbStartupConfirmationDialog("F9"))
                {
                    var automatic = (CheckBox)dialog.Controls.Find("automaticStartupRepair", true)[0];
                    automatic.Checked = true;
                    Button button = (Button)dialog.Controls.Find(accept ? "cleanStartupMarkers" : "keepStartupMarkers", true)[0];
                    // Invoke only this synthetic dialog's own event; no real UI,
                    // application instance, keyboard or external window is used.
                    typeof(Control).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(button, new object[] { EventArgs.Empty });
                    Check(dialog.IsDisposed, "Modeless action closes the dialog");
                    Check(dialog.DialogResult == (accept ? DialogResult.OK : DialogResult.Cancel), "Modeless action retains its result");
                    Check(dialog.AutoRepairChecked == accept, "Modeless consent requires explicit acceptance");
                }
            }
            ReplacedWorkClosesOldDialog(output);
            Console.WriteLine("PASS: " + checks + " startup dialog checks and German/English images (synthetic UI only)."); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
