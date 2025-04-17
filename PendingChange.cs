using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FloatySyncClient
{
	public class PendingChange
	{
		public int Id { get; set; }
		public int GroupId { get; set; }
		public string? Checksum { get; set; }
		public string RelativePath { get; set; } = default!;
		public string ChangeType { get; set; } = default!;
		public string? AuxPath { get; set; }
		public DateTime QueuedUtc { get; set; }
	}
}
