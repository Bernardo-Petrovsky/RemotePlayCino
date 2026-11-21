using System.ComponentModel;

namespace RemotePlay;

internal sealed partial class RemotePlayForm
{
    private readonly CheckBox hostPriority = new() { Text = "Priorizar transmisión en este PC" };
    private readonly Label localPriorityStatus = new() { AutoSize = true, Text = "Se activa mientras compartes y se restaura al detener la sesión." };
    private readonly Label networkPriorityStatus = new() { AutoSize = true, Text = "Comprobando prioridad de red…" };
    private readonly Button networkPriorityButton = new() { Text = "Activar prioridad de red", Width = 230 };
    private bool readingNetworkPriority;
    private bool configuringNetworkPriority;
    private bool networkPriorityConfigured;
    private Task networkPriorityTask = Task.CompletedTask;

    private void ConfigurePriorities()
    {
        hostPriority.CheckedChanged += (_, _) => UpdateLocalPriority();
        hostPort.ValueChanged += (_, _) =>
        {
            networkPriorityConfigured = false;
            networkPriorityButton.Text = "Activar prioridad de red";
            networkPriorityButton.Enabled = !configuringNetworkPriority && !RemoteSession.IsReservedPort((int)hostPort.Value);
            networkPriorityStatus.Text = "Se comprobará la prioridad para el puerto seleccionado.";
        };
        Shown += async (_, _) => await RefreshNetworkPriorityAsync();
        Disposed += (_, _) => LocalStreamPriority.SetEnabled(false);
        networkPriorityButton.Click += (_, _) => networkPriorityTask = ConfigureNetworkPriorityAsync();
    }

    private async Task ConfigureNetworkPriorityAsync()
    {
        if (configuringNetworkPriority) return;
        if (RemoteSession.IsReservedPort((int)hostPort.Value))
        {
            networkPriorityStatus.Text = RemoteSession.ReservedPortMessage;
            return;
        }
        configuringNetworkPriority = true;
        networkPriorityButton.Enabled = false;
        try
        {
            networkPriorityStatus.Text = "Windows puede pedir permisos para cambiar la prioridad de red…";
            await NetworkPriority.ConfigureAsync(!networkPriorityConfigured, (int)hostPort.Value);
            await RefreshNetworkPriorityAsync();
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            if (!IsDisposed) networkPriorityStatus.Text = "Se canceló el aviso de Windows. La transmisión puede continuar.";
        }
        catch (Exception ex) when (ex is IOException or Win32Exception or OperationCanceledException or System.Text.Json.JsonException)
        {
            ConnectionLog.Write("Anfitrión", "Prioridad de red", ex);
            if (!IsDisposed) networkPriorityStatus.Text = "No se pudo cambiar la prioridad de red. La transmisión puede continuar.";
        }
        finally
        {
            configuringNetworkPriority = false;
            if (!closing && !IsDisposed)
                networkPriorityButton.Enabled = !RemoteSession.IsReservedPort((int)hostPort.Value);
        }
    }

    private void UpdateLocalPriority()
    {
        bool requested = hostStop is not null && hostPriority.Checked && !closing;
        bool applied = LocalStreamPriority.SetEnabled(requested);
        if (!applied)
            localPriorityStatus.Text = "Windows no permitió cambiar toda la prioridad local.";
        else
            localPriorityStatus.Text = requested
                ? $"Prioridad del PC activada para {AppBrand.ShortName} y sus procesos de video."
                : "Se activa mientras compartes y se restaura al detener la sesión.";
    }

    private async Task RefreshNetworkPriorityAsync()
    {
        if (readingNetworkPriority || closing || IsDisposed) return;
        readingNetworkPriority = true;
        try
        {
            int port = (int)hostPort.Value;
            if (RemoteSession.IsReservedPort(port))
            {
                networkPriorityStatus.Text = RemoteSession.ReservedPortMessage;
                networkPriorityButton.Enabled = false;
                return;
            }
            NetworkPriorityState state = await NetworkPriority.GetStateAsync(port, publicAddressStop.Token);
            if (closing || IsDisposed || port != (int)hostPort.Value) return;
            networkPriorityConfigured = state.Configured;
            networkPriorityButton.Text = state.Configured ? "Desactivar prioridad de red" : "Activar prioridad de red";
            if (state.Effective == true || (state.Configured && state.Effective is null))
                networkPriorityStatus.Text = $"Prioridad de red configurada para UDP {port}. El router debe respetarla.";
            else
                networkPriorityStatus.Text = state.Configured
                    ? "Prioridad guardada en Windows; aún no figura aplicada."
                    : "Prioridad de red desactivada. Windows requiere permisos para configurarla.";
        }
        catch (Exception ex) when (ex is IOException or Win32Exception or OperationCanceledException or System.Text.Json.JsonException)
        {
            if (!closing && !IsDisposed) networkPriorityStatus.Text = "No se pudo comprobar la prioridad de red de Windows.";
        }
        finally { readingNetworkPriority = false; }
    }
}
