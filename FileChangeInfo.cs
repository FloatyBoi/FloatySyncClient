using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FloatySyncClient
{
	public class FileChangeInfo
	{
		public int FileId { get; set; }
		public string? RelativePath { get; set; }
		public bool IsDeleted { get; set; }
		public bool IsDirectory { get; set; }
		public DateTime LastModifiedUtc { get; set; }
	}
}
