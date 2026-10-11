using System.Runtime.InteropServices;

namespace PdfReader.Core;

/// <summary>The small part of the PDFium C API that Folio uses. Every call must hold <see cref="Sync"/>.</summary>
internal static unsafe class PdfiumNative
{
    private const string Library = "pdfium";
    /// <summary>PDFium is not thread-safe, so one lock serializes every call in the process.</summary>
    public static readonly object Sync = new();
    private static bool _initialized;

    public const int ErrorPassword = 4;
    public const int RenderAnnotations = 0x01, RenderForPrinting = 0x800;
    public const int BitmapBgra = 4;
    public const int ActionGoTo = 1;

    [StructLayout(LayoutKind.Sequential)]
    public struct PdfiumFileAccess
    {
        public uint Length;
        public delegate* unmanaged[Cdecl]<nint, uint, byte*, uint, int> GetBlock;
        public nint Parameter;
    }

    public static void EnsureInitialized()
    {
        if (_initialized) return;
        FPDF_InitLibrary(); _initialized = true;
    }

    [DllImport(Library)] private static extern void FPDF_InitLibrary();
    [DllImport(Library)] public static extern nint FPDF_LoadCustomDocument(PdfiumFileAccess* access, [MarshalAs(UnmanagedType.LPUTF8Str)] string? password);
    [DllImport(Library)] public static extern void FPDF_CloseDocument(nint document);
    [DllImport(Library)] public static extern uint FPDF_GetLastError();
    [DllImport(Library)] public static extern int FPDF_GetPageCount(nint document);
    [DllImport(Library)] public static extern uint FPDF_GetDocPermissions(nint document);
    [DllImport(Library)] public static extern int FPDF_GetPageSizeByIndex(nint document, int index, out double width, out double height);
    [DllImport(Library)] public static extern nint FPDF_LoadPage(nint document, int index);
    [DllImport(Library)] public static extern void FPDF_ClosePage(nint page);
    [DllImport(Library)] public static extern int FPDF_PageToDevice(nint page, int startX, int startY, int sizeX, int sizeY, int rotate, double pageX, double pageY, out int deviceX, out int deviceY);
    [DllImport(Library)] public static extern int FPDF_DeviceToPage(nint page, int startX, int startY, int sizeX, int sizeY, int rotate, int deviceX, int deviceY, out double pageX, out double pageY);

    [DllImport(Library)] public static extern nint FPDFBitmap_CreateEx(int width, int height, int format, nint firstScan, int stride);
    [DllImport(Library)] public static extern void FPDFBitmap_FillRect(nint bitmap, int left, int top, int width, int height, uint color);
    [DllImport(Library)] public static extern void FPDFBitmap_Destroy(nint bitmap);
    [DllImport(Library)] public static extern void FPDF_RenderPageBitmap(nint bitmap, nint page, int startX, int startY, int sizeX, int sizeY, int rotate, int flags);

    [DllImport(Library)] public static extern nint FPDFDOC_InitFormFillEnvironment(nint document, nint formInfo);
    [DllImport(Library)] public static extern void FPDFDOC_ExitFormFillEnvironment(nint form);
    [DllImport(Library)] public static extern void FPDF_SetFormFieldHighlightAlpha(nint form, byte alpha);
    [DllImport(Library)] public static extern void FORM_OnAfterLoadPage(nint page, nint form);
    [DllImport(Library)] public static extern void FORM_OnBeforeClosePage(nint page, nint form);
    [DllImport(Library)] public static extern void FPDF_FFLDraw(nint form, nint bitmap, nint page, int startX, int startY, int sizeX, int sizeY, int rotate, int flags);

    [DllImport(Library)] public static extern nint FPDFText_LoadPage(nint page);
    [DllImport(Library)] public static extern void FPDFText_ClosePage(nint textPage);
    [DllImport(Library)] public static extern int FPDFText_CountChars(nint textPage);
    [DllImport(Library)] public static extern uint FPDFText_GetUnicode(nint textPage, int index);
    [DllImport(Library)] public static extern int FPDFText_GetCharBox(nint textPage, int index, out double left, out double right, out double bottom, out double top);

    [DllImport(Library)] public static extern nint FPDFLink_GetLinkAtPoint(nint page, double x, double y);
    [DllImport(Library)] public static extern nint FPDFLink_GetDest(nint document, nint link);
    [DllImport(Library)] public static extern nint FPDFLink_GetAction(nint link);
    [DllImport(Library)] public static extern uint FPDFAction_GetType(nint action);
    [DllImport(Library)] public static extern nint FPDFAction_GetDest(nint document, nint action);
    [DllImport(Library)] public static extern int FPDFDest_GetDestPageIndex(nint document, nint destination);
}
