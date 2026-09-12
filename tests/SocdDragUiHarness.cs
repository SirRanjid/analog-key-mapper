using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using Tk75.App;
using Tk75.Mapping;

namespace Tk75.Tests
{
    public static partial class AppUiHarness
    {
        static object BeginSocdTestDrag(MainForm form, int[] edited, int[] source)
        {
            Check(Field<object>(form, "activeMappingDrag") == null, "SOCD fixture begins without a previous drag.");
            SelectKeys(form, edited); Call(form, "SetDetailMode", "input", true, false);
            var keyboard = Field<VisualKeyboard>(form, "keyboard");
            Call(form, "CaptureSocdDragContext", keyboard.LayoutModel.FindByIndex(source[0]));
            SelectKeys(form, source);
            object drag = Call(form, "PrepareKeyMappingDrag", (object)source);
            Equal("controller", Field<string>(form, "detailsMode"), "Actual key-drag preparation reveals the controller without OLE.");
            Call(form, "InitializeMappingDrag", drag);
            return drag;
        }
        static DragEventArgs SocdDragArgs(object drag, bool own)
        {
            var data = new DataObject();
            data.SetData(DataFormats.UnicodeText, false, own ? (string)drag.GetType().GetField("Token").GetValue(drag) : "external-untrusted-data");
            return new DragEventArgs(data, 0, 0, 0, DragDropEffects.Copy, DragDropEffects.None);
        }
        static void SocdControlEvent(MainForm form, string field, string method, DragEventArgs args)
        {
            var control = Field<Control>(form, field);
            typeof(Control).GetMethod(method, Private).Invoke(control, new object[] { args });
            Pump(form);
        }
        static void RevealSocdTestTarget(MainForm form, object drag)
        {
            var args = SocdDragArgs(drag, true);
            SocdControlEvent(form, "keySettingsToggle", "OnDragEnter", args);
            Equal("input", Field<string>(form, "detailsMode"), "Hovering the real Keys tab reveals Behavior during a key drag.");
            Check(args.Effect == DragDropEffects.None, "The navigation tab itself never commits a pairing.");
        }
        static void EndSocdTestDrag(MainForm form, object drag)
        {
            typeof(MainForm).GetField("activeMappingDrag", Private).SetValue(form, null);
            Call(form, "ClearMappingDragVisuals"); Call(form, "FinishSocdDrag", drag); Pump(form);
        }
        static void RunSocdPreDragGuards(MainForm form)
        {
            SelectKeys(form, 14); Call(form, "SetDetailMode", "input", true, false);
            Field<NumericUpDown>(form, "actuationPoint").Value = 29;
            Check(Field<bool>(form, "inputDirty"), "The pre-drag fixture contains a real unsaved activation edit.");
            var keyboard = Field<VisualKeyboard>(form, "keyboard");
            Call(form, "CaptureSocdDragContext", keyboard.LayoutModel.FindByIndex(21));
            Check(Field<bool>(form, "inputDirty") && Field<string>(form, "detailsMode") == "input",
                "Mouse-down capture neither discards the draft nor changes the Behavior tab.");
            SelectKeys(form, 21);
            Check(!Field<bool>(form, "inputDirty") && Math.Abs(KeyInputEditing.GetOrDefault(Current(form), 14).ActuationPoint - .29) < .000001,
                "The ordinary source-selection flush saves the original target's draft.");
            Profile flushed = Current(form);
            object drag = Call(form, "PrepareKeyMappingDrag", (object)new[] { 21 });
            Call(form, "InitializeMappingDrag", drag);
            try
            {
                Check(drag.GetType().GetField("Socd").GetValue(drag) != null,
                    "A successful matching draft flush keeps the original SOCD target eligible.");
                RevealSocdTestTarget(form, drag);
                var drop = SocdDragArgs(drag, true);
                SocdControlEvent(form, "socdDropTarget", "OnDragDrop", drop);
                Check(drop.Effect == DragDropEffects.Copy, "Pairing remains available after the automatic draft save.");
                var expected = KeyInputEditing.SetPairing(flushed, new[] { 14 }, 21, KeyInputEditing.GetOrDefault(flushed, 14).OppositePolicy);
                Equal(Json(expected), Json(Current(form)), "Pairing preserves the just-saved activation draft and all unrelated settings.");
            }
            finally { EndSocdTestDrag(form, drag); }
            Call(form, "Undo");
            Equal(Json(flushed), Json(Current(form)), "Undoing the pairing retains the preceding automatic draft save.");

            foreach (string change in new[] { "undo", "history replacement" })
            {
                var edited = Current(form); edited.Name += " pre-drag " + change; Call(form, "Commit", edited);
                SelectKeys(form, 14); Call(form, "SetDetailMode", "input", true, false);
                Call(form, "CaptureSocdDragContext", keyboard.LayoutModel.FindByIndex(9));
                SelectKeys(form, 9);
                if (change == "undo") Call(form, "Undo");
                else typeof(MainForm).GetField("history", Private).SetValue(form, new EditHistory(Current(form)));
                string unchanged = Json(Current(form));
                drag = Call(form, "PrepareKeyMappingDrag", (object)new[] { 9 });
                Call(form, "InitializeMappingDrag", drag);
                try
                {
                    Check(drag.GetType().GetField("Socd").GetValue(drag) == null,
                        "An " + change + " between mouse-down and the drag threshold discards the obsolete editing target.");
                    var rejected = SocdDragArgs(drag, true);
                    SocdControlEvent(form, "keySettingsToggle", "OnDragEnter", rejected);
                    Equal("controller", Field<string>(form, "detailsMode"), "An obsolete pre-drag target cannot reveal Behavior after " + change + ".");
                    SocdControlEvent(form, "socdDropTarget", "OnDragDrop", rejected);
                    Check(rejected.Effect == DragDropEffects.None, "An obsolete pre-drag target cannot pair after " + change + ".");
                    Equal(unchanged, Json(Current(form)), "A rejected pre-drag context preserves the current profile after " + change + ".");
                    SelectKeys(form, 15); Call(form, "SetDetailMode", "advanced", true, false);
                }
                finally { EndSocdTestDrag(form, drag); }
                Check(((int[])Call(form, "SelectedKeys")).SequenceEqual(new[] { 15 }) && Field<string>(form, "detailsMode") == "advanced",
                    "Finishing the discarded pre-drag context preserves the newer selection and tab after " + change + ".");
            }
        }
        static void RunSocdDragUi(MainForm form, string artifacts)
        {
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            Console.WriteLine("SOCD STAGE: start");
            int started = assertions;
            Profile original = Current(form);
            try
            {
                var fixture = KeyInputEditing.SetPairing(original, new[] { 14 }, 21, InputOpposedPolicy.LastPressed);
                fixture = KeyInputEditing.SetPairing(fixture, new[] { 9 }, 15, InputOpposedPolicy.FirstPressed);
                fixture = KeyInputEditing.ApplyActivation(fixture, new[] { 14 }, new KeyInputSettings { ActuationPoint = .24 }, InputActivationFields.Actuation);
                fixture = KeyInputEditing.ApplyActivation(fixture, new[] { 9 }, new KeyInputSettings { ActuationPoint = .36 }, InputActivationFields.Actuation);
                Call(form, "Commit", fixture); Pump(form);
                Console.WriteLine("SOCD STAGE: fixture ready " + elapsed.ElapsedMilliseconds + " ms");
                string before = Json(Current(form));
                object drag = BeginSocdTestDrag(form, new[] { 14 }, new[] { 9 });
                try
                {
                    RevealSocdTestTarget(form, drag);
                    Console.WriteLine("SOCD STAGE: target revealed " + elapsed.ElapsedMilliseconds + " ms");
                    Check(((int[])Call(form, "SelectedKeys")).SequenceEqual(new[] { 14 }), "Hover restores the originally edited key, not the dragged source.");
                    Equal(before, Json(Current(form)), "Revealing the target performs no profile edit.");
                    var target = Field<Label>(form, "socdDropTarget");
                    Check(target.Visible && target.Width > 100 && target.Height >= 30, "SOCD has a visible dedicated labelled drop area.");
                    Check(target.AccessibleName.Length > 0 && target.AccessibleDescription.Length > 0, "The drop area exposes a target name and alternative keyboard instruction.");
                    var foreign = SocdDragArgs(drag, false);
                    SocdControlEvent(form, "socdDropTarget", "OnDragEnter", foreign);
                    Check(foreign.Effect == DragDropEffects.None && !Field<bool>(form, "socdDropHighlighted"), "Untrusted drag data cannot highlight the pairing target.");
                    SocdControlEvent(form, "socdDropTarget", "OnDragDrop", foreign);
                    Equal(before, Json(Current(form)), "External text cannot create a key pairing.");
                    var accepted = SocdDragArgs(drag, true);
                    SocdControlEvent(form, "socdDropTarget", "OnDragOver", accepted);
                    Check(accepted.Effect == DragDropEffects.Copy && Field<bool>(form, "socdDropHighlighted"), "Only the valid internal single-key drag highlights the pairing target.");
                    using (var screenshot = new Bitmap(form.ClientSize.Width, form.ClientSize.Height))
                    { form.DrawToBitmap(screenshot, form.ClientRectangle); screenshot.Save(Path.Combine(artifacts, "socd-opposite-drop.png")); }
                    SocdControlEvent(form, "socdDropTarget", "OnDragDrop", accepted);
                    Console.WriteLine("SOCD STAGE: pair applied " + elapsed.ElapsedMilliseconds + " ms");
                    Check(accepted.Effect == DragDropEffects.Copy && !Field<bool>(form, "socdDropHighlighted"), "Confirmed SOCD drop clears its highlight and reports success.");
                    var expected = KeyInputEditing.SetPairing(fixture, new[] { 14 }, 9, InputOpposedPolicy.LastPressed);
                    Equal(Json(expected), Json(Current(form)), "The real UI pairs symmetrically, unpairs both former partners, preserves activation parameters and the target policy, and changes no mappings.");
                }
                finally { EndSocdTestDrag(form, drag); }
                string paired = Json(Current(form));
                Check(((int[])Call(form, "SelectedKeys")).SequenceEqual(new[] { 14 }), "Successful pairing retains the originally edited target key.");
                Call(form, "Undo"); Equal(before, Json(Current(form)), "One Undo reverses the entire SOCD drag transaction.");
                Call(form, "Redo"); Equal(paired, Json(Current(form)), "One Redo restores both sides and their shared policy.");
                Console.WriteLine("SOCD STAGE: undo/redo ready " + elapsed.ElapsedMilliseconds + " ms");

                drag = BeginSocdTestDrag(form, new[] { 14 }, new[] { 21 });
                try
                {
                    RevealSocdTestTarget(form, drag);
                    var tab = SocdDragArgs(drag, true);
                    SocdControlEvent(form, "controllerToggle", "OnDragEnter", tab);
                    Equal("controller", Field<string>(form, "detailsMode"), "An active keyboard drag can return from Behavior to the Controller tab.");
                    Call(form, "CancelMappingDrag");
                }
                finally { EndSocdTestDrag(form, drag); }
                Equal(paired, Json(Current(form)), "Canceled SOCD navigation never changes the profile.");
                Check(((int[])Call(form, "SelectedKeys")).SequenceEqual(new[] { 14 }) && Field<string>(form, "detailsMode") == "input", "Cancellation restores the prior selection and Behavior view.");

                foreach (string invalid in new[] { "self", "multiple-source", "multiple-target", "stale-profile", "stale-history", "stale-layout", "controller-source" })
                {
                    Console.WriteLine("SOCD STAGE: " + invalid + " " + elapsed.ElapsedMilliseconds + " ms");
                    int[] edited = invalid == "multiple-target" ? new[] { 14, 15 } : new[] { 14 };
                    int[] source = invalid == "self" ? new[] { 14 } : invalid == "multiple-source" ? new[] { 9, 21 } : new[] { 21 };
                    drag = BeginSocdTestDrag(form, edited, source);
                    try
                    {
                        if (invalid == "stale-profile" || invalid == "stale-history" || invalid == "stale-layout" || invalid == "controller-source") RevealSocdTestTarget(form, drag);
                        if (invalid == "stale-profile") { var edit = Current(form); edit.Name += " changed during drag"; Call(form, "Commit", edit); }
                        if (invalid == "stale-history") typeof(MainForm).GetField("history", Private).SetValue(form, new EditHistory(Current(form)));
                        if (invalid == "stale-profile" || invalid == "stale-history")
                        { SelectKeys(form, 15); Call(form, "SetDetailMode", "advanced", true, false); }
                        if (invalid == "stale-layout") drag.GetType().GetField("Layout").SetValue(drag, null);
                        if (invalid == "controller-source") drag.GetType().GetField("Target").SetValue(drag, (OutputTarget?)OutputTarget.A);
                        string unchanged = Json(Current(form)); var rejected = SocdDragArgs(drag, true);
                        SocdControlEvent(form, "keySettingsToggle", "OnDragEnter", rejected);
                        SocdControlEvent(form, "socdDropTarget", "OnDragOver", rejected);
                        Check(rejected.Effect == DragDropEffects.None, invalid + " cannot offer an SOCD copy effect.");
                        SocdControlEvent(form, "socdDropTarget", "OnDragDrop", rejected);
                        Equal(unchanged, Json(Current(form)), invalid + " cannot commit an SOCD edit.");
                    }
                    finally { EndSocdTestDrag(form, drag); }
                    if (invalid == "stale-profile" || invalid == "stale-history")
                        Check(((int[])Call(form, "SelectedKeys")).SequenceEqual(new[] { 15 }) && Field<string>(form, "detailsMode") == "advanced",
                            invalid + " cancellation does not restore an obsolete selection or tab over the new profile context.");
                }
                Console.WriteLine("SOCD STAGE: pre-drag history guards " + elapsed.ElapsedMilliseconds + " ms");
                RunSocdPreDragGuards(form);
                AssertPassive(form);
            }
            finally
            {
                Console.WriteLine("SOCD STAGE: restoring fixture " + elapsed.ElapsedMilliseconds + " ms");
                Call(form, "Commit", original); SelectKeys(form, 14); Call(form, "SetDetailMode", null, true, false);
                typeof(MainForm).GetField("pendingSocdDrag", Private).SetValue(form, null);
            }
            Console.WriteLine("SOCD DRAG UI PASS: " + (assertions - started) + " assertions; internal-only pairing, original selection, symmetric undo, cancellation and stale-data rejection; no OLE or hardware.");
        }
    }
}
