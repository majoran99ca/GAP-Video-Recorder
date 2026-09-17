using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenCvSharp;
using OpenCvSharp.WpfExtensions;
using Size = OpenCvSharp.Size;
using CvRect = OpenCvSharp.Rect;
using Point = System.Windows.Point;

namespace GapVideoRecorder;

public partial class MainWindow : System.Windows.Window
{
    private const string AppVersion = "1.0.4";
    private const string AboutText = "GAP Video recorder is a free purpose built easy to use software to simplify video recordings with easy to use controls and virtually no settings to change.\nVideo codec, resolution and bitrates are pre-set for optimum balance of quality and file size.\n\nGAP Video recorder is free to use, copy and distribute under GPL-3.0 license.\n\nThis software is still being tested and evaluated and may not be a final version.\nIf you have any questions or suggestions, please contact me at majoran99ca@gmail.com";
    private const int FrameRate = 30;
    private const int OutputWidth = 1920;
    private const int OutputHeight = 1080;
    private const int RecordingCrf = 20;
    private const double MinimumTileSize = 80;
    private const double MinimumVisibleTileArea = 40;
    private const double MaximumTileSize = 7680;
    private static readonly string LayoutPath = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GAP Video Recorder", "layout.json");
    private readonly ObservableCollection<CameraChoice> cameras = new();
    private readonly Dictionary<int, CameraFeed> feeds = new();
    private readonly List<CameraTile> tiles = new();
    private readonly System.Windows.Threading.DispatcherTimer recordTimer;
    private readonly System.Windows.Threading.DispatcherTimer elapsedTimer;
    private readonly Stopwatch recordingStopwatch = new();
    private FfmpegEncoder? encoder;
    private bool recording;
    private bool stopping;
    private long framesWritten;
    private Point dragStart;
    private CameraTile? movingTile;
    private CameraTile? selectedTile;
    private ResizeEdges resizeEdges;

