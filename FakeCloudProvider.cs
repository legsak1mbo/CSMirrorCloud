using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Vanara.Extensions;
using Vanara.InteropServices;
using Vanara.PInvoke;
using static Vanara.PInvoke.CldApi;
using static Vanara.PInvoke.Kernel32;

namespace CloudMirror
{
	static class FakeCloudProvider
	{
		static object LockObject = new object();
		static CF_CONNECTION_KEY s_transferCallbackConnectionKey;
		static CF_CALLBACK_REGISTRATION[] s_MirrorCallbackTable =
		{
			new CF_CALLBACK_REGISTRATION { Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_FETCH_PLACEHOLDERS, Callback = OnFetchPlaceholders },
			new CF_CALLBACK_REGISTRATION { Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_FETCH_DATA, Callback = OnFetchData },
			new CF_CALLBACK_REGISTRATION { Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_CANCEL_FETCH_DATA, Callback = OnCancelFetchData },
			CF_CALLBACK_REGISTRATION.CF_CALLBACK_REGISTRATION_END
		};

		public static async Task<bool> Start(string serverFolder = "", string clientFolder = "")
		{
			var result = false;

			if (ProviderFolderLocations.Init(serverFolder, clientFolder))
			{
				// Stage 1: Setup
				//--------------------------------------------------------------------------------------------
				// The client folder (syncroot) must be indexed in order for states to properly display
				Utilities.AddFolderToSearchIndexer(ProviderFolderLocations.GetClientFolder());
				// Start up the task that registers and hosts the services for the shell (such as custom states, menus, etc)
				ShellServices.InitAndStartServiceTask();
				// Register the provider with the shell so that the Sync Root shows up in File Explorer
				await CloudProviderRegistrar.RegisterWithShell();
				// Hook up callback methods (in this class) for transferring files between client and server
				ConnectSyncRootTransferCallbacks();

				// Create the placeholders in the client folder so the user sees something
				//Placeholders.Create(ProviderFolderLocations.GetServerFolder(), "", ProviderFolderLocations.GetClientFolder());

				// Stage 2: Running
				//--------------------------------------------------------------------------------------------
				// The file watcher loop for this sample will run until the user presses Ctrl-C.
				// The file watcher will look for any changes on the files in the client (syncroot) in order
				// to let the cloud know.
				CloudProviderSyncRootWatcher.WatchAndWait();

				// Stage 3: Done Running-- caused by CTRL-C
				//--------------------------------------------------------------------------------------------
				// Unhook up those callback methods
				DisconnectSyncRootTransferCallbacks();

				// A real sync engine should NOT unregister the sync root upon exit.
				// This is just to demonstrate the use of StorageProviderSyncRootManager.Unregister.
				CloudProviderRegistrar.Unregister();

				// And if we got here, then this was a normally run test versus crash-o-rama
				result = true;
			}

			return result;
		}

		static void ConnectSyncRootTransferCallbacks()
		{
			try
			{
				// Connect to the sync root using Cloud File API
				CfConnectSyncRoot(ProviderFolderLocations.GetClientFolder(), s_MirrorCallbackTable, default,
					CF_CONNECT_FLAGS.CF_CONNECT_FLAG_REQUIRE_PROCESS_INFO | CF_CONNECT_FLAGS.CF_CONNECT_FLAG_REQUIRE_FULL_FILE_PATH,
					out s_transferCallbackConnectionKey).ThrowIfFailed();
			}
			catch (Exception ex)
			{
				// winrt.to_hresult() will eat the exception if it is a result of winrt.check_hresult,
				// otherwise the exception will get rethrown and this method will crash out as it should
				Console.Write("Could not connect to sync root, hr {0:X8}\n", ex.HResult);
				throw;
			}
		}

		static void DisconnectSyncRootTransferCallbacks()
		{
			Console.Write("Shutting down\n");
			try
			{
				CfDisconnectSyncRoot(s_transferCallbackConnectionKey).ThrowIfFailed();
			}
			catch (Exception ex)
			{
				// winrt.to_hresult() will eat the exception if it is a result of winrt.check_hresult,
				// otherwise the exception will get rethrown and this method will crash out as it should
				Console.Write("Could not disconnect the sync root, hr {0:X8}\n", ex.HResult);
			}
		}

