using DeejNG.Models;
using DeejNG.Classes;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using SkiaSharp;
using SkiaSharp.Views.Desktop;
using SkiaSharp.Views.WPF;
using DeejNG.Views;


namespace DeejNG.Dialogs
{
    public partial class ChannelControl : UserControl
    {
        #region Private Fields
        private const int SegmentCount = 15;
        private readonly float SegmentSpacing = 4f;

        private SKRect[] _segmentRects = Array.Empty<SKRect>();
        private int _cachedWidth = 0;
        private int _cachedHeight = 0;
        private FloatingOverlay _overlay;

        private float _previousMeterLevel = 0f;
        private float _peakFade = 1f;

        private const float ClipThreshold = 0.98f;

        private DispatcherTimer _skiaRedrawTimer;
        
        // Cached paint objects to avoid creating new ones every frame
        private readonly SKPaint _basePaint = new()
        {
            Style = SKPaintStyle.Fill,
            IsAntialias = true
        };

        private readonly SKPaint _glassPaint = new()
        {
            Style = SKPaintStyle.Fill,
            IsAntialias = true
        };

        private readonly SKPaint _borderPaint = new()
        {
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1f,
            IsAntialias = true
        };

        private readonly SKPaint _peakPaint = new()
        {
            Color = SKColors.White,
            Style = SKPaintStyle.Fill,
            IsAntialias = true
        };
        
        // Cache segment rectangles
        private bool _segmentRectsCalculated = false;
        private int _lastCanvasWidth = 0;
        private int _lastCanvasHeight = 0;
        
        // Meter update optimization
        private DispatcherTimer _meterUpdateTimer;
        private bool _meterNeedsUpdate = false;
        private readonly object _meterLock = new object();
        
        // Pre-calculated segment colors
        private static readonly SKColor[] SegmentColors = new SKColor[]
        {
            new SKColor(80, 255, 80, 180),   // Green 0-11
            new SKColor(80, 255, 80, 180),
            new SKColor(80, 255, 80, 180),
            new SKColor(80, 255, 80, 180),
            new SKColor(80, 255, 80, 180),
            new SKColor(80, 255, 80, 180),
            new SKColor(80, 255, 80, 180),
            new SKColor(80, 255, 80, 180),
            new SKColor(80, 255, 80, 180),
            new SKColor(80, 255, 80, 180),
            new SKColor(80, 255, 80, 180),
            new SKColor(80, 255, 80, 180),
            new SKColor(255, 220, 50, 180), // Yellow 12-17
            new SKColor(255, 220, 50, 180),
            new SKColor(255, 220, 50, 180),
            new SKColor(255, 220, 50, 180),
            new SKColor(255, 220, 50, 180),
            new SKColor(255, 220, 50, 180),
            new SKColor(255, 60, 60, 180),   // Red 18-19
            new SKColor(255, 60, 60, 180)
        };

        private const float SmoothingFactor = 0.3f;

        private readonly Brush _muteOffBrush = Brushes.Gray;

        private readonly Brush _muteOnBrush = new SolidColorBrush(Color.FromRgb(255, 12, 12));

        private readonly TimeSpan PeakHoldDuration = TimeSpan.FromMilliseconds(500); // was 1000ms


        private List<AudioTarget> _audioTargets = new();

        private bool _isMuted = false;

        private bool _layoutReady = false;

        private float _meterLevel;

        private float _peakLevel;

        private DateTime _peakTimestamp;

        private float _smoothedVolume;

        private bool _suppressEvents = false;

        #endregion Private Fields

        #region Public Constructors

        // Bright red
        public ChannelControl()
        {
            InitializeComponent();
            Loaded += ChannelControl_Loaded;
            Unloaded += ChannelControl_Unloaded;
            MouseDoubleClick += ChannelControl_MouseDoubleClick;
            
            // Replace CompositionTarget.Rendering with controlled timer for better performance
            _meterUpdateTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(25) // Very high FPS for ultra-smooth visuals
            };
            _meterUpdateTimer.Tick += MeterUpdateTimer_Tick;
            _meterUpdateTimer.Start();
        }

        #endregion Public Constructors

        #region Public Events

        // New event for notifying the parent window when a session is disconnected
        public event EventHandler<string> SessionDisconnected;

        public event EventHandler TargetChanged;

        public event Action<List<AudioTarget>, float, bool> VolumeOrMuteChanged;

        #endregion Public Events

        #region Public Properties

        public List<AudioTarget> AudioTargets
        {
            get => _audioTargets;
            set
            {
                _audioTargets = value ?? new List<AudioTarget>();
                UpdateTargetsDisplay();
            }
        }

