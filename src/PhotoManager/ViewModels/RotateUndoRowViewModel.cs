using PhotoManager.Infrastructure;
using PhotoManager.Models;

namespace PhotoManager.ViewModels;

public sealed class RotateUndoRowViewModel(OrientationUndoEntry entry, Action selectionChanged) : ObservableObject
{
    private bool _isSelected;

    public string Path => entry.Path;
    public string FileName => System.IO.Path.GetFileName(entry.Path);
    public string BackupPath => entry.BackupPath;
    public string OrientationBefore => entry.OrientationBefore.ToString();
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value))
            {
                selectionChanged();
            }
        }
    }
}