		static void OnFetchData(in CF_CALLBACK_INFO callbackInfo, in CF_CALLBACK_PARAMETERS callbackParameters)
		{
			FileCopierWithProgress.CopyFromServerToClient(callbackInfo, callbackParameters, ProviderFolderLocations.GetServerFolder());
		}

		static void OnCancelFetchData(in CF_CALLBACK_INFO callbackInfo, in CF_CALLBACK_PARAMETERS callbackParameters)
		{
			FileCopierWithProgress.CancelCopyFromServerToClient(callbackInfo, callbackParameters);
		}

		private static void OnFetchPlaceholders(in CF_CALLBACK_INFO callbackInfo, in CF_CALLBACK_PARAMETERS callbackParameters)
		{
			lock (LockObject)
			{
				var filename = StringHelper.GetString(callbackInfo.FileIdentity, CharSet.Unicode);
				if (filename == null)
                {
					filename = ""; // The root directory
                }
				var placeholders = RetrievePlaceholdersForDirectory(filename);
				PlaceholdersCompletion(callbackInfo, placeholders);
			}
		}

		private static List<CF_PLACEHOLDER_CREATE_INFO> RetrievePlaceholdersForDirectory(string directory)
		{
			List<CF_PLACEHOLDER_CREATE_INFO> placeholders = new List<CF_PLACEHOLDER_CREATE_INFO>();

			// Logger.d1($"CloudCache: RetrievePlaceholdersForDirectory('{directory}')");
			try
			{
				var directoryInfo = new DirectoryInfo(Path.Combine(ProviderFolderLocations.GetServerFolder(), directory));

				// Add subdirs:
				//foreach (var subdirectoryInfo in directoryInfo.EnumerateDirectories())
				//{
				//	if (subdirectoryInfo.Attributes.HasFlag(FileAttributes.Hidden))
				//	{
				//		//Logger.d1($"CloudAPI: Skipping hidden directory '{subdirectoryInfo.Name}'");
				//		continue;
				//	}

				//	// Logger.d1($"CloudAPI: Adding Directory: '{subdirectoryInfo.Name}'");
				//	var relativePath = Path.Combine(directory, subdirectoryInfo.Name);
				//	var destinationPath = Path.Combine(ProviderFolderLocations.GetClientFolder(), relativePath);

				//	var placeholder = PlaceholderForDirectory(subdirectoryInfo, relativePath);
				//	placeholders.Add(placeholder);
				//}

				// Add files...
				var files = directoryInfo.EnumerateFiles("*", SearchOption.TopDirectoryOnly);
				foreach (var fileInfo in files)
				{
					if (fileInfo.Attributes.HasFlag(FileAttributes.Hidden))
					{
						//Logger.d1($"CloudAPI: Skipping hidden file '{fileInfo.Name}'");
						continue;
					}

					var relativePath = Path.Combine(directory, fileInfo.Name);
					var destinationPath = Path.Combine(ProviderFolderLocations.GetClientFolder(), relativePath);

					var placeholder = PlaceholderForFile(fileInfo, relativePath);
					placeholders.Add(placeholder);
				}
				return placeholders;
			}
			catch (Exception ex)
			{
				Console.Write($"Failed to add files in directory '{directory}'. Reason={ex}");
				return null;
			}
		}

		private static CF_PLACEHOLDER_CREATE_INFO PlaceholderForFile(FileInfo fileInfo, string path)
		{
			var pRelativeName = new SafeCoTaskMemString(path);

			FILE_BASIC_INFO metadata = new FILE_BASIC_INFO()
			{
				FileAttributes = (FileFlagsAndAttributes)fileInfo.Attributes,
				CreationTime = fileInfo.CreationTime.ToFileTimeStruct(),
				LastWriteTime = fileInfo.LastWriteTime.ToFileTimeStruct(),
				LastAccessTime = fileInfo.LastAccessTime.ToFileTimeStruct(),
				ChangeTime = fileInfo.LastWriteTime.ToFileTimeStruct()
			};

			var placeholder = new CF_PLACEHOLDER_CREATE_INFO
			{
				FileIdentity = pRelativeName,
				FileIdentityLength = pRelativeName.Size,
				RelativeFileName = path,
				Flags = CF_PLACEHOLDER_CREATE_FLAGS.CF_PLACEHOLDER_CREATE_FLAG_MARK_IN_SYNC,
				FsMetadata = new CF_FS_METADATA
				{
					FileSize = fileInfo.Length,
					BasicInfo = metadata
				}
			};

			return placeholder;
		}

