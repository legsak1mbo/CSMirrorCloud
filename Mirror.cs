using System;
using System.Drawing.Text;

namespace CloudMirror
{
	class Program
	{
		private static string DefaultServerDirectory = @"C:\Users\test\Desktop\MirrorServer";
		private static string DefaultClientDirectory = @"C:\Users\test\Desktop\MirrorClient";

		[STAThread]
		static int Main(string[] args)
		{
			
			Console.Write("Press ctrl-C to stop gracefully\n");
			Console.Write("-------------------------------\n");

			var returnCode = 0;

			try
			{
				if (FakeCloudProvider.Start(args.Length > 0 ? args[0] : DefaultServerDirectory, args.Length > 1 ? args[1] : DefaultClientDirectory).Result)
				{
					returnCode = 1;
				}
			}
			catch
			{
				CloudProviderSyncRootWatcher.Stop(0); // Param is unused
			}

			return returnCode;
		}
	}
}
