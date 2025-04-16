using Microsoft.EntityFrameworkCore.Storage.Json;
using System;
using System.Collections.Generic;
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
		public static string ComputeFileChecksum(string filePath)
		{
			// Ensure the file exists
			if (!File.Exists(filePath))
				throw new FileNotFoundException("File not found.", filePath);

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

			var value = JsonSerializer.Deserialize<GroupIdResponse>(result, JsonSerializerOptions.Web).GroupId;
			return value;
		}

		internal static async void DeleteOnServer(string relativePath, string? checksum, int serverGroupId, string groupKey, string serverUrl)
		{
			HttpClient httpClient = new HttpClient();

			var queryString = $"?relativePath={Uri.EscapeDataString(relativePath)}" +
					$"&checksum={Uri.EscapeDataString(checksum)}" +
					$"&groupId={Uri.EscapeDataString(serverGroupId.ToString())}" +
					$"&groupKeyPlaintext={Uri.EscapeDataString(groupKey)}";

			var requestUrl = $"{serverUrl}/files/delete{queryString}";

			var request = new HttpRequestMessage(HttpMethod.Delete, requestUrl);

			using var response = await httpClient.SendAsync(request);

			response.EnsureSuccessStatusCode();
		}

		internal static async Task DownloadFileServer(int groupId, string? groupKey, string relativePath, string localPath, string serverUrl)
		{
			HttpClient httpClient = new HttpClient();

			var queryString = $"?relativePath={Uri.EscapeDataString(relativePath)}" +
						  $"&groupId={groupId}" +
						  $"&groupKeyPlaintext={Uri.EscapeDataString(groupKey)}";

			var requestUrl = $"{serverUrl}/api/files/download{queryString}";

			using var response = await httpClient.GetAsync(requestUrl, HttpCompletionOption.ResponseHeadersRead);
			response.EnsureSuccessStatusCode();

			var directory = Path.GetDirectoryName(localPath);
			if (!string.IsNullOrEmpty(relativePath) && !Directory.Exists(directory))
			{
				Directory.CreateDirectory(directory);
			}

			using var responseStream = await response.Content.ReadAsStreamAsync();
			using var fileStream = new FileStream(localPath, FileMode.Create, FileAccess.Write, FileShare.None);

			await responseStream.CopyToAsync(fileStream);

			//TODO: Add changes to database
		}

		internal static async Task<string> GetGroupNameByIdFromServer(int serverGroupId, string serverUrl)
		{
			HttpClient client = new HttpClient();

			string requestUrl = $"{serverUrl}/api/groups/{serverGroupId}";

			var response = await client.GetAsync(requestUrl);
			response.EnsureSuccessStatusCode();

			string content = await response.Content.ReadAsStringAsync();

			GroupResponse groupResponse = JsonSerializer.Deserialize<GroupResponse>(content);

			return groupResponse?.Name;
		}

		internal static async void MoveFileOnServer(string oldRelativePath, string newRelativePath, int serverGroupId, string groupKey, string serverUrl)
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

		internal static async void UploadFileToServer(string filePath, DateTime lastModifiedUtc, int serverGroupId, string? groupKey, string relativePath, string serverUrl)
		{
			HttpClient httpClient = new HttpClient();

			using var formData = new MultipartFormDataContent();

			formData.Add(new StringContent(relativePath), "relativePath");
			formData.Add(new StringContent(lastModifiedUtc.ToString("o")), "lastModifiedUtc");
			formData.Add(new StringContent(serverGroupId.ToString()), "groupId");
			formData.Add(new StringContent(groupKey), "groupKeyPlaintext");

			var fileStream = File.OpenRead(filePath);
			var fileContent = new StreamContent(fileStream);
			fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");

			var fileName = Path.GetFileName(filePath);
			formData.Add(fileContent, "file", fileName);

			var requestUrl = $"{serverUrl}/api/files/upload";

			using var response = await httpClient.PostAsync(requestUrl, formData);

			response.EnsureSuccessStatusCode();
		}
	}
}
