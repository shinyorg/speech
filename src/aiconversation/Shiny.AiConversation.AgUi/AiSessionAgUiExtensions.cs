using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using AGUI.Abstractions;

namespace Shiny.AiConversation.AgUi;

/// <summary>
/// Runs an <see cref="IAiSession"/> as an AG-UI agent: each call is one AG-UI run, so any AG-UI front
/// end can drive a session. The signature matches the Shiny controls' <c>IAgentSession.RunAsync</c>, so
/// an AgentView takes a session in one line:
/// <code>AgentSession.Create(session.SessionId, session.RunAgUiAsync)</code>
/// </summary>
/// <remarks>
/// <para>The session keeps its own history, so only the newest input is taken from the run: the last
/// user message, an A2UI action (<c>forwardedProps.a2uiAction</c>), or the answers in <c>resume</c>.</para>
/// <para>Mapping: reply text streams as text messages; tool calls (including the session's own) as
/// tool call events with their results; <c>ask_user</c> questions end the run with an <c>interrupt</c>
/// outcome whose metadata carries the choices, and resuming answers them; the <c>render_ui</c> tool
/// (<see cref="AgUiRenderTool"/>) becomes an <c>a2ui-surface</c> activity; an interrupted turn ends
/// <c>cancelled</c> and a failed one with <c>RUN_ERROR</c>. Cancelling the run interrupts the turn.</para>
/// <para>Frontend tools sent in the run are not offered to the model: a session's tools are .NET
/// functions registered with AiConversation.</para>
/// </remarks>
public static class AiSessionAgUiExtensions
{
    /// <summary>The interrupt reason used for <c>ask_user</c> questions.</summary>
    public const string QuestionReason = "ask_user";

    /// <summary>Runs one AG-UI run against the session.</summary>
    public static async IAsyncEnumerable<BaseEvent> RunAgUiAsync(
        this IAiSession session,
        RunAgentInput input,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        var runId = input.RunId ?? Guid.NewGuid().ToString("N");
        var threadId = input.ThreadId ?? session.SessionId;

        yield return new RunStartedEvent { ThreadId = threadId, RunId = runId };

        var ai = ToInput(session, input);
        if (ai is null)
        {
            // nothing new to send (e.g. a frontend tool follow-up, which sessions don't take)
            yield return new RunFinishedEvent { ThreadId = threadId, RunId = runId };
            yield break;
        }

        var turn = session.Send(ai, cancellationToken);
        using var stop = cancellationToken.Register(() => turn.Interrupt());

        string? textId = null;
        var n = 0;
        string Id(string prefix) => $"{prefix}-{turn.TurnId}-{++n}";

        await foreach (var e in turn.Events.WithCancellation(CancellationToken.None))
        {
            switch (e)
            {
                case TextDelta delta when delta.Text.Length > 0:
                    if (textId is null)
                    {
                        textId = Id("msg");
                        yield return new TextMessageStartEvent { MessageId = textId, Role = AGUIRoles.Assistant };
                    }
                    yield return new TextMessageContentEvent { MessageId = textId, Delta = delta.Text };
                    break;

                case ToolCallStarted call:
                    if (textId is not null)
                    {
                        yield return new TextMessageEndEvent { MessageId = textId };
                        textId = null;
                    }
                    yield return new ToolCallStartEvent { ToolCallId = call.CallId, ToolCallName = call.Name };
                    yield return new ToolCallArgsEvent { ToolCallId = call.CallId, Delta = call.ArgumentsJson };
                    yield return new ToolCallEndEvent { ToolCallId = call.CallId };

                    if (call.Name == AgUiRenderTool.ToolName && AgUiRenderTool.ReadOperations(call.ArgumentsJson) is { } operations)
                    {
                        yield return new ActivitySnapshotEvent
                        {
                            MessageId = "surface-" + call.CallId,
                            ActivityType = AgUiRenderTool.ActivityType,
                            Content = ToElement(new JsonObject { [AgUiRenderTool.OperationsKey] = operations }),
                            Replace = true
                        };
                    }
                    break;

                case ToolCallCompleted done:
                    yield return new ToolCallResultEvent { MessageId = Id("tool"), ToolCallId = done.CallId, Content = done.Result };
                    break;

                case TurnCompleted completed:
                    if (textId is not null)
                        yield return new TextMessageEndEvent { MessageId = textId };

                    yield return completed.Result.Outcome == AiTurnOutcome.AwaitingUser && completed.Result.Questions.Count > 0
                        ? new RunFinishedEvent
                        {
                            ThreadId = threadId,
                            RunId = runId,
                            Outcome = new RunFinishedInterruptOutcome { Interrupts = completed.Result.Questions.Select(ToInterrupt).ToList() }
                        }
                        : new RunFinishedEvent { ThreadId = threadId, RunId = runId };
                    yield break;

                case TurnInterrupted:
                    if (textId is not null)
                        yield return new TextMessageEndEvent { MessageId = textId };
                    yield return new RunFinishedEvent { ThreadId = threadId, RunId = runId, Outcome = new RunFinishedCancelledOutcome() };
                    yield break;

                case TurnFailed failed:
                    if (textId is not null)
                        yield return new TextMessageEndEvent { MessageId = textId };
                    yield return new RunErrorEvent { Message = failed.Error.Message, Code = failed.Error.GetType().Name };
                    yield break;
            }
        }
    }

