using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FloatySyncClient
{
	public class BusyInterceptor : DbConnectionInterceptor
	{
		public override void ConnectionOpened(DbConnection conn,
											  ConnectionEndEventData data)
		{
			using var cmd = conn.CreateCommand();
			cmd.CommandText = "PRAGMA busy_timeout = 30000;";   // 30 seconds
			cmd.ExecuteNonQuery();
		}
	}
}
