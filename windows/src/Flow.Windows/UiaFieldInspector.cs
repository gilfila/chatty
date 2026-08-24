using Windows.Win32.UI.Accessibility;

namespace Flow.Windows;

/// <summary>What UI Automation says about the focused element.</summary>
/// <param name="IsPassword">UIA IsPassword — covers browser/Electron/UWP password fields
/// that the Win32 ES_PASSWORD style check cannot see.</param>
/// <param name="IsTextEditable">The control type is one that accepts dictated text
/// (Edit, Document, ComboBox). Anything else is refused rather than guessed at.</param>
/// <param name="FieldSignature">Stable-per-element identity from the UIA runtime id —
/// the value that closes the same-window field-change gap once TargetDescriptor
/// carries it (see TargetIdentityGapTests).</param>
public sealed record FieldClassification(bool IsPassword, bool IsTextEditable, string FieldSignature);

/// <summary>
/// Classifies the focused field via UI Automation, fail-closed: if the field cannot be
/// positively classified within the timeout — UIA error, hang, no focused element —
/// the answer is null and the caller must refuse to start recording. Per the locked
/// product decision, there is no record-only fallback for an unclassifiable field,
/// because an unclassifiable field may be a password field.
/// </summary>
public sealed class UiaFieldInspector
{
    // UIA_ControlTypeIds that accept text. Deliberately tight; expected to need tuning
    // during real-Windows validation (terminals and some editors report other types,
    // and widening the list is a reviewed decision, not a default).
    private const int UIA_EditControlTypeId = 50004;
    private const int UIA_ComboBoxControlTypeId = 50003;
    private const int UIA_DocumentControlTypeId = 50030;

    private static readonly Guid CLSID_CUIAutomation = new("ff48dba4-60ef-4201-aa87-54103eef594e");

    public TimeSpan Timeout { get; init; } = TimeSpan.FromMilliseconds(400);

    /// <summary>Null means "could not positively classify" — the caller refuses.</summary>
    public FieldClassification? TryClassifyFocusedField()
    {
        FieldClassification? result = null;

        // UIA COM calls can block indefinitely on an unresponsive provider. Run each
        // classification on its own thread and abandon it on timeout; capture happens
        // once per key press, so the cost is acceptable and the timeout is the fail-closed
        // path, not an error path.
        var worker = new Thread(() =>
        {
            try
            {
                result = Classify();
            }
            catch
            {
                result = null; // fail closed
            }
        })
        {
            IsBackground = true,
            Name = "flow-uia-classify",
        };
        worker.Start();
        if (!worker.Join(Timeout)) return null;
        return result;
    }

    private static unsafe FieldClassification? Classify()
    {
        var comType = Type.GetTypeFromCLSID(CLSID_CUIAutomation);
        if (comType is null) return null;
        if (Activator.CreateInstance(comType) is not IUIAutomation automation) return null;

        var element = automation.GetFocusedElement();
        if (element is null) return null;

        bool isPassword = element.CurrentIsPassword;
        var editable = (int)element.CurrentControlType
            is UIA_EditControlTypeId or UIA_ComboBoxControlTypeId or UIA_DocumentControlTypeId;

        // A missing runtime id means the element has no stable identity — without it the
        // pre-paste same-field check cannot be honest, so classification fails.
        var runtimeId = ReadRuntimeId((nint)element.GetRuntimeId());
        if (runtimeId.Count == 0) return null;
        var signature = "uia:" + string.Join('.', runtimeId);

        return new FieldClassification(isPassword, editable, signature);
    }

    /// <summary>Reads and frees the caller-owned SAFEARRAY of int from GetRuntimeId,
    /// through documented oleaut32 accessors rather than assumptions about its layout.</summary>
    private static List<int> ReadRuntimeId(nint safeArray)
    {
        var ids = new List<int>();
        if (safeArray == 0) return ids;
        try
        {
            if (SafeArrayGetDim(safeArray) != 1) return ids;
            if (SafeArrayGetLBound(safeArray, 1, out var lo) != 0) return ids;
            if (SafeArrayGetUBound(safeArray, 1, out var hi) != 0) return ids;
            for (var i = lo; i <= hi; i++)
            {
                var index = i;
                if (SafeArrayGetElement(safeArray, ref index, out int value) == 0)
                    ids.Add(value);
            }
            return ids;
        }
        finally
        {
            SafeArrayDestroy(safeArray);
        }
    }

    [System.Runtime.InteropServices.DllImport("oleaut32.dll")]
    private static extern uint SafeArrayGetDim(nint psa);

    [System.Runtime.InteropServices.DllImport("oleaut32.dll")]
    private static extern int SafeArrayGetLBound(nint psa, uint nDim, out int plLbound);

    [System.Runtime.InteropServices.DllImport("oleaut32.dll")]
    private static extern int SafeArrayGetUBound(nint psa, uint nDim, out int plUbound);

    [System.Runtime.InteropServices.DllImport("oleaut32.dll")]
    private static extern int SafeArrayGetElement(nint psa, ref int rgIndices, out int pv);

    [System.Runtime.InteropServices.DllImport("oleaut32.dll")]
    private static extern int SafeArrayDestroy(nint psa);
}
