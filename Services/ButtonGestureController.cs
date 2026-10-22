using DeejNG.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows.Threading;

namespace DeejNG.Services
{
    /// <summary>
    /// Turns raw button press/release events into short-press, long-press and hold-layer gestures,
    /// and decides where slider values go while a layer button is held.
    /// All members must be called on the UI thread.
    /// </summary>
    /// <remarks>
    /// Gesture rules per button:
    /// - Only a short action: executes immediately on press (no added latency).
    /// - Short + long action, no layer: long fires as soon as the threshold is reached while held,
    ///   otherwise the short action fires on release.
    /// - With a hold layer: the layer is active while held. If any layer slider was moved, nothing
    ///   else fires on release. Otherwise long (held >= threshold) or short fires on release.
    /// - With a double press: a short tap is held back for the double-press window. A second press
    ///   inside the window (measured from the first press) fires the double action immediately;
    ///   otherwise the short action fires when the window ends.
    ///
    /// Slider pickup: whenever a channel switches between its normal targets and layer targets,
    /// the slider position at that moment is taken as an anchor. Values are ignored until the slider
    /// moves more than <see cref="PickupThreshold"/> away from it, so neither target jumps to the
    /// other target's level just because the routing changed.
    /// </remarks>
    public sealed class ButtonGestureController
    {
        /// <summary>
        /// Minimum slider travel (normalized 0..1) after a layer switch before values are applied.
        /// </summary>
        public const float PickupThreshold = 0.03f;

        /// <summary>
        /// Minimum change (normalized 0..1) before a layer slider value is applied again.
        /// </summary>
        private const float LayerChangeThreshold = 0.01f;

        private readonly Func<int> _longPressThresholdMs;
        private readonly Func<int> _doublePressWindowMs;
        private readonly Action<ButtonMapping, ButtonActionBinding> _execute;
        private readonly Dictionary<int, PressState> _states = new();
        private readonly Dictionary<int, ChannelRoute> _routes = new();
        private long _pressCounter;

        public ButtonGestureController(Func<int> longPressThresholdMs, Func<int> doublePressWindowMs, Action<ButtonMapping, ButtonActionBinding> execute)
        {
            _longPressThresholdMs = longPressThresholdMs ?? throw new ArgumentNullException(nameof(longPressThresholdMs));
            _doublePressWindowMs = doublePressWindowMs ?? throw new ArgumentNullException(nameof(doublePressWindowMs));
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        }

        /// <summary>
        /// Raised when a hold layer becomes active (true) or inactive (false).
        /// </summary>
        public event Action<ButtonMapping, bool>? LayerStateChanged;

        private TimeSpan LongPressThreshold => TimeSpan.FromMilliseconds(Math.Clamp(_longPressThresholdMs(), 150, 5000));

        private TimeSpan DoublePressWindow => TimeSpan.FromMilliseconds(Math.Clamp(_doublePressWindowMs(), 100, 1000));

        /// <summary>
        /// Processes a physical button state change.
        /// </summary>
        public void OnButtonChanged(ButtonMapping mapping, bool isPressed)
        {
            if (mapping == null) return;

            if (!_states.TryGetValue(mapping.ButtonIndex, out var state))
            {
                state = new PressState();
                _states[mapping.ButtonIndex] = state;
            }

            if (isPressed)
                OnPress(mapping, state);
            else
                OnRelease(mapping, state);
        }

        /// <summary>
        /// Releases all held buttons and cancels pending gestures without firing actions
        /// (e.g. on disconnect, reconnect or settings change).
        /// </summary>
        /// <remarks>
        /// Routes are left alone: a channel that was on a layer notices the switch back to its
        /// normal targets on its next value and arms pickup then. Other channels keep their state.
        /// </remarks>
        public void Reset()
        {
            foreach (var state in _states.Values)
            {
                state.Timer?.Stop();
                state.DoubleTimer?.Stop();

                if (state.IsDown && state.Mapping?.HasHoldLayer == true)
                    LayerStateChanged?.Invoke(state.Mapping, false);

                state.IsDown = false;
                state.Mapping = null;
            }
            _states.Clear();
        }

