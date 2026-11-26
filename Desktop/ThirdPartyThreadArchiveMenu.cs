using System;
using System.Linq;
using Avalonia.Automation;
using Avalonia.Controls;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;

namespace MeowChan.Desktop;

internal static class ThirdPartyThreadArchiveMenu
{
    private const string Id = "location.open-third-party-archive";
    private const string LabelKey = "archive.command.openThirdParty";
    private const string TipKey = "archive.command.openThirdPartyTip";

    internal static Uri? TargetFor(ResourceId? id)
    {
        if (id is not { Source: "4chan", Thread: { Length: > 0 } thread } || !thread.All(char.IsAsciiDigit))
            return null;

        // One HTTPS thread page per board, based on the 4chan X archive directory.
        // https://github.com/ccd0/4chan-x/blob/master/src/Archive/archives.json
        string? host = id.Board switch
        {
            "adv" or "f" or "hr" or "o" or "pol" or "s4s" or "sp" or "tg" or "trv" or "tv" or "x" => "archive.4plebs.org",
            "a" or "aco" or "an" or "c" or "cgl" or "co" or "d" or "fit" or "g" or "his" or "int" or "k" or "m"
                or "mlp" or "mu" or "q" or "qa" or "r9k" or "trash" or "vr" or "wsg" => "desuarchive.org",
            "qb" or "v" or "vg" or "vm" or "vmg" or "vp" or "vrpg" or "vst" => "arch.b4k.dev",
            "vt" => "archive.palanq.win",
            "3" or "asp" or "b" or "bant" or "biz" or "ck" or "cm" or "diy" or "e" or "fa" or "gd" or "gif" or "h"
                or "hc" or "hm" or "i" or "ic" or "jp" or "lgbt" or "lit" or "n" or "news" or "out" or "p" or "po" or "pw"
                or "qst" or "r" or "s" or "sci" or "soc" or "t" or "toy" or "u" or "vip" or "w" or "wg" or "wsr" or "y" => "archived.moe",
            _ => null
        };
        return host == null ? null : new Uri($"https://{host}/{id.Board}/thread/{thread}/");
    }

    internal static Uri? Add(ContextMenuBuilder builder, ResourceId? thread, Action<Uri> open, Func<bool> ready)
    {
        Uri? target = TargetFor(thread);
        if (target != null)
            builder.Item(Id, Texts.T(LabelKey), () => open(target), availableWhen: ready, icon: "external-link");
        return target;
    }

    internal static void Present(ContextMenu menu, Uri? target)
    {
        if (target == null || menu.Items.OfType<MenuItem>().FirstOrDefault(item => Equals(item.Tag, Id)) is not { } action)
            return;

        void Update()
        {
            string label = Texts.T(LabelKey);
            string tip = Texts.T(TipKey, target.Host);
            action.Header = label;
            AutomationProperties.SetName(action, label);
            ToolTip.SetTip(action, tip);
            AutomationProperties.SetHelpText(action, tip);
        }

        Update();
        ToolTip.SetShowOnDisabled(action, true);
        CommandBindings.ObservePresentation(action, Update);
    }
}