        public float CurrentVolume => (float)VolumeSlider.Value;

        // Add this public property to expose the InputModeCheckBox
        public CheckBox InputModeCheckBoxControl => InputModeCheckBox;
        public bool IsInputMode
        {
            get => _audioTargets.Any(t => t.IsInputDevice);
            set
            {
                InputModeCheckBox.IsChecked = value;
                // If checked and we don't have any input devices,
                // we should present the picker dialog
            }
        }
        public bool IsMuted => _isMuted;
        public string TargetExecutable =>
             _audioTargets.FirstOrDefault()?.Name ?? "";

        #endregion Public Properties

        #region Public Methods

        /// <summary>
        /// Handles when an audio session is disconnected
        /// </summary>
        public void HandleSessionDisconnected()
        {
            // If we were controlling this session exclusively
            if (_audioTargets.Count == 1 && !_audioTargets[0].IsInputDevice && !_audioTargets[0].IsOutputDevice)
            {
                string target = _audioTargets[0].Name;
                Debug.WriteLine($"[Session] Session disconnected for {target}");

                // Notify the parent window
                SessionDisconnected?.Invoke(this, target);

                // Reset the meter level
                _meterLevel = 0;
                UpdateAudioMeter(0);

                // Visual indicator that the session is no longer active
                TargetTextBox.Foreground = Brushes.Gray;
                TargetTextBox.ToolTip = $"{TargetTextBox.Text} (Disconnected)";
            }
        }

        /// <summary>
        /// Handles when an audio session has expired
        /// </summary>
        public void HandleSessionExpired()
        {
            // Similar to disconnected, but may want different visual treatment
            if (_audioTargets.Count == 1 && !_audioTargets[0].IsInputDevice && !_audioTargets[0].IsOutputDevice)
            {
                string target = _audioTargets[0].Name;
                Debug.WriteLine($"[Session] Session expired for {target}");

                // Reset the meter
                _meterLevel = 0;
                UpdateAudioMeter(0);

                // Visual indicator
                TargetTextBox.Foreground = Brushes.Gray;
                TargetTextBox.ToolTip = $"{TargetTextBox.Text} (Expired)";
            }
        }

        /// <summary>
        /// Resets the connection state for the control's audio targets
        /// </summary>
        public void ResetConnectionState()
        {
            // Reset any disconnected/expired visual indicators
            TargetTextBox.Foreground = TryFindResource("MaterialDesign.Brush.Foreground") as Brush ?? Brushes.Black;
            if (TargetTextBox.ToolTip is string tooltip &&
                (tooltip.EndsWith("(Disconnected)") || tooltip.EndsWith("(Expired)")))
            {
                // Reset the tooltip to just show the targets
                UpdateTargetsDisplay();
            }
        }

