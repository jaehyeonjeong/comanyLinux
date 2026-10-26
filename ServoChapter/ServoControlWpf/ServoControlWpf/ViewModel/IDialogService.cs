namespace ServoControlWpf.ViewModel;

/// <summary>ViewModel 이 MessageBox 를 직접 알지 않도록 하는 작은 창구 (View 가 구현)</summary>
public interface IDialogService
{
    void ShowError(string text);
    bool Confirm(string title, string text);
    string? PickFolder(string title, string? initialDir);
}
