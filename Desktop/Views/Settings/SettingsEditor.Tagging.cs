using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;

namespace MeowChan;

// The tagging group of the settings (PT7): how many tags the stamp holds, how many queue entries one walk opens, how many queue decisions
// of a walk can be taken back (W3-18), how many cards the card
// mode shows, whether a walk starts in the card mode, and which decision each of the five cards makes. The numbers have default, minimum
// and maximum from Config and are validated like every limit; the card slots are choices that never repeat (picking a decision another
// card has swaps the two). Wired into the page through the tagging queue partial (SettingsEditor.TagQueue.cs), texts in TaggingSettingsCatalog.
public partial class SettingsEditor
{
    public int TagStampSizeMinimum => Config.TagStampSizeMinimum;
    public int TagStampSizeMaximum => Config.TagStampSizeMaximum;
    public int TagQueueWalkWindowMinimum => Config.TagQueueWalkWindowMinimum;
    public int TagQueueWalkWindowMaximum => Config.TagQueueWalkWindowMaximum;
    public int TagQueueUndoDepthMinimum => Config.TagQueueUndoDepthMinimum;
    public int TagQueueUndoDepthMaximum => Config.TagQueueUndoDepthMaximum;
    public int TaggingCardCountMinimum => Config.TaggingCardCountMinimum;
    public int TaggingCardCountMaximum => Config.TaggingCardCountMaximum;

    public string TaggingGroupLabel => Texts.T("settings.tagging.group");

    private decimal? _tagStampSize;
    public decimal? TagStampSize { get => _tagStampSize; set => SetField(ref _tagStampSize, value, nameof(TagStampSize)); }
    public string TagStampSizeLabel => Texts.T("settings.tagging.stampSize");
    public string TagStampSizeHint => Texts.T("settings.tagging.stampSize.hint") + " " + Texts.T("settings.reader.range", Config.TagStampSizeMinimum, Config.TagStampSizeMaximum);
    public string TagStampSizeHelp => FieldHelp(nameof(TagStampSizeInput), TagStampSizeHint);

    private decimal? _tagQueueWalkWindow;
    public decimal? TagQueueWalkWindow { get => _tagQueueWalkWindow; set => SetField(ref _tagQueueWalkWindow, value, nameof(TagQueueWalkWindow)); }
    public string TagQueueWalkWindowLabel => Texts.T("settings.tagging.walkWindow");
    public string TagQueueWalkWindowHint => Texts.T("settings.tagging.walkWindow.hint") + " " + Texts.T("settings.reader.range", Config.TagQueueWalkWindowMinimum, Config.TagQueueWalkWindowMaximum);
    public string TagQueueWalkWindowHelp => FieldHelp(nameof(TagQueueWalkWindowInput), TagQueueWalkWindowHint);

    private decimal? _tagQueueUndoDepth;
    public decimal? TagQueueUndoDepth { get => _tagQueueUndoDepth; set => SetField(ref _tagQueueUndoDepth, value, nameof(TagQueueUndoDepth)); }
    public string TagQueueUndoDepthLabel => Texts.T("settings.tagging.undoDepth");
    public string TagQueueUndoDepthHint => Texts.T("settings.tagging.undoDepth.hint") + " " + Texts.T("settings.reader.range", Config.TagQueueUndoDepthMinimum, Config.TagQueueUndoDepthMaximum);
    public string TagQueueUndoDepthHelp => FieldHelp(nameof(TagQueueUndoDepthInput), TagQueueUndoDepthHint);

    private decimal? _taggingCardCount;
    public decimal? TaggingCardCount { get => _taggingCardCount; set => SetField(ref _taggingCardCount, value, nameof(TaggingCardCount)); }
    public string TaggingCardCountLabel => Texts.T("settings.tagging.cardCount");
    public string TaggingCardCountHint => Texts.T("settings.tagging.cardCount.hint") + " " + Texts.T("settings.reader.range", Config.TaggingCardCountMinimum, Config.TaggingCardCountMaximum);
    public string TaggingCardCountHelp => FieldHelp(nameof(TaggingCardCountInput), TaggingCardCountHint);

