using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Printing;
using Windows.Graphics.Printing;

namespace PdfReader.Services;

/// <summary>Sends already-rendered page images through the Windows print dialog.</summary>
public sealed class PrintService(Window window)
{
    public static bool IsSupported => PrintManager.IsSupported();

    /// <summary>Shows the system print dialog and completes when the job is submitted or the dialog is dismissed.</summary>
    public async Task<bool> PrintAsync(string title, IReadOnlyList<ImageSource> pages)
    {
        var handle = WinRT.Interop.WindowNative.GetWindowHandle(window);
        var manager = PrintManagerInterop.GetForWindow(handle);
        var document = new PrintDocument(); var source = document.DocumentSource;
        var sheets = new List<UIElement>();
        var finished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        document.Paginate += (_, e) =>
        {
            // Each PDF page is centered on one sheet at the paper size chosen in the dialog.
            var paper = e.PrintTaskOptions.GetPageDescription(0).PageSize;
            sheets.Clear();
            foreach (var page in pages)
            {
                var sheet = new Grid { Width = paper.Width, Height = paper.Height, Background = new SolidColorBrush(Colors.White) };
                sheet.Children.Add(new Image { Source = page, Stretch = Stretch.Uniform });
                sheets.Add(sheet);
            }
            document.SetPreviewPageCount(sheets.Count, PreviewPageCountType.Final);
        };
        document.GetPreviewPage += (_, e) => { if (e.PageNumber >= 1 && e.PageNumber <= sheets.Count) document.SetPreviewPage(e.PageNumber, sheets[e.PageNumber - 1]); };
        document.AddPages += (_, _) => { foreach (var sheet in sheets) document.AddPage(sheet); document.AddPagesComplete(); };

        void Requested(PrintManager sender, PrintTaskRequestedEventArgs args)
        {
            var task = args.Request.CreatePrintTask(title, request => request.SetSource(source));
            task.Completed += (_, completion) => finished.TrySetResult(completion.Completion == PrintTaskCompletion.Submitted);
        }
        manager.PrintTaskRequested += Requested;
        try
        {
            if (!await PrintManagerInterop.ShowPrintUIForWindowAsync(handle)) return false;
            return await finished.Task;
        }
        finally { manager.PrintTaskRequested -= Requested; }
    }
}