        public void SetMeterVisibility(bool visible)
        {
            // MeterVisuals.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            SkiaCanvas.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }


        public void SetMuted(bool muted)
        {
            _suppressEvents = true;
            _isMuted = muted;
            MuteButton.IsChecked = muted;
            UpdateMuteButtonVisual();
            _suppressEvents = false;
        }

        public void SetTargetExecutable(string target)
        {
            if (string.IsNullOrWhiteSpace(target))
            {
                _audioTargets.Clear();
            }
            else
            {
                _audioTargets = new List<AudioTarget>
                {
                    new AudioTarget { Name = target, IsInputDevice = IsInputMode }
                };
            }

            UpdateTargetsDisplay();
            UpdateMuteButtonEnabled();
        }
        public void SetTargets(List<AudioTarget> targets)
        {
            _audioTargets = targets ?? new List<AudioTarget>();
            UpdateTargetsDisplay();
            UpdateMuteButtonEnabled();
        

        }

        public void SetVolume(float level)
        {
            _suppressEvents = true; // ✅ prevent events
            VolumeSlider.Value = level;
            _suppressEvents = false;
        }


        public void SmoothAndSetVolume(float rawLevel, bool suppressEvent = false, bool disableSmoothing = false)
        {
            _suppressEvents = suppressEvent;
            if (disableSmoothing)
            {
                SetVolume(rawLevel);
            }
            else
            {
                _smoothedVolume = _smoothedVolume == 0 ? rawLevel : _smoothedVolume + (rawLevel - _smoothedVolume) * SmoothingFactor;
                SetVolume(_smoothedVolume);
            }
            _suppressEvents = false;
        }



        private void MeterUpdateTimer_Tick(object sender, EventArgs e)
        {
            lock (_meterLock)
            {
                if (_meterNeedsUpdate)
                {
                    SkiaCanvas?.InvalidateVisual();
                    _meterNeedsUpdate = false;
                }
            }
        }

        public void UpdateAudioMeter(float rawLevel)
        {
            const float noiseFloor = 0.02f;
            bool hasSignificantChange = false;

            if (rawLevel < noiseFloor)
            {
                if (_meterLevel > 0.001f) // Very sensitive - instant response when audio stops
                {
                    _meterLevel = 0f;
                    _peakLevel = 0f;
                    _peakFade = 0f;
                    hasSignificantChange = true;
                }
            }
            else
            {
                float oldMeterLevel = _meterLevel;
                
                // Much faster rise, moderate fall for responsiveness
                if (rawLevel > _meterLevel)
                    _meterLevel = rawLevel; // Instant rise for maximum responsiveness
                else
                    _meterLevel += (rawLevel - _meterLevel) * 0.8f; // Faster fall than before

                // Very sensitive change detection for maximum responsiveness
                if (Math.Abs(oldMeterLevel - _meterLevel) > 0.005f) // Much more sensitive
                    hasSignificantChange = true;

                if (rawLevel > _peakLevel || DateTime.Now - _peakTimestamp > PeakHoldDuration)
                {
                    _peakLevel = rawLevel;
                    _peakTimestamp = DateTime.Now;
                    _peakFade = 1f;
                    hasSignificantChange = true;
                }

                _peakFade -= 0.05f;
                _peakFade = Math.Clamp(_peakFade, 0f, 1f);
            }

            // Only flag for update if there's a significant change
            if (hasSignificantChange)
            {
                lock (_meterLock)
                {
                    _meterNeedsUpdate = true;
                }
            }
        }

        private void SkiaCanvas_PaintSurface(object sender, SKPaintSurfaceEventArgs e)
        {
            var canvas = e.Surface.Canvas;
            var info = e.Info;
            canvas.Clear(SKColors.Transparent);

            int segmentCount = 20;
            
            // Only recalculate rectangles if canvas size changed
            if (!_segmentRectsCalculated || _lastCanvasWidth != info.Width || _lastCanvasHeight != info.Height)
            {
                RecalculateSegmentRects(info, segmentCount);
                _lastCanvasWidth = info.Width;
                _lastCanvasHeight = info.Height;
                _segmentRectsCalculated = true;
            }

            float activeHeight = _meterLevel * info.Height;
            float cornerRadius = _segmentRects.Length > 0 ? _segmentRects[0].Height / 2.5f : 2f;

            for (int i = 0; i < segmentCount && i < _segmentRects.Length; i++)
            {
                var rect = _segmentRects[i];
                bool isActive = info.Height - rect.Top <= activeHeight;

                // Use pre-calculated colors or inactive color
                SKColor baseColor = isActive && i < SegmentColors.Length 
                    ? SegmentColors[i] 
                    : SKColors.Gray.WithAlpha(50);
                
                // Use cached paint object, just update color
                _basePaint.Color = baseColor;
                canvas.DrawRoundRect(rect, cornerRadius, cornerRadius, _basePaint);

                // Only draw glossy highlight for active segments
                if (isActive)
                {
                    DrawGlossyHighlight(canvas, rect, cornerRadius);
                }

                // Only draw border for active segments to reduce overdraw
                if (isActive)
                {
                    _borderPaint.Color = SKColors.White.WithAlpha(40);
                    canvas.DrawRoundRect(rect, cornerRadius, cornerRadius, _borderPaint);
                }
            }

            // Peak marker (only if visible)
            if (_peakLevel > 0.01f)
            {
                float peakY = info.Height - (_peakLevel * info.Height);
                canvas.DrawRect(new SKRect(0, peakY, info.Width, peakY + 2), _peakPaint);
            }
        }

        private void RecalculateSegmentRects(SKImageInfo info, int segmentCount)
        {
            if (_segmentRects.Length != segmentCount)
                _segmentRects = new SKRect[segmentCount];

            float gap = info.Height * 0.005f;
            float segmentHeight = (info.Height - (segmentCount - 1) * gap) / segmentCount;
            float segmentWidth = info.Width;

            for (int i = 0; i < segmentCount; i++)
            {
                float y = info.Height - ((i + 1) * segmentHeight + i * gap);
                _segmentRects[i] = new SKRect(0, y, segmentWidth, y + segmentHeight);
            }
        }

        private void DrawGlossyHighlight(SKCanvas canvas, SKRect rect, float cornerRadius)
        {
            var glossRect = new SKRect(rect.Left, rect.Top, rect.Right, rect.Top + rect.Height * 0.4f);
            
            using var shader = SKShader.CreateLinearGradient(
                new SKPoint(glossRect.Left, glossRect.Top),
                new SKPoint(glossRect.Left, glossRect.Bottom),
                new[] { SKColors.White.WithAlpha(90), SKColors.Transparent },
                null,
                SKShaderTileMode.Clamp);

            _glassPaint.Shader = shader;
            canvas.DrawRoundRect(glossRect, cornerRadius, cornerRadius, _glassPaint);
            _glassPaint.Shader = null; // Clear shader reference
        }


        #endregion Public Methods

        #region Private Methods

        private void ChannelControl_Loaded(object sender, RoutedEventArgs e)
        {
            _layoutReady = true;
            UpdateMuteButtonVisual();
        }

        private void ChannelControl_Unloaded(object sender, RoutedEventArgs e)
        {
            // Stop the meter update timer
            _meterUpdateTimer?.Stop();
            _meterUpdateTimer = null;
            
            // Dispose paint objects to free resources
            _basePaint?.Dispose();
            _glassPaint?.Dispose();
            _borderPaint?.Dispose();
            _peakPaint?.Dispose();
            
            // Cleanup is now handled by the decoupled architecture in MainWindow
        }

        private void ChannelControl_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            // Open the multi-target picker instead of the single target picker
            var picker = new MultiTargetPickerDialog(_audioTargets)
            {
                Owner = Application.Current.MainWindow,
                WindowStartupLocation = WindowStartupLocation.Manual
            };

            if (picker.Owner != null)
            {
                var mainWindow = picker.Owner;
                picker.Left = mainWindow.Left + (mainWindow.Width - picker.Width) / 2;
                picker.Top = mainWindow.Top + (mainWindow.Height - picker.Height) / 2;
            }

            if (picker.ShowDialog() == true)
            {
                _audioTargets = picker.SelectedTargets;
                UpdateTargetsDisplay();
                TargetChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        private void InputModeCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            RaiseTargetChanged(); // You already track state via IsInputMode
        }

        private void InputModeCheckBox_Unchecked(object sender, RoutedEventArgs e)
        {
            RaiseTargetChanged();
        }

        private void MuteButton_Checked(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;

            _isMuted = true;
            UpdateMuteButtonVisual();
            VolumeOrMuteChanged?.Invoke(_audioTargets, CurrentVolume, _isMuted);
        }

        private void MuteButton_Unchecked(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;

            _isMuted = false;
            UpdateMuteButtonVisual();
            VolumeOrMuteChanged?.Invoke(_audioTargets, CurrentVolume, _isMuted);
        }

        private void RaiseTargetChanged()
        {
            TargetChanged?.Invoke(this, EventArgs.Empty);
        }

        private void TargetTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            UpdateMuteButtonEnabled(); // 👈 keep in sync
            TargetChanged?.Invoke(this, EventArgs.Empty);
        }

