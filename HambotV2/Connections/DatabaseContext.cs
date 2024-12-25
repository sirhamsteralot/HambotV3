using Discord;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace HambotV2.Connections
{
    public class DatabaseContext : DbContext
    {
        public DatabaseContext(DbContextOptions<DatabaseContext> options) : base(options) { }

        public DbSet<CommandMetaData> Commands { get; set; }

        public DbSet<User> Users { get; set; }

        public class User
        {
            [Key]
            public ulong DiscordId { get; set; }
            public int PermissionTier { get; set; }
        }

        public class CommandMetaData
        {
            [Key]
            public string CommandName { get; set; }
            public int PermissionTier { get; set; }
        }
    }
}
