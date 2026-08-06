using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.OS;
using Android.Util;
using Android.Views;
using Android.Widget;
using AndroidX.AppCompat.App;
using AndroidX.Camera.Core;
using AndroidX.Camera.Lifecycle;
using AndroidX.Camera.View;
using AndroidX.Core.Content;
using Java.Util.Concurrent;
using ZXing;
using CameraPreview = AndroidX.Camera.Core.Preview;
using ZXingResult = ZXing.Result;
using AndroidResult = Android.App.Result;
using View = Android.Views.View;
using Color = Android.Graphics.Color;
using Button = Android.Widget.Button;
using Orientation = Android.Widget.Orientation;

namespace IPasswrd.Mobile.Platforms.Android.Services;

/// <summary>
/// Полноэкранный сканер QR: CameraX даёт превью и поток кадров, ZXing декодирует
/// яркостную плоскость каждого кадра. Визуально повторяет iOS-версию:
/// латунная рамка по центру, подсказка и кнопка «Отмена» внизу.
/// </summary>
[Activity(
    Label = "Сканирование кода",
    Theme = "@style/Theme.AppCompat.NoActionBar",
    ScreenOrientation = ScreenOrientation.Portrait,
    Exported = false,
    ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation
        | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.Density)]
public class QrScanActivity : AppCompatActivity
{
    public const string ExtraResult = "ipw.qr.result";

    private PreviewView? _previewView;
    private IExecutorService? _analysisExecutor;
    private ProcessCameraProvider? _provider;
    private int _done;   // 0/1 — код мог прилететь несколько раз, пока камера останавливается

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        Window?.SetFlags(WindowManagerFlags.Secure, WindowManagerFlags.Secure);

        SetContentView(BuildUi());

