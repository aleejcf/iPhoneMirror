using IPhoneMirror.App.Services;

namespace IPhoneMirror.App.ViewModels;

internal sealed partial class MainViewModel
{
    internal HashSet<string> AutomationControlStartup { get; } = new(StringComparer.OrdinalIgnoreCase);

    internal async Task StartAutomationControlAsync(string source, bool wireless, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (!AutomationControlStartup.Add(source)) throw new InvalidOperationException("Control preparation already active.");
        try
        {
            // The bridge owns its DDI/download/mount deadlines. A shorter
            // API deadline would cancel a healthy first-time preparation.
            await (wireless ? StartWirelessControlAsync(source) : StartUsbControlAsync(source))
                .WaitAsync(cancellation);
            cancellation.ThrowIfCancellationRequested();
        }
        catch (Exception) when (cancellation.IsCancellationRequested)
        {
            await CancelReverseControlAsync(wireless ? ControlStatusMode.Wireless : ControlStatusMode.Usb, source);
            throw;
        }
        finally { AutomationControlStartup.Remove(source); }
    }
}
