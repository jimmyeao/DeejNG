using System.Windows;

namespace DeejNG.Dialogs
{
    /// <summary>
    /// Material Design themed confirmation dialog
    /// </summary>
    public partial class ConfirmationDialog : Window
    {
        #region Private Constructors

        private ConfirmationDialog(string title, string message, bool showCancel = true, bool showNo = true)
        {
            InitializeComponent();
            Title = title;
            MessageTextBlock.Text = message;

            // Configure button visibility
            CancelButton.Visibility = showCancel ? Visibility.Visible : Visibility.Collapsed;
            NoButton.Visibility = showNo ? Visibility.Visible : Visibility.Collapsed;

            // If only showing OK, change Yes to OK and center the button panel
            if (!showNo && !showCancel)
            {
                YesButton.Content = "OK";
                ButtonPanel.HorizontalAlignment = HorizontalAlignment.Center;
            }
        }

        #endregion Private Constructors

        #region Public Enums

        public enum ButtonResult
        {
            Yes,
            No,
            Cancel,
            OK
        }

        #endregion Public Enums

        #region Public Properties

        public ButtonResult Result { get; private set; } = ButtonResult.Cancel;

        #endregion Public Properties

        #region Public Methods

        /// <summary>
        /// Shows an OK dialog (information)
        /// </summary>
        public static void ShowOK(string title, string message, Window owner = null)
        {
            var dialog = new ConfirmationDialog(title, message, showCancel: false, showNo: false);
            if (owner != null) dialog.Owner = owner;
            dialog.ShowDialog();
        }

        /// <summary>
        /// Shows a Yes/No dialog
        /// </summary>
        public static ButtonResult ShowYesNo(string title, string message, Window owner = null)
        {
            var dialog = new ConfirmationDialog(title, message, showCancel: false, showNo: true);
            if (owner != null) dialog.Owner = owner;
            dialog.ShowDialog();
            return dialog.Result;
        }

        /// <summary>
        /// Shows a Yes/No/Cancel dialog
        /// </summary>
        public static ButtonResult ShowYesNoCancel(string title, string message, Window owner = null)
        {
            var dialog = new ConfirmationDialog(title, message, showCancel: true, showNo: true);
            if (owner != null) dialog.Owner = owner;
            dialog.ShowDialog();
            return dialog.Result;
        }

        #endregion Public Methods

        #region Private Methods

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            Result = ButtonResult.Cancel;
            base.DialogResult = false;
            Close();
        }

        private void NoButton_Click(object sender, RoutedEventArgs e)
        {
            Result = ButtonResult.No;
            base.DialogResult = false;
            Close();
        }

        private void TitleBar_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.ChangedButton == System.Windows.Input.MouseButton.Left)
            {
                DragMove();
            }
        }

        private void YesButton_Click(object sender, RoutedEventArgs e)
        {
            Result = NoButton.Visibility == Visibility.Visible ? ButtonResult.Yes : ButtonResult.OK;
            base.DialogResult = true;
            Close();
        }

        #endregion Private Methods
    }
}
