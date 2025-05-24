using DiscUtils;
using DiscUtils.Ext;
using DiscUtils.Setup;
using System.Text.RegularExpressions;

namespace SharpVMDK
{
    class Program
    {
        static void Main(string[] args)
        {
            if (args.Length != 4)
            {
                Console.WriteLine($"Requires 4 arguments but {args.Length} given.");
                Console.WriteLine();
                PrintHelp();
                Environment.Exit(2);
            }

            string VmdkFile = args[0];
            int LogicalVolumeIndex = int.Parse(args[1]);
            string InternalFilePathPattern = args[2];
            string HostCopyFilePath = args[3];

            SetupHelper.RegisterAssembly(typeof(ExtFileSystem).Assembly);
            SetupHelper.RegisterAssembly(typeof(DiscUtils.Vmdk.Disk).Assembly);

            VirtualDisk VirtualDiskObject = VirtualDisk.OpenDisk(VmdkFile, FileAccess.Read);
            VolumeManager VolumeManagerObject = new VolumeManager();
            VolumeManagerObject.AddDisk(VirtualDiskObject);

            var LogicalVolumes = VolumeManagerObject.GetLogicalVolumes();

            if (LogicalVolumes.Count() == 0)
            {
                Console.WriteLine("No logical volumes found.");
                Environment.Exit(1);
            }
            Console.WriteLine($"Number of logical volumes: {LogicalVolumes.Count()}");

            if (LogicalVolumeIndex < 0 || LogicalVolumeIndex >= LogicalVolumes.Count())
            {
                Console.WriteLine($"Selected logical volume index {LogicalVolumeIndex} is out of range.");
                Environment.Exit(1);
            }

            var SelectedLogicalVolume = LogicalVolumes[LogicalVolumeIndex];
            var SelectedVolumeFileSystemInfoList = FileSystemManager.DetectFileSystems(SelectedLogicalVolume);

            if (SelectedVolumeFileSystemInfoList.Count() == 0)
            {
                Console.WriteLine("No file system detected on the selected logical volume.");
                Environment.Exit(1);
            }

            var SelectedVolumeFileSystemInfo = SelectedVolumeFileSystemInfoList[0];
            Console.WriteLine($"Detected file system: {SelectedVolumeFileSystemInfo.Name}");

            DiscFileSystem DiscFileSystemObject;
            string SelectedVolumeFileSystemType = SelectedVolumeFileSystemInfo.Name;

            switch (SelectedVolumeFileSystemType.ToLowerInvariant())
            {
                case "ext":
                    DiscFileSystemObject = new ExtFileSystem(SelectedLogicalVolume.Open());
                    break;
                default:
                    Console.WriteLine($"Unsupported file system: {SelectedVolumeFileSystemInfo.Name}");
                    Environment.Exit(1);
                    return;
            }

            Console.WriteLine();

            string InternalSearchPatternNormalized = InternalFilePathPattern.Replace('\\', '/');
            string[] PatternSegments = InternalSearchPatternNormalized.TrimStart('/').Split('/');

            if (PatternSegments.Length == 0 || (PatternSegments.Length == 1 && string.IsNullOrEmpty(PatternSegments[0])))
            {
                Console.WriteLine("Invalid internal file pattern provided.");
                Environment.Exit(1);
                return;
            }

            List<string> FoundInternalFiles = FindFilesMatchingPattern(DiscFileSystemObject, "", PatternSegments, 0);

            if (FoundInternalFiles.Count == 0)
            {
                Console.WriteLine($"No files matching the pattern '{InternalFilePathPattern}' were found. Skipping.");
                Environment.Exit(1);
            }

            Console.WriteLine($"Found {FoundInternalFiles.Count} file(s) matching pattern '{InternalFilePathPattern}':");
            foreach (var File in FoundInternalFiles)
            {
                Console.WriteLine($"- {File}");
            }

            Console.WriteLine();

            int FileCounter = 1;
            foreach (string FoundInternalFile in FoundInternalFiles)
            {
                long FileSize = DiscFileSystemObject.GetFileLength(FoundInternalFile);
                if (FileSize == 0)
                {
                    Console.WriteLine($"The matched file '{FoundInternalFile}' has been found but its size is zero. Skipping.");
                    continue;
                }

                Console.WriteLine($"Processing matched file '{FoundInternalFile}' ({FileSize} bytes).");

                string CurrentHostCopyFilePath = HostCopyFilePath;
                if (FoundInternalFiles.Count > 1)
                {
                    string? HostDirectory = Path.GetDirectoryName(HostCopyFilePath);
                    string OriginalHostFileNameWithoutExtension = Path.GetFileNameWithoutExtension(HostCopyFilePath);
                    string HostFileExtension = Path.GetExtension(HostCopyFilePath);
                    if (string.IsNullOrEmpty(HostDirectory))
                    {
                        CurrentHostCopyFilePath = $"{OriginalHostFileNameWithoutExtension}{HostFileExtension}.{FileCounter}";
                    }
                    else
                    {
                        CurrentHostCopyFilePath = Path.Combine(HostDirectory, $"{OriginalHostFileNameWithoutExtension}{HostFileExtension}.{FileCounter}");
                    }
                }
                FileCounter++;

                string? HostTargetDirectory = Path.GetDirectoryName(CurrentHostCopyFilePath);
                if (!string.IsNullOrEmpty(HostTargetDirectory) && !Directory.Exists(HostTargetDirectory))
                {
                    Directory.CreateDirectory(HostTargetDirectory);
                }

                try
                {
                    using Stream SourceStream = DiscFileSystemObject.OpenFile(FoundInternalFile, FileMode.Open, FileAccess.Read);
                    using FileStream DestinationStream = File.Create(CurrentHostCopyFilePath);
                    SourceStream.CopyTo(DestinationStream);
                    Console.WriteLine($"File copied successfully to '{CurrentHostCopyFilePath}'");
                }
                catch (Exception Ex)
                {
                    Console.WriteLine($"Error copying file '{FoundInternalFile}' to '{CurrentHostCopyFilePath}': {Ex.Message}");
                }
            }
        }

