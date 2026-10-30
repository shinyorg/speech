using Microsoft.Extensions.AI;
using Shiny.AiConversation.Infrastructure;
using static Shiny.AiConversation.Tests.FakeChatClient;

namespace Shiny.AiConversation.Tests;

public class CameraAndImageTests
{
    static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0x01, 0x02];

    sealed class FakeCapture(params CameraMode[] modes) : ICameraCapture
    {
        public List<CameraRequest> Requests { get; } = [];
        public CameraCapture? Result { get; set; } = new(Jpeg, "image/jpeg");
        public IReadOnlyList<CameraMode> SupportedModes => modes.Length == 0 ? [CameraMode.Photo] : modes;

        public Task<CameraCapture?> Capture(CameraRequest request, CancellationToken ct)
        {
            this.Requests.Add(request);
            return Task.FromResult(this.Result);
        }
    }

    sealed class FakeConsent(bool allow) : ICameraConsent
    {
        public int Asked { get; private set; }
        public Task<bool> Confirm(CameraRequest request, CancellationToken ct)
        {
            this.Asked++;
            return Task.FromResult(allow);
        }
    }

    sealed class FakeFocus : IAudioFocusParticipant
    {
        public List<string> Calls { get; } = [];
        public Task Pause() { this.Calls.Add("pause"); return Task.CompletedTask; }
        public Task Resume() { this.Calls.Add("resume"); return Task.CompletedTask; }
    }

    static (AiSessionManager Manager, FakeChatClient Chat, InMemoryAiAttachmentStore Attachments, IAiSessionStore Store) Create(
        ICameraCapture capture,
        bool imagesInToolResults = false,
        ICameraConsent? consent = null,
        IAudioFocusParticipant? focus = null,
        CameraToolOptions? options = null
    )
    {
        var chat = new FakeChatClient();
        var provider = new FakeChatClientProvider(chat, imagesInToolResults: imagesInToolResults);
        var camera = new CameraToolInterceptor(capture, provider, focus == null ? [] : [focus], options ?? new CameraToolOptions(), consent);
        var store = new InMemoryAiSessionStore();
        var attachments = new InMemoryAiAttachmentStore();
        var manager = new AiSessionManager(store, provider, [camera], TimeProvider.System,
            memoryOptions: new AiMemoryOptions { Enabled = false }, compactor: new FakeCompactor(), attachmentStore: attachments);
        return (manager, chat, attachments, store);
    }

    static FunctionCallContent UseCamera(string mode = "photo")
        => Call("cam1", CameraToolInterceptor.ToolName, new() { ["purpose"] = "Point at the nutrition label", ["mode"] = mode });


    [Test]
    public async Task Photo_FollowsUpAsAnImageMessage_WithinTheSameTurn()
    {
        var capture = new FakeCapture();
        var focus = new FakeFocus();
        var consent = new FakeConsent(true);
        var (manager, chat, attachments, store) = Create(capture, consent: consent, focus: focus);
        chat.Reply(Text("Let me look."), UseCamera());
        chat.Reply(Text("That has 12g of sugar."));

        await using var session = await manager.Start();
        var turn = session.Send(AiInput.Text("how much sugar is in this?"));
        var result = await turn.WaitAsync();

        await Assert.That(result.Outcome).IsEqualTo(AiTurnOutcome.Completed);
        await Assert.That(result.Reply!.Text).IsEqualTo("That has 12g of sugar.");
        await Assert.That(consent.Asked).IsEqualTo(1);
        await Assert.That(String.Join(",", focus.Calls)).IsEqualTo("pause,resume");
        await Assert.That(capture.Requests.Single().Purpose).IsEqualTo("Point at the nutrition label");

        // round 2 saw the photo as image content in a user message
        var second = chat.Requests[1].Messages;
        var imageMessage = second.Last(m => m.Role == ChatRole.User);
        await Assert.That(imageMessage.Contents.OfType<DataContent>().Single().MediaType).IsEqualTo("image/jpeg");

        // stored: user, assistant (tool call), user (camera + image ref), assistant (answer); no binary inline
        var stored = await store.GetMessages(new MessageQuery(session.SessionId));
        await Assert.That(String.Join(",", stored.Select(x => x.Role))).IsEqualTo("User,Assistant,User,Assistant");
        var imagePart = stored[2].Parts.OfType<ImagePart>().Single();
        await Assert.That(stored[2].Source).IsEqualTo(MessageSource.Camera);
        await Assert.That((await attachments.Load(imagePart.Attachment.AttachmentId))!.Length).IsEqualTo(Jpeg.Length);

        // the turn's events covered both rounds
        var deltas = new List<string>();
        await foreach (var e in turn.Events)
            if (e is TextDelta d) deltas.Add(d.Text);
        await Assert.That(String.Concat(deltas)).IsEqualTo("Let me look.That has 12g of sugar.");

        // DropAfterTurn: the next turn only mentions the image
        chat.Reply(Text("ok"));
        await session.Send(AiInput.Text("thanks")).WaitAsync();
        var third = chat.Requests[2].Messages;
        await Assert.That(third.SelectMany(m => m.Contents).OfType<DataContent>().Any()).IsFalse();
        await Assert.That(third.Any(m => m.Text.Contains("[image: Point at the nutrition label]"))).IsTrue();
    }


    [Test]
    public async Task Photo_InlineInToolResult_WhenTheProviderSupportsIt()
    {
        var (manager, chat, _, store) = Create(new FakeCapture(), imagesInToolResults: true);
        chat.Reply(UseCamera());
        chat.Reply(Text("I see it."));

        await using var session = await manager.Start();
        var result = await session.Send(AiInput.Text("look at this")).WaitAsync();

        await Assert.That(result.Reply!.Text).IsEqualTo("I see it.");
        var toolMessage = chat.Requests[1].Messages.Single(m => m.Role == ChatRole.Tool);
        var payload = (IEnumerable<AIContent>)toolMessage.Contents.OfType<FunctionResultContent>().Single().Result!;
        await Assert.That(payload.OfType<DataContent>().Any()).IsTrue();

        var stored = (await store.GetMessages(new MessageQuery(session.SessionId))).ToList();
        await Assert.That(stored.Count).IsEqualTo(2);
        var resultPart = stored[1].Parts.OfType<ToolResultPart>().Single();
        await Assert.That(resultPart.Result.Contains("[image/jpeg attachment]")).IsTrue();
        await Assert.That(resultPart.Result.Length < 200).IsTrue();
    }


    [Test]
    public async Task Declined_Cancelled_AndScanModes()
    {
        var capture = new FakeCapture(CameraMode.Photo, CameraMode.Barcode);
        var (manager, chat, _, _) = Create(capture, consent: new FakeConsent(false));
        chat.Reply(UseCamera());
        chat.Reply(Text("No problem."));
        await using var declinedSession = await manager.Start();
        await declinedSession.Send(AiInput.Text("look")).WaitAsync();
        var declined = chat.Requests[1].Messages.Single(m => m.Role == ChatRole.Tool).Contents.OfType<FunctionResultContent>().Single().Result;
        await Assert.That(declined?.ToString()).IsEqualTo("The user declined to open the camera.");
        await Assert.That(capture.Requests.Count).IsEqualTo(0);

        capture.Result = new CameraCapture(Text: "0123456789012 (EAN-13)");
        var (manager2, chat2, _, _) = Create(capture, consent: new FakeConsent(true));
        chat2.Reply(UseCamera("barcode"));
        chat2.Reply(Text("Found it."));
        await using var scanSession = await manager2.Start();
        await scanSession.Send(AiInput.Text("scan")).WaitAsync();
        var scanned = chat2.Requests[1].Messages.Single(m => m.Role == ChatRole.Tool).Contents.OfType<FunctionResultContent>().Single().Result;
        await Assert.That(scanned?.ToString()).IsEqualTo("barcode result:\n0123456789012 (EAN-13)");

        // the tool only offers modes the capture service supports
        var schema = chat2.Requests[0].Options!.Tools!.OfType<AIFunction>().Single(t => t.Name == CameraToolInterceptor.ToolName).JsonSchema.GetRawText();
        await Assert.That(schema.Contains("\"barcode\"") && !schema.Contains("\"document\"")).IsTrue();
    }


    [Test]
    public async Task UserImages_Retention_KeepInContext_And_DontStore()
    {
        var h = TestHelpers.CreateHarness();
        var attachments = new InMemoryAiAttachmentStore();
        var manager = new AiSessionManager(h.Store, new FakeChatClientProvider(h.Chat), [], TimeProvider.System,
            memoryOptions: new AiMemoryOptions { Enabled = false }, attachmentStore: attachments);

        await using var session = await manager.Start();
        h.Chat.Reply(Text("A cat."));
        h.Chat.Reply(Text("Still a cat."));
        h.Chat.Reply(Text("A dog."));
        h.Chat.Reply(Text("No idea now."));

        await session.Send(AiInput.Text("what is this?").WithImage(Jpeg, "image/jpeg", "cat", ImageRetention.KeepInContext)).WaitAsync();
        await session.Send(AiInput.Text("and now?")).WaitAsync();
        await Assert.That(h.Chat.Requests[1].Messages.SelectMany(m => m.Contents).OfType<DataContent>().Count()).IsEqualTo(1);

        await session.Send(AiInput.Text("this one?").WithImage(Jpeg, "image/png", "dog", ImageRetention.DontStore)).WaitAsync();
        await Assert.That(h.Chat.Requests[2].Messages.SelectMany(m => m.Contents).OfType<DataContent>().Count()).IsEqualTo(2);

        await session.Send(AiInput.Text("again?")).WaitAsync();
        var last = h.Chat.Requests[3].Messages;
        await Assert.That(last.SelectMany(m => m.Contents).OfType<DataContent>().Count()).IsEqualTo(1);   // only the kept cat
        await Assert.That(last.Any(m => m.Text.Contains("[image: dog]"))).IsTrue();

        // DontStore never reached the attachment store; deleting the session removes the rest
        var stored = await h.Store.GetMessages(new MessageQuery(session.SessionId));
        var dogRef = stored.SelectMany(x => x.Parts).OfType<ImagePart>().Single(p => p.Description == "dog").Attachment.AttachmentId;
        await Assert.That(await attachments.Load(dogRef)).IsNull();

        await manager.Delete(session.SessionId);
        var catRef = stored.SelectMany(x => x.Parts).OfType<ImagePart>().Single(p => p.Description == "cat").Attachment.AttachmentId;
        await Assert.That(await attachments.Load(catRef)).IsNull();
    }


    [Test]
    public async Task FileAttachmentStore_RoundTrips_AndDeletesPerSession()
    {
        var dir = Path.Combine(Path.GetTempPath(), "shiny-aiconv-tests", Guid.NewGuid().ToString("N"));
        var store = new FileAiAttachmentStore(dir);

        var a = await store.Save(Jpeg, "image/jpeg", "s1");
        var b = await store.Save(Jpeg, "image/png", "s2");
        await Assert.That((await store.Load(a.AttachmentId))!.Length).IsEqualTo(Jpeg.Length);
        await Assert.That(store.GetDisplayUri(a.AttachmentId)!.EndsWith(".jpg")).IsTrue();
        await Assert.That(() => store.Load("../../etc/passwd")).Throws<ArgumentException>();

        await store.DeleteForSession("s1");
        await Assert.That(await store.Load(a.AttachmentId)).IsNull();
        await Assert.That(await store.Load(b.AttachmentId)).IsNotNull();
    }


    [Test]
    public async Task Images_RoundTripThroughJson()
    {
        var part = new ImagePart(new AttachmentRef("s/x.jpg", "image/jpeg", 5), "label", ImageRetention.KeepInContext);
        var doc = new AiMessageDocument("m", "s", 1, 1, DateTimeOffset.UtcNow, MessageRole.User, MessageSource.Camera, MessageStatus.Complete,
            [part], null, null, null, false, null, "r");

        var json = System.Text.Json.JsonSerializer.Serialize(doc, AiConversationJsonContext.Default.AiMessageDocument);
        var back = System.Text.Json.JsonSerializer.Deserialize(json, AiConversationJsonContext.Default.AiMessageDocument)!;
        await Assert.That(back.Parts.Single()).IsEqualTo(part);
    }
}