        _analysisExecutor = Executors.NewSingleThreadExecutor();
        StartCamera();
    }

    // ================= интерфейс =================

    private View BuildUi()
    {
        var root = new FrameLayout(this) { LayoutParameters = Match() };
        root.SetBackgroundColor(Color.Black);

        _previewView = new PreviewView(this) { LayoutParameters = Match() };
        root.AddView(_previewView);

        // латунная рамка кадрирования (#E1B85E), сторона ~62% меньшей стороны экрана
        var metrics = Resources?.DisplayMetrics;
        int screenW = metrics?.WidthPixels ?? 1080;
        int screenH = metrics?.HeightPixels ?? 1920;
        int side = (int)(Math.Min(screenW, screenH) * 0.62);

        var frameDrawable = new GradientDrawable();
        frameDrawable.SetShape(ShapeType.Rectangle);
        frameDrawable.SetCornerRadius(Dp(14));
        frameDrawable.SetStroke(Dp(2), Color.Argb(255, 225, 184, 94));
        frameDrawable.SetColor(Color.Transparent);

        var frame = new View(this)
        {
            LayoutParameters = new FrameLayout.LayoutParams(side, side, GravityFlags.Center),
        };
        frame.Background = frameDrawable;
        root.AddView(frame);

        var hint = new TextView(this)
        {
            Text = "Наведите камеру на QR-код",
            TextAlignment = global::Android.Views.TextAlignment.Center,
            LayoutParameters = new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent,
                GravityFlags.Bottom | GravityFlags.CenterHorizontal)
            {
                BottomMargin = Dp(112),
                LeftMargin = Dp(20),
                RightMargin = Dp(20),
            },
        };
        hint.SetTextColor(Color.White);
        hint.SetTextSize(ComplexUnitType.Sp, 15);
        hint.Gravity = GravityFlags.Center;
        root.AddView(hint);

        var cancelBg = new GradientDrawable();
        cancelBg.SetShape(ShapeType.Rectangle);
        cancelBg.SetCornerRadius(Dp(12));
        cancelBg.SetColor(Color.Argb(40, 255, 255, 255));

        var cancel = new Button(this)
        {
            Text = "Отмена",
            LayoutParameters = new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent, Dp(48),
                GravityFlags.Bottom | GravityFlags.CenterHorizontal)
            {
                BottomMargin = Dp(32),
                LeftMargin = Dp(20),
                RightMargin = Dp(20),
            },
        };
        cancel.SetTextColor(Color.White);
        cancel.Background = cancelBg;
        cancel.SetAllCaps(false);
        cancel.Click += (_, _) => Finish(null);
        root.AddView(cancel);

        return root;
    }

    private static FrameLayout.LayoutParams Match() => new(
        ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent);

    private int Dp(double value)
    {
        float density = Resources?.DisplayMetrics?.Density ?? 2f;
        return (int)Math.Round(value * density);
    }

    // ================= камера =================

    private void StartCamera()
    {
        try
        {
            var future = ProcessCameraProvider.GetInstance(this);
            future.AddListener(new Java.Lang.Runnable(() =>
            {
                try
                {
                    if (future.Get() is not ProcessCameraProvider provider) { Finish(null); return; }
                    _provider = provider;
                    provider.UnbindAll();

                    var preview = new CameraPreview.Builder().Build();
                    // в этой версии биндинга у SetSurfaceProvider только перегрузка с executor
                    preview.SetSurfaceProvider(ContextCompat.GetMainExecutor(this)!, _previewView!.SurfaceProvider);

                    var analysis = new ImageAnalysis.Builder()
                        .SetBackpressureStrategy(ImageAnalysis.StrategyKeepOnlyLatest)
                        .Build();
                    analysis.SetAnalyzer(_analysisExecutor!, new QrAnalyzer(OnDecoded));

                    provider.BindToLifecycle(this, CameraSelector.DefaultBackCamera, preview, analysis);
                }
                catch (Exception)
                {
                    Finish(null);
                }
            }), ContextCompat.GetMainExecutor(this)!);
        }
        catch (Exception)
        {
            Finish(null);
        }
    }

    private void OnDecoded(string text)
    {
        if (Interlocked.Exchange(ref _done, 1) == 1) return;
        RunOnUiThread(() =>
        {
            try { _previewView?.PerformHapticFeedback(FeedbackConstants.LongPress); } catch (Exception) { }
            FinishCore(text);
        });
    }

    private void Finish(string? text)
    {
        if (Interlocked.Exchange(ref _done, 1) == 1) return;
        RunOnUiThread(() => FinishCore(text));
    }

    private void FinishCore(string? text)
    {
        try { _provider?.UnbindAll(); } catch (Exception) { }

        if (text is null)
        {
            SetResult(AndroidResult.Canceled);
        }
        else
        {
            var data = new Intent();
            data.PutExtra(ExtraResult, text);
            SetResult(AndroidResult.Ok, data);
        }
        Finish();
    }

    protected override void OnDestroy()
    {
        try { _provider?.UnbindAll(); } catch (Exception) { }
        try { _analysisExecutor?.Shutdown(); } catch (Exception) { }
        base.OnDestroy();
    }

    // ================= декодер =================

    private sealed class QrAnalyzer : Java.Lang.Object, ImageAnalysis.IAnalyzer
    {
        private readonly Action<string> _onDecoded;
        private readonly BarcodeReaderGeneric _reader;

        public QrAnalyzer(Action<string> onDecoded)
        {
            _onDecoded = onDecoded;
            _reader = new BarcodeReaderGeneric
            {
                AutoRotate = true,
                Options = new ZXing.Common.DecodingOptions
                {
                    TryHarder = true,
                    PossibleFormats = new List<BarcodeFormat> { BarcodeFormat.QR_CODE },
                },
            };
        }

        public void Analyze(IImageProxy image)
        {
            try
            {
                var planes = image.GetPlanes();
                if (planes is null || planes.Length == 0) return;

                Java.Nio.ByteBuffer? buffer = planes[0].Buffer;
                if (buffer is null) return;

                int rowStride = planes[0].RowStride;
                int width = image.Width;
                int height = image.Height;

                buffer.Rewind();
                byte[] bytes = new byte[buffer.Remaining()];
                buffer.Get(bytes);

                // Y-плоскость YUV_420_888 — готовый источник яркости для ZXing.
                // Ширина строки в буфере — rowStride, а не width (бывает выравнивание).
                int dataWidth = rowStride > 0 ? rowStride : width;
                if (dataWidth < width || (long)dataWidth * height > bytes.Length) return;

                var source = new global::ZXing.PlanarYUVLuminanceSource(
                    bytes, dataWidth, height, 0, 0, width, height, false);

                ZXingResult? result = _reader.Decode(source);
                if (result is not null && !string.IsNullOrEmpty(result.Text))
                    _onDecoded(result.Text);
            }
            catch (Exception)
            {
                // битый кадр — просто ждём следующий
            }
            finally
            {
                try { image.Close(); } catch (Exception) { }
            }
        }
    }
}
