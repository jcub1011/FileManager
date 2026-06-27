using CommunityToolkit.Mvvm.ComponentModel;
using FileManager.Core.Profiles;

namespace FileManager.Gui.ViewModels;

/// <summary>
/// One editable transformer step (§5.1 <c>Transformers[]</c>). A plain CommunityToolkit POCO that
/// projects to an immutable <see cref="TransformerStep"/> for validation/save. <see cref="IsShell"/>
/// surfaces the higher-risk Shell argument mode so the view can flag it visibly (§9).
/// </summary>
public sealed partial class TransformerStepViewModel : ObservableObject
{
    [ObservableProperty] private int _step = 1;
    [ObservableProperty] private string _name = "step";
    [ObservableProperty] private string _executablePath = string.Empty;

    [NotifyPropertyChangedFor(nameof(IsShell))]
    [ObservableProperty] private ArgumentMode _argumentMode = ArgumentMode.Literal;

    [ObservableProperty] private string _arguments = string.Empty;
    [ObservableProperty] private OutputMode _outputMode = OutputMode.InPlace;
    [ObservableProperty] private string? _expectedOutputExtension;
    [ObservableProperty] private int _timeoutSeconds = 60;

    /// <summary>True for the opt-in, higher-risk <see cref="ArgumentMode.Shell"/> mode (visibly flagged).</summary>
    public bool IsShell => ArgumentMode == ArgumentMode.Shell;

    /// <summary>Projects the editor row to an immutable <see cref="TransformerStep"/>.</summary>
    public TransformerStep ToStep() => new()
    {
        Step = Step,
        Name = Name,
        ExecutablePath = ExecutablePath,
        ArgumentMode = ArgumentMode,
        Arguments = Arguments,
        OutputMode = OutputMode,
        ExpectedOutputExtension = string.IsNullOrWhiteSpace(ExpectedOutputExtension) ? null : ExpectedOutputExtension,
        SuccessExitCodes = null,
        TimeoutSeconds = TimeoutSeconds,
    };
}
