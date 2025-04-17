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
		public static bool isRunningSync = false;

		static ClientConfig config = null;
		static async Task Main(string[] args)
		{
			var configPath = Path.Combine(AppContext.BaseDirectory, "config.json");

			if (File.Exists(configPath))
			{
				config = JsonSerializer.Deserialize<ClientConfig>(File.ReadAllText(configPath));

				if (config.ServerUrl != null)
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

			using var db = new SyncDbContext();
			db.Database.EnsureCreated();
			var allGroups = db.Groups.ToList();

			List<GroupFileWatcher> watchers = new List<GroupFileWatcher>();

			foreach (var localGroup in allGroups)
			{
				var watcher = new GroupFileWatcher(
					localGroup.IdOnServer,
					localGroup.Key,
					localGroup.LocalFolder,
					config.ServerUrl);

				watcher.LastSyncUtc = localGroup.LastSyncUtc;

				watcher.StartWatching();
				watchers.Add(watcher);
			}

			_ = Task.Run(async () =>
			{
				try
				{
					while (true)
					{
						foreach (var wg in watchers)
						{
							isRunningSync = true;
							await wg.RunFullSync();
							isRunningSync = false;
						}
						await Task.Delay(TimeSpan.FromMinutes(1));
					}
				}
				catch (Exception ex)
				{
					Console.WriteLine(ex.ToString());
				}
			});

			Console.WriteLine("Started background synching of directories.\nAdditional Options:");
			//TODO: Authentication with the server. Is group valid?
			while (true)
			{
				Console.WriteLine("1) Create New Group");
				Console.WriteLine("2) Join Existing Group");
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
						Name = groupName,
						Key = groupKey,
						LocalFolder = localPath,
						LastSyncUtc = DateTime.MinValue
					};
					db.Groups.Add(group);
					db.SaveChanges();

					ForceUploadAll(localPath, serverGroupId, groupKey);

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
						Key = groupKey,
						LocalFolder = localPath,
						LastSyncUtc = DateTime.MinValue
					};
					db.Groups.Add(group);
					db.SaveChanges();

					ForceDownloadAll(localPath, serverGroupId, groupKey);

					var watcher = new GroupFileWatcher(
						group.IdOnServer,
						group.Key,
						group.LocalFolder,
						config.ServerUrl);

					watcher.LastSyncUtc = group.LastSyncUtc;

					watcher.StartWatching();
					watchers.Add(watcher);

					Console.WriteLine($"Succesfully joined group {groupName} with id: {serverGroupId}");
				}
			}
		}

		private static async void ForceDownloadAll(string? localFolder, int groupId, string? groupKey)
		{
			HttpClient client = new HttpClient();

			//So that server returns all values
			DateTime lastSync = DateTime.MinValue;

			string lastSyncParam = Uri.EscapeDataString(lastSync.ToString("O"));
			string url = $"{config.ServerUrl}/api/files/changes?groupId={groupId}&groupKeyPlaintext={groupKey}&lastSyncUtc={lastSyncParam}";

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

				var localPath = Path.Combine(localFolder, PathNorm.ToDisk(serverFile.RelativePath));
				Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);

				if (!serverFile.IsDirectory)
					await Helpers.DownloadFileServer(groupId, groupKey, serverFile.RelativePath, localPath, config.ServerUrl);

				var existing = db.Files
					.FirstOrDefault(f => f.RelativePath == serverFile.RelativePath
									  && f.GroupId == groupId.ToString());

				if (existing == null)
				{
					db.Files.Add(new FileMetadata
					{
						RelativePath = serverFile.RelativePath,
						LastModifiedUtc = serverFile.LastModifiedUtc,
						GroupId = groupId.ToString(),
						StoredPathOnClient = localPath,
						Checksum = Helpers.ComputeFileChecksum(localPath),
						IsDirectory = serverFile.IsDirectory
					});
				}
				else
				{
					existing.LastModifiedUtc = serverFile.LastModifiedUtc;
					existing.StoredPathOnClient = localPath;
					existing.Checksum = Helpers.ComputeFileChecksum(localPath);
					existing.IsDirectory = serverFile.IsDirectory;
				}
			}

			db.SaveChanges();
			Console.WriteLine($"Download completed for group {groupId}");
		}

		private static void ForceUploadAll(string? localPath, int serverGroupId, string? groupKey)
		{
			HttpClient client = new HttpClient();

			var allFiles = Directory.GetFiles(localPath, "*", SearchOption.AllDirectories);

			var allDirectories = Directory.GetDirectories(localPath, "*", SearchOption.AllDirectories);

			using var db = new SyncDbContext();

			foreach (var filePath in allFiles)
			{
				string relativePath = PathNorm.Normalize(Path.GetRelativePath(localPath, filePath));
				Helpers.UploadFileToServer(filePath, DateTime.UtcNow, serverGroupId, groupKey, relativePath, config.ServerUrl);

				var existing = db.Files
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
						StoredPathOnClient = filePath,
						IsDirectory = Helpers.WasDirectory(filePath, serverGroupId)
					};
					db.Files.Add(newRecord);
				}
				else
				{
					existing.LastModifiedUtc = DateTime.UtcNow;
					existing.Checksum = Helpers.ComputeFileChecksum(filePath);
					existing.StoredPathOnClient = filePath;
					existing.IsDirectory = Helpers.WasDirectory(filePath, serverGroupId);
				}
			}

			foreach (var directory in allDirectories)
			{
				string relativePath = PathNorm.Normalize(Path.GetRelativePath(localPath, directory));
				Helpers.CreateDirectoryOnServer(directory, serverGroupId, groupKey, relativePath, config.ServerUrl);

				var existing = db.Files
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
						StoredPathOnClient = directory,
						IsDirectory = Helpers.WasDirectory(directory, serverGroupId)
					};

					db.Files.Add(newRecord);
				}
				else
				{
					existing.LastModifiedUtc = DateTime.UtcNow;
					existing.StoredPathOnClient = directory;
					existing.IsDirectory = true;
				}
			}

			db.SaveChanges();
			Console.WriteLine($"Upload completed for group {serverGroupId}");
		}
	}
}
