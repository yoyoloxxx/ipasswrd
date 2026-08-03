using AVFoundation;
using CoreFoundation;
using CoreGraphics;
using Foundation;
using IPasswrd.Mobile.Services;
using UIKit;

namespace IPasswrd.Mobile.Platforms.iOS.Services;

/// <summary>Нативный сканер QR-кодов на AVFoundation (без сторонних библиотек).</summary>
public sealed class QrScannerIos : IQrScanner
{
    public async Task<string?> ScanAsync()
    {
        var status = AVCaptureDevice.GetAuthorizationStatus(AVAuthorizationMediaType.Video);
        if (status == AVAuthorizationStatus.NotDetermined)
        {
            bool granted = await AVCaptureDevice.RequestAccessForMediaTypeAsync(AVAuthorizationMediaType.Video);
            if (!granted) return null;
        }
        else if (status != AVAuthorizationStatus.Authorized)
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                var top = Platform.GetCurrentUIViewController();
                if (top is null) return;
                var alert = UIAlertController.Create("Нет доступа к камере",
                    "Разрешите доступ: Настройки → IPasswrd → Камера.", UIAlertControllerStyle.Alert);
                alert.AddAction(UIAlertAction.Create("Ок", UIAlertActionStyle.Default, null));
                top.PresentViewController(alert, true, null);
            });
            return null;
        }

        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);

        MainThread.BeginInvokeOnMainThread(() =>
        {
            var host = Platform.GetCurrentUIViewController();
            if (host is null) { tcs.TrySetResult(null); return; }
            var vc = new ScannerViewController(tcs)
            {
                ModalPresentationStyle = UIModalPresentationStyle.FullScreen,
            };
            host.PresentViewController(vc, true, null);
        });

        return await tcs.Task;
    }
}

internal sealed class ScannerViewController : UIViewController
{
    private readonly TaskCompletionSource<string?> _tcs;
    private AVCaptureSession? _session;
    private AVCaptureVideoPreviewLayer? _preview;
    private MetadataDelegate? _delegate;
    private bool _done;

    public ScannerViewController(TaskCompletionSource<string?> tcs) => _tcs = tcs;

    public override void ViewDidLoad()
    {
        base.ViewDidLoad();
        View!.BackgroundColor = UIColor.Black;

        var session = new AVCaptureSession();
        AVCaptureDevice? device = AVCaptureDevice.GetDefaultDevice(AVMediaTypes.Video);
        if (device is null) { Finish(null); return; }

        AVCaptureDeviceInput? input = AVCaptureDeviceInput.FromDevice(device, out NSError? error);
        if (input is null || error is not null || !session.CanAddInput(input)) { Finish(null); return; }
        session.AddInput(input);

        var output = new AVCaptureMetadataOutput();
        if (!session.CanAddOutput(output)) { Finish(null); return; }
        session.AddOutput(output);
        _delegate = new MetadataDelegate(OnCode);
        output.SetDelegate(_delegate, DispatchQueue.MainQueue);
        output.MetadataObjectTypes = AVMetadataObjectType.QRCode;

        _preview = new AVCaptureVideoPreviewLayer(session)
        {
            Frame = View.Bounds,
            VideoGravity = AVLayerVideoGravity.ResizeAspectFill,
        };
        View.Layer.AddSublayer(_preview);

        AddOverlay();

        _session = session;
        DispatchQueue.GetGlobalQueue(DispatchQueuePriority.Default).DispatchAsync(session.StartRunning);
    }

    private void AddOverlay()
    {
        double w = (double)View!.Bounds.Width;
        double h = (double)View.Bounds.Height;

        double side = Math.Min(w, h) * 0.62;
        var frameView = new UIView(new CGRect((w - side) / 2, (h - side) / 2, side, side))
        {
            BackgroundColor = UIColor.Clear,
        };
        frameView.Layer.BorderColor = new UIColor(0.882f, 0.722f, 0.369f, 1f).CGColor;   // латунь #E1B85E
        frameView.Layer.BorderWidth = 2;
        frameView.Layer.CornerRadius = 14;
        View.AddSubview(frameView);

        var hint = new UILabel(new CGRect(20, h - 160, w - 40, 44))
        {
            Text = "Наведите камеру на QR-код",
            TextAlignment = UITextAlignment.Center,
            TextColor = UIColor.White,
            Font = UIFont.SystemFontOfSize(15),
            Lines = 2,
        };
        View.AddSubview(hint);

        var cancel = UIButton.FromType(UIButtonType.System);
        cancel.Frame = new CGRect(20, h - 104, w - 40, 48);
        cancel.SetTitle("Отмена", UIControlState.Normal);
        cancel.SetTitleColor(UIColor.White, UIControlState.Normal);
        cancel.BackgroundColor = new UIColor(1f, 1f, 1f, 0.16f);
        cancel.Layer.CornerRadius = 12;
        cancel.TouchUpInside += (_, _) => Finish(null);
        View.AddSubview(cancel);
    }

    public override void ViewDidLayoutSubviews()
    {
        base.ViewDidLayoutSubviews();
        if (_preview is not null && View is not null) _preview.Frame = View.Bounds;
    }

    private void OnCode(string value)
    {
#pragma warning disable CA1422 // конструктор устарел только с iOS 17.5; на 15+ работает
        var gen = new UIImpactFeedbackGenerator(UIImpactFeedbackStyle.Medium);
#pragma warning restore CA1422
        gen.Prepare();
        gen.ImpactOccurred();
        Finish(value);
    }

    private void Finish(string? result)
    {
        if (_done) return;
        _done = true;

        AVCaptureSession? session = _session;
        _session = null;
        if (session is not null)
            DispatchQueue.GetGlobalQueue(DispatchQueuePriority.Default).DispatchAsync(session.StopRunning);

        MainThread.BeginInvokeOnMainThread(() =>
            DismissViewController(true, () => _tcs.TrySetResult(result)));
    }

    public override void ViewDidDisappear(bool animated)
    {
        base.ViewDidDisappear(animated);
        _tcs.TrySetResult(null);   // смахнули шторку — отмена (если код ещё не считан)
    }

    private sealed class MetadataDelegate : AVCaptureMetadataOutputObjectsDelegate
    {
        private readonly Action<string> _onCode;
        public MetadataDelegate(Action<string> onCode) => _onCode = onCode;

        public override void DidOutputMetadataObjects(AVCaptureMetadataOutput captureOutput, AVMetadataObject[] metadataObjects, AVCaptureConnection connection)
        {
            foreach (AVMetadataObject obj in metadataObjects)
            {
                if (obj is AVMetadataMachineReadableCodeObject code && !string.IsNullOrEmpty(code.StringValue))
                {
                    _onCode(code.StringValue);
                    return;
                }
            }
        }
    }
}
