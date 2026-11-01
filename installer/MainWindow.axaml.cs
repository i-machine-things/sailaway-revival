using Avalonia.Controls;
using Avalonia.Platform.Storage;
using SailawayRevival.Installer.ViewModels;

namespace SailawayRevival.Installer;

public partial class MainWindow : Window
{
    readonly InstallViewModel _vm = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;
        InstallButton.Click += async (_, _) => await _vm.RunInstallAsync();
        BrowseButton.Click += async (_, _) => await OnBrowse();
    }

    async Task OnBrowse()
    {
        var topLevel = GetTopLevel(this);
        if (topLevel == null)
        {
            return;
        }
        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select the Sailaway folder (steamapps/common/Sailaway)",
            AllowMultiple = false,
        });
        if (folders.Count > 0)
        {
            _vm.GamePath = folders[0].Path.LocalPath;
            _vm.NeedsManualGamePath = false;
        }
    }
}
