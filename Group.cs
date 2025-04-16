using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FloatySyncClient
{
	public class Group
	{
		public int Id { get; set; }
		public int IdOnServer { get; set; }
		public string Name { get; set; }
		public string Key { get; set; }
		public string LocalFolder { get; set; }

		public DateTime LastSyncUtc { get; set; }
	}
}
