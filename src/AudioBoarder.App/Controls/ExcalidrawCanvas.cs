using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using AudioBoarder.Core.Excalidraw;
using AudioBoarder.Core.Scene;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Debug = System.Diagnostics.Debug;

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
    public event EventHandler<ExcalidrawSceneChangedEventArgs>? UserSceneChanged;

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
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("type", out var t))
                return;

            switch (t.GetString())
            {
                case "ready":
                    _ready = true;
                    if (_pendingJson is not null)
                    {
                        _web.CoreWebView2.PostWebMessageAsString(_pendingJson);
                        _pendingJson = null;
                    }
                    break;
                case "scene-change":
                    if (TryParseSceneChange(doc.RootElement, out var change))
                        UserSceneChanged?.Invoke(this, new ExcalidrawSceneChangedEventArgs(change));
                    else
                        Debug.WriteLine("Ignoring malformed Excalidraw scene-change message.");
                    break;
                case "error":
                    if (doc.RootElement.TryGetProperty("message", out var message) &&
                        message.ValueKind == JsonValueKind.String)
                        Debug.WriteLine("Excalidraw page error: " + message.GetString());
                    break;
            }
        }
        catch (JsonException ex)
        {
            Debug.WriteLine("Ignoring malformed Excalidraw message: " + ex.Message);
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
            int revision;
            lock (Scene.SyncRoot)
            {
                json = _converter.ConvertToJson(Scene);
                revision = Scene.Revision;
            }
            json = AttachSceneRevision(json, revision);
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

    private static string AttachSceneRevision(string json, int revision)
    {
        using var doc = JsonDocument.Parse(json);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                if (!property.NameEquals("sceneRevision"))
                    property.WriteTo(writer);
            }
            writer.WriteNumber("sceneRevision", revision);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static bool TryParseSceneChange(JsonElement root, out ExcalidrawSceneChange change)
    {
        change = default!;
        if (!root.TryGetProperty("elements", out var elementsElement) ||
            elementsElement.ValueKind != JsonValueKind.Array)
            return false;

        var elements = new List<ExcalidrawSceneElementChange>();
        foreach (var item in elementsElement.EnumerateArray())
        {
            if (TryParseElementChange(item, out var parsed))
                elements.Add(parsed);
        }

        var appState = root.TryGetProperty("appState", out var appStateElement)
            ? ParseAppState(appStateElement)
            : new ExcalidrawSceneAppState(null, null, null);
        var viewport = root.TryGetProperty("viewport", out var viewportElement)
            ? ParseViewport(viewportElement)
            : new ExcalidrawViewport(0d, 0d, 1d, null, null);
        var revision = TryGetInt32(root, "sceneRevision");

        change = new ExcalidrawSceneChange(elements, appState, viewport, revision);
        return true;
    }

    private static bool TryParseElementChange(JsonElement element, out ExcalidrawSceneElementChange change)
    {
        change = default!;
        if (element.ValueKind != JsonValueKind.Object)
            return false;

        var id = TryGetString(element, "id");
        var type = TryGetString(element, "type");
        var x = TryGetDouble(element, "x");
        var y = TryGetDouble(element, "y");
        var width = TryGetDouble(element, "width");
        var height = TryGetDouble(element, "height");

        if (id is null || type is null || x is null || y is null || width is null || height is null)
            return false;

        change = new ExcalidrawSceneElementChange(
            id,
            type,
            x.Value,
            y.Value,
            width.Value,
            height.Value,
            TryGetBoolean(element, "locked") ?? false,
            TryGetBoolean(element, "isDeleted") ?? false,
            TryGetString(element, "frameId"),
            TryGetString(element, "containerId"));

        return true;
    }

    private static ExcalidrawSceneAppState ParseAppState(JsonElement element) => new(
        TryGetString(element, "viewBackgroundColor"),
        TryGetString(element, "theme"),
        TryGetBoolean(element, "zenModeEnabled"));

    private static ExcalidrawViewport ParseViewport(JsonElement element) => new(
        TryGetDouble(element, "scrollX") ?? 0d,
        TryGetDouble(element, "scrollY") ?? 0d,
        TryGetDouble(element, "zoom") ?? 1d,
        TryGetDouble(element, "width"),
        TryGetDouble(element, "height"));

    private static string? TryGetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool? TryGetBoolean(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            ? value.GetBoolean()
            : null;

    private static double? TryGetDouble(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.Number &&
        value.TryGetDouble(out var number)
            ? number
            : null;

    private static int? TryGetInt32(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out var number)
            ? number
            : null;
}

public sealed class ExcalidrawSceneChangedEventArgs(ExcalidrawSceneChange change) : EventArgs
{
    public ExcalidrawSceneChange Change { get; } = change;
}

public sealed record ExcalidrawSceneChange(
    IReadOnlyList<ExcalidrawSceneElementChange> Elements,
    ExcalidrawSceneAppState AppState,
    ExcalidrawViewport Viewport,
    int? SceneRevision);

public sealed record ExcalidrawSceneElementChange(
    string Id,
    string Type,
    double X,
    double Y,
    double Width,
    double Height,
    bool Locked,
    bool IsDeleted,
    string? FrameId,
    string? ContainerId);

public sealed record ExcalidrawSceneAppState(
    string? ViewBackgroundColor,
    string? Theme,
    bool? ZenModeEnabled);

public sealed record ExcalidrawViewport(
    double ScrollX,
    double ScrollY,
    double Zoom,
    double? Width,
    double? Height);
