using System;
using System.Threading;

namespace Tk75.App
{
    // An output already detached from its mapping worker. Neutralization and
    // device removal are separate so a group can stop before slow cleanup.
    public sealed class ControllerRelease : IDisposable
    {
        readonly Action neutral;
        Action dispose;
        public ControllerRelease(Action neutralize, Action remove)
        {
            if (neutralize == null || remove == null) throw new ArgumentNullException("release action");
            neutral = neutralize; dispose = remove;
        }
        public void Neutral() { neutral(); }
        public void Dispose()
        { Action action = Interlocked.Exchange(ref dispose, null); if (action != null) action(); }
    }
}
