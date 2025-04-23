using Microsoft.EntityFrameworkCore.Storage.Json;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace FloatySyncClient
{
	public class Helpers
	{
		public static string? ComputeFileChecksum(string filePath)
		{
			// Ensure the file exists
			if (!File.Exists(filePath))
				return null;

			using var sha256 = SHA256.Create();
			using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read);

			// Compute hash returns a byte[]
			byte[] hashBytes = sha256.ComputeHash(stream);

			// Convert to a readable hex string
			return BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
		}

		public static async Task<int> CreateGroupOnServer(string? groupName, string? groupKey, string serverUrl)
		{
			HttpClient client = new HttpClient();

			var requestUrl = $"{serverUrl}/api/groups";

			var requestBodyObject = new
			{
				name = groupName,
				secretKey = groupKey,
			};

			string jsonBody = JsonSerializer.Serialize(requestBodyObject);

			using var content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

			HttpResponseMessage response = await client.PostAsync(requestUrl, content);
			response.EnsureSuccessStatusCode();
			string result = await response.Content.ReadAsStringAsync();

			var value = JsonSerializer.Deserialize<GroupIdResponse>(result, JsonSerializerOptions.Web)!.GroupId;
			return value;
		}

		internal static async Task DeleteOnServer(string relativePath, string? checksum, int serverGroupId, string groupKey, string serverUrl, string localRoot)
		{
			HttpClient httpClient = new HttpClient();

			var queryString = $"?relativePath={Uri.EscapeDataString(relativePath)}" +
					$"&checksum={Uri.EscapeDataString(checksum!)}" +
					$"&groupId={Uri.EscapeDataString(serverGroupId.ToString())}" +
					$"&groupKeyPlaintext={Uri.EscapeDataString(groupKey)}";

			var requestUrl = $"{serverUrl}/api/files/delete{queryString}";

			var request = new HttpRequestMessage(HttpMethod.Delete, requestUrl);

			using var response = await httpClient.SendAsync(request);

			if (response.IsSuccessStatusCode)
				return;

			if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
			{
				Console.WriteLine($"[Conflict] Checksum mismatch deleting {relativePath} – " +
						  $"downloading authoritative copy.");
				var disk = Path.Combine(localRoot, PathNorm.ToDisk(relativePath));
				if (File.Exists(disk))
				{
					var backup = disk + ".local-conflict-" +
								 DateTime.UtcNow.ToString("yyyyMMddHHmmss");
					File.Move(disk, backup, overwrite: true);
				}

				await DownloadFileServer(serverGroupId, groupKey, relativePath, disk, serverUrl);

				using var db = new SyncDbContext();

				var existing = db.Files!.FirstOrDefault(f => f.RelativePath == relativePath &&
															f.GroupId == serverGroupId.ToString());
				if (existing != null)
				{
					existing.IsDeleted = false;
					existing.Checksum = ComputeFileChecksum(disk);
					existing.LastModifiedUtc = DateTime.UtcNow;
				}
				else
				{
					db.Files!.Add(new FileMetadata
					{
						Checksum = ComputeFileChecksum(disk),
						IsDeleted = false,
						RelativePath = relativePath,
						StoredPathOnClient = disk,
						LastModifiedUtc = DateTime.UtcNow,
						GroupId = serverGroupId.ToString()
					});
				}
				db.SaveChanges();
			}
			else
				response.EnsureSuccessStatusCode();
		}

		internal static async Task DownloadFileServer(int groupId, string? groupKey, string relativePath, string localPath, string serverUrl)
		{
			HttpClient httpClient = new HttpClient();

			var queryString = $"?relativePath={Uri.EscapeDataString(relativePath)}" +
						  $"&groupId={groupId}" +
						  $"&groupKeyPlaintext={Uri.EscapeDataString(groupKey!)}";

			var requestUrl = $"{serverUrl}/api/files/download{queryString}";

			using var response = await httpClient.GetAsync(requestUrl, HttpCompletionOption.ResponseHeadersRead);
			response.EnsureSuccessStatusCode();

			var directory = Path.GetDirectoryName(localPath);
			if (!string.IsNullOrEmpty(relativePath) && !Directory.Exists(directory))
			{
				Directory.CreateDirectory(directory!);
			}

			using var responseStream = await response.Content.ReadAsStreamAsync();
			using var fileStream = new FileStream(localPath, FileMode.Create, FileAccess.Write, FileShare.None);

			await responseStream.CopyToAsync(fileStream);

		}

		internal static async Task<string> GetGroupNameByIdFromServer(int serverGroupId, string serverUrl)
		{
			HttpClient client = new HttpClient();

			string requestUrl = $"{serverUrl}/api/groups/{serverGroupId}";

			var response = await client.GetAsync(requestUrl);
			response.EnsureSuccessStatusCode();

			string content = await response.Content.ReadAsStringAsync();

			GroupResponse groupResponse = JsonSerializer.Deserialize<GroupResponse>(content, JsonSerializerOptions.Web)!;

			return groupResponse?.Name!;
		}

		internal static async Task MoveFileOnServer(string oldRelativePath, string newRelativePath, int serverGroupId, string groupKey, string serverUrl)
		{
			HttpClient httpClient = new HttpClient();

			string requestUrl = $"{serverUrl}/api/files/move";

			var requestData = new MoveFileRequest
			{
				OldRelativePath = oldRelativePath,
				NewRelativePath = newRelativePath,
				GroupId = serverGroupId.ToString(),
				GroupKeyPlaintext = groupKey
			};

			var json = JsonSerializer.Serialize(requestData);
			var content = new StringContent(json, Encoding.UTF8, "application/json");

			using var response = await httpClient.PostAsync(requestUrl, content);

			response.EnsureSuccessStatusCode();
		}

		internal static async Task UploadFileToServer(string filePath, DateTime lastModifiedUtc, int serverGroupId, string? groupKey, string relativePath, string serverUrl)
		{
			HttpClient httpClient = new HttpClient();

			using var formData = new MultipartFormDataContent();

			formData.Add(new StringContent(relativePath), "relativePath");
			formData.Add(new StringContent(lastModifiedUtc.ToString("o")), "lastModifiedUtc");
			formData.Add(new StringContent(serverGroupId.ToString()), "groupId");
			formData.Add(new StringContent(groupKey!), "groupKeyPlaintext");

			using var fileStream = File.OpenRead(filePath);
			var fileContent = new StreamContent(fileStream);
			fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");

			var fileName = Path.GetFileName(filePath);
			formData.Add(fileContent, "file", fileName);

			var requestUrl = $"{serverUrl}/api/files/upload";

			try
			{
				var resp = await httpClient.PostAsync(requestUrl, formData);

				if (resp.IsSuccessStatusCode)
					return;

				if (resp.StatusCode == System.Net.HttpStatusCode.Conflict)
				{
					fileStream.Dispose();
					Console.WriteLine($"[Conflict] Server newer → keeping local copy as backup " +
						  $"and pulling server version: {relativePath}");

					var backup = filePath + ".local-conflict-" +
					 DateTime.UtcNow.ToString("yyyyMMddHHmmss");
					File.Move(filePath, backup, overwrite: true);

					await DownloadFileServer(serverGroupId, groupKey, relativePath, filePath, serverUrl);

					using var db = new SyncDbContext();
					var row = db.Files!.FirstOrDefault(f => f.GroupId == serverGroupId.ToString()
											   && f.RelativePath == relativePath);

					if (row == null)
						return;

					row.LastModifiedUtc = DateTime.UtcNow;
					row.Checksum = Helpers.ComputeFileChecksum(filePath);
					row.StoredPathOnClient = filePath;
					db.SaveChanges();
				}
				else
					resp.EnsureSuccessStatusCode();

			}
			catch (HttpRequestException)
			{
				throw;
			}
		}

		public static bool WasDirectory(string fullPath, int serverGroupId)
		{
			var abs = Path.GetFullPath(fullPath);

			using var db = new SyncDbContext();
			var meta = db.Files!
						 .Where(f => f.GroupId == serverGroupId.ToString())
						 .AsEnumerable()
						 .FirstOrDefault(f =>
							  Path.GetFullPath(f.StoredPathOnClient!)
								  .Equals(abs, StringComparison.OrdinalIgnoreCase));

			if (meta != null) return meta.IsDirectory;
			return true; // Bad assumption, but should work (mostly)
		}

		internal static async Task CreateDirectoryOnServer(int serverGroupId, string groupKey, string relativePath, string serverUrl)
		{
			HttpClient httpClient = new HttpClient();

			var requestUrl = $"{serverUrl}/api/directories/create";

			var requestBodyObject = new
			{
				relativePath = relativePath,
				groupKey = groupKey,
				groupId = serverGroupId
			};

			string jsonBody = JsonSerializer.Serialize(requestBodyObject);

			using var content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

			HttpResponseMessage response = await httpClient.PostAsync(requestUrl, content);
			response.EnsureSuccessStatusCode();
		}

		public static async Task<bool> TryUpload(PendingChange p, string localFolder, int serverGroupId, string groupKey, string serverUrl)
		{
			var path = Path.Combine(localFolder, PathNorm.ToDisk(p.RelativePath));
			if (!File.Exists(path))
				return true;

			try
			{
				await UploadFileToServer(path, DateTime.UtcNow, serverGroupId, groupKey, p.RelativePath, serverUrl);
				return true;
			}
			catch
			{
				return false;
			}

		}

		public static async Task<bool> TryDelete(PendingChange p, int serverGroupId, string groupKey, string serverUrl, string localRoot)
		{
			try
			{
				await DeleteOnServer(p.RelativePath, p.Checksum, serverGroupId, groupKey, serverUrl, localRoot);
				return true;
			}
			catch
			{
				return false;
			}

		}

		public static async Task<bool> TryMove(PendingChange p, int serverGroupId, string groupKey, string serverUrl)
		{
			try
			{
				await MoveFileOnServer(p.RelativePath, p.AuxPath!, serverGroupId, groupKey, serverUrl);
				return true;
			}
			catch
			{
				return false;
			}

		}

		public static async Task<bool> TryCreateDir(PendingChange p, string localFolder, int serverGroupId, string groupKey, string serverUrl)
		{
			try
			{
				await CreateDirectoryOnServer(serverGroupId, groupKey, p.RelativePath, serverUrl);
				return true;
			}
			catch
			{
				return false;
			}

		}
	}
}
