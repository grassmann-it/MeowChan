using System;
using System.Linq;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Interop;

namespace MeowChan;

/// <summary>
/// The "Filename template for exported media" row of the Downloads page: an empty template (the default) keeps the current naming; a template
/// shows a live preview of three sample files and its problem inline while typing, and a problem blocks Save. The value is a plain setting
/// (<see cref="Config.ExportFilenameTemplate"/>) that the original export reads when it names the copied files.
/// </summary>
public partial class SettingsEditor
{
    private string _exportFilenameTemplate = string.Empty;

    private static readonly string[] ExportFilenameTemplateDerivedProperties =
    {
        nameof(ExportFilenameTemplateHelp), nameof(ExportFilenameTemplateError), nameof(HasExportFilenameTemplateError),
        nameof(ExportFilenameTemplatePreviewText)
    };

    public string ExportFilenameTemplate
    {
        get => _exportFilenameTemplate;
        set
        {
            string next = value ?? string.Empty;
            if (string.Equals(_exportFilenameTemplate, next, StringComparison.Ordinal)) return;
            SetField(ref _exportFilenameTemplate, next, nameof(ExportFilenameTemplate));
            Raise(ExportFilenameTemplateDerivedProperties);
        }
    }

    public string ExportFilenameTemplateLabel => Texts.T("settings.exportFilenameTemplate");
    public string ExportFilenameTemplateHint => Texts.T("settings.exportFilenameTemplateHint",
        MeowChan.Interop.ExportFilenameTemplate.TokenList, MeowChan.Interop.ExportFilenameTemplate.MaxTemplateLength);
    public string ExportFilenameTemplateHelp => ExportFilenameTemplateHint + (HasExportFilenameTemplateError ? " " + ExportFilenameTemplateError : string.Empty);
    public string ExportFilenameTemplatePlaceholder => Texts.T("settings.exportFilenameTemplatePlaceholder");

    public bool HasExportFilenameTemplateError => !MeowChan.Interop.ExportFilenameTemplate.Check(_exportFilenameTemplate).Ok;

    /// <summary>The problem of the typed template in the current language; empty while the template is empty or usable.</summary>
    public string ExportFilenameTemplateError => ExportFilenameTemplateProblemText(MeowChan.Interop.ExportFilenameTemplate.Check(_exportFilenameTemplate).Problem);

    /// <summary>Three sample files and the names they would get; a short sentence while the template is off or has a problem.</summary>
    public string ExportFilenameTemplatePreviewText
    {
        get
        {
            ExportFilenamePreview preview = MeowChan.Interop.ExportFilenameTemplate.Preview(_exportFilenameTemplate);
            if (!MeowChan.Interop.ExportFilenameTemplate.IsActive(_exportFilenameTemplate)) return Texts.T("settings.exportFilenameTemplateOff");
            if (!preview.Check.Ok) return string.Empty;
            return Texts.T("settings.exportFilenameTemplatePreview") + "\n"
                + string.Join("\n", preview.Lines.Select(line => line.Original + "  →  " + line.Result));
        }
    }

    private static string ExportFilenameTemplateProblemText(ExportFilenameTemplateProblem problem) => problem switch
    {
        ExportFilenameTemplateProblem.None => string.Empty,
        ExportFilenameTemplateProblem.TooLong => Texts.T("settings.exportFilenameTemplate.tooLong", MeowChan.Interop.ExportFilenameTemplate.MaxTemplateLength),
        ExportFilenameTemplateProblem.InvalidTemplate => Texts.T("settings.exportFilenameTemplate.invalid", MeowChan.Interop.ExportFilenameTemplate.TokenList),
        ExportFilenameTemplateProblem.MustEndWithExtension => Texts.T("settings.exportFilenameTemplate.extension", MeowChan.Interop.ExportFilenameTemplate.ExtensionEnding),
        ExportFilenameTemplateProblem.UnsafeName => Texts.T("settings.exportFilenameTemplate.unsafe"),
        ExportFilenameTemplateProblem.NameTooLong => Texts.T("settings.exportFilenameTemplate.nameTooLong", MeowChan.Interop.ExportFilenameTemplate.MaxResultBytes),
        _ => Texts.T("settings.exportFilenameTemplate.unusable")
    };

    private static readonly string[] ExportFilenameTemplateTextProperties =
    {
        nameof(ExportFilenameTemplateLabel), nameof(ExportFilenameTemplateHint), nameof(ExportFilenameTemplatePlaceholder)
    };

    /// <summary>A language change refreshes text only; it is not a change of any value.</summary>
    private void RaiseExportFilenameTemplateTextsChanged() =>
        Raise(ExportFilenameTemplateTextProperties.Concat(ExportFilenameTemplateDerivedProperties));

    private void LoadExportFilenameTemplateDraft(Config config) => ExportFilenameTemplate = config.ExportFilenameTemplate;

    private void ApplyExportFilenameTemplateDraft(Config candidate) => candidate.ExportFilenameTemplate = ExportFilenameTemplate;

    /// <summary>Save validation: the inline error is already shown; this moves the focus to the field and stops the save.</summary>
    private bool ValidateExportFilenameTemplateDraft(bool focusError = true)
    {
        if (!HasExportFilenameTemplateError) return true;
        _firstInvalidInput = ExportFilenameTemplateInput;
        return FinishFieldValidation(focusError);
    }
}