        private static bool WildcardMatchSegment(string text, string patternSegment)
        {
            string RegexPattern = "^" + Regex.Escape(patternSegment).Replace("\\?", ".").Replace("\\*", ".*") + "$";
            return Regex.IsMatch(text, RegexPattern);
        }

        private static List<string> FindFilesMatchingPattern(DiscFileSystem dfs, string currentSearchDirectoryDfs, string[] patternSegments, int segmentIndex)
        {
            List<string> Results = new List<string>();
            string CurrentPatternSegment = patternSegments[segmentIndex];

            bool DirectoryExistsOrIsRoot = string.IsNullOrEmpty(currentSearchDirectoryDfs) || dfs.DirectoryExists(currentSearchDirectoryDfs);
            if (!DirectoryExistsOrIsRoot)
            {
                return Results;
            }

            if (segmentIndex == patternSegments.Length - 1)
            {
                string[] FilesInDirectory = dfs.GetFiles(currentSearchDirectoryDfs);
                foreach (string DfsFilePath in FilesInDirectory)
                {
                    string FileName = GetFileNameFromDfsPath(DfsFilePath);
                    if (WildcardMatchSegment(FileName, CurrentPatternSegment))
                    {
                        Results.Add(DfsFilePath);
                    }
                }
            }
            else
            {
                string[] DirectoriesInDirectory = dfs.GetDirectories(currentSearchDirectoryDfs);
                foreach (string DfsDirectoryPath in DirectoriesInDirectory)
                {
                    string DirectoryName = GetFileNameFromDfsPath(DfsDirectoryPath);
                    if (WildcardMatchSegment(DirectoryName, CurrentPatternSegment))
                    {
                        Results.AddRange(FindFilesMatchingPattern(dfs, DfsDirectoryPath, patternSegments, segmentIndex + 1));
                    }
                }
            }
            return Results;
        }

        private static string GetFileNameFromDfsPath(string dfsPath)
        {
            if (string.IsNullOrEmpty(dfsPath)) return "";
            string NormalizedPath = dfsPath.Replace('\\', '/');
            if (NormalizedPath.Length > 1 && NormalizedPath.EndsWith("/"))
            {
                NormalizedPath = NormalizedPath.Substring(0, NormalizedPath.Length - 1);
            }
            int LastSlashIndex = NormalizedPath.LastIndexOf('/');
            return (LastSlashIndex >= 0) ? NormalizedPath.Substring(LastSlashIndex + 1) : NormalizedPath;
        }

        private static void PrintHelp()
        {
            Console.WriteLine("Usage: SharpVMDK [VMDK File] [Logical Volume Index] \"[Internal Path Pattern]\" \"[Copy to Path]\"");
            Console.WriteLine();
            Console.WriteLine("Arguments:");
            Console.WriteLine("  [VMDK file]                 : Path to the VMDK disk image file.");
            Console.WriteLine("  [Logical Volume Index]      : Zero-based index of the logical volume to inspect.");
            Console.WriteLine("  \"[Internal Path Pattern]\"   : Path pattern within the VMDK's volume. Supports '*' wildcard.");
            Console.WriteLine("                                  Use '/' as path separator. Quote if pattern contains spaces.");
            Console.WriteLine("  \"[Copy to Path]\"            : Path on the host system where found file(s) will be copied.");
            Console.WriteLine("                                  If multiple files match, a numeric suffix is added (e.g., file.1.txt).");
            Console.WriteLine();
            Console.WriteLine("Examples:");
            Console.WriteLine("  SharpVMDK.exe .\\rootfs.vmdk 0 \"/root/root.txt\" .\\root.txt");
            Console.WriteLine("  SharpVMDK.exe .\\rootfs.vmdk 0 \"/home/*/*.txt\" .\\user.txt");
            Console.WriteLine("  SharpVMDK.exe .\\rootfs.vmdk 0 \"/home/*/*/id_rsa\" .\\id_rsa");
        }
    }
}