    // K in the gallery saves the same value as a view preference. The page only writes it when the person changed it here, so a K pressed
    // while the page was open is not undone by an unrelated save.
    private bool _taggingCardsDefault, _initialTaggingCardsDefault;
    public bool TaggingCardsDefault { get => _taggingCardsDefault; set => SetField(ref _taggingCardsDefault, value, nameof(TaggingCardsDefault)); }
    public string TaggingCardsDefaultLabel => Texts.T("settings.tagging.cardsStart");
    public string TaggingCardsDefaultHint => Texts.T("settings.tagging.cardsStart.hint");

    public string TaggingCardsHeadingLabel => Texts.T("settings.tagging.cards");
    public string TaggingCardSlotsHint => Texts.T("settings.tagging.cards.hint");
    public string TaggingCard1Label => Texts.T("settings.tagging.cardSlot", 1);
    public string TaggingCard2Label => Texts.T("settings.tagging.cardSlot", 2);
    public string TaggingCard3Label => Texts.T("settings.tagging.cardSlot", 3);
    public string TaggingCard4Label => Texts.T("settings.tagging.cardSlot", 4);
    public string TaggingCard5Label => Texts.T("settings.tagging.cardSlot", 5);

    public IReadOnlyList<SettingsChoice> TaggingCardChoices { get; private set; } = Array.Empty<SettingsChoice>();
    // Lazy: editors that tests create without the constructor must not fail on the slots.
    private SettingsChoice?[]? _taggingCardSlotStore;
    private SettingsChoice?[] _taggingCardSlots => _taggingCardSlotStore ??= new SettingsChoice?[TaggingCardCommands.CardKeys];
    private bool _taggingCardsLoading;
    private static readonly string[] TaggingCardSlotProperties =
    {
        nameof(SelectedTaggingCard1), nameof(SelectedTaggingCard2), nameof(SelectedTaggingCard3), nameof(SelectedTaggingCard4), nameof(SelectedTaggingCard5),
    };
    public SettingsChoice? SelectedTaggingCard1 { get => _taggingCardSlots[0]; set => SetTaggingCardSlot(0, value); }
    public SettingsChoice? SelectedTaggingCard2 { get => _taggingCardSlots[1]; set => SetTaggingCardSlot(1, value); }
    public SettingsChoice? SelectedTaggingCard3 { get => _taggingCardSlots[2]; set => SetTaggingCardSlot(2, value); }
    public SettingsChoice? SelectedTaggingCard4 { get => _taggingCardSlots[3]; set => SetTaggingCardSlot(3, value); }
    public SettingsChoice? SelectedTaggingCard5 { get => _taggingCardSlots[4]; set => SetTaggingCardSlot(4, value); }

    private void SetTaggingCardSlot(int index, SettingsChoice? value)
    {
        if (Equals(_taggingCardSlots[index], value)) return;
        // A transient null (the items were replaced) and the load go straight in; a real choice keeps the five slots different.
        if (value == null || _taggingCardsLoading || _taggingCardSlots.Any(slot => slot == null))
        {
            SetField(ref _taggingCardSlots[index], value, TaggingCardSlotProperties[index]);
            return;
        }
        PlaceTaggingCards(TaggingSettings.WithCard(TaggingCardSlotIds(), index, value.Value));
    }

    private string[] TaggingCardSlotIds() => _taggingCardSlots.Select(slot => slot?.Value ?? string.Empty).ToArray();

    private void PlaceTaggingCards(IReadOnlyList<string> ids)
    {
        for (int index = 0; index < _taggingCardSlots.Length; index++)
        {
            SettingsChoice? choice = TaggingCardChoices.FirstOrDefault(item => item.Value == ids[index]);
            if (choice != null && !Equals(_taggingCardSlots[index], choice)) SetField(ref _taggingCardSlots[index], choice, TaggingCardSlotProperties[index]);
        }
    }

    // The labels follow the interface language: the choices are rebuilt and the same decisions chosen again (semantic values first, because
    // replacing the items can clear the selections for a moment).
    private void RefreshTaggingChoices(IReadOnlyList<string> ids)
    {
        _taggingCardsLoading = true;
        try
        {
            TaggingCardChoices = TaggingCardActions.All.Select(action => new SettingsChoice(action.Id, action.Label())).ToArray();
            Raise(new[] { nameof(TaggingCardChoices) });
            PlaceTaggingCards(ids);
        }
        finally { _taggingCardsLoading = false; }
    }

    private void RefreshTaggingChoiceLabels()
    {
        string[] ids = TaggingCardSlotIds();
        RefreshTaggingChoices(ids.All(id => id.Length > 0) ? ids : TaggingSettings.CardSlotIds(_config ?? new Config()).ToArray());
    }