        private void UpdateMuteButtonEnabled()
        {
            MuteButton.IsEnabled = _audioTargets.Count > 0;
        }

        private void UpdateMuteButtonVisual()
        {
            if (MuteButton != null)
            {
                MuteButton.Content = _isMuted ? "Unmute" : "Mute";
                MuteButton.Background = _isMuted ? _muteOnBrush : _muteOffBrush;
                MuteButton.Foreground = Brushes.White;
            }
        }

        private void UpdateTargetsDisplay()
        {
            if (_audioTargets.Count == 0)
            {
                TargetTextBox.Text = "";
                TargetTextBox.ToolTip = "";
            }
            else if (_audioTargets.Count == 1)
            {
                TargetTextBox.Text = _audioTargets[0].Name;
                TargetTextBox.ToolTip = _audioTargets[0].Name;
            }
            else
            {
                // Show count and first app
                var firstTarget = _audioTargets[0].Name;
                TargetTextBox.Text = $"{firstTarget} +{_audioTargets.Count - 1}";

                // Set tooltip to show all targets
                TargetTextBox.ToolTip = string.Join("\n", _audioTargets.Select(t =>
                    $"{t.Name} {(t.IsInputDevice ? "(Input)" : (t.IsOutputDevice ? "(Output)" : ""))}"));
            }

            // Reset foreground color (in case it was previously set to indicate disconnection)
            TargetTextBox.Foreground = TryFindResource("MaterialDesign.Brush.Foreground") as Brush ?? Brushes.Black;

            UpdateMuteButtonEnabled();
        }

        #endregion Private Methods
    }
}