        /// <summary>
        /// Returns the targets of all currently active hold layers.
        /// </summary>
        public IEnumerable<AudioTarget> GetActiveLayerTargets()
        {
            foreach (var state in _states.Values)
            {
                if (!state.IsDown || state.Mapping?.HoldLayer == null) continue;

                foreach (var layer in state.Mapping.HoldLayer)
                {
                    if (layer.Targets == null) continue;
                    foreach (var target in layer.Targets)
                        yield return target;
                }
            }
        }

        /// <summary>
        /// Decides where a slider value should go. Call once per incoming value with the final
        /// (inverted/curved) level. Returns false if the value should be ignored (pickup pending,
        /// or a layer slider that has not moved since its last applied value).
        /// <paramref name="layerTargets"/> is null for the channel's normal targets.
        /// </summary>
        public bool RouteSliderValue(int channelIndex, float level, out List<AudioTarget>? layerTargets)
        {
            if (!_routes.TryGetValue(channelIndex, out var route))
            {
                route = new ChannelRoute();
                _routes[channelIndex] = route;
            }

            var (layerButton, targets) = FindActiveLayer(channelIndex);
            layerTargets = targets;

            if (route.CurrentLayerButton != layerButton)
            {
                // Routing switched since the last value: anchor at the last known position
                route.CurrentLayerButton = layerButton;
                route.Anchor = route.LastLevel;
                route.LastLayerLevel = null;
            }

            route.LastLevel = level;

            if (route.Anchor is float anchor)
            {
                if (Math.Abs(level - anchor) < PickupThreshold)
                    return false;

                route.Anchor = null;
            }

            if (layerButton >= 0)
            {
                // The normal path skips unchanged values via the channel's volume; layer values need their own check
                if (route.LastLayerLevel is float last && Math.Abs(level - last) < LayerChangeThreshold)
                    return false;

                route.LastLayerLevel = level;

                if (_states.TryGetValue(layerButton, out var state))
                    state.LayerUsed = true;
            }

            return true;
        }

        /// <summary>
        /// Returns true if the button is currently physically held.
        /// </summary>
        public bool IsHeld(int buttonIndex) => _states.TryGetValue(buttonIndex, out var s) && s.IsDown;

        private void OnPress(ButtonMapping mapping, PressState state)
        {
            if (state.IsDown) return;

            state.IsDown = true;
            state.Mapping = mapping;
            state.DownAt = DateTime.UtcNow;
            state.Order = ++_pressCounter;
            state.LongFired = false;
            state.LayerUsed = false;
            state.Consumed = false;

            bool hasLong = mapping.HasLongPress;
            bool hasLayer = mapping.HasHoldLayer;

            if (state.DoubleTimer?.IsEnabled == true)
            {
                state.DoubleTimer.Stop();

                if (state.DownAt <= state.DoubleDeadline)
                {
                    // Second press inside the double-press window
                    state.Consumed = true;
                    Fire(mapping, mapping.DoublePress);
                }
                else
                {
                    // Window is over but the timer has not ticked yet: run the pending single press first
                    Fire(mapping, mapping);
                }
            }
            else if (!hasLong && !hasLayer && !mapping.HasDoublePress)
            {
                // Plain button: act immediately like before
                Fire(mapping, mapping);
                return;
            }

            if (state.Consumed && !hasLayer) return;

            if (hasLayer)
            {
                Debug.WriteLine($"[Button] Layer of button {mapping.ButtonIndex} active");
                LayerStateChanged?.Invoke(mapping, true);
            }
            else if (hasLong)
            {
                // Long press without layer fires as soon as the threshold is reached
                state.Timer ??= new DispatcherTimer();
                state.Timer.Interval = LongPressThreshold;
                state.Timer.Tick -= state.TimerHandler;
                state.TimerHandler = (_, _) =>
                {
                    state.Timer!.Stop();
                    if (!state.IsDown || state.LongFired || state.Consumed || state.Mapping == null) return;
                    state.LongFired = true;
                    Fire(state.Mapping, state.Mapping.LongPress);
                };
                state.Timer.Tick += state.TimerHandler;
                state.Timer.Start();
            }
        }