		private static CF_PLACEHOLDER_CREATE_INFO PlaceholderForDirectory(DirectoryInfo directoryInfo, string path)
		{
			var pRelativeName = new SafeCoTaskMemString(path);

			FILE_BASIC_INFO metadata = new FILE_BASIC_INFO()
			{
				FileAttributes = (FileFlagsAndAttributes)directoryInfo.Attributes,
				CreationTime = directoryInfo.CreationTime.ToFileTimeStruct(),
				LastWriteTime = directoryInfo.LastWriteTime.ToFileTimeStruct(),
				LastAccessTime = directoryInfo.LastAccessTime.ToFileTimeStruct(),
				ChangeTime = directoryInfo.LastWriteTime.ToFileTimeStruct()
			};

			var placeholder = new CF_PLACEHOLDER_CREATE_INFO
			{
				FileIdentity = pRelativeName,
				FileIdentityLength = pRelativeName.Size,
				RelativeFileName = path,
				Flags = CF_PLACEHOLDER_CREATE_FLAGS.CF_PLACEHOLDER_CREATE_FLAG_MARK_IN_SYNC,
				FsMetadata = new CF_FS_METADATA
				{
					FileSize = 0,
					BasicInfo = metadata
				}
			};

			return placeholder;
		}

		private static void PlaceholdersCompletion(CF_CALLBACK_INFO callbackInfo, List<CF_PLACEHOLDER_CREATE_INFO> placeholders)
		{
			var opInfo = new CF_OPERATION_INFO()
			{
				StructSize = (uint)Marshal.SizeOf<CF_OPERATION_INFO>(),
				Type = CF_OPERATION_TYPE.CF_OPERATION_TYPE_TRANSFER_PLACEHOLDERS,
				ConnectionKey = callbackInfo.ConnectionKey,
				TransferKey = callbackInfo.TransferKey,
				RequestKey = callbackInfo.RequestKey,
				SyncStatus = CF_SYNC_PROVIDER_STATUS.CF_PROVIDER_STATUS_SYNC_FULL.MarshalToPtr(Marshal.AllocHGlobal, out _)
			};

			IntPtr rawPlaceholders = default;
			int count = placeholders?.Count ?? 0;

			if (count > 0)
			{
				var array = placeholders.ToArray();
				rawPlaceholders = array.MarshalToPtr(Marshal.AllocHGlobal, out _);
			}
			var opParams = new CF_OPERATION_PARAMETERS
			{
				ParamSize = (uint)Marshal.SizeOf<CF_OPERATION_PARAMETERS>(),
				TransferPlaceholders = new CF_OPERATION_PARAMETERS.TRANSFERPLACEHOLDERS()
				{
					PlaceholderArray = rawPlaceholders,
					PlaceholderCount = (uint)count,
					PlaceholderTotalCount = (uint)count,
					EntriesProcessed = 0,
					Flags = CF_OPERATION_TRANSFER_PLACEHOLDERS_FLAGS.CF_OPERATION_TRANSFER_PLACEHOLDERS_FLAG_NONE,
					CompletionStatus = HRESULT.S_OK
				}
			};

			var hresult = CfExecute(opInfo, ref opParams);
			if (count > 0)
			{
				Marshal.FreeHGlobal(rawPlaceholders);
			}

			if (hresult != HRESULT.S_OK)
			{
				Console.Write($"Failed with error {hresult}");
				hresult.ThrowIfFailed($"Failed with error {hresult}");
			}
			Console.Write($"PlaceholdersCompletion: completed Processed={opParams.TransferPlaceholders.EntriesProcessed} result={opParams.TransferPlaceholders.CompletionStatus}");
		}
	}
}
