using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using LocalChat.Core.Contracts;

namespace ChatBox.Client.Managers
{
    public class ConnectionManager
    {
        private readonly IChatClient _chatClient;
        private readonly IFileClient _fileClient;
        private readonly MessageRouter _messageRouter;
        private readonly ChannelManager _channelManager;

        public string ServerIp { get; private set; } = "";
        public string UserId { get; private set; } = "";
        public string AvatarBase64 { get; private set; } = "";

        private CancellationTokenSource _cts = new();

        public event Action<string>? OnStatusChanged;
        public event Action<bool>? OnConnectionStateChanged;

        public ConnectionManager(
            IChatClient chatClient,
            IFileClient fileClient,
            MessageRouter messageRouter,
            ChannelManager channelManager)
        {
            _chatClient = chatClient;
            _fileClient = fileClient;
            _messageRouter = messageRouter;
            _channelManager = channelManager;
        }

        public void SetUserIdentity(string userId, string avatarBase64)
        {
            UserId = userId;
            AvatarBase64 = avatarBase64;
        }

        public async Task ConnectAsync(string serverIp, string username)
        {
            ServerIp = serverIp;

            if (_cts.IsCancellationRequested) _cts = new CancellationTokenSource();

            try
            {
                await _chatClient.ConnectAsync(serverIp, _cts.Token);
                await _chatClient.SendMessageAsync($"JOIN|{UserId}|{username}|{AvatarBase64}");
                OnStatusChanged?.Invoke("Connected");
                OnConnectionStateChanged?.Invoke(true);
            }
            catch (OperationCanceledException)
            {
                OnStatusChanged?.Invoke("Cancelled");
            }
            catch (Exception ex)
            {
                OnStatusChanged?.Invoke("Connection Failed");
                OnConnectionStateChanged?.Invoke(false);
                throw;
            }
        }

        public void CancelConnect()
        {
            _cts.Cancel();
            _chatClient.Disconnect();
        }

        public void Disconnect()
        {
            _chatClient.Disconnect();
            _cts.Cancel();
            _channelManager.AllMessages.Clear();
            _channelManager.RefreshMessageList();
            OnStatusChanged?.Invoke("Disconnected");
            OnConnectionStateChanged?.Invoke(false);
        }

        public async Task SendMessageAsync(string message)
        {
            await _chatClient.SendMessageAsync(message);
        }

        public void LoadUserConfig(string userId, string avatarBase64)
        {
            UserId = userId;
            AvatarBase64 = avatarBase64;
        }
    }
}