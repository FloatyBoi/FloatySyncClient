namespace FloatySyncClient
{
	public class MoveFileRequest
	{
		public string OldRelativePath { get; set; }
		public string NewRelativePath { get; set; }
		public string GroupId { get; set; }
		public string GroupKeyPlaintext { get; set; }
	}
}