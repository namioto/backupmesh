using System.Windows;

namespace BackupMesh.Storage.App;

public partial class RegisterDeviceWindow : Window
{
    private readonly MainWindowViewModel _viewModel;

    public RegisterDeviceWindow(MainWindowViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
    }

    private void OnRegisterDriveClick(object sender, RoutedEventArgs e)
    {
        _viewModel.RegisterDeviceCommand.Execute(null);
        Close();
    }

    private void OnRegisterFolderClick(object sender, RoutedEventArgs e)
    {
        _viewModel.RegisterFolderCommand.Execute(null);
        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => Close();
}
