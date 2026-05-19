using LocalChat.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace LocalChat.Core.Data
{
    public class ChatDbContext : DbContext
    {
        public DbSet<User> Users { get; set; } = null!;
        public DbSet<ChatMessage> ChatMessages { get; set; } = null!;

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            if (!optionsBuilder.IsConfigured)
            {
                optionsBuilder.UseNpgsql("Host=localhost;Database=ChatBoxDb;Username=postgres;Password=12345");
            }
        }
    }
}
