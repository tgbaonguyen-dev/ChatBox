namespace LocalChat.Core.Contracts
{
    public interface IChatServer
    {
        string RoomName { get; set; }
        string Greeting { get; set; }
        event Action<string>? OnLog;
        Task SetRoomName(string name);
        Task SetGreeting(string greeting);
        Task StartListeningAsync(CancellationToken token);
        Task BroadcastAsync(string message, string excludeClientId = "");
    }
}