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

        private const float ClipThreshold = 0.98f;
        private const int SegmentCount = 15;
        private const float SmoothingFactor = 0.3f;
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

        // Cached paint objects to avoid creating new ones every frame
        private readonly SKPaint _basePaint = new()
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

        private readonly SKPaint _glassPaint = new()
        {
            Style = SKPaintStyle.Fill,
            IsAntialias = true
        };

        private readonly object _meterLock = new object();
        private readonly Brush _muteOffBrush = Brushes.Gray;
        private readonly Brush _muteOnBrush = new SolidColorBrush(Color.FromRgb(255, 12, 12));
        private readonly SKPaint _peakPaint = new()
        {
            Color = SKColors.White,
            Style = SKPaintStyle.Fill,
            IsAntialias = true
        };

        private readonly TimeSpan PeakHoldDuration = TimeSpan.FromMilliseconds(500);
        private readonly float SegmentSpacing = 4f;

        private List<AudioTarget> _audioTargets = new();
        private int _cachedHeight = 0;
        private int _cachedWidth = 0;
        // was 1000ms
        private bool _isMuted = false;

        private int _lastCanvasHeight = 0;
        private int _lastCanvasWidth = 0;
        private bool _layoutReady = false;
        private float _meterLevel;
        private bool _meterNeedsUpdate = false;
        // Meter update optimization
        private DispatcherTimer _meterUpdateTimer;

        private float _peakFade = 1f;
        private float _peakLevel;
        private DateTime _peakTimestamp;
        private float _previousMeterLevel = 0f;
        private SKRect[] _segmentRects = Array.Empty<SKRect>();
        // Cache segment rectangles
        private bool _segmentRectsCalculated = false;

        //  private FloatingOverlay _overlay;
        private DispatcherTimer _skiaRedrawTimer;
        private float _smoothedVolume;

        private bool _suppressEvents = false;

        #endregion Private Fields

        #region Public Constructors

        // Bright red
        /// <summary>
        /// Initializes a new instance of the ChannelControl UI component.
        /// Sets up event handlers and starts a high-frequency timer for VU meter updates.
        /// </summary>
        public ChannelControl()
        {
            // Load the visual components defined in XAML
            InitializeComponent();

            // Register event handlers for lifecycle and interaction events
            Loaded += ChannelControl_Loaded;                   // Called when the control is added to the visual tree
            Unloaded += ChannelControl_Unloaded;               // Called when the control is removed from the visual tree
            MouseDoubleClick += ChannelControl_MouseDoubleClick; // Allow editing or selection on double-click

            // Initialize a DispatcherTimer to update VU meter visuals at high frequency (every 25ms)
            _meterUpdateTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(25) // Approx. 40 FPS for smooth animations
            };

            // Hook up the Tick event to update meter visuals
            _meterUpdateTimer.Tick += MeterUpdateTimer_Tick;

            // Start the timer immediately
            _meterUpdateTimer.Start();

            // Note: This approach replaces CompositionTarget.Rendering for better control and performance
        }


        #endregion Public Constructors

        #region Public Events

        // New event for notifying the parent window when a session is disconnected
        public event EventHandler<string> SessionDisconnected;

        public event EventHandler TargetChanged;

        public event Action<List<AudioTarget>, float, bool> VolumeOrMuteChanged;

        #endregion Public Events

        #region Public Properties

        /// <summary>
        /// Gets or sets the list of audio targets (e.g., apps or devices to control).
        /// When set, it updates the UI or internal state to reflect the new target list.
        /// </summary>
        public List<AudioTarget> AudioTargets
        {
            // Returns the current list of audio targets
            get => _audioTargets;

            // Sets a new list of audio targets and updates the display
            set
            {
                // Ensure the internal list is never null
                _audioTargets = value ?? new List<AudioTarget>();

                // Refresh any visual or logical representation of the target list
                UpdateTargetsDisplay();
            }
        }


        public float CurrentVolume => (float)VolumeSlider.Value;

        public bool IsMuted => _isMuted;

        public string TargetExecutable =>
             _audioTargets.FirstOrDefault()?.Name ?? "";

        #endregion Public Properties

        #region Public Methods

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
                TargetTextBox.Foreground = TryFindResource("TextSecondaryBrush") as Brush ?? Brushes.Gray;
                TargetTextBox.ToolTip = $"{TargetTextBox.Text} (Expired)";
            }
        }

        /// <summary>
        /// Resets the connection state for the control's audio targets
        /// </summary>
        public void ResetConnectionState()
        {
            // Reset any disconnected/expired visual indicators by clearing the local value
            // This allows the XAML binding to take over again
            TargetTextBox.ClearValue(TextBlock.ForegroundProperty);
            if (TargetTextBox.ToolTip is string tooltip &&
                (tooltip.EndsWith("(Disconnected)") || tooltip.EndsWith("(Expired)")))
            {
                // Reset the tooltip to just show the targets
                UpdateTargetsDisplay();
            }
        }

        /// <summary>
        /// Sets the visibility of the volume meter (e.g., a Skia-based VU meter).
        /// </summary>
        /// <param name="visible">True to show the meter; false to hide it.</param>
        public void SetMeterVisibility(bool visible)
        {
            // Show or hide the SkiaCanvas that renders the meter graphics
            SkiaCanvas.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

            // Optionally switch to another UI element instead (commented out line)
            // MeterVisuals.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>
        /// Programmatically sets the muted state of the control,
        /// updating the internal state, UI, and visuals, then triggers volume application.
        /// </summary>
        /// <param name="muted">True to mute, false to unmute.</param>
        public void SetMuted(bool muted)
        {
            _suppressEvents = true;        // Prevent external event handling during update
            _isMuted = muted;              // Update internal state
            MuteButton.IsChecked = muted;  // Reflect change in the UI control
            UpdateMuteButtonVisual();      // Update any related visual styling (e.g., icon color)
            _suppressEvents = false;       // Re-enable events
            
            // Trigger volume application with the new mute state
            VolumeOrMuteChanged?.Invoke(_audioTargets, CurrentVolume, _isMuted);
        }

        /// <summary>
        /// Programmatically sets the volume slider's value without firing change events.
        /// </summary>
        /// <param name="level">The new volume level (typically between 0.0 and 1.0).</param>
        public void SetVolume(float level)
        {
            _suppressEvents = true;        // Prevent event handlers from reacting to slider change
            VolumeSlider.Value = level;    // Update the slider UI
            _suppressEvents = false;       // Re-enable events
        }


        /// <summary>
        /// Applies optional smoothing to the incoming volume level and sets the final value.
        /// Can also suppress volume change events temporarily during this operation.
        /// </summary>
        /// <param name="rawLevel">The raw input volume level (expected range: 0.0f to 1.0f).</param>
        /// <param name="suppressEvent">If true, suppresses any volume change events triggered during the update.</param>
        /// <param name="disableSmoothing">If true, disables smoothing and sets the raw volume directly.</param>
        public void SmoothAndSetVolume(float rawLevel, bool suppressEvent = false, bool disableSmoothing = false)
        {
            // Optionally suppress volume change events during this update
            _suppressEvents = suppressEvent;

            if (disableSmoothing)
            {
                // If smoothing is disabled, apply the raw level immediately
                SetVolume(rawLevel);
            }
            else
            {
                // Apply exponential smoothing to the volume level
                // If it's the first value (_smoothedVolume == 0), initialize with raw level
                // Otherwise, interpolate toward raw level using the smoothing factor
                _smoothedVolume = _smoothedVolume == 0
                    ? rawLevel
                    : _smoothedVolume + (rawLevel - _smoothedVolume) * SmoothingFactor;

                // Apply the smoothed volume
                SetVolume(_smoothedVolume);
            }

            // Re-enable event firing after update
            _suppressEvents = false;
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

        #endregion Public Methods

        #region Private Methods

        private void ChannelControl_Loaded(object sender, RoutedEventArgs e)
        {
            _layoutReady = true;
            UpdateMuteButtonVisual();
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

        private void SkiaCanvas_PaintSurface(object sender, SKPaintSurfaceEventArgs e)
        {
            var canvas = e.Surface.Canvas;
            var info = e.Info;
            canvas.Clear(SKColors.Transparent);

            var width = info.Width;
            var height = info.Height;

            if (width <= 0 || height <= 0)
                return;

            // Draw background with rounded corners using theme color
            var surfaceColor = TryFindResource("SurfaceColor") as Color? ?? Color.FromRgb(54, 54, 80);
            _basePaint.Color = new SKColor(surfaceColor.R, surfaceColor.G, surfaceColor.B);
            var bgRect = new SKRoundRect(new SKRect(0, 0, width, height), 8, 8);
            canvas.DrawRoundRect(bgRect, _basePaint);

            // Calculate level height
            var levelHeight = height * Math.Clamp(_meterLevel, 0f, 1f);

            if (levelHeight > 0)
            {
                // Get color based on level (green -> yellow -> red)
                SKColor levelColor = GetColorForLevel(_meterLevel);

                // Draw level indicator with rounded corners
                _basePaint.Color = levelColor;
                var levelRect = new SKRect(4, height - levelHeight, width - 4, height - 4);
                var levelRoundRect = new SKRoundRect(levelRect, 4, 4);
                canvas.DrawRoundRect(levelRoundRect, _basePaint);
            }

            // Draw dB marker segments (subtle lines)
            DrawSegmentMarkers(canvas, width, height);
        }

        private void DrawSegmentMarkers(SKCanvas canvas, int width, int height)
        {
            _borderPaint.Color = new SKColor(30, 30, 46, 150);
            _borderPaint.StrokeWidth = 1;

            int segmentCount = 10;
            float segmentHeight = height / (float)segmentCount;

            for (int i = 1; i < segmentCount; i++)
            {
                float y = i * segmentHeight;
                canvas.DrawLine(4, y, width - 4, y, _borderPaint);
            }
        }

        private SKColor GetColorForLevel(float level)
        {
            // Green for low levels (0-0.7)
            // Yellow for medium levels (0.7-0.85)
            // Red for high levels (0.85-1.0)
            if (level < 0.7f)
            {
                return new SKColor(16, 185, 129); // Green
            }
            else if (level < 0.85f)
            {
                // Interpolate between green and yellow
                float t = (level - 0.7f) / 0.15f;
                return InterpolateColor(
                    new SKColor(16, 185, 129),   // Green
                    new SKColor(245, 158, 11),   // Yellow
                    t
                );
            }
            else
            {
                // Interpolate between yellow and red
                float t = (level - 0.85f) / 0.15f;
                return InterpolateColor(
                    new SKColor(245, 158, 11),   // Yellow
                    new SKColor(239, 68, 68),    // Red
                    t
                );
            }
        }

        private SKColor InterpolateColor(SKColor color1, SKColor color2, float t)
        {
            t = Math.Clamp(t, 0f, 1f);
            return new SKColor(
                (byte)(color1.Red + (color2.Red - color1.Red) * t),
                (byte)(color1.Green + (color2.Green - color1.Green) * t),
                (byte)(color1.Blue + (color2.Blue - color1.Blue) * t),
                (byte)(color1.Alpha + (color2.Alpha - color1.Alpha) * t)
            );
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
                string displayName = FormatDisplayName(_audioTargets[0].Name);
                TargetTextBox.Text = displayName;
                TargetTextBox.ToolTip = displayName;
            }
            else
            {
                // Show count and first app
                var firstTarget = FormatDisplayName(_audioTargets[0].Name);
                TargetTextBox.Text = $"{firstTarget} +{_audioTargets.Count - 1}";

                // Set tooltip to show all targets
                TargetTextBox.ToolTip = string.Join("\n", _audioTargets.Select(t =>
                {
                    string displayName = FormatDisplayName(t.Name);
                    string suffix = t.IsInputDevice ? " (Input)" : (t.IsOutputDevice ? " (Output)" : "");
                    return displayName + suffix;
                }));
            }

            // Reset foreground color by clearing any local override
            // This allows the XAML DynamicResource binding to work
            TargetTextBox.ClearValue(TextBlock.ForegroundProperty);

            UpdateMuteButtonEnabled();
        }

        /// <summary>
        /// Formats a target name for display by removing .exe extension and capitalizing
        /// </summary>
        private string FormatDisplayName(string name)
        {
            if (string.IsNullOrEmpty(name))
                return name;

            // Remove .exe extension if present
            string displayName = name;
            if (displayName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                displayName = displayName.Substring(0, displayName.Length - 4);
            }

            // Capitalize first letter for nicer display (e.g., "spotify" -> "Spotify")
            if (displayName.Length > 0)
            {
                displayName = char.ToUpper(displayName[0]) + displayName.Substring(1);
            }

            return displayName;
        }

        #endregion Private Methods
    }
}