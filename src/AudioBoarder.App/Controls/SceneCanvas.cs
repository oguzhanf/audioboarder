using System.Windows;
using System.Windows.Input;
using AudioBoarder.Core.Scene;
using AudioBoarder.Services.Rendering;
using SkiaSharp;
using SkiaSharp.Views.Desktop;
using SkiaSharp.Views.WPF;

namespace AudioBoarder.App.Controls;

/// <summary>
/// Hosts an <see cref="SKElement"/> and delegates painting to
/// <see cref="SceneRenderer"/>. Supports mouse drag to lock node positions.
/// </summary>
public class SceneCanvas : SKElement
{
    public static readonly DependencyProperty SceneProperty = DependencyProperty.Register(
        nameof(Scene), typeof(SceneGraph), typeof(SceneCanvas),
        new PropertyMetadata(null, (d, _) => ((SceneCanvas)d).InvalidateVisual()));

    public static readonly DependencyProperty RendererProperty = DependencyProperty.Register(
        nameof(Renderer), typeof(SceneRenderer), typeof(SceneCanvas),
        new PropertyMetadata(null, (d, _) => ((SceneCanvas)d).InvalidateVisual()));

    public SceneGraph? Scene
    {
        get => (SceneGraph?)GetValue(SceneProperty);
        set => SetValue(SceneProperty, value);
    }

    public SceneRenderer? Renderer
    {
        get => (SceneRenderer?)GetValue(RendererProperty);
        set => SetValue(RendererProperty, value);
    }

    // View transform: screen(px) = pan + zoom * world.
    private float _zoom = 1f;
    private float _panX;
    private float _panY;

    private SceneNode? _draggingNode;
    private double _grabOffsetX;
    private double _grabOffsetY;

    private bool _panning;
    private System.Windows.Point _panStart;
    private float _panStartX;
    private float _panStartY;

    public SceneCanvas()
    {
        PaintSurface += OnPaint;
        MouseLeftButtonDown += OnMouseDown;
        MouseMove += OnMouseMove;
        MouseLeftButtonUp += OnMouseUp;
        MouseRightButtonDown += OnPanStart;
        MouseRightButtonUp += OnPanEnd;
        MouseWheel += OnWheel;
        MouseDown += OnAnyMouseDown;
        Cursor = Cursors.Arrow;
    }

    private void OnPaint(object? sender, SKPaintSurfaceEventArgs e)
    {
        var canvas = e.Surface.Canvas;
        canvas.Clear(SKColors.White);
        if (Scene is null) return;

        canvas.Save();
        canvas.Translate(_panX, _panY);
        canvas.Scale(_zoom);
        var renderer = Renderer ?? new SceneRenderer();
        renderer.Render(canvas, Scene, e.Info.Width, e.Info.Height);
        canvas.Restore();
    }

    public void Invalidate() => InvalidateVisual();

    /// <summary>WPF device-independent units → SkiaSharp surface pixels.</summary>
    private (float sx, float sy) ToSurface(System.Windows.Point p)
    {
        var scaleX = ActualWidth > 0 ? (float)(CanvasSize.Width / ActualWidth) : 1f;
        var scaleY = ActualHeight > 0 ? (float)(CanvasSize.Height / ActualHeight) : 1f;
        return ((float)p.X * scaleX, (float)p.Y * scaleY);
    }

    /// <summary>Mouse point → world (scene) coordinates accounting for zoom/pan/DPI.</summary>
    private (double wx, double wy) ToWorld(System.Windows.Point p)
    {
        var (sx, sy) = ToSurface(p);
        return ((sx - _panX) / _zoom, (sy - _panY) / _zoom);
    }

    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        var (sx, sy) = ToSurface(e.GetPosition(this));
        var factor = e.Delta > 0 ? 1.1f : 1f / 1.1f;
        var newZoom = Math.Clamp(_zoom * factor, 0.2f, 6f);
        if (Math.Abs(newZoom - _zoom) < 1e-4f) return;
        // Keep the world point under the cursor fixed on screen.
        _panX = sx - (sx - _panX) * (newZoom / _zoom);
        _panY = sy - (sy - _panY) * (newZoom / _zoom);
        _zoom = newZoom;
        InvalidateVisual();
    }

    private void OnAnyMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) // double-click resets the view
        {
            _zoom = 1f; _panX = 0f; _panY = 0f;
            InvalidateVisual();
        }
    }

    private void OnPanStart(object sender, MouseButtonEventArgs e)
    {
        _panning = true;
        _panStart = e.GetPosition(this);
        _panStartX = _panX;
        _panStartY = _panY;
        CaptureMouse();
        Cursor = Cursors.ScrollAll;
    }

    private void OnPanEnd(object sender, MouseButtonEventArgs e)
    {
        if (!_panning) return;
        _panning = false;
        ReleaseMouseCapture();
        Cursor = Cursors.Arrow;
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (Scene is null) return;
        var (wx, wy) = ToWorld(e.GetPosition(this));
        var node = HitTest(wx, wy);
        if (node is null) return;
        _draggingNode = node;
        _grabOffsetX = (node.X ?? 0) - wx;
        _grabOffsetY = (node.Y ?? 0) - wy;
        CaptureMouse();
        Cursor = Cursors.Hand;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_panning)
        {
            var (sx, sy) = ToSurface(e.GetPosition(this));
            var (ox, oy) = ToSurface(_panStart);
            _panX = _panStartX + (sx - ox);
            _panY = _panStartY + (sy - oy);
            InvalidateVisual();
            return;
        }
        if (_draggingNode is null) return;
        var (wx, wy) = ToWorld(e.GetPosition(this));
        _draggingNode.X = wx + _grabOffsetX;
        _draggingNode.Y = wy + _grabOffsetY;
        InvalidateVisual();
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_draggingNode is null) return;
        _draggingNode.Locked = true;
        _draggingNode = null;
        ReleaseMouseCapture();
        Cursor = Cursors.Arrow;
        InvalidateVisual();
    }

    private SceneNode? HitTest(double wx, double wy)
    {
        if (Scene is null) return null;
        lock (Scene.SyncRoot)
        {
            foreach (var node in Scene.Nodes.Values)
            {
                if (!node.X.HasValue || !node.Y.HasValue) continue;
                var hw = node.Width / 2;
                var hh = node.Height / 2;
                if (wx >= node.X - hw && wx <= node.X + hw && wy >= node.Y - hh && wy <= node.Y + hh)
                    return node;
            }
        }
        return null;
    }
}
