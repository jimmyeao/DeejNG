using DeejNG.Classes;
using DeejNG.Models;
using DeejNG.Services;
using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace DeejNG.Dialogs
{
    /// <summary>
    /// Interaction logic for ButtonSettingsDialog.
    /// Allows configuration of physical button mappings.
    /// </summary>
    public partial class ButtonSettingsDialog : Window
    {
        #region Private Fields

        private AppSettings _settings;
        private ObservableCollection<ButtonMappingViewModel> _buttonMappings = new();

        #endregion

        #region Public Constructors

        /// <summary>
        /// Initializes the button settings dialog with current settings.
        /// </summary>
        public ButtonSettingsDialog(AppSettings settings)
        {
            InitializeComponent();

            _settings = settings ?? new AppSettings();

            // Load button configuration after window loads
            this.Loaded += ButtonSettingsDialog_Loaded;
        }

        private void ButtonSettingsDialog_Loaded(object sender, RoutedEventArgs e)
        {
            // Load button configuration after all controls are initialized
            LoadButtonConfiguration();
        }

        #endregion

        #region Private Methods

        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                try { DragMove(); } catch { }
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            if (!ValidateConfiguration()) return;

            SaveButtonConfiguration();
            DialogResult = true;
            Close();
        }

        #endregion

        #region Button Configuration

        /// <summary>
        /// Loads button configuration from settings and initializes UI.
        /// Buttons are now auto-detected (10000/10001 values), so we show all 8 slots for configuration.
        /// </summary>
        private void LoadButtonConfiguration()
        {
            try
            {
                LongPressThresholdBox.Text = _settings.LongPressThresholdMs.ToString();
                DoublePressWindowBox.Text = _settings.DoublePressWindowMs.ToString();

                // Always show 8 button slots (max supported)
                // Users can configure ahead of time; only buttons detected from hardware will activate
                LoadButtonMappingSlots();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ERROR] LoadButtonConfiguration: {ex.Message}");
            }
        }

        /// <summary>
        /// Loads 8 button mapping slots for configuration.
        /// Buttons are auto-detected from hardware (10000/10001 protocol).
        /// </summary>
        private void LoadButtonMappingSlots()
        {
            _buttonMappings.Clear();

            // Show all 8 button slots (users can configure ahead of time)
            const int maxButtons = 8;

            for (int i = 0; i < maxButtons; i++)
            {
                var existingMapping = _settings?.ButtonMappings?.FirstOrDefault(m => m.ButtonIndex == i);
                _buttonMappings.Add(new ButtonMappingViewModel(i, existingMapping));
            }

            // Set ItemsSource if the control is initialized
            if (ButtonMappingsItemsControl != null)
            {
                ButtonMappingsItemsControl.ItemsSource = _buttonMappings;
            }
        }

        /// <summary>
        /// Validates that only numbers can be entered in numeric text boxes.
        /// </summary>
        private void NumberValidationTextBox(object sender, TextCompositionEventArgs e)
        {
            Regex regex = new Regex("[^0-9]+");
            e.Handled = regex.IsMatch(e.Text);
        }

        /// <summary>
        /// Checks key combos before saving so typos are caught here and not on the next button press.
        /// </summary>
        private bool ValidateConfiguration()
        {
            foreach (var vm in _buttonMappings)
            {
                foreach (var (binding, label) in new[] { (vm.Short, "press"), (vm.Double, "double press"), (vm.Long, "long press") })
                {
                    if (binding.Action != ButtonAction.SendKeys) continue;

                    var combos = (binding.KeyCombo ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    var invalid = combos.FirstOrDefault(c => !ButtonActionHandler.TryParseKeyCombo(c, out _));
                    if (combos.Length == 0 || invalid != null)
                    {
                        MessageBox.Show(this,
                            $"{vm.ButtonIndexDisplay} ({label}): '{invalid ?? binding.KeyCombo}' is not a valid key combination.\n\nExamples: Ctrl+Shift+M, F13, Alt+F4, Ctrl+K, Ctrl+C",
                            "Invalid key combination", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return false;
                    }
                }
            }

            return true;
        }

        /// <summary>
        /// Saves button configuration to settings.
        /// Only saves button mappings that have something configured.
        /// Buttons are auto-detected from hardware (10000/10001 protocol).
        /// </summary>
        private void SaveButtonConfiguration()
        {
            if (int.TryParse(LongPressThresholdBox.Text, out int threshold))
            {
                _settings.LongPressThresholdMs = Math.Clamp(threshold, 150, 5000);
            }

            if (int.TryParse(DoublePressWindowBox.Text, out int window))
            {
                _settings.DoublePressWindowMs = Math.Clamp(window, 100, 1000);
            }

            _settings.ButtonMappings = _buttonMappings
                .Select(vm => vm.ToMapping())
                .Where(m => m.HasAnything)
                .ToList();
        }

        #endregion

        #region Action Editor Events

        private void RecordKeys_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement { DataContext: ActionBindingViewModel vm } button) return;

            vm.IsRecording = !vm.IsRecording;

            // Move focus to the key box next to the button so the next key press lands there
            if (vm.IsRecording && button.Parent is Grid grid)
            {
                grid.Children.OfType<TextBox>().FirstOrDefault()?.Focus();
            }
        }

        /// <summary>
        /// While recording, captures the pressed combination instead of typing it.
        /// </summary>
        private void KeyComboBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (sender is not TextBox { DataContext: ActionBindingViewModel vm } || !vm.IsRecording) return;

            e.Handled = true;

            Key key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
                or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
            {
                return; // wait for the actual key
            }

            var parts = new List<string>();
            var modifiers = Keyboard.Modifiers;
            if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
            if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
            if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
            if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");

            string keyName = new KeyConverter().ConvertToInvariantString(key) ?? key.ToString();
            if (keyName.Length == 2 && keyName[0] == 'D' && char.IsDigit(keyName[1]))
                keyName = keyName[1..];
            parts.Add(keyName);

            vm.KeyCombo = string.Join("+", parts);
            vm.IsRecording = false;
        }

        private void BrowseCommand_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement { DataContext: ActionBindingViewModel vm }) return;

            var dialog = new OpenFileDialog
            {
                Title = "Select a program or script",
                Filter = "Programs and scripts|*.exe;*.bat;*.cmd;*.ps1;*.vbs;*.ahk;*.lnk|All files|*.*"
            };

            if (dialog.ShowDialog(this) != true) return;

            string path = dialog.FileName;
            vm.Command = Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".ps1" => $"powershell -NoProfile -ExecutionPolicy Bypass -File \"{path}\"",
                ".exe" or ".bat" or ".cmd" => $"\"{path}\"",
                // Let Windows pick the associated program (AutoHotkey, wscript, shortcuts, ...)
                _ => $"start \"\" \"{path}\""
            };
        }

        private void TestCommand_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement { DataContext: ActionBindingViewModel vm }) return;

            new ButtonActionHandler(new List<ChannelControl>()).ExecuteAction(vm.ToBinding());
        }

        private void AddLayer_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement { DataContext: ButtonMappingViewModel vm }) return;

            int nextChannel = vm.Layers.Count == 0 ? 0 : vm.Layers.Max(l => l.ChannelIndex) + 1;
            vm.Layers.Add(new LayerChannelViewModel { ChannelIndex = nextChannel });
        }

        private void RemoveLayer_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement { DataContext: LayerChannelViewModel layer }) return;

            _buttonMappings.FirstOrDefault(b => b.Layers.Contains(layer))?.Layers.Remove(layer);
        }

        private void PickLayerTargets_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement { DataContext: LayerChannelViewModel layer }) return;

            var picker = new MultiTargetPickerDialog(layer.Targets)
            {
                Owner = this,
                WindowStartupLocation = WindowStartupLocation.CenterOwner
            };

            if (picker.ShowDialog() == true)
            {
                layer.Targets = picker.SelectedTargets;
            }
        }

        #endregion

        #region View Models

        public abstract class ViewModelBase : INotifyPropertyChanged
        {
            public event PropertyChangedEventHandler? PropertyChanged;

            protected void OnPropertyChanged(params string[] propertyNames)
            {
                foreach (var name in propertyNames)
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
            }
        }

        public sealed record ActionOption(ButtonAction Value, string Label)
        {
            // The themed ComboBox template ignores DisplayMemberPath, so show the label via ToString
            public override string ToString() => Label;
        }

        /// <summary>
        /// Editable form of a <see cref="ButtonActionBinding"/>.
        /// </summary>
        public class ActionBindingViewModel : ViewModelBase
        {
            private static readonly IReadOnlyList<ActionOption> _actionOptions = new[]
            {
                new ActionOption(ButtonAction.None, "None"),
                new ActionOption(ButtonAction.MediaPlayPause, "Media Play/Pause"),
                new ActionOption(ButtonAction.MediaNext, "Media Next"),
                new ActionOption(ButtonAction.MediaPrevious, "Media Previous"),
                new ActionOption(ButtonAction.MediaStop, "Media Stop"),
                new ActionOption(ButtonAction.MuteChannel, "Mute Channel"),
                new ActionOption(ButtonAction.GlobalMute, "Global Mute"),
                new ActionOption(ButtonAction.SendKeys, "Keyboard Shortcut"),
                new ActionOption(ButtonAction.RunCommand, "Run Command / Script"),
            };

            private ButtonAction _action;
            private int _targetChannelIndex = -1;
            private string _keyCombo = string.Empty;
            private string _command = string.Empty;
            private bool _isRecording;

            public ActionBindingViewModel(ButtonActionBinding? source)
            {
                if (source == null) return;

                _action = source.Action;
                _targetChannelIndex = source.TargetChannelIndex;
                _keyCombo = source.KeyCombo ?? string.Empty;
                _command = source.Command ?? string.Empty;
            }

            public IReadOnlyList<ActionOption> ActionOptions => _actionOptions;

            public ButtonAction Action
            {
                get => _action;
                set
                {
                    _action = value;
                    OnPropertyChanged(nameof(Action), nameof(NeedsTargetChannel), nameof(IsSendKeys), nameof(IsRunCommand));
                }
            }

            public int TargetChannelIndex
            {
                get => _targetChannelIndex;
                set
                {
                    _targetChannelIndex = value;
                    OnPropertyChanged(nameof(TargetChannelIndex), nameof(TargetChannelDisplay));
                }
            }

            public string TargetChannelDisplay
            {
                get => _targetChannelIndex >= 0 ? (_targetChannelIndex + 1).ToString() : "";
                set => TargetChannelIndex = int.TryParse(value, out int channelNum) && channelNum > 0 ? channelNum - 1 : -1;
            }

            public string KeyCombo
            {
                get => _keyCombo;
                set { _keyCombo = value; OnPropertyChanged(nameof(KeyCombo)); }
            }

            public string Command
            {
                get => _command;
                set { _command = value; OnPropertyChanged(nameof(Command)); }
            }

            public bool IsRecording
            {
                get => _isRecording;
                set { _isRecording = value; OnPropertyChanged(nameof(IsRecording), nameof(RecordButtonText)); }
            }

            public string RecordButtonText => _isRecording ? "Press keys…" : "Record";

            public bool NeedsTargetChannel => _action == ButtonAction.MuteChannel;
            public bool IsSendKeys => _action == ButtonAction.SendKeys;
            public bool IsRunCommand => _action == ButtonAction.RunCommand;

            public ButtonActionBinding ToBinding() => CopyTo(new ButtonActionBinding());

            public T CopyTo<T>(T target) where T : ButtonActionBinding
            {
                target.Action = _action;
                target.TargetChannelIndex = NeedsTargetChannel ? _targetChannelIndex : -1;
                target.KeyCombo = IsSendKeys ? _keyCombo.Trim() : string.Empty;
                target.Command = IsRunCommand ? _command.Trim() : string.Empty;
                return target;
            }
        }

        /// <summary>
        /// One slider override of a hold layer.
        /// </summary>
        public class LayerChannelViewModel : ViewModelBase
        {
            private int _channelIndex;
            private List<AudioTarget> _targets = new();

            public int ChannelIndex
            {
                get => _channelIndex;
                set { _channelIndex = value; OnPropertyChanged(nameof(ChannelIndex), nameof(ChannelDisplay)); }
            }

            public string ChannelDisplay
            {
                get => (_channelIndex + 1).ToString();
                set => ChannelIndex = int.TryParse(value, out int n) && n > 0 ? n - 1 : 0;
            }

            public List<AudioTarget> Targets
            {
                get => _targets;
                set { _targets = value ?? new(); OnPropertyChanged(nameof(Targets), nameof(TargetsSummary)); }
            }

            public string TargetsSummary => _targets.Count == 0
                ? "(no targets selected)"
                : string.Join(", ", _targets.Select(t => t.Name));
        }

        /// <summary>
        /// View model for one button slot.
        /// </summary>
        public class ButtonMappingViewModel : ViewModelBase
        {
            public ButtonMappingViewModel(int buttonIndex, ButtonMapping? source)
            {
                ButtonIndex = buttonIndex;
                Short = new ActionBindingViewModel(source);
                Long = new ActionBindingViewModel(source?.LongPress);
                Double = new ActionBindingViewModel(source?.DoublePress);

                foreach (var layer in source?.HoldLayer ?? new List<ButtonLayerChannel>())
                {
                    Layers.Add(new LayerChannelViewModel
                    {
                        ChannelIndex = layer.ChannelIndex,
                        Targets = layer.Targets?.Select(t => new AudioTarget
                        {
                            Name = t.Name,
                            IsInputDevice = t.IsInputDevice,
                            IsOutputDevice = t.IsOutputDevice
                        }).ToList() ?? new()
                    });
                }
            }

            public int ButtonIndex { get; }

            public string ButtonIndexDisplay => $"Button {ButtonIndex + 1}";

            public ActionBindingViewModel Short { get; }

            public ActionBindingViewModel Long { get; }

            public ActionBindingViewModel Double { get; }

            public ObservableCollection<LayerChannelViewModel> Layers { get; } = new();

            public ButtonMapping ToMapping()
            {
                var mapping = Short.CopyTo(new ButtonMapping
                {
                    ButtonIndex = ButtonIndex,
                    FriendlyName = ButtonIndexDisplay
                });

                mapping.LongPress = Long.ToBinding();
                mapping.DoublePress = Double.ToBinding();
                mapping.HoldLayer = Layers
                    .Where(l => l.Targets.Count > 0)
                    .Select(l => new ButtonLayerChannel { ChannelIndex = l.ChannelIndex, Targets = l.Targets })
                    .ToList();

                return mapping;
            }
        }

        #endregion
    }
}
