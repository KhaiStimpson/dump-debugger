using DumpDebugger.Core.Contracts;
using DumpDebugger.Session;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.Web.WebView2.Core;
using Windows.Graphics;

namespace DumpDebugger.Host;

/// <summary>
/// The only window. Its client area is one WebView2; the web app draws everything, including
/// the titlebar (the system caption buttons sit on top of it, top-right). This class boots
/// WebView2, maps the bundled web assets to a virtual host, and pumps bridge messages between
/// the page and <see cref="HostRpcServer"/>.
/// </summary>
public sealed partial class MainWindow : Window, IHostPlatform
{
    private const string VirtualHost = "app.dumpdebugger.local";

    private readonly HostOptions _options;
    private readonly HostRpcServer _server;

    public MainWindow(HostOptions options)
    {
        _options = options;
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Standard;
        AppWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.ButtonForegroundColor = ColorHelper.FromArgb(0xff, 0x8b, 0x93, 0xa1);
        AppWindow.TitleBar.ButtonInactiveForegroundColor = ColorHelper.FromArgb(0xff, 0x5e, 0x66, 0x73);
        AppWindow.TitleBar.ButtonHoverBackgroundColor = ColorHelper.FromArgb(0xff, 0x1e, 0x23, 0x2b);
        AppWindow.TitleBar.ButtonHoverForegroundColor = ColorHelper.FromArgb(0xff, 0xee, 0xf0, 0xf3);
        AppWindow.TitleBar.ButtonPressedBackgroundColor = ColorHelper.FromArgb(0xff, 0x26, 0x2c, 0x35);
        var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        if (File.Exists(icon))
        {
            AppWindow.SetIcon(icon);
        }

        AppWindow.Resize(new SizeInt32(1480, 920));

        _server = new HostRpcServer(this, SendToWebAsync, new HostRpcServerOptions(options.StartupDumpPath));
        Closed += async (_, _) => await _server.DisposeAsync();
        _ = InitializeWebViewAsync();
    }

    private async Task InitializeWebViewAsync()
    {
        var envOptions = new CoreWebView2EnvironmentOptions();
        if (_options.CdpPort is { } port)
        {
            envOptions.AdditionalBrowserArguments = $"--remote-debugging-port={port}";
        }

        var environment = await CoreWebView2Environment.CreateWithOptionsAsync(
            browserExecutableFolder: null,
            userDataFolder: AppPaths.Ensure("WebView2"),
            options: envOptions);
        await Web.EnsureCoreWebView2Async(environment);

        var core = Web.CoreWebView2;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreDevToolsEnabled = true;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.Settings.IsGeneralAutofillEnabled = false;
        core.WebMessageReceived += OnWebMessageReceived;

        if (_options.DevServerUrl is { } devUrl)
        {
            core.Navigate(devUrl);
        }
        else
        {
            var webRoot = Path.Combine(AppContext.BaseDirectory, "web");
            core.SetVirtualHostNameToFolderMapping(VirtualHost, webRoot, CoreWebView2HostResourceAccessKind.DenyCors);
            core.Navigate($"https://{VirtualHost}/index.html");
        }
    }

    private void OnWebMessageReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        string json;
        try
        {
            json = args.TryGetWebMessageAsString();
        }
        catch (ArgumentException)
        {
            json = args.WebMessageAsJson;
        }

        var files = new List<string>();
        if (args.AdditionalObjects is { } objects)
        {
            foreach (var item in objects)
            {
                if (item is CoreWebView2File file)
                {
                    files.Add(file.Path);
                }
            }
        }

        _ = _server.HandleWebMessageAsync(json, files);
    }

    private Task SendToWebAsync(string json)
    {
        var tcs = new TaskCompletionSource();
        var queued = DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                Web.CoreWebView2?.PostWebMessageAsString(json);
            }
            catch (Exception)
            {
                // The page may be mid-navigation; dropping a message then is harmless.
            }

            tcs.TrySetResult();
        });
        if (!queued)
        {
            tcs.TrySetResult();
        }

        return tcs.Task;
    }

    public Task<string?> PickDumpFileAsync() => OnUiThreadAsync(async () =>
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker
        {
            SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Downloads,
            ViewMode = Windows.Storage.Pickers.PickerViewMode.List,
        };
        picker.FileTypeFilter.Add(".dmp");
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var file = await picker.PickSingleFileAsync();
        return file?.Path;
    });

    public Task<string?> PickSaveFileAsync(string suggestedName, string extension) => OnUiThreadAsync(async () =>
    {
        var picker = new Windows.Storage.Pickers.FileSavePicker
        {
            SuggestedFileName = Path.GetFileNameWithoutExtension(suggestedName),
            SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary,
        };
        picker.FileTypeChoices.Add(extension.TrimStart('.').ToUpperInvariant(), [extension]);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var file = await picker.PickSaveFileAsync();
        return file?.Path;
    });

    public void SetTitlebarPassthrough(IReadOnlyList<Rect> rects)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            var scale = Content?.XamlRoot?.RasterizationScale ?? 1.0;
            var source = InputNonClientPointerSource.GetForWindowId(AppWindow.Id);
            source.SetRegionRects(NonClientRegionKind.Passthrough, rects.Select(r => new RectInt32(
                (int)Math.Round(r.X * scale),
                (int)Math.Round(r.Y * scale),
                (int)Math.Round(r.Width * scale),
                (int)Math.Round(r.Height * scale))).ToArray());
        });
    }

    private Task<T> OnUiThreadAsync<T>(Func<Task<T>> work)
    {
        var tcs = new TaskCompletionSource<T>();
        DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                tcs.TrySetResult(await work());
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });
        return tcs.Task;
    }
}
