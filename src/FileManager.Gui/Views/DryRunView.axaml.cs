using Avalonia.Controls;

namespace FileManager.Gui.Views;

/// <summary>The dry-run view: renders the report sections (matches, commands, writes, deletions, disposition).</summary>
public sealed partial class DryRunView : UserControl
{
    /// <summary>Initializes the view from its XAML.</summary>
    public DryRunView() => InitializeComponent();
}
