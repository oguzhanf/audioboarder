using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using AudioBoarder.Core.Excalidraw;
using AudioBoarder.Core.Scene;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace AudioBoarder.App.Controls;

/// <summary>
/// Hosts the real Excalidraw web app inside a <see cref="WebView2"/> and renders the
/// current <see cref="SceneGraph"/> as a hand-drawn whiteboard. The vendored bundle in
/// <c>Assets/web</c> is served via a virtual-host folder mapping (fully offline); scenes
/// are pushed to it as <c>.excalidraw</c> JSON over the WebView2 message channel.
/// </summary>
public sealed class ExcalidrawCanvas : UserControl
{
    private const string VirtualHost = "audioboarder.local";

    private readonly WebView2 _web = new();
    private readonly SceneToExcalidrawConverter _converter = new();
    private bool _ready;
    private string? _pendingJson;
    private bool _initFailed;

    public SceneGraph? Scene { get; set; }

    public ExcalidrawCanvas()
    {
        Content = _web;
        _web.DefaultBackgroundColor = System.Drawing.Color.White;
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_web.CoreWebView2 is not null || _initFailed) return;
        try
        {
            var dataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AudioBoarder", "webview2");
            Directory.CreateDirectory(dataDir);

            var env = await CoreWebView2Environment.CreateAsync(null, dataDir);
            await _web.EnsureCoreWebView2Async(env);

            var core = _web.CoreWebView2;
            if (core is null)
            {
                _initFailed = true;
                ShowFallback("WebView2 core unavailable.");
                return;
            }
            var assetDir = Path.Combine(AppContext.BaseDirectory, "Assets", "web");
            core.SetVirtualHostNameToFolderMapping(
                VirtualHost, assetDir, CoreWebView2HostResourceAccessKind.Allow);

            // Lock the embedded browser down: it only ever serves our local bundle.
            core.Settings.AreDefaultContextMenusEnabled = true;
            core.Settings.IsZoomControlEnabled = true;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;

            core.WebMessageReceived += OnWebMessageReceived;
            _web.Source = new Uri($"https://{VirtualHost}/index.html");
        }
        catch (Exception ex)
        {
            _initFailed = true;
            ShowFallback(ex.Message);
        }
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            if (doc.RootElement.TryGetProperty("type", out var t) && t.GetString() == "ready")
            {
                _ready = true;
                if (_pendingJson is not null)
                {
                    _web.CoreWebView2.PostWebMessageAsString(_pendingJson);
                    _pendingJson = null;
                }
            }
        }
        catch
        {
            /* ignore malformed messages from the page */
        }
    }

    /// <summary>Re-serialise the current scene and push it to the whiteboard.</summary>
    public void Refresh()
    {
        if (Scene is null || _initFailed) return;
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(Refresh);
            return;
        }

        string json;
        try
        {
            // The converter locks SceneGraph.SyncRoot internally, so this is safe even
            // while a background patch is mutating the graph.
            json = _converter.ConvertToJson(Scene);
        }
        catch
        {
            return; // never let a transient scene state crash the UI
        }

        if (_ready && _web.CoreWebView2 is not null)
            _web.CoreWebView2.PostWebMessageAsString(json);
        else
            _pendingJson = json;
    }

    private void ShowFallback(string detail)
    {
        Content = new TextBlock
        {
            Margin = new Thickness(24),
            TextWrapping = TextWrapping.Wrap,
            Text = "The Excalidraw whiteboard could not start (WebView2 runtime issue). " +
                   "Switch to Classic view from the toolbar.\n\nDetails: " + detail,
        };
    }
}
