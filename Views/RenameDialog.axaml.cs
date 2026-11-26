using System;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace ComicReader.Views
{
    /// <summary>
    /// Modal dialog allowing the user to edit a comic's display name without touching disk files.
    /// </summary>
    public partial class RenameDialog : Window
    {
        public RenameDialog()
        {
            InitializeComponent();
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="RenameDialog"/> class.
        /// </summary>
        public RenameDialog(string currentName)
        {
            InitializeComponent();
            NameTextBox.Text = currentName;
            Opened += OnOpened;
        }

        private void OnOpened(object? sender, EventArgs e)
        {
            NameTextBox.Focus();
            NameTextBox.SelectAll();
        }

        private void OnSaveClick(object? sender, RoutedEventArgs e)
        {
            Close(NameTextBox.Text);
        }

        private void OnCancelClick(object? sender, RoutedEventArgs e)
        {
            Close(null);
        }
    }
}
