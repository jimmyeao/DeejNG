using System.Windows;

namespace DeejNG.Dialogs
{
    /// <summary>
    /// Material Design themed input dialog
    /// </summary>
    public partial class InputDialog : Window
    {
        #region Public Constructors

        public InputDialog(string title, string prompt, string defaultValue = "")
        {
            InitializeComponent();
            Title = title;
            PromptTextBlock.Text = prompt;
            InputTextBox.Text = defaultValue;

            // Select all text when dialog opens
            Loaded += (s, e) =>
            {
                InputTextBox.Focus();
                InputTextBox.SelectAll();
            };
        }

        #endregion Public Constructors

        #region Public Properties

        public string ResponseText { get; private set; } = string.Empty;

        #endregion Public Properties

        #region Public Methods

        /// <summary>
        /// Shows the input dialog and returns the entered text, or empty string if cancelled
        /// </summary>
        public static string Show(string title, string prompt, string defaultValue = "", Window owner = null)
        {
            var dialog = new InputDialog(title, prompt, defaultValue);
            if (owner != null)
            {
                dialog.Owner = owner;
            }

            bool? result = dialog.ShowDialog();
            return result == true ? dialog.ResponseText : string.Empty;
        }

        #endregion Public Methods

        #region Private Methods

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            ResponseText = InputTextBox.Text?.Trim() ?? string.Empty;
            DialogResult = true;
            Close();
        }

        private void TitleBar_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.ChangedButton == System.Windows.Input.MouseButton.Left)
            {
                DragMove();
            }
        }

        #endregion Private Methods
    }
}
