namespace LocalChat.Core.Contracts
{
    public interface IChatClient
    {
        event Action<string>? OnMessageReceived;
        Task ConnectAsync(string serverIp, CancellationToken token);
        Task SendMessageAsync(string message);
        void Disconnect();
    }
}