using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Threading;
using IPhoneMirror.App.Services.Automation;
using IPhoneMirror.App.Updater;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static int RunAutomationLifecycleTests(bool verifyLongStartup = false)
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var root = Path.Combine(Path.GetTempPath(), "iphoneMirror-automation-lifecycle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown, IsUiPreviewMode = true };
        app.InitializeComponent();
        // Consume queued WPF startup while real-device startup is suppressed.
        AdvanceDispatcher(TimeSpan.FromMilliseconds(30));
        var previousStore = KeyboardField(app, "_settingsStore");
        SetKeyboardField(app, "_settingsStore", new UpdateSettingsStore(Path.Combine(root, "settings.json")));
        app.UpdateSettings.ApplicationDisplayMode = ApplicationDisplayMode.Tray;
        MainWindow? window = null;
        try
        {
            void Check(bool value, string message) => InteractionAssert(value, message);
            int FreePort()
            {
                using var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                return ((IPEndPoint)listener.LocalEndpoint).Port;
            }
            MainWindow NewWindow(string keyName)
            {
                var result = new MainWindow();
                app.MainWindow = result;
                SetKeyboardField(result, "_startupServicesStarted", true);
                SetKeyboardField(result, "_automationKeys", new AutomationKeyStore(Path.Combine(root, keyName)));
                return result;
            }
            void StopWindow()
            {
                if (window is null) return;
                AwaitMapping((Task)KeyboardCall(window, "ShutdownAutomationAsync")!);
                KeyboardCall(window, "DisposeTray");
                CloseWorkspaceTestWindow(window);
                window = null;
            }

            app.UpdateSettings.AutomationApi = new() { Enabled = true, Port = FreePort() };
            window = NewWindow("tray-key.bin");
            app.IsUiPreviewMode = false;
            window.StartInTray();
            AwaitMapping((Task)KeyboardCall(window, "StartAutomationOnStartupAsync")!);
            Check(!window.IsLoaded && window.AutomationRunning, "Saved API must start without loading the tray window.");
            var host = KeyboardField(window, "_automationHost");
            window.StartInTray();
            AwaitMapping((Task)KeyboardCall(window, "StartAutomationOnStartupAsync")!);
            Check(ReferenceEquals(host, KeyboardField(window, "_automationHost")) && window.AutomationRunning,
                "Repeated startup must preserve the running host.");
            var trayKey = new AutomationKeyStore(Path.Combine(root, "tray-key.bin")).LoadOrCreate();
            AwaitMapping(window.RegenerateAutomationKeyAsync());
            Check(window.AutomationRunning && trayKey != new AutomationKeyStore(Path.Combine(root, "tray-key.bin")).LoadOrCreate(),
                "Persistently enabled API must remain running after rotation.");
            StopWindow();

            app.IsUiPreviewMode = true;
            app.UpdateSettings.AutomationApi = new() { Port = FreePort() };
            window = NewWindow("background-key.bin");
            app.IsUiPreviewMode = false;
            AwaitMapping(window.StartAutomationBackgroundAsync());
            Check(window.AutomationRunning && !app.UpdateSettings.AutomationApi.Enabled,
                "Background mode must enable the API without changing saved settings.");
            var effective = app.UpdateSettings.AutomationApi.Clone();
            effective.Enabled = true;
            effective.Port = FreePort();
            effective.Permissions = ["device.read"];
            AwaitMapping(window.ConfigureAutomationAsync(effective, save: false));
            var oldKey = new AutomationKeyStore(Path.Combine(root, "background-key.bin")).LoadOrCreate();
            AwaitMapping(window.RegenerateAutomationKeyAsync());
            Check(window.AutomationRunning && !app.UpdateSettings.AutomationApi.Enabled &&
                window.AutomationAddress.EndsWith(":" + effective.Port), "Rotation must preserve the effective transient port and enabled state.");
            using var http = new HttpClient { BaseAddress = new Uri(window.AutomationAddress), Timeout = TimeSpan.FromSeconds(5) };
            HttpStatusCode GetStatus(string path = "/api/v1/devices")
            {
                var request = http.GetAsync(path);
                AwaitMapping(request);
                using var response = request.GetAwaiter().GetResult();
                return response.StatusCode;
            }
            http.DefaultRequestHeaders.Add("X-API-Key", oldKey);
            Check(GetStatus() == HttpStatusCode.Unauthorized,
                "Old API key must be rejected after rotation.");
            http.DefaultRequestHeaders.Remove("X-API-Key");
            http.DefaultRequestHeaders.Add("X-API-Key", new AutomationKeyStore(Path.Combine(root, "background-key.bin")).LoadOrCreate());
            Check(GetStatus() == HttpStatusCode.OK,
                "New API key must authenticate after rotation.");
            Check(GetStatus("/api/v1/devices/test/clipboard") == HttpStatusCode.Forbidden,
                "Rotation must preserve transient permissions without restoring saved clipboard access.");
            effective.Enabled = false;
            AwaitMapping(window.ConfigureAutomationAsync(effective, save: false));
            // A saved enable flag must not resurrect a deliberately stopped host.
            app.UpdateSettings.AutomationApi.Enabled = true;
            AwaitMapping(window.RegenerateAutomationKeyAsync());
            Check(!window.AutomationRunning, "Rotating a stopped API must leave it stopped.");
            TestAutomationManagementCancellation(window, root, verifyLongStartup);
            StopWindow();
            Console.WriteLine("Automation lifecycle: unloaded tray startup, repeated startup, transient settings, key authentication and stopped rotation passed.");
            return 0;
        }
        finally
        {
            app.IsUiPreviewMode = true;
            if (window is not null)
            {
                AwaitMapping((Task)KeyboardCall(window, "ShutdownAutomationAsync")!);
                KeyboardCall(window, "DisposeTray");
                CloseWorkspaceTestWindow(window);
            }
            SetKeyboardField(app, "_settingsStore", previousStore);
            app.Shutdown();
            Directory.Delete(root, recursive: true);
        }
    }
}
