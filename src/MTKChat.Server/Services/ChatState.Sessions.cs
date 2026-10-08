namespace MTKChat.Server.Services;

public sealed partial class ChatState
{
    // Browser invite sessions need not outlive their membership confirmation.
    // Removing one bearer never signs out another device/account session.
    public bool RevokeSession(string token) => _sessions.TryRemove(token, out _);
}
