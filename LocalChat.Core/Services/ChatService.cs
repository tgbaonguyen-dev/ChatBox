using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LocalChat.Core.Data;
using LocalChat.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace LocalChat.Core.Services
{
    public class ChatServer
    {
        private const int ChatPort = 9999;
        private TcpListener? _listener;
        private ConcurrentDictionary<string, TcpClient> _clients = new();
        private ConcurrentDictionary<string, string> _onlineUsers = new();
        private string _roomName = "LAN Global Chat";
        private string _greeting = "Welcome to the server!";
        
        public string RoomName
        {
            get => _roomName;
            set => _roomName = value;
        }

        public string Greeting
        {
            get => _greeting;
            set => _greeting = value;
        }

        public event Action<string>? OnLog;

        public async Task SetRoomName(string name)
        {
            _roomName = name;
            await BroadcastAsync($"ROOM_NAME|{_roomName}");
        }

        public async Task SetGreeting(string greeting)
        {
            _greeting = greeting;
            await BroadcastAsync($"GREETING|{_greeting}");
        }

        private async Task BroadcastOnlineUsersAsync()
        {
            var users = string.Join(",", _onlineUsers.Values.Distinct());
            await BroadcastAsync($"ONLINE_USERS|{users}");
        }

        public async Task StartListeningAsync(CancellationToken token)
        {
            _listener = new TcpListener(IPAddress.Any, ChatPort);
            _listener.Start();
            OnLog?.Invoke($"Server Chat started on port {ChatPort}");

            try
            {
                while (!token.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(token);
                    string clientEndpoint = client.Client.RemoteEndPoint!.ToString()!;
                    _clients.TryAdd(clientEndpoint, client);
                    OnLog?.Invoke($"Client connected: {clientEndpoint}");
                    
                    _ = Task.Run(() => HandleClientAsync(clientEndpoint, client, token));
                }
            }
            finally { _listener.Stop(); }
        }

        private async Task HandleClientAsync(string clientId, TcpClient client, CancellationToken token)
        {
            using (client)
            using (var stream = client.GetStream())
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            {
                try
                {
                    while (!token.IsCancellationRequested)
                    {
                        var message = await reader.ReadLineAsync();
                        if (message == null) break;

                        // Process incoming message and save to DB
                        await ProcessMessageAsync(message, clientId);
                    }
                }
                catch { }
                finally
                {
                    _clients.TryRemove(clientId, out _);
                    if (_onlineUsers.TryRemove(clientId, out string? username))
                    {
                        await BroadcastAsync($"MSG|System|{username} has left the chat.||{DateTime.UtcNow:O}");
                        await BroadcastOnlineUsersAsync();
                    }
                    OnLog?.Invoke($"Client disconnected: {clientId}");
                }
            }
        }

        private async Task ProcessMessageAsync(string rawMessage, string sourceClientId)
        {
            var parts = rawMessage.Split('|');
            if (parts.Length < 3) return;

            string type = parts[0];

            using var db = new ChatDbContext();

            if (type == "JOIN") // JOIN|UserId|Username|AvatarBase64
            {
                if (parts.Length < 4) return;
                string userId = parts[1];
                string username = parts[2];
                string avatar = parts[3];

                var user = await db.Users.FindAsync(userId);
                if (user == null)
                {
                    user = new User { Id = userId, Username = username, AvatarBase64 = avatar };
                    db.Users.Add(user);
                }
                else
                {
                    user.Username = username;
                    user.AvatarBase64 = avatar;
                    user.LastSeen = DateTime.UtcNow;
                }
                await db.SaveChangesAsync();

                // Load last 50 messages and send them back to this user
                var history = await db.ChatMessages
                    .Include(m => m.Sender)
                    .OrderByDescending(m => m.Timestamp)
                    .Take(50)
                    .ToListAsync();
                    
                history.Reverse();

                // Batch-load all reactions for all messages upfront to avoid N+1 queries
                var messageIds = history.Select(m => m.Id).ToList();
                var allReactions = await db.MessageReactions
                    .Include(r => r.User)
                    .Where(r => messageIds.Contains(r.MessageId))
                    .ToListAsync();

                if (_clients.TryGetValue(sourceClientId, out var client))
                {
                    var writer = new StreamWriter(client.GetStream(), Encoding.UTF8) { AutoFlush = true };
                    await writer.WriteLineAsync($"ROOM_NAME|{_roomName}");
                    await writer.WriteLineAsync($"GREETING|{_greeting}");
                    foreach (var msg in history)
                    {
                        if (msg.Sender == null) continue;

                        // Get reactions for this message from preloaded data
                        var msgReactions = allReactions.Where(r => r.MessageId == msg.Id).ToList();
                        var reactionsJson = JsonSerializer.Serialize(GroupReactions(msgReactions));

                        if (msg.IsFile)
                        {
                            await writer.WriteLineAsync($"FILE_READY|{msg.FileId}|{msg.Content}|{msg.FileSize}|{msg.Sender.Username}|{msg.Sender.AvatarBase64}|{msg.Timestamp:O}|{reactionsJson}");
                        }
                        else
                        {
                            await writer.WriteLineAsync($"MSG|{msg.Sender.Username}|{msg.Content}|{msg.Sender.AvatarBase64}|{msg.Timestamp:O}|{reactionsJson}");
                        }
                    }
                }
                
                await BroadcastAsync($"MSG|System|{username} has joined the chat.||{DateTime.UtcNow:O}", sourceClientId);
                OnLog?.Invoke($"[JOIN] {username}");
                
                _onlineUsers[sourceClientId] = username;
                await BroadcastOnlineUsersAsync();
            }
            else if (type == "MSG") // MSG|UserId|Content
            {
                string userId = parts[1];
                string content = string.Join("|", parts, 2, parts.Length - 2);

                var user = await db.Users.FindAsync(userId);
                if (user != null)
                {
                    var msg = new ChatMessage { SenderId = userId, Content = content, IsFile = false, Timestamp = DateTime.UtcNow };
                    db.ChatMessages.Add(msg);
                    await db.SaveChangesAsync();

                    // Thêm sourceClientId để không dội ngược tin nhắn về người gửi
                    await BroadcastAsync($"MSG|{user.Username}|{content}|{user.AvatarBase64}|{msg.Timestamp:O}", sourceClientId);
                    OnLog?.Invoke($"[MSG] {user.Username}: {content}");
                }
            }
            else if (type == "FILE_READY") // FILE_READY|UserId|FileId|FileName|Size
            {
                if (parts.Length < 5) return;
                string userId = parts[1];
                string fileId = parts[2];
                string fileName = parts[3];
                long size = long.Parse(parts[4]);

                var user = await db.Users.FindAsync(userId);
                if (user != null)
                {
                    var msg = new ChatMessage { SenderId = userId, Content = fileName, IsFile = true, FileId = fileId, FileSize = size, Timestamp = DateTime.UtcNow };
                    db.ChatMessages.Add(msg);
                    await db.SaveChangesAsync();

                    // Thêm sourceClientId
                    await BroadcastAsync($"FILE_READY|{fileId}|{fileName}|{size}|{user.Username}|{user.AvatarBase64}|{msg.Timestamp:O}", sourceClientId);
                    OnLog?.Invoke($"[FILE] {user.Username}: {fileName}");
                }
            }
            else if (type == "UPDATE_PROFILE") // UPDATE_PROFILE|UserId|Username|AvatarBase64
            {
                if (parts.Length < 4) return;
                string userId = parts[1];
                string username = parts[2];
                string avatar = parts[3];

                var user = await db.Users.FindAsync(userId);
                if (user != null)
                {
                    user.Username = username;
                    user.AvatarBase64 = avatar;
                    await db.SaveChangesAsync();
                    OnLog?.Invoke($"[PROFILE] {username} updated their profile.");
                    
                    _onlineUsers[sourceClientId] = username;
                    await BroadcastAsync($"UPDATE_PROFILE|{userId}|{username}|{avatar}");
                    await BroadcastOnlineUsersAsync();
                }
            }
            else if (type == "REACT") // REACT|UserId|MessageId|Emoji
            {
                if (parts.Length < 4) return;
                string userId = parts[1];
                string messageId = parts[2];
                string emoji = parts[3];

                // Validate emoji parameter
                if (string.IsNullOrWhiteSpace(emoji) || emoji.Length > 32)
                {
                    OnLog?.Invoke($"[REACT] Invalid emoji received from user {userId}: '{emoji}'");
                    return;
                }

                var user = await db.Users.FindAsync(userId);
                var chatMessage = await db.ChatMessages.FindAsync(messageId);

                if (user == null)
                {
                    OnLog?.Invoke($"[REACT] Reaction failed - user not found: {userId}");
                    return;
                }
                if (chatMessage == null)
                {
                    OnLog?.Invoke($"[REACT] Reaction failed - message not found: {messageId}");
                    return;
                }

                // Check if user already reacted with this emoji (toggle behavior)
                var existingReaction = await db.MessageReactions
                    .FirstOrDefaultAsync(r => r.MessageId == messageId && r.UserId == userId && r.Emoji == emoji);

                if (existingReaction != null)
                {
                    // Remove reaction (toggle off)
                    db.MessageReactions.Remove(existingReaction);
                    await db.SaveChangesAsync();
                    OnLog?.Invoke($"[REACT] {user.Username} removed reaction {emoji} from message {messageId}");
                }
                else
                {
                    // Add reaction
                    var reaction = new MessageReaction
                    {
                        MessageId = messageId,
                        UserId = userId,
                        Emoji = emoji,
                        CreatedAt = DateTime.UtcNow
                    };
                    db.MessageReactions.Add(reaction);
                    await db.SaveChangesAsync();
                    OnLog?.Invoke($"[REACT] {user.Username} reacted {emoji} to message {messageId}");
                }

                await BroadcastReactionUpdate(messageId, db);
            }
        }

        private string SerializeReactions(IEnumerable<MessageReaction> reactions)
        {
            return JsonSerializer.Serialize(GroupReactions(reactions));
        }

        private List<object> GroupReactions(IEnumerable<MessageReaction> reactions)
        {
            return reactions
                .GroupBy(r => r.Emoji)
                .Select(g => new { emoji = g.Key, users = g.Where(r => r.User != null).Select(r => r.User!.Username).ToList() } as object)
                .ToList();
        }

        private async Task BroadcastReactionUpdate(string messageId, ChatDbContext db)
        {
            var reactions = await db.MessageReactions
                .Include(r => r.User)
                .Where(r => r.MessageId == messageId)
                .ToListAsync();

            var reactionsJson = SerializeReactions(reactions);
            await BroadcastAsync($"REACTION_UPDATE|{messageId}|{reactionsJson}");
        }

        public async Task BroadcastAsync(string message, string excludeClientId = "")
        {
            var data = Encoding.UTF8.GetBytes(message + "\n");
            foreach (var kvp in _clients)
            {
                if (kvp.Key == excludeClientId) continue;
                try
                {
                    var stream = kvp.Value.GetStream();
                    await stream.WriteAsync(data);
                    await stream.FlushAsync();
                }
                catch { }
            }
        }
    }

    public class ChatClient
    {
        private const int ChatPort = 9999;
        private TcpClient? _client;
        private StreamWriter? _writer;
        
        public event Action<string>? OnMessageReceived;

        public async Task ConnectAsync(string serverIp, CancellationToken token)
        {
            _client = new TcpClient();
            await _client.ConnectAsync(serverIp, ChatPort);
            var stream = _client.GetStream();
            _writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };

            // Start listening loop
            _ = Task.Run(async () =>
            {
                using var reader = new StreamReader(stream, Encoding.UTF8);
                try
                {
                    while (!token.IsCancellationRequested)
                    {
                        var message = await reader.ReadLineAsync();
                        if (message == null) break;
                        OnMessageReceived?.Invoke(message);
                    }
                }
                catch { }
            }, token);
        }

        public async Task SendMessageAsync(string message)
        {
            if (_client == null || !_client.Connected || _writer == null)
            {
                throw new InvalidOperationException("Not connected to server.");
            }

            try
            {
                await _writer.WriteLineAsync(message);
                await _writer.FlushAsync();
            }
            catch (Exception)
            {
                _client?.Close();
                _client = null;
                _writer = null;
                throw;
            }
        }

        public void Disconnect()
        {
            _client?.Close();
            _client = null;
        }
    }
}
