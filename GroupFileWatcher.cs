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

		private SyncDbContext _syncDbContext;

		private FileSystemWatcher _watcher;

		public DateTime LastSyncUtc { get; set; } = DateTime.MinValue;

		public GroupFileWatcher(int serverGroupId, string groupKey, string localFolder, string serverUrl, SyncDbContext syncDbContext)
		{
			_serverGroupId = serverGroupId;
			_groupKey = groupKey;
			_localFolder = localFolder;
			_httpClient = new HttpClient();
			_serverUrl = serverUrl;
			_syncDbContext = syncDbContext;
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
		//TODO: Sanitize the events somehow
		//TODO: What happens on conflict?
		private void OnCreated(object sender, FileSystemEventArgs e)
		{
			Task.Delay(100);

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
				//TODO: Directory
			}

		}
		private void OnChanged(object sender, FileSystemEventArgs e)
		{
			Task.Delay(100);

			if (File.Exists(e.FullPath))
			{
				var fileMetadata = _syncDbContext.Files
					.First(f => f.RelativePath == Path.GetRelativePath(_localFolder, e.FullPath)
									  && f.GroupId == _serverGroupId.ToString());

				fileMetadata.Checksum = Helpers.ComputeFileChecksum(_localFolder);
				fileMetadata.LastModifiedUtc = DateTime.UtcNow;

				_syncDbContext.SaveChanges();

				Helpers.UploadFileToServer(e.FullPath, fileMetadata.LastModifiedUtc, _serverGroupId, _groupKey, Path.GetRelativePath(_localFolder, e.FullPath), _serverUrl);
			}
			else if (Directory.Exists(e.FullPath))
			{
				//TODO: Directory
			}
		}
		private void OnRenamed(object sender, RenamedEventArgs e)
		{
			Task.Delay(100);

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
				//TODO: Directory
			}
		}
		private void OnDeleted(object sender, FileSystemEventArgs e)
		{
			Task.Delay(100);

			var fileMetadata = _syncDbContext.Files
				.First(f => f.RelativePath == Path.GetRelativePath(_localFolder, e.FullPath)
								  && f.GroupId == _serverGroupId.ToString());

			Helpers.DeleteOnServer(fileMetadata.RelativePath, fileMetadata.Checksum, _serverGroupId, _groupKey, _serverUrl);
		}

		//TODO: What happens on conflict?
		public async Task RunFullSync()
		{
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
		}

		private async Task PushLocalChanges(DateTime lastSyncUtc)
		{
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

					// Download
					await Helpers.DownloadFileServer(_serverGroupId, _groupKey, serverFile.RelativePath, localPath, _serverUrl);

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
			var maxLocal = _syncDbContext.Files
				.Where(f => f.GroupId == _serverGroupId.ToString())
				.Select(f => f.LastModifiedUtc)
				.DefaultIfEmpty(LastSyncUtc)
				.Max();

			return maxLocal > LastSyncUtc ? maxLocal : LastSyncUtc;
		}
	}

}
