using System.Windows;

namespace BackupMesh.Storage.App;

public partial class RenameSourceWindow : Window
{
    public string? ResultDisplayName { get; private set; }

    public RenameSourceWindow(string currentDisplayName, string reportedAgentName)
    {
        InitializeComponent();
        DisplayNameText.Text = string.IsNullOrWhiteSpace(currentDisplayName) ? reportedAgentName : currentDisplayName;
        DisplayNameText.SelectAll();
        DisplayNameText.Focus();
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        ResultDisplayName = DisplayNameText.Text;
        DialogResult = true;
        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => Close();
}
