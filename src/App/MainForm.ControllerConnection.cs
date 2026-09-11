using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Tk75.Mapping;
using Tk75.Output;

namespace Tk75.App
{
    public sealed partial class MainForm
    {
        // UI callers await one owned request; no connection work runs on the
        // message loop. Startup owns its cancellation/generation separately.
        async Task RequestControllerConnectionAsync(string controllerId, bool connect, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (closing || deviceDetachInProgress || rgbClosePending || IsDisposed)
                throw new OperationCanceledException("Controller connection is no longer current.");
            try
            {
                if (connect)
                {
                    ControllerDefinition definition = ControllerRouting.EffectiveControllers(UiReadProfile).FirstOrDefault(d => d.Id == controllerId);
                    if (definition == null) throw new InvalidOperationException(Tr("Dieser Controller ist nicht mehr im Profil vorhanden.", "This controller is no longer in the profile."));
                    string error = ControllerOutputs.AvailabilityError(definition.Kind);
                    if (error != null) throw new InvalidOperationException(UiText.Get(error));
                    settings.EndEdit(); FlushInputDraft();
                    Task connection = runtime.EnableControllerAsync(controllerId, cancellationToken);
                    UpdateControllerConnectionUi();
                    await connection;
                }
                else
                    await runtime.DisableControllerAsync(controllerId, Tr("Controller getrennt", "Controller disconnected"));
            }
            finally
            {
                if (!closing && !IsDisposed && !Disposing)
                {
                    RefreshKeyboardSuppression(false); RefreshInputModeUi(); UpdateControllerConnectionUi();
                }
            }
        }
    }
}
