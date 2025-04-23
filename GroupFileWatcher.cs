using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using System;
using System.Collections.Generic;
using System.IO;
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

		public bool _suppressEvents = false;

		private FileSystemWatcher? _watcher;

		public DateTime LastSyncUtc { get; set; } = DateTime.MinValue;

		public GroupFileWatcher(int serverGroupId, string groupKey, string localFolder, string serverUrl)
		{
			_serverGroupId = serverGroupId;
			_groupKey = groupKey;
			_localFolder = localFolder;
			_httpClient = new HttpClient();
			_serverUrl = serverUrl;
		}

		private string RelFromFull(string full) =>
	PathNorm.Normalize(Path.GetRelativePath(_localFolder, full));

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
		private async void OnCreated(object sender, FileSystemEventArgs e)
		{
			if (_suppressEvents || Program.isSynching) return;

			var _syncDbContext = new SyncDbContext();
			await WaitForFileClose(e.FullPath);

			if (File.Exists(e.FullPath))
			{

				FileMetadata fileMetadata = new FileMetadata();
				fileMetadata.LastModifiedUtc = DateTime.UtcNow;
				fileMetadata.RelativePath = RelFromFull(e.FullPath);
				fileMetadata.StoredPathOnClient = e.FullPath;
				fileMetadata.Checksum = Helpers.ComputeFileChecksum(e.FullPath);
				fileMetadata.GroupId = _serverGroupId.ToString();

				_syncDbContext.Files!.Add(fileMetadata);
				_syncDbContext.SaveChanges();

				try
				{
					await Helpers.UploadFileToServer(e.FullPath, fileMetadata.LastModifiedUtc, _serverGroupId, _groupKey, RelFromFull(e.FullPath), _serverUrl);
					LastSyncUtc = DateTime.UtcNow;
				}
				catch (HttpRequestException)
				{
					QueueChange("Upload", RelFromFull(e.FullPath), fileMetadata.Checksum);
				}
			}
			else if (Directory.Exists(e.FullPath))
			{
				FileMetadata directoryMetadata = new FileMetadata();
				directoryMetadata.LastModifiedUtc = DateTime.UtcNow;
				directoryMetadata.RelativePath = RelFromFull(e.FullPath);
				directoryMetadata.StoredPathOnClient = e.FullPath;
				directoryMetadata.Checksum = null;
				directoryMetadata.GroupId = _serverGroupId.ToString();
				directoryMetadata.IsDirectory = true;

				_syncDbContext.Files!.Add(directoryMetadata);
				_syncDbContext.SaveChanges();

				try
				{
					await Helpers.CreateDirectoryOnServer(_serverGroupId, _groupKey, RelFromFull(e.FullPath), _serverUrl);
				}
				catch (HttpRequestException)
				{
					QueueChange("CreateDir", RelFromFull(e.FullPath), null);
				}
			}

		}
		private async void OnChanged(object sender, FileSystemEventArgs e)
		{
			if (_suppressEvents || Program.isSynching) return;

			var _syncDbContext = new SyncDbContext();
			await WaitForFileClose(e.FullPath);

			if (File.Exists(e.FullPath))
			{
				var fileMetadata = _syncDbContext.Files!
					.FirstOrDefault(f => f.RelativePath == RelFromFull(e.FullPath)
									  && f.GroupId == _serverGroupId.ToString());

				if (fileMetadata == null)
					return;

				fileMetadata.Checksum = Helpers.ComputeFileChecksum(e.FullPath);
				fileMetadata.LastModifiedUtc = DateTime.UtcNow;

				_syncDbContext.SaveChanges();
				try
				{
					await Helpers.UploadFileToServer(e.FullPath, fileMetadata.LastModifiedUtc, _serverGroupId, _groupKey, RelFromFull(e.FullPath), _serverUrl);
					LastSyncUtc = DateTime.UtcNow;
				}
				catch (HttpRequestException)
				{
					QueueChange("Upload", RelFromFull(e.FullPath), fileMetadata.Checksum);
				}
			}
			else if (Directory.Exists(e.FullPath))
			{
				//Ignore (What can change in directories?)
			}
		}
		private async void OnRenamed(object sender, RenamedEventArgs e)
		{
			if (_suppressEvents || Program.isSynching) return;

			var _syncDbContext = new SyncDbContext();
			await WaitForFileClose(e.FullPath);

			if (File.Exists(e.FullPath))
			{
				var fileMetadata = _syncDbContext.Files!
					.FirstOrDefault(f => f.RelativePath == PathNorm.Normalize(Path.GetRelativePath(_localFolder, e.OldFullPath))
									  && f.GroupId == _serverGroupId.ToString());

				if (fileMetadata == null)
					return;

				fileMetadata.Checksum = Helpers.ComputeFileChecksum(e.FullPath);
				fileMetadata.LastModifiedUtc = DateTime.UtcNow;
				fileMetadata.StoredPathOnClient = e.FullPath;
				fileMetadata.RelativePath = PathNorm.Normalize(Path.GetRelativePath(_localFolder, e.FullPath));

				_syncDbContext.SaveChanges();

				try
				{
					await Helpers.MoveFileOnServer(PathNorm.Normalize(Path.GetRelativePath(_localFolder, e.OldFullPath)), PathNorm.Normalize(Path.GetRelativePath(_localFolder, e.FullPath)), _serverGroupId, _groupKey, _serverUrl);
					LastSyncUtc = DateTime.UtcNow;
				}
				catch (HttpRequestException)
				{
					QueueChange("Move", PathNorm.Normalize(Path.GetRelativePath(_localFolder, e.OldFullPath)), fileMetadata.Checksum, PathNorm.Normalize(Path.GetRelativePath(_localFolder, e.FullPath)));
				}
			}
			else if (Directory.Exists(e.FullPath))
			{
				try
				{
					await HandleDirectoryRename(e.OldFullPath, e.FullPath);
					LastSyncUtc = DateTime.UtcNow;
				}
				catch (HttpRequestException)
				{
					var oldRel = RelFromFull(e.OldFullPath);
					var newRel = RelFromFull(e.FullPath);

					QueueChange("RenameDir", oldRel, null, newRel);
				}
			}
		}

		private async Task<bool> TryHandleDirRename(PendingChange p)
		{
			try
			{
				await HandleDirectoryRename(Path.Combine(_localFolder, p.RelativePath), Path.Combine(_localFolder, p.AuxPath!));
				LastSyncUtc = DateTime.UtcNow;
				return true;
			}
			catch
			{
				return false;
			}
		}

		private async Task HandleDirectoryRename(string oldFullPath, string fullPath)
		{
			HttpClient client = new HttpClient();

			var oldRel = RelFromFull(oldFullPath);
			var newRel = RelFromFull(fullPath);

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
			var affected = db.Files!.Where(f => f.RelativePath.StartsWith(oldPrefix) &&
												f.GroupId == _serverGroupId.ToString())
												.ToList();
			foreach (var meta in affected)
			{
				string tail = meta.RelativePath.Substring(oldPrefix.Length);
				string newPath = newPrefix + tail;

				if (!meta.IsDirectory && File.Exists(meta.StoredPathOnClient))
				{
					var newDiskPath = Path.Combine(_localFolder, PathNorm.ToDisk(newPath));
					Directory.CreateDirectory(Path.GetDirectoryName(newDiskPath)!);
					File.Move(meta.StoredPathOnClient, newDiskPath, overwrite: true);
					meta.StoredPathOnClient = newDiskPath;
				}

				meta.RelativePath = newPath;
				meta.LastModifiedUtc = DateTime.UtcNow;
			}

			db.SaveChanges();
		}

		private async void OnDeleted(object sender, FileSystemEventArgs e)
		{
			if (_suppressEvents || Program.isSynching) return;

			var _syncDbContext = new SyncDbContext();
			await Task.Delay(500);

			var fileMetadata = _syncDbContext.Files!
				.FirstOrDefault(f => f.RelativePath == RelFromFull(e.FullPath)
								  && f.GroupId == _serverGroupId.ToString());

			if (fileMetadata == null)
				return;

			if (!Helpers.WasDirectory(e.FullPath, _serverGroupId))
			{
				try
				{
					_syncDbContext.Remove(fileMetadata);
					await Helpers.DeleteOnServer(fileMetadata.RelativePath, fileMetadata.Checksum, _serverGroupId, _groupKey, _serverUrl, _localFolder);
				}
				catch (HttpRequestException)
				{
					QueueChange("Delete", fileMetadata.RelativePath, fileMetadata.Checksum);
				}
			}
			else
			{
				try
				{
					_syncDbContext.Remove(fileMetadata);
					await HandleDirectoryDelete(e.FullPath);
					LastSyncUtc = DateTime.UtcNow;
				}
				catch (HttpRequestException)
				{
					QueueChange("DeleteDir", fileMetadata.RelativePath, null);
				}
			}

			_syncDbContext.SaveChanges();
		}

		private async Task<bool> TryHandleDirDelete(PendingChange p)
		{
			var path = Path.Combine(_localFolder, PathNorm.ToDisk(p.RelativePath));

			try
			{
				await HandleDirectoryDelete(path);
				LastSyncUtc = DateTime.UtcNow;
				return true;
			}
			catch
			{
				return false;
			}
		}

		private async Task HandleDirectoryDelete(string fullPath)
		{
			var rel = RelFromFull(fullPath);

			var query = $"groupId={_serverGroupId}&groupKeyPlaintext={_groupKey}&relativePath={Uri.EscapeDataString(rel)}";
			await _httpClient.DeleteAsync($"{_serverUrl}/api/directories?{query}");

			string prefix = rel + "/";
			using var db = new SyncDbContext();
			var toDelete = db.Files!.Where(f => f.RelativePath.StartsWith(prefix)
											&& f.GroupId == _serverGroupId.ToString())
				.ToList();

			db.Files!.RemoveRange(toDelete);
			db.SaveChanges();

			_suppressEvents = true;
			if (Directory.Exists(fullPath))
				Directory.Delete(fullPath, true);
			_suppressEvents = false;
		}

		public async Task RunFullSync()
		{
			if (!await ServerReachable())
				return;

			var _syncDbContext = new SyncDbContext();
			Console.WriteLine($"[Sync Group {_serverGroupId} Start]");
			DateTime lastSyncCopy = LastSyncUtc;
			//await PushLocalChanges(lastSyncCopy); // No need to actually push

			await PullServerChanges(lastSyncCopy);

			var groupRecord = _syncDbContext.Groups!.FirstOrDefault(g => g.IdOnServer == _serverGroupId);

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
			var changedLocalFiles = _syncDbContext.Files!
				.Where(f => f.GroupId == _serverGroupId.ToString() && f.LastModifiedUtc > lastSyncUtc)
				.ToList();

			if (!changedLocalFiles.Any())
			{
				Console.WriteLine("[Sync] No local changes to push.");
				return;
			}
			_suppressEvents = true;
			foreach (var localFile in changedLocalFiles)
			{
				string fullPath = localFile.StoredPathOnClient!;

				if (localFile.IsDirectory && Directory.Exists(fullPath))
				{
					Console.WriteLine($"[Sync] Creating directory on server: {localFile.RelativePath}");
					await Helpers.CreateDirectoryOnServer(_serverGroupId, _groupKey, localFile.RelativePath, _serverUrl);
					continue;
				}

				if (!File.Exists(fullPath))
				{
					Console.WriteLine($"[Sync] Skipping {localFile.RelativePath} because file missing locally.");
					continue;
				}

				Console.WriteLine($"[Sync] Pushing local change: {localFile.RelativePath}");

				await Helpers.UploadFileToServer(fullPath, localFile.LastModifiedUtc, _serverGroupId, _groupKey, localFile.RelativePath, _serverUrl);

			}
			_suppressEvents = false;
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
			_suppressEvents = true;
			foreach (var serverFile in changedFiles)
			{
				if (serverFile.IsDeleted)
				{
					string localPath = Path.Combine(_localFolder, PathNorm.ToDisk(serverFile.RelativePath!));
					_suppressEvents = true;
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
					_suppressEvents = false;
					// Remove from local DB
					var existing = _syncDbContext.Files!
						.FirstOrDefault(f => f.RelativePath == serverFile.RelativePath
										  && f.GroupId == _serverGroupId.ToString());
					if (existing != null)
					{
						_syncDbContext.Files!.Remove(existing);
					}
				}
				else
				{
					// New or updated file
					string localPath = Path.Combine(_localFolder, PathNorm.ToDisk(serverFile.RelativePath!));
					Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);

					if (!serverFile.IsDirectory)
					{
						// Download
						await Helpers.DownloadFileServer(_serverGroupId, _groupKey, serverFile.RelativePath!, localPath, _serverUrl);
					}
					var existing = _syncDbContext.Files!
						.FirstOrDefault(f => f.RelativePath == serverFile.RelativePath
										  && f.GroupId == _serverGroupId.ToString());
					if (existing == null)
					{
						_syncDbContext.Files!.Add(new FileMetadata
						{
							RelativePath = serverFile.RelativePath!,
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
					_suppressEvents = false;
					Console.WriteLine($"[Sync] Pulled file from server: {serverFile.RelativePath}");
				}
			}
			_suppressEvents = false;
			_syncDbContext.SaveChanges();
		}

		public async Task CatchUpSync()
		{
			if (!await ServerReachable())
			{
				Console.WriteLine("[Sync] Server offline");
				return;
			}

			await FlushQueue();
			await RunFullSync();
		}

		public void ScanLocalFolder()
		{
			using var db = new SyncDbContext();

			// What database thinks exists
			var dbRows = db.Files!
						   .Where(f => f.GroupId == _serverGroupId.ToString())
						   .ToDictionary(f => f.RelativePath, f => f);

			foreach (var path in Directory.EnumerateFileSystemEntries(
						   _localFolder, "*", SearchOption.AllDirectories))
			{
				var rel = RelFromFull(path);
				bool isDir = Directory.Exists(path);

				// New file / directory
				if (!dbRows.TryGetValue(rel, out var row))
				{
					string? checksum = null;

					if (!isDir)
					{
						if (!File.Exists(path))
							continue;
						checksum = Helpers.ComputeFileChecksum(path);
					}
					db.Files!.Add(new FileMetadata
					{
						RelativePath = rel,
						IsDirectory = isDir,
						IsDeleted = false,
						GroupId = _serverGroupId.ToString(),
						LastModifiedUtc = DateTime.UtcNow,
						StoredPathOnClient = path,
						Checksum = checksum
					});

					QueueChange(isDir ? "CreateDir" : "Upload", rel, checksum);
					continue;
				}

				// Exists but was deleted
				if (row.IsDeleted)
				{
					row.IsDeleted = false;
					QueueChange(isDir ? "CreateDir" : "Upload", rel, row.Checksum);
				}

				// File updated
				if (!isDir)
				{
					var lastWrite = File.GetLastWriteTimeUtc(path);
					if (lastWrite > row.LastModifiedUtc)
					{
						row.LastModifiedUtc = lastWrite;
						row.Checksum = Helpers.ComputeFileChecksum(path);
						QueueChange("Upload", rel, row.Checksum);
					}
				}

				dbRows.Remove(rel);
			}

			// Rest is missing on disk
			foreach (var kvp in dbRows.Values)
			{
				if (kvp.IsDeleted) continue;

				kvp.IsDeleted = true;
				QueueChange(kvp.IsDirectory ? "DeleteDir" : "Delete", kvp.RelativePath, checksum: kvp.Checksum);
			}

			db.SaveChanges();
			Console.WriteLine($"[Scan] Completed full scan for group {_serverGroupId}");
		}

		public async Task FlushQueue()
		{
			using var db = new SyncDbContext();
			var batch = db.PendingChanges!
						  .Where(p => p.GroupId == _serverGroupId)
						  .Take(20).ToList();

			_suppressEvents = true;

			foreach (var p in batch)
			{
				bool done = p.ChangeType switch
				{
					"Upload" => await Helpers.TryUpload(p, _localFolder, _serverGroupId, _groupKey, _serverUrl),
					"Delete" => await Helpers.TryDelete(p, _serverGroupId, _groupKey, _serverUrl, _localFolder),
					"Move" => await Helpers.TryMove(p, _serverGroupId, _groupKey, _serverUrl),
					"CreateDir" => await Helpers.TryCreateDir(p, _localFolder, _serverGroupId, _groupKey, _serverUrl),
					"DeleteDir" => await TryHandleDirDelete(p),
					"RenameDir" => await TryHandleDirRename(p),
					_ => true
				};
				if (done) db.PendingChanges!.Remove(p);
			}
			_suppressEvents = false;
			db.SaveChanges();
		}

		private void QueueChange(string type, string rel, string? checksum, string? aux = null)
		{
			using var db = new SyncDbContext();

			var existing = db.PendingChanges!
							 .FirstOrDefault(p => p.GroupId == _serverGroupId &&
												  p.RelativePath == rel);

			if (existing == null)
			{
				db.PendingChanges!.Add(new PendingChange
				{
					GroupId = _serverGroupId,
					RelativePath = rel,
					ChangeType = type,
					AuxPath = aux,
					QueuedUtc = DateTime.UtcNow,
					Checksum = checksum
				});
			}
			else
			{
				switch (type)
				{
					case "Delete":
					case "DeleteDir":
						// Deletion wins over everything
						existing.ChangeType = type;
						existing.AuxPath = null;
						break;

					case "Move":
					case "RenameDir":
						// Overwrite any prior action with latest target path
						existing.ChangeType = type;
						existing.AuxPath = aux;
						break;

					case "Upload":
						// If we already queued a Move/RenameDir keep that
						// Or make all uploads into one
						if (existing.ChangeType is not ("Move" or "RenameDir"))
							existing.ChangeType = "Upload";

						existing.Checksum = checksum;
						existing.QueuedUtc = DateTime.UtcNow;
						break;

					case "CreateDir":
						// Keep CreateDir unless a Delete/DeleteDir already queued
						if (existing.ChangeType.StartsWith("Delete", StringComparison.Ordinal) == false)
							existing.ChangeType = "CreateDir";
						existing.Checksum = checksum;
						break;
				}
			}

			db.SaveChanges();
		}

		private DateTime CalculateNewMaxSyncTime()
		{
			var _syncDbContext = new SyncDbContext();
			DateTime maxLocal = _syncDbContext.Files!
				.Where(f => f.GroupId == _serverGroupId.ToString())
				.Select(f => f.LastModifiedUtc)
				.ToList()
				.DefaultIfEmpty(DateTime.UtcNow)
				.Max();

			return maxLocal > LastSyncUtc ? maxLocal : LastSyncUtc;
		}

		private async Task<bool> ServerReachable()
		{
			try
			{
				var resp = await Helpers.GetGroupNameByIdFromServer(_serverGroupId, _serverUrl);
				return true;
			}
			catch
			{
				return false;
			}
		}

		private async Task WaitForFileClose(string path)
		{
			if (Directory.Exists(path))
				return;

			//Wait for up to 2 seconds
			for (int i = 0; i < 20; i++)
			{
				try
				{
					File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None).Dispose();
					return;
				}
				catch (IOException)
				{
					await Task.Delay(100);
				}
			}
		}
	}

}
