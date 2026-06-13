using System;
using System.Collections.Concurrent;
using System.Text;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace DeathrollManager.Helpers;

/// <summary>
/// Sends native FFXIV chat commands via UIModule.ProcessChatBoxEntry,
/// which mirrors exactly what happens when the player presses Enter in the chat box.
/// Pattern sourced from ChatAnywhere / ChatTwo plugins.
/// </summary>
public static class ChatSender
{
    /// <summary>Sends /random [max] on behalf of the local player.</summary>
    public static void SendRandom(int max) => Send($"/random {max}");

    // ── Paced queue — multi-message macros ───────────────────────────────
    // 2s spacing mirrors Shoutmaker's "/wait 2" flood-safety default.

    private static readonly ConcurrentQueue<string> PacedQueue = new();
    private static long _lastPacedSendMs;

    /// <summary>Queues a message for paced sending (~one per 2s).</summary>
    public static void EnqueuePaced(string text) => PacedQueue.Enqueue(text);

    /// <summary>Drains one queued message per 2s. Called from Framework.Update.</summary>
    public static void PumpQueue()
    {
        if (PacedQueue.IsEmpty) return;
        long now = Environment.TickCount64;
        if (now - _lastPacedSendMs < 2000) return;
        if (!PacedQueue.TryDequeue(out var msg)) return;
        _lastPacedSendMs = now;
        Send(msg);
    }

    public static unsafe void Send(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        if (!text.StartsWith('/')) text = '/' + text;

        Plugin.Log.Info($"[DeathrollManager] SendNative: {text}");

        try
        {
            var uiModule = UIModule.Instance();
            if (uiModule == null) { Plugin.Log.Warning("[DR] UIModule.Instance() is null"); return; }

            var bytes = Encoding.UTF8.GetBytes(text);
            if (bytes.Length > 500) { Plugin.Log.Warning("[DR] Command too long"); return; }

            var mes = Utf8String.FromSequence([.. bytes, 0]);
            uiModule->ProcessChatBoxEntry(mes);
            mes->Dtor(true);
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "[DR] SendNative failed");
        }
    }
}
