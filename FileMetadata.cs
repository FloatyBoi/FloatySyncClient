using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FloatySyncClient
{
	public class FileMetadata
	{
		public int Id { get; set; }
		public string RelativePath { get; set; } = default!;
		public DateTime LastModifiedUtc { get; set; }
		public string? Checksum { get; set; }
		public bool IsDirectory { get; set; }
		public bool IsDeleted { get; set; }
		public string? GroupId { get; set; }
		public string? StoredPathOnClient { get; set; }
	}
}
