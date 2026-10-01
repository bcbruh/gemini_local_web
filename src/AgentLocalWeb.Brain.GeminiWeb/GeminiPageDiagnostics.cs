namespace AgentLocalWeb.Brain.GeminiWeb;

internal enum GeminiPageStage
{
    Composer,
    WaitIdle,
    Baseline,
    FocusComposer,
    Typing,
    VerifyText,
    PressEnterSubmit,
    FindSend,
    LocateSendNode,
    ClickSubmit,
    ConfirmSubmission,
    ReadResponse,
    GenerationStatus,
    TimeoutDiagnostics
}

internal sealed record GeminiPageFailureDiagnostic(
    GeminiPageStage Stage,
    string? CdpMethod,
    int? CdpErrorCode,
    bool EvaluationException)
{
    public string ToLogLine() =>
        $"gemini.failure stage={Stage} cdp={SafeMethod(CdpMethod)} " +
        $"code={CdpErrorCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none"} " +
        $"evaluation_exception={EvaluationException}";

    // Never interpolate arbitrary exception messages, method names or CDP payloads.
    private static string SafeMethod(string? method) => method switch
    {
        "Runtime.evaluate" or "DOM.describeNode" or "DOM.focus" or
        "DOM.scrollIntoViewIfNeeded" or "DOM.getBoxModel" or
        "Input.dispatchKeyEvent" or "Input.insertText" or "Input.dispatchMouseEvent" or
        "Page.bringToFront" => method,
        _ => "other"
    };
}
