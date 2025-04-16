using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FloatySyncClient
{
	public class SyncDbContext : DbContext
	{
		public SyncDbContext()
		{
			var folder = AppContext.BaseDirectory;
			DbPath = System.IO.Path.Combine(folder, "LocalSync.db");
		}

		protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
		{
			optionsBuilder.UseSqlite($"Data Source={DbPath}");
		}

		public DbSet<Group> Groups { get; set; }
		public DbSet<FileMetadata> Files { get; set; }

		public string DbPath { get; }
	}
}
