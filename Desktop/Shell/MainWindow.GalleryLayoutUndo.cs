using System;
using System.Threading.Tasks;
using MeowChan.Desktop;
using MeowChan.Desktop.Feedback;
using MeowChan.Desktop.Localization;

namespace MeowChan;

public partial class MainWindow
{
    // GL3: "Reset layout" of the gallery's layout menu says what it did and offers the way back. The viewer raises LayoutResetPerformed with
    // the choice it replaced; the toast's "Undo" puts that choice back through the viewer's ChangeLayoutChoice, so it is saved per profile and
    // mode like every other change (GL4). The toast is the transient kind that the open gallery does not suppress (WV12's notice uses it too).
    private FeedbackToastContent? _galleryLayoutToast;

    private void OnGalleryLayoutReset(GalleryViewer owner, GalleryChromeMode mode, GalleryLayoutChoice previous)
    {
        if (!ReferenceEquals(_gallery, owner) || _isClosing) return;
        CloseGalleryLayoutToast();
        string modeName = Texts.T(mode == GalleryChromeMode.Tag ? "gallery.mode.tag" : "gallery.mode.view");
        FeedbackToastContent? content = null;
        content = ShowTransientActionNotification(Texts.T("gallery.layout.button"), Texts.T("gallery.layout.resetDone", modeName), ToastDuration.Action,
            [
                new FeedbackToastAction(Texts.T("watch.undo"), () =>
                {
                    // Only while nothing else was chosen for the mode since: an undo must not overwrite a newer choice.
                    if (ReferenceEquals(_gallery, owner) && !_isClosing && owner.LayoutChoice(mode) == GalleryLayoutChoice.Default)
                        owner.ChangeLayoutChoice(mode, previous);
                    return Task.CompletedTask;
                })
            ], () => { if (ReferenceEquals(_galleryLayoutToast, content)) _galleryLayoutToast = null; });
        _galleryLayoutToast = content;
    }

    private void CloseGalleryLayoutToast()
    {
        FeedbackToastContent? content = _galleryLayoutToast;
        _galleryLayoutToast = null;
        if (content != null) _notificationManager.Close(content);
    }
}
