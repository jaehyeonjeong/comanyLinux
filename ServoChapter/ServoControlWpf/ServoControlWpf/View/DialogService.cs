using System.Windows;
using Microsoft.Win32;
using ServoControlWpf.ViewModel;

namespace ServoControlWpf.View;

/// <summary>MessageBox / 폴더 선택 대화상자 (ViewModel 의 IDialogService 구현)</summary>
public sealed class DialogService : IDialogService
{
    private readonly Window _owner;

    public DialogService(Window owner) => _owner = owner;

    public void ShowError(string text)
        => MessageBox.Show(_owner, text, "서보 제어", MessageBoxButton.OK, MessageBoxImage.Warning);

    public bool Confirm(string title, string text)
        => MessageBox.Show(_owner, text, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    public string? PickFolder(string title, string? initialDir)
    {
        var dlg = new OpenFolderDialog { Title = title, Multiselect = false };
        if (!string.IsNullOrWhiteSpace(initialDir))
            dlg.InitialDirectory = initialDir;
        return dlg.ShowDialog(_owner) == true ? dlg.FolderName : null;
    }
}
