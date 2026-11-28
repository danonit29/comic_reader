using System;
using Android.App;
using Android.Content.PM;
using Android.Graphics;
using Android.OS;
using Android.Views;
using AndroidX.Core.View;
using Avalonia;
using Avalonia.Android;
using ComicReader;
using ComicReader.Android.Services;

namespace ComicReader.Android
{
    [Activity(
        Label = "ComicReader",
        Theme = "@style/MyTheme.NoActionBar",
        Icon = "@drawable/icon",
        MainLauncher = true,
        ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode)]
    public class MainActivity : AvaloniaMainActivity<App>
    {
        private bool _isNativePinching;
        private bool _blockLeftoverFingerUntilUp;
        private double _pinchStartSpan;

        protected override void OnCreate(Bundle? savedInstanceState)
        {
            base.OnCreate(savedInstanceState);
            ApplySafeSystemBars();
            App.ReaderOpenStateChanged += OnReaderOpenStateChanged;
        }

        protected override void OnResume()
        {
            base.OnResume();
            ApplySafeSystemBars();
        }

        protected override void OnDestroy()
        {
            App.ReaderOpenStateChanged -= OnReaderOpenStateChanged;
            base.OnDestroy();
        }

        public override bool DispatchTouchEvent(MotionEvent? ev)
        {
            if (ev is null || !App.IsReaderOpen)
            {
                _isNativePinching = false;
                _blockLeftoverFingerUntilUp = false;
                return base.DispatchTouchEvent(ev);
            }

            MotionEventActions action = ev.ActionMasked;
            float density = Resources?.DisplayMetrics?.Density ?? 1f;
            if (density <= 0f) density = 1f;

            if (action == MotionEventActions.Down)
            {
                _isNativePinching = false;
                _blockLeftoverFingerUntilUp = false;
                return base.DispatchTouchEvent(ev);
            }

            if (_blockLeftoverFingerUntilUp)
            {
                if (action == MotionEventActions.PointerDown && ev.PointerCount >= 2)
                {
                    _blockLeftoverFingerUntilUp = false;
                    StartNativePinch(ev, density);
                    return true;
                }

                if (action == MotionEventActions.Up || action == MotionEventActions.Cancel)
                {
                    _blockLeftoverFingerUntilUp = false;
                }
                return true;
            }

            if (!_isNativePinching && ev.PointerCount >= 2 &&
                (action == MotionEventActions.PointerDown || action == MotionEventActions.Move))
            {
                // Abort any active 1-finger scroll inside Avalonia ScrollViewer cleanly before pinching.
                using MotionEvent? cancelEv = MotionEvent.Obtain(ev);
                if (cancelEv is not null)
                {
                    cancelEv.Action = MotionEventActions.Cancel;
                    base.DispatchTouchEvent(cancelEv);
                }

                StartNativePinch(ev, density);
                return true;
            }

            if (_isNativePinching)
            {
                if ((action == MotionEventActions.PointerUp && ev.PointerCount <= 2) ||
                    action == MotionEventActions.Up ||
                    action == MotionEventActions.Cancel)
                {
                    _isNativePinching = false;
                    _blockLeftoverFingerUntilUp = action == MotionEventActions.PointerUp;
                    App.NotifyAndroidPinchEnded();
                    return true;
                }

                if (action == MotionEventActions.Move && ev.PointerCount >= 2 && _pinchStartSpan > 1.0)
                {
                    (double span, double midX, double midY) = ComputeTwoFingerGeometry(ev, density);
                    if (span > 8.0)
                    {
                        double ratio = span / _pinchStartSpan;
                        App.NotifyAndroidPinchUpdated(ratio, midX, midY);
                    }
                    return true;
                }

                return true;
            }

            return base.DispatchTouchEvent(ev);
        }

        private void StartNativePinch(MotionEvent ev, float density)
        {
            (double span, double midX, double midY) = ComputeTwoFingerGeometry(ev, density);
            _pinchStartSpan = Math.Max(span, 20.0);
            _isNativePinching = true;
            App.NotifyAndroidPinchStarted(midX, midY);
        }

        private static (double Span, double MidXDip, double MidYDip) ComputeTwoFingerGeometry(MotionEvent ev, float density)
        {
            double x0 = ev.GetX(0) / density;
            double y0 = ev.GetY(0) / density;
            double x1 = ev.GetX(1) / density;
            double y1 = ev.GetY(1) / density;
            double dx = x1 - x0;
            double dy = y1 - y0;
            double span = Math.Sqrt(dx * dx + dy * dy);
            return (span, (x0 + x1) * 0.5, (y0 + y1) * 0.5);
        }

        private void OnReaderOpenStateChanged(bool isReaderOpen)
        {
            RunOnUiThread(() =>
            {
                if (Window is not { } window) return;
                if (isReaderOpen)
                {
                    window.AddFlags(WindowManagerFlags.KeepScreenOn);
                }
                else
                {
                    window.ClearFlags(WindowManagerFlags.KeepScreenOn);
                }
            });
        }

        private void ApplySafeSystemBars()
        {
            if (Window is not { } window) return;

            WindowCompat.SetDecorFitsSystemWindows(window, false);
            window.ClearFlags(WindowManagerFlags.TranslucentStatus | WindowManagerFlags.TranslucentNavigation);
            window.AddFlags(WindowManagerFlags.DrawsSystemBarBackgrounds);

            Color darkBg = Color.ParseColor("#141414");
            window.DecorView.SetBackgroundColor(darkBg);

#pragma warning disable CA1422
            window.SetStatusBarColor(Color.Transparent);
            window.SetNavigationBarColor(darkBg);
#pragma warning restore CA1422

            var insetsController = new WindowInsetsControllerCompat(window, window.DecorView);
            insetsController.AppearanceLightStatusBars = false;
            insetsController.AppearanceLightNavigationBars = false;

            float density = Resources?.DisplayMetrics?.Density ?? 1f;
            ViewCompat.SetOnApplyWindowInsetsListener(window.DecorView, new SystemBarsInsetsListener(density));
            ViewCompat.RequestApplyInsets(window.DecorView);
        }

        private sealed class SystemBarsInsetsListener : Java.Lang.Object, IOnApplyWindowInsetsListener
        {
            private readonly float _density;

            public SystemBarsInsetsListener(float density)
            {
                _density = density > 0f ? density : 1f;
            }

            public WindowInsetsCompat OnApplyWindowInsets(global::Android.Views.View v, WindowInsetsCompat insets)
            {
                var bars = insets.GetInsets(
                    WindowInsetsCompat.Type.SystemBars() | WindowInsetsCompat.Type.DisplayCutout());

                if (bars is not null)
                {
                    double left = bars.Left / (double)_density;
                    double top = bars.Top / (double)_density;
                    double right = bars.Right / (double)_density;
                    double bottom = bars.Bottom / (double)_density;
                    App.NotifyAndroidSafeAreaChanged(new global::Avalonia.Thickness(left, top, right, bottom));
                }

                return ViewCompat.OnApplyWindowInsets(v, insets) ?? insets;
            }
        }

        protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
        {
            App.NativePdfRendererFactory = static () => new AndroidNativePdfRenderer();
            return base.CustomizeAppBuilder(builder)
                .WithInterFont();
        }
    }
}
