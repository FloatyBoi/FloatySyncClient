using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http.Json;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace FloatySyncClient
{
	public class GroupFileWatcher
	{
		private readonly string _serverUrl;
		private readonly int _serverGroupId;
		private readonly string _groupKey;
		private readonly string _localFolder;
		private readonly HttpClient _httpClient;


		private FileSystemWatcher _watcher;

		public DateTime LastSyncUtc { get; set; } = DateTime.MinValue;

		public GroupFileWatcher(int serverGroupId, string groupKey, string localFolder, string serverUrl)
		{
			_serverGroupId = serverGroupId;
			_groupKey = groupKey;
			_localFolder = localFolder;
			_httpClient = new HttpClient();
			_serverUrl = serverUrl;
		}

		public void StartWatching()
		{
			Directory.CreateDirectory(_localFolder);

			_watcher = new FileSystemWatcher(_localFolder);
			_watcher.IncludeSubdirectories = true;
			_watcher.EnableRaisingEvents = true;

			_watcher.Created += OnCreated;
			_watcher.Changed += OnChanged;
			_watcher.Renamed += OnRenamed;
			_watcher.Deleted += OnDeleted;
		}

		// FileSystemWatcher handlers
		//TODO: What happens on conflict?
		private void OnCreated(object sender, FileSystemEventArgs e)
		{

			var _syncDbContext = new SyncDbContext();
			Task.Delay(500);

			if (File.Exists(e.FullPath))
			{

				FileMetadata fileMetadata = new FileMetadata();
				fileMetadata.LastModifiedUtc = DateTime.UtcNow;
				fileMetadata.RelativePath = Path.GetRelativePath(_localFolder, e.FullPath);
				fileMetadata.StoredPathOnClient = e.FullPath;
				fileMetadata.Checksum = Helpers.ComputeFileChecksum(e.FullPath);
				fileMetadata.GroupId = _serverGroupId.ToString();

				_syncDbContext.Files.Add(fileMetadata);
				_syncDbContext.SaveChanges();

				Helpers.UploadFileToServer(e.FullPath, fileMetadata.LastModifiedUtc, _serverGroupId, _groupKey, Path.GetRelativePath(_localFolder, e.FullPath), _serverUrl);
			}
			else if (Directory.Exists(e.FullPath))
			{
				FileMetadata directoryMetadata = new FileMetadata();
				directoryMetadata.LastModifiedUtc = DateTime.UtcNow;
				directoryMetadata.RelativePath = Path.GetRelativePath(_localFolder, e.FullPath);
				directoryMetadata.StoredPathOnClient = e.FullPath;
				directoryMetadata.Checksum = null;
				directoryMetadata.GroupId = _serverGroupId.ToString();
				directoryMetadata.IsDirectory = true;

				_syncDbContext.Files.Add(directoryMetadata);
				_syncDbContext.SaveChanges();

				Helpers.CreateDirectoryOnServer(e.FullPath, _serverGroupId, _groupKey, Path.GetRelativePath(_localFolder, e.FullPath), _serverUrl);
			}

		}
		private void OnChanged(object sender, FileSystemEventArgs e)
		{
			if (Program.isRunningSync)
				return;

			var _syncDbContext = new SyncDbContext();
			Task.Delay(500);

			if (File.Exists(e.FullPath))
			{
				var fileMetadata = _syncDbContext.Files
					.First(f => f.RelativePath == Path.GetRelativePath(_localFolder, e.FullPath)
									  && f.GroupId == _serverGroupId.ToString());

				fileMetadata.Checksum = Helpers.ComputeFileChecksum(e.FullPath);
				fileMetadata.LastModifiedUtc = DateTime.UtcNow;

				_syncDbContext.SaveChanges();

				Helpers.UploadFileToServer(e.FullPath, fileMetadata.LastModifiedUtc, _serverGroupId, _groupKey, Path.GetRelativePath(_localFolder, e.FullPath), _serverUrl);
			}
			else if (Directory.Exists(e.FullPath))
			{
				//Ignore (What can change in directories?)
			}
		}
		private void OnRenamed(object sender, RenamedEventArgs e)
		{
			if (Program.isRunningSync)
				return;

			var _syncDbContext = new SyncDbContext();
			Task.Delay(500);

			if (File.Exists(e.FullPath))
			{
				var fileMetadata = _syncDbContext.Files
					.First(f => f.RelativePath == Path.GetRelativePath(_localFolder, e.OldFullPath)
									  && f.GroupId == _serverGroupId.ToString());

				fileMetadata.Checksum = Helpers.ComputeFileChecksum(e.FullPath);
				fileMetadata.LastModifiedUtc = DateTime.UtcNow;
				fileMetadata.StoredPathOnClient = e.FullPath;
				fileMetadata.RelativePath = Path.GetRelativePath(_localFolder, e.FullPath);

				_syncDbContext.SaveChanges();

				Helpers.MoveFileOnServer(Path.GetRelativePath(_localFolder, e.OldFullPath), Path.GetRelativePath(_localFolder, e.FullPath), _serverGroupId, _groupKey, _serverUrl);
			}
			else if (Directory.Exists(e.FullPath))
			{
				HandleDirectoryRename(e.OldFullPath, e.FullPath);
			}
		}

		private async void HandleDirectoryRename(string oldFullPath, string fullPath)
		{
			HttpClient client = new HttpClient();

			var oldRel = Path.GetRelativePath(_localFolder, oldFullPath);
			var newRel = Path.GetRelativePath(_localFolder, fullPath);

			var body = new
			{
				GroupId = _serverGroupId,
				GroupKey = _groupKey,
				OldPath = oldRel,
				NewPath = newRel,
			};

			await client.PostAsJsonAsync($"{_serverUrl}/api/directories/rename", body);

			string oldPrefix = oldRel + "/";
			string newPrefix = newRel + "/";

			using var db = new SyncDbContext();
			var affected = db.Files.Where(f => f.RelativePath.StartsWith(oldPrefix) &&
												f.GroupId == _serverGroupId.ToString())
												.ToList();
			foreach (var meta in affected)
			{
				string tail = meta.RelativePath.Substring(oldPrefix.Length);
				string newPath = newPrefix + tail;

				if (!meta.IsDirectory && File.Exists(meta.StoredPathOnClient))
				{
					var newDiskPath = Path.Combine(_localFolder, newPath);
					Directory.CreateDirectory(Path.GetDirectoryName(newDiskPath)!);
					File.Move(meta.StoredPathOnClient, newPath, overwrite: true);
					meta.StoredPathOnClient = newDiskPath;
				}

				meta.RelativePath = newPath;
				meta.LastModifiedUtc = DateTime.UtcNow;
			}

			db.SaveChanges();
		}

		private void OnDeleted(object sender, FileSystemEventArgs e)
		{
			if (Program.isRunningSync)
				return;

			var _syncDbContext = new SyncDbContext();
			Task.Delay(500);

			var fileMetadata = _syncDbContext.Files
				.First(f => f.RelativePath == Path.GetRelativePath(_localFolder, e.FullPath)
								  && f.GroupId == _serverGroupId.ToString());

			if (!Helpers.WasDirectory(e.FullPath, _serverGroupId))
				Helpers.DeleteOnServer(fileMetadata.RelativePath, fileMetadata.Checksum, _serverGroupId, _groupKey, _serverUrl);
			else
			{
				HandleDirectoryDelete(e.FullPath);
			}

			_syncDbContext.Remove(fileMetadata);
			_syncDbContext.SaveChanges();
		}

		private async void HandleDirectoryDelete(string fullPath)
		{
			var rel = Path.GetRelativePath(_localFolder, fullPath);

			var query = $"groupId={_serverGroupId}&groupKeyPlaintext={_groupKey}&relativePath={Uri.EscapeDataString(rel)}";
			await _httpClient.DeleteAsync($"{_serverUrl}/api/directories?{query}");

			string prefix = rel + "/";
			using var db = new SyncDbContext();
			var toDelete = db.Files.Where(f => f.RelativePath.StartsWith(prefix)
											&& f.GroupId == _serverGroupId.ToString())
				.ToList();

			db.Files.RemoveRange(toDelete);
			db.SaveChanges();

			if (Directory.Exists(fullPath))
				Directory.Delete(fullPath, true);
		}

		//TODO: What happens on conflict?
		public async Task RunFullSync()
		{
			var _syncDbContext = new SyncDbContext();
			Console.WriteLine($"[Sync Group {_serverGroupId} Start]");
			DateTime lastSyncCopy = LastSyncUtc;
			await PushLocalChanges(lastSyncCopy);

			await PullServerChanges(lastSyncCopy);

			var groupRecord = _syncDbContext.Groups.FirstOrDefault(g => g.IdOnServer == _serverGroupId);

			DateTime newMaxSyncTime = CalculateNewMaxSyncTime();

			if (newMaxSyncTime > LastSyncUtc)
			{
				LastSyncUtc = newMaxSyncTime;
				if (groupRecord != null)
				{
					groupRecord.LastSyncUtc = LastSyncUtc;
					_syncDbContext.SaveChanges();
				}
				Console.WriteLine($"[Sync] Sync complete. Updated last sync to {LastSyncUtc:O}");
			}
			else
			{
				Console.WriteLine("[Sync] Sync complete. No new changes found.");
			}
			Console.WriteLine($"[Sync Group {_serverGroupId} End]");
		}

		private async Task PushLocalChanges(DateTime lastSyncUtc)
		{
			var _syncDbContext = new SyncDbContext();
			var changedLocalFiles = _syncDbContext.Files
				.Where(f => f.GroupId == _serverGroupId.ToString() && f.LastModifiedUtc > lastSyncUtc)
				.ToList();

			if (!changedLocalFiles.Any())
			{
				Console.WriteLine("[Sync] No local changes to push.");
				return;
			}

			foreach (var localFile in changedLocalFiles)
			{
				string fullPath = localFile.StoredPathOnClient;

				if (localFile.IsDirectory && Directory.Exists(fullPath))
				{
					Console.WriteLine($"[Sync] Creating directory on server: {localFile.RelativePath}");
					Helpers.CreateDirectoryOnServer(fullPath, _serverGroupId, _groupKey, localFile.RelativePath, _serverUrl);
					continue;
				}

				if (!File.Exists(fullPath))
				{
					Console.WriteLine($"[Sync] Skipping {localFile.RelativePath} because file missing locally.");
					continue;
				}

				Console.WriteLine($"[Sync] Pushing local change: {localFile.RelativePath}");

				Helpers.UploadFileToServer(fullPath, localFile.LastModifiedUtc, _serverGroupId, _groupKey, localFile.RelativePath, _serverUrl);

			}
		}

		private async Task PullServerChanges(DateTime lastSyncUtc)
		{
			var _syncDbContext = new SyncDbContext();

			string lastSyncParam = Uri.EscapeDataString(lastSyncUtc.ToString("O"));
			string url = $"{_serverUrl}/api/files/changes?groupId={_serverGroupId}" +
						 $"&groupKeyPlaintext={_groupKey}" +
						 $"&lastSyncUtc={lastSyncParam}";

			var response = await _httpClient.GetAsync(url);
			if (!response.IsSuccessStatusCode)
			{
				Console.WriteLine($"[Sync] server returned {response.StatusCode}");
				return;
			}

			var changedFiles = await response.Content.ReadFromJsonAsync<List<FileChangeInfo>>();
			if (changedFiles == null || changedFiles.Count == 0)
			{
				Console.WriteLine("[Sync] No server changes to pull.");
				return;
			}

			foreach (var serverFile in changedFiles)
			{
				if (serverFile.IsDeleted)
				{
					string localPath = Path.Combine(_localFolder, serverFile.RelativePath);
					if (File.Exists(localPath))
					{
						File.Delete(localPath);
						Console.WriteLine($"[Sync] Pulled server delete: {serverFile.RelativePath}");
					}
					else if (Directory.Exists(localPath))
					{
						Directory.Delete(localPath, true);
						Console.WriteLine($"[Sync] Pulled server directory delete: {serverFile.RelativePath}");
					}
					// Remove from local DB
					var existing = _syncDbContext.Files
						.FirstOrDefault(f => f.RelativePath == serverFile.RelativePath
										  && f.GroupId == _serverGroupId.ToString());
					if (existing != null)
					{
						_syncDbContext.Files.Remove(existing);
					}
				}
				else
				{
					// New or updated file
					string localPath = Path.Combine(_localFolder, serverFile.RelativePath);
					Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);

					if (!serverFile.IsDirectory)
					{
						// Download
						await Helpers.DownloadFileServer(_serverGroupId, _groupKey, serverFile.RelativePath, localPath, _serverUrl);
					}
					var existing = _syncDbContext.Files
						.FirstOrDefault(f => f.RelativePath == serverFile.RelativePath
										  && f.GroupId == _serverGroupId.ToString());
					if (existing == null)
					{
						_syncDbContext.Files.Add(new FileMetadata
						{
							RelativePath = serverFile.RelativePath,
							LastModifiedUtc = serverFile.LastModifiedUtc,
							GroupId = _serverGroupId.ToString(),
							StoredPathOnClient = localPath
						});
					}
					else
					{
						existing.LastModifiedUtc = serverFile.LastModifiedUtc;
						existing.StoredPathOnClient = localPath;
					}

					Console.WriteLine($"[Sync] Pulled file from server: {serverFile.RelativePath}");
				}
			}

			_syncDbContext.SaveChanges();
		}

		private DateTime CalculateNewMaxSyncTime()
		{
			var _syncDbContext = new SyncDbContext();
			DateTime maxLocal = _syncDbContext.Files
				.Where(f => f.GroupId == _serverGroupId.ToString())
				.Select(f => f.LastModifiedUtc)
				.ToList()
				.DefaultIfEmpty(DateTime.UtcNow)
				.Max();

			return maxLocal > LastSyncUtc ? maxLocal : LastSyncUtc;
		}
	}

}
