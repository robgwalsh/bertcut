using System.Windows;
using BertCut.Core.Picking;
using Microsoft.Win32;

namespace BertCut.App;

/// <summary>
/// The Windows common file dialog: the picker that is always there.
/// </summary>
/// <remarks>
/// Modal on its owner, so the task it returns is already complete — the wait happened inside
/// <c>ShowDialog</c>. It is also what every other picker falls back to.
/// </remarks>
internal sealed class WindowsDialogPicker(Window owner) : IFilePicker
{
    public Task<string?> PickAsync(PickRequest request, CancellationToken cancellation = default)
    {
        FileDialog dialog = request.Kind == PickKind.Save
            ? new SaveFileDialog()
            : new OpenFileDialog();

        dialog.Title = request.Title;
        dialog.Filter = request.Filter;

        if (request.DefaultName is { Length: > 0 } name) dialog.FileName = name;
        if (request.StartDirectory is { Length: > 0 } start) dialog.InitialDirectory = start;

        return Task.FromResult(dialog.ShowDialog(owner) == true ? dialog.FileName : null);
    }
}