    private void LoadTaggingDraft(Config config)
    {
        TagStampSize = config.TagStampSize;
        ResetNumber(TagStampSizeInput, TagStampSize.Value);
        TagQueueWalkWindow = config.TagQueueWalkWindow;
        ResetNumber(TagQueueWalkWindowInput, TagQueueWalkWindow.Value);
        TagQueueUndoDepth = config.TagQueueUndoDepth;
        ResetNumber(TagQueueUndoDepthInput, TagQueueUndoDepth.Value);
        TaggingCardCount = config.TaggingCardCount;
        ResetNumber(TaggingCardCountInput, TaggingCardCount.Value);
        TaggingCardsDefault = _initialTaggingCardsDefault = config.TaggingCardsDefault;
        RefreshTaggingChoices(TaggingSettings.CardSlotIds(config));
    }

    private void ApplyTaggingDraft(Config candidate)
    {
        candidate.TagStampSize = GetInteger(TagStampSize, TagStampSizeLabel, Config.TagStampSizeMinimum, Config.TagStampSizeMaximum);
        candidate.TagQueueWalkWindow = GetInteger(TagQueueWalkWindow, TagQueueWalkWindowLabel, Config.TagQueueWalkWindowMinimum, Config.TagQueueWalkWindowMaximum);
        candidate.TagQueueUndoDepth = GetInteger(TagQueueUndoDepth, TagQueueUndoDepthLabel, Config.TagQueueUndoDepthMinimum, Config.TagQueueUndoDepthMaximum);
        candidate.TaggingCardCount = GetInteger(TaggingCardCount, TaggingCardCountLabel, Config.TaggingCardCountMinimum, Config.TaggingCardCountMaximum);
        candidate.TaggingCards = TaggingSettings.CardsToSave(TaggingCardSlotIds());
        if (TaggingCardsDefault != _initialTaggingCardsDefault) candidate.TaggingCardsDefault = TaggingCardsDefault;
    }

    private IEnumerable<(NumericUpDown Input, decimal? Value, string Label, string Unit, decimal Min, decimal Max, bool Integer)> TaggingNumericFields()
    {
        yield return (TagStampSizeInput, TagStampSize, TagStampSizeLabel, string.Empty, Config.TagStampSizeMinimum, Config.TagStampSizeMaximum, true);
        yield return (TagQueueWalkWindowInput, TagQueueWalkWindow, TagQueueWalkWindowLabel, string.Empty, Config.TagQueueWalkWindowMinimum, Config.TagQueueWalkWindowMaximum, true);
        yield return (TagQueueUndoDepthInput, TagQueueUndoDepth, TagQueueUndoDepthLabel, string.Empty, Config.TagQueueUndoDepthMinimum, Config.TagQueueUndoDepthMaximum, true);
        yield return (TaggingCardCountInput, TaggingCardCount, TaggingCardCountLabel, string.Empty, Config.TaggingCardCountMinimum, Config.TaggingCardCountMaximum, true);
    }

    private static readonly string[] TaggingTextProperties =
    {
        nameof(TaggingGroupLabel),
        nameof(TagStampSizeLabel), nameof(TagStampSizeHint), nameof(TagStampSizeHelp),
        nameof(TagQueueWalkWindowLabel), nameof(TagQueueWalkWindowHint), nameof(TagQueueWalkWindowHelp),
        nameof(TagQueueUndoDepthLabel), nameof(TagQueueUndoDepthHint), nameof(TagQueueUndoDepthHelp),
        nameof(TaggingCardCountLabel), nameof(TaggingCardCountHint), nameof(TaggingCardCountHelp),
        nameof(TaggingCardsDefaultLabel), nameof(TaggingCardsDefaultHint),
        nameof(TaggingCardsHeadingLabel), nameof(TaggingCardSlotsHint),
        nameof(TaggingCard1Label), nameof(TaggingCard2Label), nameof(TaggingCard3Label), nameof(TaggingCard4Label), nameof(TaggingCard5Label),
    };
    private static readonly string[] TaggingHelpProperties = { nameof(TagStampSizeHelp), nameof(TagQueueWalkWindowHelp), nameof(TagQueueUndoDepthHelp), nameof(TaggingCardCountHelp) };
}
