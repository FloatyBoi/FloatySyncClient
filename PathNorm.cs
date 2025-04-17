using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FloatySyncClient
{
	public static class PathNorm
	{
		/// Convert ANY relative path to canonical storage form (forward‑slashes)
		public static string Normalize(string rel) =>
			rel.Replace('\\', '/').TrimStart('/');

		/// Convert a stored canonical path to the current OS’s separator
		public static string ToDisk(string rel) =>
			rel.Replace('/', Path.DirectorySeparatorChar);
	}
}