    public MainWindow()
    {
        InitializeComponent();
        CameraList.ItemsSource = cameras;
        recordTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1d / FrameRate) };
        recordTimer.Tick += (_, _) => WriteFrame();
        elapsedTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        elapsedTimer.Tick += (_, _) => TimerText.Text = recordingStopwatch.Elapsed.ToString(@"hh\:mm\:ss");
        RefreshCameras();
        RestoreLayout();
    }

    private void RefreshCameras()
    {
        // DirectShow indexes are stable for the running session and work with ordinary USB webcams.
        for (var index = 0; index < 10; index++)
        {
            using var probe = new VideoCapture(index, VideoCaptureAPIs.DSHOW);
            if (probe.IsOpened() && cameras.All(camera => camera.Index != index))
                cameras.Add(new CameraChoice(index, $"Camera {index + 1}"));
        }
        if (cameras.Count == 0)
            StatusText.Text = "No cameras found. Connect a camera and allow desktop apps in Windows camera privacy settings.";
    }

    private void RefreshCameras_Click(object sender, RoutedEventArgs e)
    {
        var countBefore = cameras.Count;
        RefreshCameras();
        StatusText.Text = cameras.Count == countBefore
            ? "No new cameras found."
            : $"Found {cameras.Count - countBefore} new camera(s).";
    }

    private void CameraList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (CameraList.SelectedItem is CameraChoice camera)
            AddCamera(camera, new Point(0.5, 0.5));
    }

    private void CameraList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || CameraList.SelectedItem is not CameraChoice camera)
            return;
        DragDrop.DoDragDrop(CameraList, new DataObject(typeof(CameraChoice), camera), DragDropEffects.Copy);
    }

    private void OutputCanvas_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = !recording && e.Data.GetDataPresent(typeof(CameraChoice)) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OutputCanvas_Drop(object sender, DragEventArgs e)
    {
        if (recording || e.Data.GetData(typeof(CameraChoice)) is not CameraChoice camera)
            return;
        var point = e.GetPosition(OutputCanvas);
        AddCamera(camera, new Point(point.X / OutputCanvas.Width, point.Y / OutputCanvas.Height));
    }

    private void AddCamera(CameraChoice camera, Point center, SavedCameraLayout? savedLayout = null)
    {
        var existing = tiles.FirstOrDefault(tile => tile.Feed.Index == camera.Index);
        if (existing is not null)
        {
            SelectTile(existing);
            return;
        }
        if (!feeds.TryGetValue(camera.Index, out var feed))
        {
            feed = new CameraFeed(camera.Index, camera.DisplayName, Dispatcher, image =>
            {
                var tile = tiles.FirstOrDefault(item => item.Feed == feed);
                if (tile is not null)
                    tile.SetPreview(image);
            }, message => StatusText.Text = message);
            feeds.Add(camera.Index, feed);
            feed.Start();
        }
        var width = savedLayout?.Width ?? 760;
        var height = savedLayout?.Height ?? 430;
        var left = savedLayout?.Left ?? center.X * OutputCanvas.Width - width / 2;
        var top = savedLayout?.Top ?? center.Y * OutputCanvas.Height - height / 2;
        width = Math.Clamp(width, MinimumTileSize, MaximumTileSize);
        height = Math.Clamp(height, MinimumTileSize, MaximumTileSize);
        var tile = new CameraTile(feed, left, top, width, height);
        KeepTileReachable(tile);
        tile.ApplyLayout();
        tile.Frame.MouseLeftButtonDown += Tile_MouseLeftButtonDown;
        tile.Frame.MouseMove += Tile_MouseMove;
        tile.Frame.MouseLeftButtonUp += Tile_MouseLeftButtonUp;
        tile.SetRotated180(savedLayout?.Rotated180 ?? false);
        var rotationItem = new MenuItem { Header = "Rotate 180°", IsCheckable = true, IsChecked = tile.Rotated180 };
        rotationItem.Click += (_, _) =>
        {
            tile.SetRotated180(rotationItem.IsChecked);
            SaveLayout();
        };
        tile.Frame.ContextMenu = new ContextMenu { Items = { rotationItem } };
        OutputCanvas.Children.Add(tile.Frame);
        tiles.Add(tile);
        SelectTile(tile);
        OutputCanvas.Focus();
    }

    private void CloseMenuItem_Click(object sender, RoutedEventArgs e) => Close();

    private void AboutMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new System.Windows.Window
        {
            Owner = this,
            Title = "About GAP Video Recorder",
            Width = 470,
            Height = 350,
            MinWidth = 470,
            MinHeight = 350,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(Color.FromRgb(23, 26, 30)),
            Foreground = Brushes.White,
            Icon = new BitmapImage(new Uri("pack://application:,,,/assets/app-icon.ico")),
        };
        var content = new StackPanel { Margin = new Thickness(24) };
        content.Children.Add(new TextBlock
        {
            Text = "GAP Video Recorder",
            FontSize = 22,
            FontWeight = FontWeights.SemiBold,
        });
        content.Children.Add(new TextBlock
        {
            Text = $"Version {AppVersion}",
            Foreground = new SolidColorBrush(Color.FromRgb(190, 199, 208)),
            Margin = new Thickness(0, 4, 0, 18),
        });
        content.Children.Add(new TextBlock
        {
            Text = AboutText,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(220, 225, 230)),
        });
        var ok = new Button
        {
            Content = "OK",
            Width = 80,
            IsDefault = true,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 20, 0, 0),
            Padding = new Thickness(12, 5, 12, 5),
        };
        ok.Click += (_, _) => dialog.Close();
        content.Children.Add(ok);
        dialog.Content = content;
        dialog.ShowDialog();
    }

    private void RestoreLayout()
    {
        try
        {
            if (!File.Exists(LayoutPath))
                return;
            var layout = JsonSerializer.Deserialize<SavedLayout>(File.ReadAllText(LayoutPath));
            if (layout?.Cameras is null)
                return;
            foreach (var savedCamera in layout.Cameras)
            {
                if (!double.IsFinite(savedCamera.Left) || !double.IsFinite(savedCamera.Top) ||
                    !double.IsFinite(savedCamera.Width) || !double.IsFinite(savedCamera.Height))
                    continue;
                var camera = cameras.FirstOrDefault(item => item.Index == savedCamera.CameraIndex);
                if (camera is not null)
                    AddCamera(camera, new Point(0.5, 0.5), savedCamera);
            }
            if (tiles.Count > 0)
                StatusText.Text = "Restored the previous camera layout.";
        }
        catch
        {
            // A missing or damaged preference file should never prevent the recorder from opening.
        }
    }

    private void SaveLayout()
    {
        try
        {
            var folder = System.IO.Path.GetDirectoryName(LayoutPath)!;
            Directory.CreateDirectory(folder);
            var layout = new SavedLayout
            {
                Cameras = tiles.Select(tile => new SavedCameraLayout
                {
                    CameraIndex = tile.Feed.Index,
                    Left = tile.Left,
                    Top = tile.Top,
                    Width = tile.Width,
                    Height = tile.Height,
                    Rotated180 = tile.Rotated180,
                }).ToList(),
            };
            var temporaryPath = LayoutPath + ".new";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(layout, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporaryPath, LayoutPath, true);
        }
        catch
        {
            // Layout persistence is optional and must not interrupt recording or camera use.
        }
    }

    private void SelectTile(CameraTile? selected, bool rememberSelection = true)
    {
        if (rememberSelection)
            selectedTile = selected;
        foreach (var tile in tiles)
            tile.SetSelected(tile == selected && !recording);
    }

    private void Tile_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (recording || sender is not Border border || tiles.FirstOrDefault(tile => tile.Frame == border) is not { } tile)
            return;
        SelectTile(tile);
        OutputCanvas.Focus();
        movingTile = tile;
        dragStart = e.GetPosition(OutputCanvas);
        var pointer = e.GetPosition(border);
        resizeEdges = ResizeEdges.None;
        if (pointer.X <= 34)
            resizeEdges |= ResizeEdges.Left;
        else if (pointer.X >= border.ActualWidth - 34)
            resizeEdges |= ResizeEdges.Right;
        if (pointer.Y <= 34)
            resizeEdges |= ResizeEdges.Top;
        else if (pointer.Y >= border.ActualHeight - 34)
            resizeEdges |= ResizeEdges.Bottom;
        border.CaptureMouse();
        e.Handled = true;
    }

    private void Tile_MouseMove(object sender, MouseEventArgs e)
    {
        if (recording || movingTile is null || e.LeftButton != MouseButtonState.Pressed)
            return;
        var point = e.GetPosition(OutputCanvas);
        var dx = point.X - dragStart.X;
        var dy = point.Y - dragStart.Y;
        if (resizeEdges != ResizeEdges.None)
        {
            ResizeTile(movingTile, dx, dy);
        }
        else
        {
            movingTile.Left += dx;
            movingTile.Top += dy;
            KeepTileReachable(movingTile);
        }
        movingTile.ApplyLayout();
        dragStart = point;
    }

    private void Tile_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border border)
            border.ReleaseMouseCapture();
        if (movingTile is not null)
            SaveLayout();
        movingTile = null;
        resizeEdges = ResizeEdges.None;
    }

    private void ResizeTile(CameraTile tile, double dx, double dy)
    {
        if (resizeEdges.HasFlag(ResizeEdges.Left))
        {
            var right = tile.Left + tile.Width;
            tile.Left = Math.Min(right - MinimumTileSize, tile.Left + dx);
            tile.Width = Math.Clamp(right - tile.Left, MinimumTileSize, MaximumTileSize);
            tile.Left = right - tile.Width;
        }
        else if (resizeEdges.HasFlag(ResizeEdges.Right))
        {
            tile.Width = Math.Clamp(tile.Width + dx, MinimumTileSize, MaximumTileSize);
        }
        if (resizeEdges.HasFlag(ResizeEdges.Top))
        {
            var bottom = tile.Top + tile.Height;
            tile.Top = Math.Min(bottom - MinimumTileSize, tile.Top + dy);
            tile.Height = Math.Clamp(bottom - tile.Top, MinimumTileSize, MaximumTileSize);
            tile.Top = bottom - tile.Height;
        }
        else if (resizeEdges.HasFlag(ResizeEdges.Bottom))
        {
            tile.Height = Math.Clamp(tile.Height + dy, MinimumTileSize, MaximumTileSize);
        }
        KeepTileReachable(tile);
    }

    private void KeepTileReachable(CameraTile tile)
    {
        tile.Left = Math.Clamp(tile.Left, -tile.Width + MinimumVisibleTileArea, OutputCanvas.Width - MinimumVisibleTileArea);
        tile.Top = Math.Clamp(tile.Top, -tile.Height + MinimumVisibleTileArea, OutputCanvas.Height - MinimumVisibleTileArea);
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (recording || e.Key is not (Key.Delete or Key.Back))
            return;
        if (DeleteSelectedTile())
            e.Handled = true;
    }

    private void OutputCanvas_KeyDown(object sender, KeyEventArgs e)
    {
        if (recording || e.Key is not (Key.Delete or Key.Back))
            return;
        if (DeleteSelectedTile())
            e.Handled = true;
    }

    private bool DeleteSelectedTile()
    {
        var selected = selectedTile;
        if (selected is null || !tiles.Contains(selected))
            return false;
        OutputCanvas.Children.Remove(selected.Frame);
        tiles.Remove(selected);
        selected.Feed.Stop();
        feeds.Remove(selected.Feed.Index);
        selectedTile = null;
        SaveLayout();
        return true;
    }

    private void RecordButton_Click(object sender, RoutedEventArgs e)
    {
        if (tiles.Count == 0)
        {
            StatusText.Text = "Add at least one camera to the canvas before recording.";
            return;
        }
        var folder = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        Directory.CreateDirectory(folder);
        var path = System.IO.Path.Combine(folder, $"GAP_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.mp4");
        try
        {
            encoder = new FfmpegEncoder(path, OutputWidth, OutputHeight, FrameRate, RecordingCrf);
        }
        catch (Exception exception)
        {
            encoder?.Dispose();
            encoder = null;
            MessageBox.Show(exception.Message, "Could not start H.264 recording", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        recording = true;
        SelectTile(null, rememberSelection: false);
        CameraList.IsEnabled = false;
        RecordButton.IsEnabled = false;
        StopButton.IsEnabled = true;
        framesWritten = 0;
        recordingStopwatch.Restart();
        elapsedTimer.Start();
        recordTimer.Start();
        StatusText.Text = $"Recording H.264 (1080p, high quality) to {path}";
    }

    private void StopButton_Click(object sender, RoutedEventArgs e) => StopRecording();

    private void StopRecording()
    {
        if (!recording || stopping)
            return;
        stopping = true;
        recordTimer.Stop();
        var finalFrameCount = Math.Max(1, (long)Math.Floor(recordingStopwatch.Elapsed.TotalSeconds * FrameRate));
        recordingStopwatch.Stop();
        elapsedTimer.Stop();
        // A composition frame can take longer than the timer interval.  Write copies
        // of the latest composition up to the elapsed wall-clock time so ffmpeg's
        // fixed 30 fps timeline remains aligned with the on-screen timer.
        WriteFrame(finalFrameCount);
        try
        {
            encoder?.Finish();
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, "Recording could not finish", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        encoder?.Dispose();
        encoder = null;
        recording = false;
        CameraList.IsEnabled = true;
        RecordButton.IsEnabled = true;
        StopButton.IsEnabled = false;
        if (selectedTile is not null && tiles.Contains(selectedTile))
            SelectTile(selectedTile, rememberSelection: false);
        StatusText.Text = "Saved H.264 MP4 to Videos.";
        stopping = false;
    }

    private void WriteFrame(long? requestedFrameCount = null)
    {
        if (!recording || encoder is null)
            return;
        var targetFrameCount = requestedFrameCount ?? (long)Math.Floor(recordingStopwatch.Elapsed.TotalSeconds * FrameRate);
        if (targetFrameCount <= framesWritten)
            return;
        using var output = new Mat(OutputHeight, OutputWidth, MatType.CV_8UC3, Scalar.Black);
        foreach (var tile in tiles)
        {
            using var source = tile.Feed.CopyLatestFrame();
            if (source is null || source.Empty())
                continue;
            using var rotated = new Mat();
            var sourceForOutput = source;
            if (tile.Rotated180)
            {
                Cv2.Rotate(source, rotated, RotateFlags.Rotate180);
                sourceForOutput = rotated;
            }
            var fullRect = tile.FullOutputRect(OutputWidth, OutputHeight);
            var rect = IntersectWithCanvas(fullRect, OutputWidth, OutputHeight);
            if (rect is null)
                continue;
            using var sourceCrop = CropToAspect(sourceForOutput, fullRect.Width / (double)fullRect.Height);
            using var resized = new Mat();
            Cv2.Resize(sourceCrop, resized, new Size(fullRect.Width, fullRect.Height), 0, 0, InterpolationFlags.Area);
            var sourceRect = new CvRect(rect.Value.X - fullRect.X, rect.Value.Y - fullRect.Y, rect.Value.Width, rect.Value.Height);
            using var sourceRegion = new Mat(resized, sourceRect);
            using var outputRegion = new Mat(output, rect.Value);
            sourceRegion.CopyTo(outputRegion);
        }
        var bytes = new byte[checked((int)(output.Total() * output.ElemSize()))];
        Marshal.Copy(output.Data, bytes, 0, bytes.Length);
        try
        {
            while (framesWritten < targetFrameCount)
            {
                encoder.Write(bytes);
                framesWritten++;
            }
        }
        catch (Exception exception)
        {
            if (!stopping)
            {
                StopRecording();
                MessageBox.Show(exception.Message, "Recording stopped", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private static Mat CropToAspect(Mat source, double targetAspect)
    {
        var sourceAspect = source.Width / (double)source.Height;
        if (sourceAspect > targetAspect)
        {
            var width = (int)Math.Round(source.Height * targetAspect);
            return new Mat(source, new CvRect((source.Width - width) / 2, 0, width, source.Height)).Clone();
        }
        var height = (int)Math.Round(source.Width / targetAspect);
        return new Mat(source, new CvRect(0, (source.Height - height) / 2, source.Width, height)).Clone();
    }

    private static CvRect? IntersectWithCanvas(CvRect source, int canvasWidth, int canvasHeight)
    {
        var left = Math.Max(0, source.X);
        var top = Math.Max(0, source.Y);
        var right = Math.Min(canvasWidth, source.X + source.Width);
        var bottom = Math.Min(canvasHeight, source.Y + source.Height);
        return right > left && bottom > top ? new CvRect(left, top, right - left, bottom - top) : null;
    }

    protected override void OnClosed(EventArgs e)
    {
        StopRecording();
        SaveLayout();
        foreach (var feed in feeds.Values)
            feed.Stop();
        base.OnClosed(e);
    }
}

public sealed class FfmpegEncoder : IDisposable
{
    private readonly Process process;
    private readonly Stream input;
    private bool finished;

    public FfmpegEncoder(string outputPath, int width, int height, int frameRate, int crf)
    {
        var executable = System.IO.Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
        if (!File.Exists(executable))
            throw new FileNotFoundException("The bundled H.264 encoder (ffmpeg.exe) is missing.", executable);
        var info = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        info.ArgumentList.Add("-y");
        info.ArgumentList.Add("-loglevel");
        info.ArgumentList.Add("error");
        info.ArgumentList.Add("-f");
        info.ArgumentList.Add("rawvideo");
        info.ArgumentList.Add("-pix_fmt");
        info.ArgumentList.Add("bgr24");
        info.ArgumentList.Add("-video_size");
        info.ArgumentList.Add($"{width}x{height}");
        info.ArgumentList.Add("-framerate");
        info.ArgumentList.Add(frameRate.ToString());
        info.ArgumentList.Add("-i");
        info.ArgumentList.Add("pipe:0");
        info.ArgumentList.Add("-an");
        info.ArgumentList.Add("-c:v");
        info.ArgumentList.Add("libx264");
        // yuv420p is the H.264 color format supported by Windows' built-in players.
        info.ArgumentList.Add("-pix_fmt");
        info.ArgumentList.Add("yuv420p");
        info.ArgumentList.Add("-preset");
        info.ArgumentList.Add("veryfast");
        info.ArgumentList.Add("-crf");
        info.ArgumentList.Add(crf.ToString());
        info.ArgumentList.Add("-movflags");
        info.ArgumentList.Add("+faststart");
        info.ArgumentList.Add(outputPath);
        process = Process.Start(info) ?? throw new InvalidOperationException("Could not start the bundled H.264 encoder.");
        input = process.StandardInput.BaseStream;
    }

    public void Write(byte[] frame)
    {
        if (process.HasExited)
            throw new InvalidOperationException($"The H.264 encoder stopped unexpectedly: {process.StandardError.ReadToEnd()}");
        input.Write(frame, 0, frame.Length);
    }

    public void Finish()
    {
        if (finished)
            return;
        finished = true;
        input.Close();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"The H.264 encoder reported: {process.StandardError.ReadToEnd()}");
    }

    public void Dispose()
    {
        if (!finished && !process.HasExited)
        {
            input.Close();
            process.WaitForExit(5000);
        }
        input.Dispose();
        process.Dispose();
    }
}

public sealed record CameraChoice(int Index, string DisplayName);

[Flags]
public enum ResizeEdges
{
    None = 0,
    Left = 1,
    Top = 2,
    Right = 4,
    Bottom = 8,
}

public sealed class SavedLayout
{
    public List<SavedCameraLayout> Cameras { get; set; } = new();
}

public sealed class SavedCameraLayout
{
    public int CameraIndex { get; set; }
    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public bool Rotated180 { get; set; }
}

public sealed class CameraFeed
{
    private readonly System.Windows.Threading.Dispatcher dispatcher;
    private readonly Action<BitmapSource> onImage;
    private readonly Action<string> onError;
    private readonly object sync = new();
    private CancellationTokenSource? cancellation;
    private Mat? latest;
    public int Index { get; }
    public string Name { get; }

    public CameraFeed(int index, string name, System.Windows.Threading.Dispatcher dispatcher, Action<BitmapSource> onImage, Action<string> onError)
    {
        Index = index; Name = name; this.dispatcher = dispatcher; this.onImage = onImage; this.onError = onError;
    }

    public void Start()
    {
        cancellation = new CancellationTokenSource();
        _ = Task.Run(() => CaptureLoop(cancellation.Token));
    }

    private void CaptureLoop(CancellationToken token)
    {
        using var camera = new VideoCapture(Index, VideoCaptureAPIs.DSHOW);
        if (!camera.IsOpened())
        {
            dispatcher.Invoke(() => onError($"Could not open {Name}. It may be in use by another application."));
            return;
        }
        camera.Set(VideoCaptureProperties.FourCC, FourCC.FromFourChars('M', 'J', 'P', 'G'));
        camera.Set(VideoCaptureProperties.FrameWidth, 1280);
        camera.Set(VideoCaptureProperties.FrameHeight, 720);
        camera.Set(VideoCaptureProperties.Fps, 30);
        while (!token.IsCancellationRequested)
        {
            using var frame = new Mat();
            if (!camera.Read(frame) || frame.Empty())
            {
                Thread.Sleep(20);
                continue;
            }
            lock (sync)
            {
                latest?.Dispose();
                latest = frame.Clone();
            }
            var preview = BitmapSourceConverter.ToBitmapSource(frame);
            preview.Freeze();
            dispatcher.BeginInvoke(() => onImage(preview));
        }
    }

    public Mat? CopyLatestFrame()
    {
        lock (sync)
            return latest?.Clone();
    }

    public void Stop()
    {
        cancellation?.Cancel();
        lock (sync)
        {
            latest?.Dispose();
            latest = null;
        }
    }
}

public sealed class CameraTile
{
    public CameraFeed Feed { get; }
    public Border Frame { get; }
    public Image Preview { get; }
    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public bool IsSelected { get; private set; }
    public bool Rotated180 { get; private set; }
    private BitmapSource? latestPreview;

    public CameraTile(CameraFeed feed, double left, double top, double width, double height)
    {
        Feed = feed; Left = left; Top = top; Width = width; Height = height;
        Preview = new Image { Stretch = Stretch.UniformToFill, SnapsToDevicePixels = true };
        var grid = new Grid { ClipToBounds = true, UseLayoutRounding = true };
        grid.Children.Add(Preview);
        grid.Children.Add(CreateResizeHandle(HorizontalAlignment.Left, VerticalAlignment.Top));
        grid.Children.Add(CreateResizeHandle(HorizontalAlignment.Right, VerticalAlignment.Top));
        grid.Children.Add(CreateResizeHandle(HorizontalAlignment.Left, VerticalAlignment.Bottom));
        grid.Children.Add(CreateResizeHandle(HorizontalAlignment.Right, VerticalAlignment.Bottom));
        Frame = new Border { Child = grid, BorderBrush = Brushes.SlateGray, BorderThickness = new Thickness(3), Background = Brushes.Black };
        ApplyLayout();
    }

    public void SetSelected(bool selected)
    {
        IsSelected = selected;
        Frame.BorderBrush = selected ? Brushes.DeepSkyBlue : Brushes.SlateGray;
        Frame.BorderThickness = new Thickness(selected ? 4 : 2);
    }

    public void SetRotated180(bool rotated)
    {
        Rotated180 = rotated;
        if (latestPreview is not null)
            RenderPreview(latestPreview);
    }

    public void SetPreview(BitmapSource image)
    {
        latestPreview = image;
        RenderPreview(image);
    }

    private void RenderPreview(BitmapSource image)
    {
        if (!Rotated180)
        {
            Preview.Source = image;
            return;
        }
        var transformed = new TransformedBitmap(image, new RotateTransform(180));
        transformed.Freeze();
        Preview.Source = transformed;
    }

    public void ApplyLayout()
    {
        Frame.Width = Width; Frame.Height = Height;
        Canvas.SetLeft(Frame, Left); Canvas.SetTop(Frame, Top);
    }

    private static System.Windows.Shapes.Rectangle CreateResizeHandle(HorizontalAlignment horizontal, VerticalAlignment vertical)
        => new()
        {
            Width = 20,
            Height = 20,
            Fill = Brushes.DeepSkyBlue,
            HorizontalAlignment = horizontal,
            VerticalAlignment = vertical,
        };

    public CvRect FullOutputRect(int canvasWidth, int canvasHeight)
    {
        var x = (int)Math.Round(Left / 1920 * canvasWidth);
        var y = (int)Math.Round(Top / 1080 * canvasHeight);
        var width = Math.Max(1, (int)Math.Round(Width / 1920 * canvasWidth));
        var height = Math.Max(1, (int)Math.Round(Height / 1080 * canvasHeight));
        return new CvRect(x, y, width, height);
    }
}
