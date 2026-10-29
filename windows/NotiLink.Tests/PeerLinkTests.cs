using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using NotiLink.Core.Networking;
using NotiLink.Core.Storage;
using Xunit;

namespace NotiLink.Tests;

/// <summary>Two real PeerLinkService instances talking over loopback through two real TcpServers
/// (the same hand-off the app uses) — pairing with the 6-digit code, a message, clipboard text,
/// a multi-chunk file and a nudge, end to end.</summary>
[SupportedOSPlatform("windows")]
public sealed class PeerLinkTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "notilink-peer-test-" + Guid.NewGuid().ToString("N"));

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static async Task<T> Wait<T>(TaskCompletionSource<T> tcs, string what)
    {
        var done = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(15)));
        Assert.True(done == tcs.Task, $"timed out waiting for {what}");
        return await tcs.Task;
    }

    [Fact]
    public async Task Pair_Message_Clipboard_File_Nudge_RoundTrip()
    {
        int portA = FreePort(), portB = FreePort();
        // Each service dials the OTHER one's port (in the app both are 45212).
        using var a = new PeerLinkService(() => new[] { "127.0.0.1" }, portB, Path.Combine(_root, "a"), Path.Combine(_root, "a-dl"));
        using var b = new PeerLinkService(() => new[] { "127.0.0.1" }, portA, Path.Combine(_root, "b"), Path.Combine(_root, "b-dl"));
        using var serverA = new TcpServer(portA) { PeerHandoff = a.AcceptIncoming };
        using var serverB = new TcpServer(portB) { PeerHandoff = b.AcceptIncoming };
        serverA.Start();
        serverB.Start();

        // --- pairing: A adds B; B shows a code; A types it.
        var codeOnB = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var codeNeededOnA = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var resultOnA = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        b.PairCodeShown += (_, code) => codeOnB.TrySetResult(code);
        a.PairCodeNeeded += name => codeNeededOnA.TrySetResult(name);
        a.PairResult += (ok, msg) => { if (ok || !msg.Contains("intento")) resultOnA.TrySetResult(ok); };

        await a.BeginPairAsync("127.0.0.1");
        var code = await Wait(codeOnB, "code on B");
        await Wait(codeNeededOnA, "code prompt on A");

        // A wrong code first: should be refused but leave room to retry.
        var wrongOnA = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        Action<bool, string> wrongHandler = (ok, msg) => { if (!ok) wrongOnA.TrySetResult(msg); };
        a.PairResult += wrongHandler;
        await a.SubmitPairCodeAsync(code == "000000" ? "111111" : "000000");
        Assert.Contains("intento", await Wait(wrongOnA, "wrong-code result"));
        a.PairResult -= wrongHandler;

        await a.SubmitPairCodeAsync(code);
        Assert.True(await Wait(resultOnA, "pair result on A"));
        await WaitUntil(() => a.ListPeers().Any(p => p.Connected) && b.ListPeers().Any(p => p.Connected), "both connected");
        Assert.Equal(b.MyPcId, a.ListPeers().Single().PcId);
        Assert.Equal(a.MyPcId, b.ListPeers().Single().PcId);

        // --- text + clipboard
        var textOnB = new TaskCompletionSource<PeerChatItem>(TaskCreationOptions.RunContinuationsAsynchronously);
        var clipOnB = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        b.ChatItemChanged += (_, item) => { if (item.Kind == "text" && !item.Outgoing) textOnB.TrySetResult(item); };
        b.ClipboardReceived += (_, text) => clipOnB.TrySetResult(text);
        Assert.True(await a.SendTextAsync(b.MyPcId, "hola desde A ✨"));
        Assert.Equal("hola desde A ✨", (await Wait(textOnB, "text on B")).Text);
        Assert.True(await a.SendTextAsync(b.MyPcId, "copiado", "clipboard"));
        Assert.Equal("copiado", await Wait(clipOnB, "clipboard on B"));

        // Formatting arrives exactly as sent: line breaks (both kinds), a blank line, tabs, spaces.
        const string formatted = "Primer párrafo,\r\nsegunda línea.\n\n\tSangría  con  dos espacios \n- punto 1\n- punto 2";
        var formattedOnB = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        b.ChatItemChanged += (_, item) => { if (item.Kind == "text" && !item.Outgoing && item.Text != "hola desde A ✨") formattedOnB.TrySetResult(item.Text!); };
        Assert.True(await a.SendTextAsync(b.MyPcId, formatted));
        Assert.Equal(formatted, await Wait(formattedOnB, "formatted text on B"));
        Assert.Equal(formatted, b.History(a.MyPcId).Last(i => i.Kind == "text").Text);

        // --- a file bigger than one chunk (512 KB), B → A this time
        var payload = new byte[1_300_000];
        new Random(42).NextBytes(payload);
        var src = Path.Combine(_root, "prueba.bin");
        Directory.CreateDirectory(_root);
        File.WriteAllBytes(src, payload);
        var fileOnA = new TaskCompletionSource<PeerChatItem>(TaskCreationOptions.RunContinuationsAsynchronously);
        a.ChatItemChanged += (_, item) => { if (item.Kind == "file" && !item.Outgoing && item.State is "done" or "failed" or "canceled") fileOnA.TrySetResult(item); };
        Assert.True(await b.SendFilesAsync(a.MyPcId, new[] { src }));
        var received = await Wait(fileOnA, "file on A");
        Assert.Equal("done", received.State);
        Assert.Equal(payload, File.ReadAllBytes(received.Path!));
        Assert.Equal(Path.Combine(_root, "a-dl", "prueba.bin"), received.Path);

        // --- nudge
        var nudgeOnB = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        b.NudgeReceived += (pcId, _) => nudgeOnB.TrySetResult(pcId);
        Assert.True(await a.NudgeAsync(b.MyPcId));
        Assert.Equal(a.MyPcId, await Wait(nudgeOnB, "nudge on B"));

        // --- history is persisted on both sides
        Assert.Contains(b.History(a.MyPcId), i => i.Text == "hola desde A ✨");
        Assert.Contains(a.History(b.MyPcId), i => i.Kind == "file" && i.State == "done");

        // --- unpair from A: B forgets A too
        await a.UnpairAsync(b.MyPcId);
        await WaitUntil(() => b.ListPeers().Count == 0, "B forgets A");
        Assert.Empty(a.ListPeers());
    }

    [Fact]
    public async Task Reconnects_With_Saved_Token_Without_A_New_Code()
    {
        int portA = FreePort(), portB = FreePort();
        string dirA = Path.Combine(_root, "a"), dirB = Path.Combine(_root, "b");

        // Pair once.
        using (var a = new PeerLinkService(() => Array.Empty<string>(), portB, dirA, _root))
        using (var b = new PeerLinkService(() => Array.Empty<string>(), portA, dirB, _root))
        using (var serverA = new TcpServer(portA) { PeerHandoff = a.AcceptIncoming })
        using (var serverB = new TcpServer(portB) { PeerHandoff = b.AcceptIncoming })
        {
            serverA.Start();
            serverB.Start();
            var code = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var asked = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            b.PairCodeShown += (_, c) => code.TrySetResult(c);
            a.PairCodeNeeded += _ => asked.TrySetResult(true);
            await a.BeginPairAsync("127.0.0.1");
            await Wait(asked, "code prompt");
            await a.SubmitPairCodeAsync(await Wait(code, "code"));
            await WaitUntil(() => a.ListPeers().Any(p => p.Connected), "first link");
        }

        // "Restart" both apps: the reconnect loop must link them again on its own, no code.
        using var a2 = new PeerLinkService(() => Array.Empty<string>(), portB, dirA, _root);
        using var b2 = new PeerLinkService(() => Array.Empty<string>(), portA, dirB, _root);
        var codeShown = false;
        b2.PairCodeShown += (_, _) => codeShown = true;
        a2.PairCodeShown += (_, _) => codeShown = true;
        using var serverA2 = new TcpServer(portA) { PeerHandoff = a2.AcceptIncoming };
        using var serverB2 = new TcpServer(portB) { PeerHandoff = b2.AcceptIncoming };
        serverA2.Start();
        serverB2.Start();
        a2.Start();
        b2.Start();
        await WaitUntil(() => a2.ListPeers().Any(p => p.Connected) && b2.ListPeers().Any(p => p.Connected), "automatic reconnect", 25);
        Assert.False(codeShown);
    }

    [Fact]
    public async Task Screen_Request_Needs_Permission_Once_Then_Streams_With_Acks()
    {
        int portA = FreePort(), portB = FreePort();
        using var a = new PeerLinkService(() => Array.Empty<string>(), portB, Path.Combine(_root, "a"), _root);
        using var b = new PeerLinkService(() => Array.Empty<string>(), portA, Path.Combine(_root, "b"), _root);
        using var serverA = new TcpServer(portA) { PeerHandoff = a.AcceptIncoming };
        using var serverB = new TcpServer(portB) { PeerHandoff = b.AcceptIncoming };
        serverA.Start();
        serverB.Start();
        var code = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var asked = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        b.PairCodeShown += (_, c) => code.TrySetResult(c);
        a.PairCodeNeeded += _ => asked.TrySetResult(true);
        await a.BeginPairAsync("127.0.0.1");
        await Wait(asked, "code prompt");
        await a.SubmitPairCodeAsync(await Wait(code, "code"));
        await WaitUntil(() => a.ListPeers().Any(p => p.Connected) && b.ListPeers().Any(p => p.Connected), "link");

        // A asks for B's screen: B has no standing permission, so B's user is asked — and says no.
        var requestOnB = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var endedOnA = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        b.ScreenRequested += (pcId, _) => requestOnB.TrySetResult(pcId);
        a.ScreenViewEnded += (_, reason) => endedOnA.TrySetResult(reason);
        Assert.True(await a.RequestScreenAsync(b.MyPcId));
        Assert.Equal(a.MyPcId, await Wait(requestOnB, "request on B"));
        b.AnswerScreenRequest(a.MyPcId, allow: false, always: false);
        Assert.Equal("denied", await Wait(endedOnA, "denied on A"));

        // Asked again, "Permitir siempre": A starts viewing and frames arrive, acked so B keeps sending.
        requestOnB = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var startedOnA = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var frames = 0;
        a.ScreenViewStarted += (_, _) => startedOnA.TrySetResult(true);
        a.ScreenFrameReceived += (_, jpeg) => { if (jpeg.Length == 1000) Interlocked.Increment(ref frames); };
        Assert.True(await a.RequestScreenAsync(b.MyPcId));
        await Wait(requestOnB, "second request on B");
        b.AnswerScreenRequest(a.MyPcId, allow: true, always: true);
        await Wait(startedOnA, "viewing starts on A");
        Assert.True(b.ListPeers().Single().AllowScreen);
        Assert.Equal(new[] { a.MyPcId }, b.ScreenViewers());
        for (var i = 0; i < 6; i++)
        {
            await WaitUntil(() => b.WantsScreenFrame(a.MyPcId), "room for a frame");
            await b.SendScreenFrameAsync(a.MyPcId, new byte[1000], 64, 36);
        }
        await WaitUntil(() => Volatile.Read(ref frames) == 6, "6 frames on A");

        // A closes its viewer: B stops sending.
        a.StopViewingScreen(b.MyPcId);
        await WaitUntil(() => b.ScreenViewers().Count == 0, "B stops sharing");

        // With the standing permission, the next request starts without asking B's user.
        var askedAgain = false;
        b.ScreenRequested += (_, _) => askedAgain = true;
        startedOnA = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(await a.RequestScreenAsync(b.MyPcId));
        await Wait(startedOnA, "viewing starts again without a prompt");
        Assert.False(askedAgain);

        // B ends it from its side: A's viewer is told.
        endedOnA = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        b.StopSharingScreen(a.MyPcId);
        Assert.Equal("stopped", await Wait(endedOnA, "stopped on A"));
        Assert.False(a.ListPeers().Single().ViewingScreen);
    }

    [Fact]
    public async Task Control_Only_After_Grant_And_Only_While_Sharing()
    {
        int portA = FreePort(), portB = FreePort();
        using var a = new PeerLinkService(() => Array.Empty<string>(), portB, Path.Combine(_root, "a"), _root);
        using var b = new PeerLinkService(() => Array.Empty<string>(), portA, Path.Combine(_root, "b"), _root);
        using var serverA = new TcpServer(portA) { PeerHandoff = a.AcceptIncoming };
        using var serverB = new TcpServer(portB) { PeerHandoff = b.AcceptIncoming };
        serverA.Start();
        serverB.Start();
        var code = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var asked = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        b.PairCodeShown += (_, c) => code.TrySetResult(c);
        a.PairCodeNeeded += _ => asked.TrySetResult(true);
        await a.BeginPairAsync("127.0.0.1");
        await Wait(asked, "code prompt");
        await a.SubmitPairCodeAsync(await Wait(code, "code"));
        await WaitUntil(() => a.ListPeers().Any(p => p.Connected) && b.ListPeers().Any(p => p.Connected), "link");

        var inputs = new System.Collections.Concurrent.ConcurrentQueue<PeerInput>();
        b.InputReceived += (_, input) => inputs.Enqueue(input);

        // Not even viewing yet: asking for control is refused locally.
        Assert.False(await a.RequestControlAsync(b.MyPcId));

        // B pushes its screen to A; A asks for control; B's user is asked and allows it once.
        var controlRequested = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var controlling = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        b.ControlRequested += (_, _) => controlRequested.TrySetResult(true);
        a.ControllingChanged += (_, on) => controlling.TrySetResult(on);
        Assert.True(await b.ShareScreenAsync(a.MyPcId));
        await WaitUntil(() => a.ListPeers().Single().ViewingScreen, "A viewing");

        // Input sent before any grant never reaches B.
        a.SendInput(b.MyPcId, new PeerInput("move", 0.5, 0.5));
        Assert.True(await a.RequestControlAsync(b.MyPcId));
        await Wait(controlRequested, "control prompt on B");
        b.AnswerControlRequest(a.MyPcId, allow: true, always: false);
        Assert.True(await Wait(controlling, "control granted on A"));
        Assert.True(b.ListPeers().Single().ControlledBy);
        Assert.False(b.ListPeers().Single().AllowControl);

        a.SendInput(b.MyPcId, new PeerInput("move", 0.25, 0.75));
        a.SendInput(b.MyPcId, new PeerInput("down", 0.25, 0.75, "left"));
        a.SendInput(b.MyPcId, new PeerInput("key", Vk: 0x41, Down: true));
        await WaitUntil(() => inputs.Count == 3, "3 inputs on B");
        var all = inputs.ToArray();
        Assert.Equal(new PeerInput("move", 0.25, 0.75), all[0]);
        Assert.Equal("left", all[1].Button);
        Assert.Equal(0x41, all[2].Vk);
        Assert.True(all[2].Down);

        // B stops sharing: control goes with it, and later input is dropped.
        b.StopSharingScreen(a.MyPcId);
        await WaitUntil(() => !a.ListPeers().Single().Controlling && !a.ListPeers().Single().ViewingScreen, "A lost control");
        Assert.False(b.ListPeers().Single().ControlledBy);
        a.SendInput(b.MyPcId, new PeerInput("move", 0.1, 0.1));
        await Task.Delay(300);
        Assert.Equal(3, inputs.Count);
    }

    private static async Task WaitUntil(Func<bool> condition, string what, int seconds = 15)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < until, $"timed out waiting for {what}");
            await Task.Delay(100);
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
