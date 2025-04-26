using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Serilog;
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
			_watcher.Error += OnErrored;
		}

		private async void OnErrored(object sender, ErrorEventArgs e)
		{
			Console.WriteLine(e.ToString());
			Log.Error("FileSystemWatcher errored: ", e.GetException());

			_watcher = new FileSystemWatcher(_localFolder);
			_watcher.IncludeSubdirectories = true;
			_watcher.EnableRaisingEvents = true;

			_watcher.Created += OnCreated;
			_watcher.Changed += OnChanged;
			_watcher.Renamed += OnRenamed;
			_watcher.Deleted += OnDeleted;
			_watcher.Error += OnErrored;
		}

		// FileSystemWatcher handlers
		//TODO: What happens on conflict?
		private async void OnCreated(object sender, FileSystemEventArgs e)
		{
			if (_suppressEvents || Program.isSynching) return;

			var _syncDbContext = new SyncDbContext();
			await WaitForFileClose(e.FullPath);

			var existing = _syncDbContext.Files!
					.FirstOrDefault(f => f.RelativePath == RelFromFull(e.FullPath)
									  && f.GroupId == _serverGroupId.ToString());

			if (existing != null)
				return; //Duplicate

			if (File.Exists(e.FullPath))
			{

				FileMetadata fileMetadata = new FileMetadata();
				fileMetadata.LastModifiedUtc = DateTime.UtcNow;
				fileMetadata.RelativePath = RelFromFull(e.FullPath);
				fileMetadata.StoredPathOnClient = PathNorm.Normalize(e.FullPath);
				fileMetadata.Checksum = Helpers.ComputeFileChecksum(e.FullPath);
				fileMetadata.GroupId = _serverGroupId.ToString();

				_syncDbContext.Files!.Add(fileMetadata);
				_syncDbContext.SaveChanges();

				try
				{
					await Helpers.UploadFileToServer(e.FullPath, fileMetadata.LastModifiedUtc, _serverGroupId, _groupKey, RelFromFull(e.FullPath), _serverUrl);
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
				directoryMetadata.StoredPathOnClient = PathNorm.Normalize(e.FullPath);
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
					await Helpers.UploadFileToServer(PathNorm.Normalize(e.FullPath), fileMetadata.LastModifiedUtc, _serverGroupId, _groupKey, RelFromFull(e.FullPath), _serverUrl);
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
					.FirstOrDefault(f => f.RelativePath == RelFromFull(e.OldFullPath)
									  && f.GroupId == _serverGroupId.ToString());

				if (fileMetadata == null)
					return;

				fileMetadata.Checksum = Helpers.ComputeFileChecksum(e.FullPath);
				fileMetadata.LastModifiedUtc = DateTime.UtcNow;
				fileMetadata.StoredPathOnClient = PathNorm.Normalize(e.FullPath);
				fileMetadata.RelativePath = RelFromFull(e.FullPath);

				var newDirRel = PathNorm.Normalize(
					Path.GetDirectoryName(
						Path.GetRelativePath(_localFolder, e.FullPath)) ?? "");
				if (newDirRel.Length > 0)
				{
					var dirRow = _syncDbContext.Files!
								.FirstOrDefault(f => f.RelativePath == newDirRel &&
													 f.GroupId == _serverGroupId.ToString());
					if (dirRow == null)
					{
						_syncDbContext.Files!.Add(new FileMetadata
						{
							RelativePath = newDirRel,
							LastModifiedUtc = DateTime.UtcNow,
							GroupId = _serverGroupId.ToString(),
							StoredPathOnClient = PathNorm.Normalize(Path.Combine(_localFolder, PathNorm.ToDisk(newDirRel))),
							IsDirectory = true,
							Checksum = null
						});

						try
						{
							await Helpers.CreateDirectoryOnServer(_serverGroupId, _groupKey, newDirRel, _serverUrl);
						}
						catch (HttpRequestException)
						{
							QueueChange("CreateDir", newDirRel, checksum: null);
						}
					}
				}

				_syncDbContext.SaveChanges();

				try
				{
					await Helpers.MoveFileOnServer(RelFromFull(e.OldFullPath), RelFromFull(e.FullPath), _serverGroupId, _groupKey, _serverUrl);
				}
				catch (HttpRequestException)
				{
					QueueChange("Move", RelFromFull(e.OldFullPath), fileMetadata.Checksum, RelFromFull(e.FullPath));
				}
			}
			else if (Directory.Exists(e.FullPath))
			{
				try
				{
					await HandleDirectoryRename(e.OldFullPath, e.FullPath);
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
				return true;
			}
			catch
			{
				return false;
			}
		}

		private async Task HandleDirectoryRename(string oldFullPath, string newFullPath)
		{
			var oldRel = RelFromFull(oldFullPath);
			var newRel = RelFromFull(newFullPath);

			var payload = new
			{
				GroupId = _serverGroupId,
				GroupKey = _groupKey,
				OldPath = oldRel,
				NewPath = newRel
			};
			await _httpClient.PostAsJsonAsync($"{_serverUrl}/api/directories/rename", payload);

			string oldPrefix = oldRel + '/';
			string newPrefix = newRel + '/';

			using var db = new SyncDbContext();
			var rows = db.Files!
						 .Where(f => f.GroupId == _serverGroupId.ToString() &&
									(f.RelativePath == oldRel ||
									 f.RelativePath.StartsWith(oldPrefix)))
						 .ToList();

			EnsureDirectoryRow(PathNorm.Normalize(Path.GetDirectoryName(PathNorm.ToDisk(newRel))));

			foreach (var m in rows)
			{
				string newPath =
					(m.RelativePath == oldRel)
					? newRel
					: newPrefix + m.RelativePath.Substring(oldPrefix.Length);

				m.RelativePath = PathNorm.Normalize(newPath);
				m.StoredPathOnClient = PathNorm.Normalize(Path.Combine(_localFolder, PathNorm.ToDisk(newPath)));
				m.LastModifiedUtc = DateTime.UtcNow;
			}

			db.SaveChanges();
			return;

			void EnsureDirectoryRow(string? dirRel)
			{
				if (string.IsNullOrEmpty(dirRel) || dirRel == ".") return;

				dirRel = PathNorm.Normalize(dirRel);

				var existing = db.Files!.FirstOrDefault(f => f.GroupId == _serverGroupId.ToString()
														  && f.RelativePath == dirRel);
				if (existing != null) return;

				db.Files!.Add(new FileMetadata
				{
					RelativePath = dirRel,
					StoredPathOnClient = PathNorm.Normalize(Path.Combine(_localFolder, dirRel)),
					LastModifiedUtc = DateTime.UtcNow,
					GroupId = _serverGroupId.ToString(),
					IsDirectory = true,
					Checksum = null
				});

				_ = Helpers.CreateDirectoryOnServer(_serverGroupId, _groupKey, dirRel, _serverUrl)
						   .ContinueWith(_ => { });
			}
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
				}
				catch (HttpRequestException)
				{
					QueueChange("DeleteDir", fileMetadata.RelativePath, null);
				}
			}

			try
			{
				_syncDbContext.SaveChanges();
			}
			catch (DbUpdateConcurrencyException)
			{
				//Rows already gone
				_syncDbContext.ChangeTracker.Clear();
			}
		}

		private async Task<bool> TryHandleDirDelete(PendingChange p)
		{
			var path = Path.Combine(_localFolder, PathNorm.ToDisk(p.RelativePath));

			try
			{
				await HandleDirectoryDelete(path);
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

			var root = db.Files.FirstOrDefault(f => f.RelativePath == rel
												&& f.GroupId == _serverGroupId.ToString());

			if (root != null)
				db.Files!.Remove(root);

			db.SaveChanges();

			_suppressEvents = true;
			if (Directory.Exists(fullPath))
				Directory.Delete(fullPath, true);
			_suppressEvents = false;
		}

		public async Task RunFullSync()
		{
			if (!await ServerReachable())
			{
				Console.WriteLine("[Sync] Server not reachable");
				return;
			}
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
						 $"&groupKeyPlaintext={Uri.EscapeDataString(_groupKey)}" +
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
					string prefix = serverFile.RelativePath! + "/";
					var rowsToDelete = _syncDbContext.Files!
						   .Where(f => (f.RelativePath == serverFile.RelativePath! ||
					f.RelativePath.StartsWith(prefix)) &&
					f.GroupId == _serverGroupId.ToString())
						   .ToList();
					_syncDbContext.Files!.RemoveRange(rowsToDelete);
				}
				else
				{
					// New or updated file
					string localPath = Path.Combine(_localFolder, PathNorm.ToDisk(serverFile.RelativePath!));

					var existingDirectory = _syncDbContext.Files!.FirstOrDefault(f => f.RelativePath == PathNorm.Normalize(Path.GetDirectoryName(serverFile.RelativePath) ?? "")
																	&& f.GroupId == _serverGroupId.ToString());

					Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);

					if (Directory.Exists(Path.GetDirectoryName(localPath)) && Path.GetDirectoryName(localPath) != _localFolder && existingDirectory == null)
					{
						_syncDbContext.Files!.Add(new FileMetadata
						{
							RelativePath = PathNorm.Normalize(Path.GetDirectoryName(PathNorm.ToDisk(serverFile.RelativePath))),
							LastModifiedUtc = DateTime.UtcNow,
							GroupId = _serverGroupId.ToString(),
							StoredPathOnClient = PathNorm.Normalize(Path.GetDirectoryName(PathNorm.ToDisk(localPath))),
							Checksum = null,
							IsDirectory = true
						});

						await Helpers.CreateDirectoryOnServer(_serverGroupId, _groupKey, PathNorm.Normalize(Path.GetDirectoryName(serverFile.RelativePath) ?? ""), url);
					}

					if (!serverFile.IsDirectory && File.GetLastWriteTimeUtc(localPath) > serverFile.LastModifiedUtc)
					{
						// Download
						await Helpers.DownloadFileServer(_serverGroupId, _groupKey, PathNorm.ToDisk(serverFile.RelativePath), localPath, _serverUrl);
					}
					var existing = _syncDbContext.Files!
						.FirstOrDefault(f => f.RelativePath == PathNorm.Normalize(serverFile.RelativePath)
										  && f.GroupId == _serverGroupId.ToString());
					if (existing == null)
					{
						_syncDbContext.Files!.Add(new FileMetadata
						{
							RelativePath = PathNorm.Normalize(serverFile.RelativePath!),
							LastModifiedUtc = DateTime.UtcNow,
							GroupId = _serverGroupId.ToString(),
							StoredPathOnClient = PathNorm.Normalize(localPath),
							IsDirectory = serverFile.IsDirectory,
							Checksum = serverFile.IsDirectory ? null : Helpers.ComputeFileChecksum(localPath),
						});
					}
					else
					{
						existing.LastModifiedUtc = DateTime.UtcNow;
						existing.StoredPathOnClient = PathNorm.Normalize(localPath);
						existing.IsDirectory = serverFile.IsDirectory;

						if (!serverFile.IsDirectory)
							existing.Checksum = Helpers.ComputeFileChecksum(localPath);
					}
					_suppressEvents = false;
					if (serverFile.IsDirectory && Directory.GetLastWriteTimeUtc(localPath) > serverFile.LastModifiedUtc)
					{
						Directory.CreateDirectory(localPath);
						Console.WriteLine($"[Sync] Pulled directory from server: {serverFile.RelativePath}");
					}
					else if (File.GetLastWriteTimeUtc(localPath) > serverFile.LastModifiedUtc)
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

			var fileSystemEntries = Directory.EnumerateFileSystemEntries(
						   _localFolder, "*", SearchOption.AllDirectories);
			if (fileSystemEntries.Count() != 0)
			{
				foreach (var path in fileSystemEntries)
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
							StoredPathOnClient = PathNorm.Normalize(path),
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
							Console.WriteLine($"Queue upload: {lastWrite:0} - {row.LastModifiedUtc:0}");
							row.LastModifiedUtc = lastWrite;
							row.Checksum = Helpers.ComputeFileChecksum(path);
							QueueChange("Upload", rel, row.Checksum);
						}
					}

					dbRows.Remove(rel);
				}
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

			rel = PathNorm.Normalize(rel);

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
