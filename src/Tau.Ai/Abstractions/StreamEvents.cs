namespace Tau.Ai;

/// <summary>
/// Stream events forming a nested lifecycle:
///   start → (text|thinking|toolcall)_(start → delta* → end)* → done|error
/// </summary>
public abstract record StreamEvent(string Type);

public sealed record StartEvent(AssistantMessage Partial) : StreamEvent("start");

public sealed record TextStartEvent(int ContentIndex, AssistantMessage Partial) : StreamEvent("text_start");
public sealed record TextDeltaEvent(int ContentIndex, string Delta, AssistantMessage Partial) : StreamEvent("text_delta");
public sealed record TextEndEvent(int ContentIndex, AssistantMessage Partial) : StreamEvent("text_end");

public sealed record ThinkingStartEvent(int ContentIndex, AssistantMessage Partial) : StreamEvent("thinking_start");
public sealed record ThinkingDeltaEvent(int ContentIndex, string Delta, AssistantMessage Partial) : StreamEvent("thinking_delta");
public sealed record ThinkingEndEvent(int ContentIndex, AssistantMessage Partial) : StreamEvent("thinking_end");

public sealed record ToolCallStartEvent(int ContentIndex, AssistantMessage Partial) : StreamEvent("toolcall_start");
public sealed record ToolCallDeltaEvent(int ContentIndex, string Delta, AssistantMessage Partial) : StreamEvent("toolcall_delta");
public sealed record ToolCallEndEvent(int ContentIndex, AssistantMessage Partial) : StreamEvent("toolcall_end");

public sealed record DoneEvent(AssistantMessage Message) : StreamEvent("done");
public sealed record ErrorEvent(string Error, AssistantMessage? Partial = null) : StreamEvent("error");
