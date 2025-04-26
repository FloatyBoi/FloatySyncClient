using Serilog.Events;
using Serilog;
using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace FloatySyncClient
{
	internal class Program
	{
		static ClientConfig? config;
		public static bool isSynching = false;
		static async Task Main(string[] args)
		{
			var configPath = Path.Combine(AppContext.BaseDirectory, "config.json");

			Log.Logger = new LoggerConfiguration()
				.MinimumLevel.Debug()
				.WriteTo.Console()
				.WriteTo.File("logs/sync-.log",
						rollingInterval: RollingInterval.Day,
						retainedFileCountLimit: 7,
						restrictedToMinimumLevel: LogEventLevel.Debug)
				.CreateLogger();


			AppDomain.CurrentDomain.UnhandledException += (s, e) =>
			{
				Log.Fatal((Exception)e.ExceptionObject, "Unhandled exception");
				Log.CloseAndFlush();
			};

			TaskScheduler.UnobservedTaskException += (s, e) =>
			{
				Log.Fatal(e.Exception, "Unobserved task exception");
				e.SetObserved();
				Log.CloseAndFlush();
			};

			if (File.Exists(configPath))
			{
				config = JsonSerializer.Deserialize<ClientConfig>(File.ReadAllText(configPath));

				if (config != null && config.ServerUrl != null)
					Console.WriteLine("Succesfully loaded config");
				else
				{
					Console.WriteLine("Please fill out relevant info in config file.");
					Console.ReadKey();
					Environment.Exit(0);
				}
			}
			else
			{
				File.WriteAllText(configPath, JsonSerializer.Serialize(new ClientConfig { }));
				Console.WriteLine("Couldnt load config, created a new file.\nPlease fill out relevant info");
				Console.ReadKey();
				Environment.Exit(0);
			}

			//Sanitize server ip
			config.ServerUrl = config.ServerUrl.Trim();
			if (config.ServerUrl.EndsWith('/'))
				config.ServerUrl = config.ServerUrl.Remove(config.ServerUrl.LastIndexOf('/'));

			using var db = new SyncDbContext();
			db.Database.EnsureCreated();
			DatabaseCleanup();
			var allGroups = db.Groups!.ToList();

			List<GroupFileWatcher> watchers = new List<GroupFileWatcher>();

			foreach (var localGroup in allGroups)
			{
				var watcher = new GroupFileWatcher(
					localGroup.IdOnServer,
					localGroup.Key,
					localGroup.LocalFolder,
					config.ServerUrl);

				watcher.LastSyncUtc = localGroup.LastSyncUtc;
				watcher.ScanLocalFolder();
				await watcher.CatchUpSync();
				watcher.StartWatching();
				watchers.Add(watcher);
			}

			_ = Task.Run(async () =>
			{

				while (true)
				{
					try
					{
						foreach (var wg in watchers)
						{
							isSynching = true;
							await wg.RunFullSync();
							wg.ScanLocalFolder();
							await wg.FlushQueue();
							isSynching = false;
						}
						await Task.Delay(TimeSpan.FromMinutes(1));
					}
					catch (Exception ex)
					{
						Log.Error(ex, "Error inside background sync loop");
						await Task.Delay(TimeSpan.FromMinutes(1));
					}
				}
			});

			_ = Task.Run(async () =>
			{
				while (true)
				{
					try
					{
						await Task.Delay(TimeSpan.FromMinutes(10));
						DatabaseCleanup();
					}
					catch (Exception ex)
					{
						Log.Error(ex, "Error inside background cleanup loop");
						await Task.Delay(TimeSpan.FromMinutes(10));
					}
				}
			});

			Console.WriteLine("Started background synching of directories.\nAdditional Options:");
			while (true)
			{
				try
				{
					Console.WriteLine("1) Create New Group");
					Console.WriteLine("2) Join Existing Group");
					Console.WriteLine("3) List Joined Group(s)");
					var choice = Console.ReadLine();

					if (choice == "1")
					{
						Console.Write("Enter local folder path: ");
						var localPath = Console.ReadLine();

						Console.Write("Enter group name: ");
						var groupName = Console.ReadLine();

						Console.Write("Enter group key: ");
						var groupKey = Console.ReadLine();

						int serverGroupId = await Helpers.CreateGroupOnServer(groupName, groupKey, config.ServerUrl);

						var group = new Group
						{
							IdOnServer = serverGroupId,
							Name = groupName!,
							Key = groupKey!,
							LocalFolder = localPath!,
							LastSyncUtc = DateTime.MinValue
						};
						db.Groups!.Add(group);
						allGroups.Add(group);
						db.SaveChanges();

						await ForceUploadAll(localPath, serverGroupId, groupKey);

						var watcher = new GroupFileWatcher(
							group.IdOnServer,
							group.Key,
							group.LocalFolder,
							config.ServerUrl);

						watcher.LastSyncUtc = group.LastSyncUtc;

						watcher.StartWatching();
						watchers.Add(watcher);

						Console.WriteLine($"Succesfully created group {groupName} with id: {group.IdOnServer}");
					}
					else if (choice == "2")
					{
						Console.Write("Enter server group ID: ");
						int serverGroupId = int.Parse(Console.ReadLine()!);

						Console.Write("Enter group key: ");
						var groupKey = Console.ReadLine();

						Console.Write("Enter local folder path (MUST be empty): ");
						var localPath = Console.ReadLine();

						var groupName = await Helpers.GetGroupNameByIdFromServer(serverGroupId, config.ServerUrl);

						var group = new Group
						{
							IdOnServer = serverGroupId,
							Name = groupName,
							Key = groupKey!,
							LocalFolder = localPath!,
							LastSyncUtc = DateTime.MinValue
						};
						db.Groups!.Add(group);
						allGroups.Add(group);
						db.SaveChanges();

						await ForceDownloadAll(localPath, serverGroupId, groupKey);

						var watcher = new GroupFileWatcher(
							group.IdOnServer,
							group.Key!,
							group.LocalFolder,
							config.ServerUrl);

						watcher.LastSyncUtc = group.LastSyncUtc;

						watcher.StartWatching();
						watchers.Add(watcher);

						Console.WriteLine($"Succesfully joined group {groupName} with id: {serverGroupId}");
					}
					else if (choice == "3")
					{
						Console.WriteLine();
						foreach (var localGroup in allGroups)
						{
							Console.WriteLine($"{localGroup.Id}: {localGroup.Name} - {localGroup.Key}");
						}
						Console.WriteLine();
					}
				}
				catch (Exception ex)
				{
					Console.WriteLine($"Request failed: {ex.Message}");
					Log.Error(ex, "Error with groups:");
				}
			}
		}

		private static async Task ForceDownloadAll(string? localFolder, int groupId, string? groupKey)
		{
			HttpClient client = new HttpClient();

			//So that server returns all values
			DateTime lastSync = DateTime.MinValue;

			string lastSyncParam = Uri.EscapeDataString(lastSync.ToString("O"));
			string url = $"{config!.ServerUrl}/api/files/changes?groupId={groupId}&groupKeyPlaintext={Uri.EscapeDataString(groupKey!)}&lastSyncUtc={lastSyncParam}";

			var response = await client.GetAsync(url);
			response.EnsureSuccessStatusCode();

			var changedFiles = await response.Content.ReadFromJsonAsync<List<FileChangeInfo>>();
			if (changedFiles == null)
			{
				Console.WriteLine("No files returned from the server.");
				return;
			}

			using var db = new SyncDbContext();

			foreach (var serverFile in changedFiles)
			{
				// If its deleted on the server skip it
				if (serverFile.IsDeleted)
				{
					continue;
				}

				var localPath = Path.Combine(localFolder!, PathNorm.ToDisk(serverFile.RelativePath!));

				var directoryName = Path.GetDirectoryName(PathNorm.ToDisk(localPath));

				Directory.CreateDirectory(directoryName!);

				var existingDirectory = db.Files!.FirstOrDefault(f => f.RelativePath == Path.GetDirectoryName(serverFile.RelativePath)
																	&& f.GroupId == groupId.ToString());

				if (Directory.Exists(directoryName) && Path.GetDirectoryName(PathNorm.ToDisk(localPath)) != localFolder && existingDirectory == null)
				{
					db.Files!.Add(new FileMetadata
					{
						RelativePath = PathNorm.Normalize(Path.GetDirectoryName(PathNorm.ToDisk(serverFile.RelativePath))),
						LastModifiedUtc = DateTime.UtcNow,
						GroupId = groupId.ToString(),
						StoredPathOnClient = PathNorm.Normalize(directoryName),
						Checksum = null,
						IsDirectory = true
					});

					await Helpers.CreateDirectoryOnServer(groupId, groupKey, PathNorm.Normalize(Path.GetDirectoryName(PathNorm.ToDisk(serverFile.RelativePath))), url);
				}

				if (!serverFile.IsDirectory)
					await Helpers.DownloadFileServer(groupId, groupKey, PathNorm.Normalize(serverFile.RelativePath!), localPath, config.ServerUrl!);

				var existing = db.Files!
					.FirstOrDefault(f => f.RelativePath == PathNorm.Normalize(serverFile.RelativePath)
									  && f.GroupId == groupId.ToString());

				if (existing == null)
				{
					db.Files!.Add(new FileMetadata
					{
						RelativePath = PathNorm.Normalize(serverFile.RelativePath!),
						LastModifiedUtc = serverFile.LastModifiedUtc,
						GroupId = groupId.ToString(),
						StoredPathOnClient = PathNorm.Normalize(localPath),
						Checksum = Helpers.ComputeFileChecksum(localPath),
						IsDirectory = serverFile.IsDirectory
					});
				}
				else
				{
					existing.LastModifiedUtc = serverFile.LastModifiedUtc;
					existing.StoredPathOnClient = PathNorm.Normalize(localPath);
					existing.Checksum = Helpers.ComputeFileChecksum(localPath);
					existing.IsDirectory = serverFile.IsDirectory;
				}
			}

			db.SaveChanges();
			Console.WriteLine($"Download completed for group {groupId}");
		}

		private static async Task ForceUploadAll(string? localPath, int serverGroupId, string? groupKey)
		{
			var allFiles = Directory.GetFiles(localPath!, "*", SearchOption.AllDirectories);

			var allDirectories = Directory.GetDirectories(localPath!, "*", SearchOption.AllDirectories);

			using var db = new SyncDbContext();

			foreach (var filePath in allFiles)
			{
				string relativePath = PathNorm.Normalize(Path.GetRelativePath(localPath!, filePath));
				await Helpers.UploadFileToServer(filePath, DateTime.UtcNow, serverGroupId, groupKey, relativePath, config!.ServerUrl!);

				var existing = db.Files!
					.FirstOrDefault(f => f.RelativePath == relativePath &&
										 f.GroupId == serverGroupId.ToString());

				if (existing == null)
				{
					var newRecord = new FileMetadata
					{
						RelativePath = relativePath,
						LastModifiedUtc = DateTime.UtcNow,
						Checksum = Helpers.ComputeFileChecksum(filePath),
						GroupId = serverGroupId.ToString(),
						StoredPathOnClient = PathNorm.Normalize(filePath),
						IsDirectory = Helpers.WasDirectory(filePath, serverGroupId)
					};
					db.Files!.Add(newRecord);
				}
				else
				{
					existing.LastModifiedUtc = DateTime.UtcNow;
					existing.Checksum = Helpers.ComputeFileChecksum(filePath);
					existing.StoredPathOnClient = PathNorm.Normalize(filePath);
					existing.IsDirectory = Helpers.WasDirectory(filePath, serverGroupId);
				}
			}

			foreach (var directory in allDirectories)
			{
				string relativePath = PathNorm.Normalize(Path.GetRelativePath(localPath!, directory));
				await Helpers.CreateDirectoryOnServer(serverGroupId, groupKey!, relativePath, config!.ServerUrl!);

				var existing = db.Files!
					.FirstOrDefault(f => f.RelativePath == relativePath &&
										 f.GroupId == serverGroupId.ToString());
				if (existing == null)
				{
					var newRecord = new FileMetadata
					{
						RelativePath = relativePath,
						LastModifiedUtc = DateTime.UtcNow,
						Checksum = null,
						GroupId = serverGroupId.ToString(),
						StoredPathOnClient = PathNorm.Normalize(directory),
						IsDirectory = Helpers.WasDirectory(directory, serverGroupId)
					};

					db.Files!.Add(newRecord);
				}
				else
				{
					existing.LastModifiedUtc = DateTime.UtcNow;
					existing.StoredPathOnClient = PathNorm.Normalize(directory);
					existing.IsDirectory = true;
				}
			}

			db.SaveChanges();
			Console.WriteLine($"Upload completed for group {serverGroupId}");
		}

		private static void DatabaseCleanup()
		{
			using var db = new SyncDbContext();

			Console.WriteLine("[Cleanup] Sweep started");

			var dupGroups = db.Files!
				.Select(f => new
				{
					f.Id,
					f.GroupId,
					f.RelativePath,
					f.LastModifiedUtc,
					f.Checksum,
					f.IsDirectory,
					f.IsDeleted
				})
				.AsEnumerable()
				.GroupBy(f => (f.GroupId, f.RelativePath))
				.Where(g => g.Count() > 1);

			foreach (var g in dupGroups)
			{
				var ordered = g.OrderByDescending(r => r.LastModifiedUtc).ToList();
				var keeper = db.Files!.Find(ordered[0].Id)!;

				foreach (var extra in ordered.Skip(1))
				{
					var row = db.Files!.Find(extra.Id)!;

					if (!keeper.IsDirectory)
						keeper.Checksum ??= row.Checksum;

					keeper.IsDeleted &= row.IsDeleted;
					db.Files!.Remove(row);
				}
			}
			db.SaveChanges();

			var toRemove = db.Files!
				.Where(f => f.IsDeleted)
				.Join(db.Files!,
					  d => new { d.GroupId, d.RelativePath },
					  l => new { l.GroupId, l.RelativePath },
					  (d, l) => d)
				.Distinct()
				.ToList();

			if (toRemove.Count > 0)
			{
				db.Files!.RemoveRange(toRemove);
				db.SaveChanges();
			}

			var missing = db.PendingChanges!
				.Where(p => !db.Files!.Any(f => f.GroupId == p.GroupId.ToString() &&
												f.RelativePath == p.RelativePath))
				.ToList();

			if (missing.Count > 0)
			{
				db.PendingChanges!.RemoveRange(missing);
				db.SaveChanges();
			}

			Console.WriteLine($"[Cleanup] Done");
		}

	}
}
