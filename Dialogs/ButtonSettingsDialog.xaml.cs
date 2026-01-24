using DeejNG.Classes;
using DeejNG.Models;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
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

                var viewModel = new ButtonMappingViewModel
                {
                    ButtonIndex = i,
                    Action = existingMapping?.Action ?? ButtonAction.None,
                    TargetChannelIndex = existingMapping?.TargetChannelIndex ?? -1
                };

                _buttonMappings.Add(viewModel);
            }

            // Set ItemsSource if the control is initialized
            if (ButtonMappingsItemsControl != null)
            {
                ButtonMappingsItemsControl.ItemsSource = _buttonMappings;
            }
        }

        /// <summary>
        /// Handles button action selection changes to enable/disable channel selector.
        /// </summary>
        private void ButtonAction_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // The binding handles this automatically via NeedsTargetChannel property
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
        /// Saves button configuration to settings.
        /// Only saves button mappings that have actions assigned.
        /// Buttons are auto-detected from hardware (10000/10001 protocol).
        /// </summary>
        private void SaveButtonConfiguration()
        {
            _settings.ButtonMappings = new List<ButtonMapping>();

            // Only save button mappings that have actions configured
            foreach (var viewModel in _buttonMappings.Where(vm => vm.Action != ButtonAction.None))
            {
                _settings.ButtonMappings.Add(new ButtonMapping
                {
                    ButtonIndex = viewModel.ButtonIndex,
                    Action = viewModel.Action,
                    TargetChannelIndex = viewModel.TargetChannelIndex,
                    FriendlyName = $"Button {viewModel.ButtonIndex + 1}"
                });
            }
        }

        #endregion

        #region Button Mapping View Model

        /// <summary>
        /// View model for button mapping UI.
        /// </summary>
        public class ButtonMappingViewModel : INotifyPropertyChanged
        {
            private int _buttonIndex;
            private ButtonAction _action;
            private int _targetChannelIndex;

            public int ButtonIndex
            {
                get => _buttonIndex;
                set
                {
                    _buttonIndex = value;
                    OnPropertyChanged(nameof(ButtonIndex));
                    OnPropertyChanged(nameof(ButtonIndexDisplay));
                }
            }

            public string ButtonIndexDisplay => $"Button {ButtonIndex + 1}";

            public ButtonAction Action
            {
                get => _action;
                set
                {
                    _action = value;
                    OnPropertyChanged(nameof(Action));
                    OnPropertyChanged(nameof(ActionIndex));
                    OnPropertyChanged(nameof(NeedsTargetChannel));
                }
            }

            public int ActionIndex
            {
                get => (int)_action;
                set
                {
                    _action = (ButtonAction)value;
                    OnPropertyChanged(nameof(Action));
                    OnPropertyChanged(nameof(ActionIndex));
                    OnPropertyChanged(nameof(NeedsTargetChannel));
                }
            }

            public int TargetChannelIndex
            {
                get => _targetChannelIndex;
                set
                {
                    _targetChannelIndex = value;
                    OnPropertyChanged(nameof(TargetChannelIndex));
                    OnPropertyChanged(nameof(TargetChannelDisplay));
                }
            }

            public string TargetChannelDisplay
            {
                get => _targetChannelIndex >= 0 ? (_targetChannelIndex + 1).ToString() : "";
                set
                {
                    if (int.TryParse(value, out int channelNum) && channelNum > 0)
                    {
                        TargetChannelIndex = channelNum - 1;
                    }
                    else
                    {
                        TargetChannelIndex = -1;
                    }
                }
            }

            public bool NeedsTargetChannel => _action == ButtonAction.MuteChannel;

            public event PropertyChangedEventHandler? PropertyChanged;

            protected void OnPropertyChanged(string propertyName)
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            }
        }

        #endregion
    }
}