    /// <summary>An <c>ask_user</c> question as an AG-UI interrupt (choices in <c>metadata</c>).</summary>
    public static AGUIInterrupt ToInterrupt(AiQuestion question)
    {
        JsonObject schema;
        var metadata = new JsonObject { ["allowMultiple"] = question.AllowMultiple };
        if (question.HasChoices)
        {
            var ids = new JsonArray(question.Choices!.Select(c => (JsonNode)JsonValue.Create(c.Id)).ToArray());
            schema = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["choiceIds"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["enum"] = ids } }
                },
                ["required"] = new JsonArray((JsonNode)"choiceIds")
            };
            metadata["choices"] = new JsonArray(question.Choices!.Select(c => (JsonNode)new JsonObject { ["id"] = c.Id, ["label"] = c.Label }).ToArray());
        }
        else
        {
            schema = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject { ["text"] = new JsonObject { ["type"] = "string" } },
                ["required"] = new JsonArray((JsonNode)"text")
            };
        }

        return new AGUIInterrupt
        {
            Id = question.Id,
            Reason = QuestionReason,
            Message = question.Text,
            ResponseSchema = ToElement(schema),
            Metadata = ToElement(metadata)
        };
    }

    // The newest thing the user did, as session input; null when the run brings nothing new.
    internal static AiInput? ToInput(IAiSession session, RunAgentInput input)
    {
        if (input.Resume is { Count: > 0 } resume)
            return FromResume(session, resume);

        if (input.ForwardedProperties is { ValueKind: JsonValueKind.Object } fp
            && fp.TryGetProperty("a2uiAction", out var a2ui)
            && a2ui.TryGetProperty("userAction", out var action))
        {
            var name = action.TryGetProperty("name", out var n) ? n.GetString() : "action";
            var context = action.TryGetProperty("context", out var c) ? c.GetRawText() : "{}";
            return new AiInput(
            [
                new TextPart($"▸ {name}", Audience.Displayed),
                new TextPart($"The user triggered the UI action '{name}' on surface '{(action.TryGetProperty("surfaceId", out var s) ? s.GetString() : "")}' with context {context}. Respond to it.", Audience.ModelOnly)
            ], MessageSource.Typed);
        }

        return input.Messages?.LastOrDefault() is AGUIUserMessage user && user.Content.ToString() is { Length: > 0 } text
            ? AiInput.Text(text)
            : null;
    }

    static AiInput? FromResume(IAiSession session, IList<AGUIResume> resume)
    {
        var parts = new List<MessagePart>();
        foreach (var r in resume)
        {
            var question = session.PendingQuestions.FirstOrDefault(q => q.Id == r.InterruptId);
            if (r.Status != ResumeStatus.Resolved)
            {
                parts.Add(new TextPart(question is null ? "(dismissed)" : $"(dismissed: {question.Text})", Audience.Displayed));
                continue;
            }

            var payload = r.Payload;
            if (question?.HasChoices == true
                && payload is { ValueKind: JsonValueKind.Object } p
                && p.TryGetProperty("choiceIds", out var ids)
                && ids.ValueKind == JsonValueKind.Array)
            {
                var choiceIds = ids.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToArray();
                parts.AddRange(AiInput.Answer(question, choiceIds).Parts);
            }
            else if (payload is { ValueKind: JsonValueKind.Object } t && t.TryGetProperty("text", out var text) && text.GetString() is { Length: > 0 } s)
            {
                parts.Add(question is null
                    ? new TextPart(s, Audience.Displayed)
                    : new AnswerPart(question.Id, [], s));
            }
            else
            {
                parts.Add(new TextPart("Yes", Audience.Displayed));
            }
        }

        if (parts.Count == 0)
            return null;
        return new AiInput(parts, parts.All(x => x is AnswerPart) ? MessageSource.ChoiceAnswer : MessageSource.Typed);
    }

    static JsonElement ToElement(JsonNode node) => JsonElement.Parse(node.ToJsonString());
}