        private void OnRelease(ButtonMapping mapping, PressState state)
        {
            if (!state.IsDown) return;

            state.IsDown = false;
            state.Timer?.Stop();
            state.Mapping = null;

            bool hasLong = mapping.HasLongPress;
            bool hasLayer = mapping.HasHoldLayer;

            if (hasLayer)
            {
                Debug.WriteLine($"[Button] Layer of button {mapping.ButtonIndex} released (used={state.LayerUsed})");
                LayerStateChanged?.Invoke(mapping, false);
            }

            if (!hasLong && !hasLayer && !mapping.HasDoublePress) return;   // already fired on press
            if (state.Consumed || state.LongFired || state.LayerUsed) return;

            // Only a button with a long-press binding treats a long hold differently
            bool isLong = hasLong && DateTime.UtcNow - state.DownAt >= LongPressThreshold;
            if (isLong)
            {
                Fire(mapping, mapping.LongPress);
                return;
            }

            if (!mapping.HasDoublePress)
            {
                Fire(mapping, mapping);
                return;
            }

            // The double-press window runs from the first press, not from the release
            state.DoubleDeadline = state.DownAt + DoublePressWindow;
            var remaining = state.DoubleDeadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                Fire(mapping, mapping);
            }
            else
            {
                // Wait for a possible second press before running the single-press action
                state.DoubleTimer ??= new DispatcherTimer();
                state.DoubleTimer.Interval = remaining;
                state.DoubleTimer.Tick -= state.DoubleTimerHandler;
                state.DoubleTimerHandler = (_, _) =>
                {
                    state.DoubleTimer!.Stop();
                    Fire(mapping, mapping);
                };
                state.DoubleTimer.Tick += state.DoubleTimerHandler;
                state.DoubleTimer.Start();
            }
        }

        private void Fire(ButtonMapping mapping, ButtonActionBinding binding)
        {
            if (binding == null || !binding.IsAssigned) return;
            string kind = ReferenceEquals(binding, mapping) ? "short" : ReferenceEquals(binding, mapping.DoublePress) ? "double" : "long";
            Debug.WriteLine($"[Button] Button {mapping.ButtonIndex}: {kind} -> {binding.Action}");
            _execute(mapping, binding);
        }

        private (int buttonIndex, List<AudioTarget>? targets) FindActiveLayer(int channelIndex)
        {
            // The most recently pressed held button wins if several layers cover the same channel
            int bestButton = -1;
            long bestOrder = -1;
            List<AudioTarget>? bestTargets = null;

            foreach (var (buttonIndex, state) in _states)
            {
                if (!state.IsDown || state.Mapping == null || state.Order <= bestOrder) continue;

                var layer = state.Mapping.HoldLayer?.FirstOrDefault(l => l.ChannelIndex == channelIndex && l.Targets?.Count > 0);
                if (layer == null) continue;

                bestButton = buttonIndex;
                bestOrder = state.Order;
                bestTargets = layer.Targets;
            }

            return (bestButton, bestTargets);
        }

        private sealed class PressState
        {
            public bool IsDown;
            public ButtonMapping? Mapping;
            public DateTime DownAt;
            public long Order;
            public bool LongFired;
            public bool LayerUsed;
            public bool Consumed;
            public DispatcherTimer? Timer;
            public EventHandler? TimerHandler;
            public DispatcherTimer? DoubleTimer;
            public DateTime DoubleDeadline;
            public EventHandler? DoubleTimerHandler;
        }

        private sealed class ChannelRoute
        {
            public int CurrentLayerButton = -1;
            public float? LastLevel;
            public float? Anchor;
            public float? LastLayerLevel;
        }
    }
}
