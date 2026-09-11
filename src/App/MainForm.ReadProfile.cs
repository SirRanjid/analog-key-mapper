using System;
using Tk75.Mapping;

namespace Tk75.App
{
    public sealed partial class MainForm
    {
        EditHistory cachedUiHistory;
        object cachedUiSnapshotToken;
        Profile cachedUiProfile;

        // UI thread only. Use solely for audited reads; edits and editing
        // helpers must keep obtaining their own copy from history.Current.
        // Snapshot identity also tracks undo/redo and bounded-history rollover.
        Profile UiReadProfile
        {
            get
            {
                object token = history.SnapshotToken;
                if (!Object.ReferenceEquals(cachedUiHistory, history) || !Object.ReferenceEquals(cachedUiSnapshotToken, token))
                {
                    Profile next = history.Current;
                    cachedUiHistory = history; cachedUiSnapshotToken = token; cachedUiProfile = next;
                }
                return cachedUiProfile;
            }
        }
    }
}
