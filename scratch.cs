using System;
using System.Text.Json;

public class MainWindow
{
    public class UserConfig
    {
        public string UserId { get; set; } = "";
        public string Username { get; set; } = "";
        public string AvatarBase64 { get; set; } = "";
    }
}

class Program
{
    static void Main()
    {
        var config = new MainWindow.UserConfig { UserId = "123", Username = "Test", AvatarBase64 = "abc" };
        string json = JsonSerializer.Serialize(config);
        Console.WriteLine(json);
        
        var deserialized = JsonSerializer.Deserialize<MainWindow.UserConfig>(json);
        Console.WriteLine(deserialized.UserId);
    }
}
