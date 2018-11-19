﻿using SkyEditor.Core.IO;
using SkyEditor.Core.Utilities;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DotNet3dsToolkit.Ctr
{
    public class RomFs
    {
        /// <summary>
        /// Arbitrary upper bound of a filename that DotNet3dsToolkit will attempt to read, to prevent hogging all memory if there's a problem
        /// </summary>
        const int MaxFilenameLength = 1000;

        public static async Task<bool> IsRomFs(IBinaryDataAccessor file)
        {
            try
            {
                if (file.Length < 4)
                {
                    return false;
                }

                return await file.ReadStringAsync(0, 4, Encoding.ASCII) == "IVFC";
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static async Task<RomFs> Load(IBinaryDataAccessor data)
        {
            var header = new RomFsHeader(await data.ReadAsync(0, 0x6B));
            var romfs = new RomFs(data, header);
            await romfs.Initialize();
            return romfs;
        }

        public RomFs(IBinaryDataAccessor data, RomFsHeader header)
        {
            Data = data ?? throw new ArgumentNullException(nameof(data));
            Header = header ?? throw new ArgumentNullException(nameof(header));

            LevelLocations = new IvfcLevelLocation[]
            {
                new IvfcLevelLocation
                {
                    HashBlockSize = 1 << header.Level1BlockSize,
                    HashOffset = 0x60
                },
                new IvfcLevelLocation
                {
                    HashBlockSize = 1 << header.Level2BlockSize
                },
                new IvfcLevelLocation
                {
                    HashBlockSize = 1 << header.Level3BlockSize
                }
            };

            BodyOffset = Util.Align64(LevelLocations[0].HashOffset + header.MasterHashSize, LevelLocations[2].HashBlockSize);
            BodySize = header.Level3HashDataSize;

            LevelLocations[2].DataOffset = BodyOffset;
            LevelLocations[2].DataSize = Util.Align64(BodySize, LevelLocations[2].HashBlockSize);

            LevelLocations[1].HashOffset = Util.Align64(BodyOffset + BodySize, LevelLocations[2].HashBlockSize);
            LevelLocations[2].HashOffset = LevelLocations[1].HashOffset + header.Level2LogicalOffset - header.Level1LogicalOffset;

            LevelLocations[1].DataOffset = LevelLocations[2].HashOffset;
            LevelLocations[1].DataSize = Util.Align64(header.Level2HashDataSize, LevelLocations[1].HashBlockSize);

            LevelLocations[0].DataOffset = LevelLocations[2].HashOffset;
            LevelLocations[0].DataSize = Util.Align64(header.Level1HashDataSize, LevelLocations[0].HashBlockSize);

            // To-do: verify hashes
        }

        public async Task Initialize()
        {
            Level3 = await IvfcLevel.Load(Data, LevelLocations[2]);
        }

        public IBinaryDataAccessor Data { get; }

        public RomFsHeader Header { get; }

        private IvfcLevelLocation[] LevelLocations { get; }

        public IvfcLevel Level3 { get; private set; }

        private long BodyOffset { get; }

        private long BodySize { get; }

        public async Task ExtractFiles(string directoryName, IIOProvider provider, ExtractionProgressedToken progressReportToken = null)
        {
            if (progressReportToken != null)
            {
                progressReportToken.TotalFileCount = Level3.RootFiles.Length + Level3.RootDirectoryMetadataTable.CountChildFiles();
            }

            if (!provider.DirectoryExists(directoryName))
            {
                provider.CreateDirectory(directoryName);
            }

            async Task extractDirectory(DirectoryMetadata dir, string subDirectory)
            {
                var destDirectory = Path.Combine(subDirectory, dir.Name);
                if (!provider.DirectoryExists(destDirectory))
                {
                    provider.CreateDirectory(destDirectory);
                }

                var fileExtractor = new AsyncFor();
                var directoryExtractor = new AsyncFor();

                await Task.WhenAll(
                    fileExtractor.RunForEach(dir.ChildFiles, async f =>
                    {
                        provider.WriteAllBytes(Path.Combine(destDirectory, f.Name), await f.GetDataReference().ReadAsync());
                        if (progressReportToken != null)
                        {
                            progressReportToken.IncrementExtractedFileCount();
                        }
                    }),
                    directoryExtractor.RunForEach(dir.ChildDirectories, async d =>
                    {
                        await extractDirectory(d, destDirectory);
                    })
                );
            }

            var directoryExtractTasks = Level3
                .RootDirectoryMetadataTable
                .ChildDirectories
                .Select(d => extractDirectory(d, directoryName))
                .ToList();

            var fileExtractTasks = Level3
                .RootFiles
                .Select(async f => provider.WriteAllBytes(
                                        Path.Combine(directoryName, f.Name),
                                        await f.GetDataReference().ReadAsync()))
                .ToList();

            await Task.WhenAll(directoryExtractTasks);
            await Task.WhenAll(fileExtractTasks);
        }

        #region Child Classes
        public class RomFsHeader
        {
            public RomFsHeader(byte[] header)
            {
                if (header == null)
                {
                    throw new ArgumentNullException(nameof(header));
                }

                if (header.Length < 0x6B)
                {
                    throw new ArgumentException(Properties.Resources.RomFsHeader_ConstructorDataTooSmall, nameof(header));
                }

                Magic = Encoding.ASCII.GetString(header, 0, 4);
                MagicNumber = BitConverter.ToInt32(header, 0x4);
                MasterHashSize = BitConverter.ToInt32(header, 0x8);
                Level1LogicalOffset = BitConverter.ToInt64(header, 0xC);
                Level1HashDataSize = BitConverter.ToInt64(header, 0x14);
                Level1BlockSize = BitConverter.ToInt32(header, 0x1C);
                Reserved1 = BitConverter.ToInt32(header, 0x20);
                Level2LogicalOffset = BitConverter.ToInt64(header, 0x24);
                Level2HashDataSize = BitConverter.ToInt64(header, 0x2C);
                Level2BlockSize = BitConverter.ToInt32(header, 0x34);
                Reserved2 = BitConverter.ToInt32(header, 0x38);
                Level3LogicalOffset = BitConverter.ToInt64(header, 0x3C);
                Level3HashDataSize = BitConverter.ToInt64(header, 0x44);
                Level3BlockSize = BitConverter.ToInt32(header, 0x4C);
                Reserved3 = BitConverter.ToInt32(header, 0x50);
                Reserved4 = BitConverter.ToInt32(header, 0x54);
                OptionalInfoSize = BitConverter.ToInt32(header, 0x58);
            }

            /// <summary>
            /// Magic "IVFC"
            /// </summary>
            public string Magic { get; set; } // Offset: 0, size: 4

            /// <summary>
            /// Magic number 0x10000
            /// </summary>
            public int MagicNumber { get; set; } // Offset: 0x04, size: 4

            public int MasterHashSize { get; set; } // Offset: 0x08, size: 4

            public long Level1LogicalOffset { get; set; } // Offset: 0x0C, size: 8

            public long Level1HashDataSize { get; set; } // Offset: 0x14, size: 8

            /// <summary>
            /// Level 1 block size, in log2
            /// </summary>
            public int Level1BlockSize { get; set; } // Offset: 0x1C, size: 4

            public int Reserved1 { get; set; } // Offset: 0x20, size: 4

            public long Level2LogicalOffset { get; set; } // Offset: 0x24, size: 8

            public long Level2HashDataSize { get; set; } // Offset: 0x2C, size: 8

            /// <summary>
            /// Level 2 block size, in log2
            /// </summary>
            public int Level2BlockSize { get; set; } // Offset: 0x34, size: 4

            public int Reserved2 { get; set; } // Offset: 0x38, size: 4

            public long Level3LogicalOffset { get; set; } // Offset: 0x3C, size: 8

            public long Level3HashDataSize { get; set; } // Offset: 0x44, size: 8

            /// <summary>
            /// Level 3 block size, in log2
            /// </summary>
            public int Level3BlockSize { get; set; } // Offset: 0x4C, size: 4

            public int Reserved3 { get; set; } // Offset: 0x50, size: 4

            public int Reserved4 { get; set; } // Offset: 0x54, size: 4

            public int OptionalInfoSize { get; set; } // Offset: 0x58, size: 4
        }

        public class DirectoryMetadata
        {
            public static async Task<DirectoryMetadata> Load(IBinaryDataAccessor data, IvfcLevelHeader header, int offsetOffDirTable)
            {
                var offset = header.DirectoryMetadataTableOffset + offsetOffDirTable;
                var metadata = new DirectoryMetadata(data, header);
                metadata.ParentDirectoryOffset = await data.ReadInt32Async(offset + 0);
                metadata.SiblingDirectoryOffset = await data.ReadInt32Async(offset + 4);
                metadata.FirstChildDirectoryOffset = await data.ReadInt32Async(offset + 8);
                metadata.FirstFileOffset = await data.ReadInt32Async(offset + 0xC);
                metadata.NextDirectoryOffset = await data.ReadInt32Async(offset + 0x10);
                metadata.NameLength = await data.ReadInt32Async(offset + 0x14);
                if (metadata.NameLength > 0)
                {
                    metadata.Name = Encoding.Unicode.GetString(await data.ReadAsync(offset + 0x18, Math.Min(metadata.NameLength, MaxFilenameLength)));
                }
                
                await Task.WhenAll(
                    metadata.LoadChildDirectories(),
                    metadata.LoadChildFiles()
                );

                return metadata;
            }

            public DirectoryMetadata(IBinaryDataAccessor data, IvfcLevelHeader header)
            {
                LevelData = data ?? throw new ArgumentNullException(nameof(data));
                IvfcLevelHeader = header ?? throw new ArgumentNullException(nameof(data));
            }

            private IBinaryDataAccessor LevelData { get; }
            private IvfcLevelHeader IvfcLevelHeader { get; }

            /// <summary>
            /// Offset of Parent Directory (self if Root)
            /// </summary>
            public int ParentDirectoryOffset { get; set; } // Offset: 0x0

            /// <summary>
            /// Offset of next Sibling Directory
            /// </summary>
            public int SiblingDirectoryOffset { get; set; } // Offset: 0x4

            /// <summary>
            /// Offset of first Child Directory (Subdirectory)
            /// </summary>
            public int FirstChildDirectoryOffset { get; set; } // Offset: 0x8

            /// <summary>
            /// Offset of first File (in File Metadata Table)
            /// </summary>
            public int FirstFileOffset { get; set; } // Offset: 0xC

            /// <summary>
            /// Offset of next Directory in the same Hash Table bucket
            /// </summary>
            public int NextDirectoryOffset { get; set; } // Offset: 0x10

            /// <summary>
            /// Name Length
            /// </summary>
            public int NameLength { get; set; } // Offset: 0x14

            /// <summary>
            /// Directory Name (Unicode)
            /// </summary>
            public string Name { get; set; }

            public List<DirectoryMetadata> ChildDirectories { get; set; }

            public List<FileMetadata> ChildFiles { get; set; }

            public async Task LoadChildDirectories()
            {
                ChildDirectories = new List<DirectoryMetadata>();

                if (FirstChildDirectoryOffset > 0)
                {
                    var currentChild = await DirectoryMetadata.Load(LevelData, IvfcLevelHeader, FirstChildDirectoryOffset);
                    ChildDirectories.Add(currentChild);
                    while (currentChild.SiblingDirectoryOffset > 0)
                    {
                        currentChild = await DirectoryMetadata.Load(LevelData, IvfcLevelHeader, currentChild.SiblingDirectoryOffset);
                        ChildDirectories.Add(currentChild);
                    }
                }                
            }

            public async Task LoadChildFiles()
            {
                ChildFiles = new List<FileMetadata>();
                if (FirstFileOffset > 0)
                {
                    var currentChild = await FileMetadata.Load(LevelData, IvfcLevelHeader, FirstFileOffset);
                    ChildFiles.Add(currentChild);
                    while (currentChild.NextSiblingFileOffset > 0)
                    {
                        currentChild = await FileMetadata.Load(LevelData, IvfcLevelHeader, currentChild.NextSiblingFileOffset);
                        ChildFiles.Add(currentChild);
                    }
                }
            }

            public int CountChildFiles()
            {
                return ChildFiles.Count + ChildDirectories.Select(d => d.CountChildFiles()).Sum();
            }

            public override string ToString()
            {
                return !string.IsNullOrEmpty(Name) ? $"RomFs Directory Metadata: {Name}" : "RomFs Directory Metadata (No Name)";
            }
        }

        public class FileMetadata
        {
            public static async Task<FileMetadata> Load(IBinaryDataAccessor data, IvfcLevelHeader header, long offsetFromMetadataTable)
            {
                var offset = header.FileMetadataTableOffset + offsetFromMetadataTable;
                var metadata = new FileMetadata(data, header);
                metadata.ContainingDirectoryOffset = await data.ReadInt32Async(offset + 0);
                metadata.NextSiblingFileOffset = await data.ReadInt32Async(offset + 4);
                metadata.FileDataOffset = await data.ReadInt64Async(offset + 8);
                metadata.FileDataLength = await data.ReadInt64Async(offset + 0x10);
                metadata.NextFileOffset = await data.ReadInt32Async(offset + 0x18);
                metadata.NameLength = await data.ReadInt32Async(offset + 0x1C);
                if (metadata.NameLength > 0)
                {
                    metadata.Name = Encoding.Unicode.GetString(await data.ReadAsync(offset + 0x20, Math.Min(metadata.NameLength, MaxFilenameLength)));
                }
                return metadata;
            }

            public FileMetadata(IBinaryDataAccessor data, IvfcLevelHeader header)
            {
                LevelData = data ?? throw new ArgumentNullException(nameof(data));
                Header = header ?? throw new ArgumentNullException(nameof(header));
            }

            private IBinaryDataAccessor LevelData { get; }

            public IvfcLevelHeader Header { get; }

            /// <summary>
            /// Offset of Containing Directory (within Directory Metadata Table)
            /// </summary>
            public int ContainingDirectoryOffset { get; set; } // Offset: 0x0

            /// <summary>
            /// Offset of next Sibling File
            /// </summary>
            public int NextSiblingFileOffset { get; set; } // Offset: 0x4

            /// <summary>
            /// Offset of File's Data
            /// </summary>
            public long FileDataOffset { get; set; } // Offset: 0x8

            /// <summary>
            /// Length of File's Data
            /// </summary>
            public long FileDataLength { get; set; } // Offset: 0x10

            /// <summary>
            /// Offset of next File in the same Hash Table bucket
            /// </summary>
            public int NextFileOffset { get; set; } // Offset: 0x18

            /// <summary>
            /// Name Length
            /// </summary>
            public int NameLength { get; set; } // Offset: 0x1C

            /// <summary>
            /// File Name (Unicode)
            /// </summary>
            public string Name { get; set; } // Offset: 0x20

            public IBinaryDataAccessor GetDataReference()
            {
                return LevelData.GetDataReference(Header.FileDataOffset + FileDataOffset, FileDataLength);
            }

            public override string ToString()
            {
                return !string.IsNullOrEmpty(Name) ? $"RomFs File Metadata: {Name}" : "RomFs File Metadata (No Name)";
            }
        }

        public class IvfcLevelHeader
        {
            public IvfcLevelHeader(byte[] header)
            {
                if (header == null)
                {
                    throw new ArgumentNullException(nameof(header));
                }

                if (header.Length < 0x28)
                {
                    throw new ArgumentException(string.Format(Properties.Resources.BufferUnderflow, 0x28.ToString()), nameof(header));
                }

                Length = BitConverter.ToInt32(header, 0);
                DirectoryHashTableOffset = BitConverter.ToInt32(header, 4);
                DirectoryHashTableLength = BitConverter.ToInt32(header, 8);
                DirectoryMetadataTableOffset = BitConverter.ToInt32(header, 0xC);
                DirectoryMetadataTableLength = BitConverter.ToInt32(header, 0x10);
                FileHashTableOffset = BitConverter.ToInt32(header, 0x14);
                FileHashTableLength = BitConverter.ToInt32(header, 0x18);
                FileMetadataTableOffset = BitConverter.ToInt32(header, 0x1C);
                FileMetadataTableLength = BitConverter.ToInt32(header, 0x20);
                FileDataOffset = BitConverter.ToInt32(header, 0x24);
            }

            public int Length { get; set; } // Offset: 0x0
            public int DirectoryHashTableOffset { get; set; } // Offset: 0x4
            public int DirectoryHashTableLength { get; set; } // Offset: 0x8
            public int DirectoryMetadataTableOffset { get; set; } // Offset: 0xC
            public int DirectoryMetadataTableLength { get; set; } // Offset: 0x10
            public int FileHashTableOffset { get; set; } // Offset: 0x14
            public int FileHashTableLength { get; set; } // Offset: 0x18
            public int FileMetadataTableOffset { get; set; } // Offset: 0x1C
            public int FileMetadataTableLength { get; set; } // Offset: 0x20
            public int FileDataOffset { get; set; } // Offset: 0x24
        }

        /// <summary>
        /// Calculated properties used to find the location of a <see cref="IvfcLevel"/>.
        /// </summary>
        /// <remarks>
        /// Unlike most other child classes here, this does not represent a physical data structure.
        /// </remarks>
        public class IvfcLevelLocation
        {
            public long DataOffset { get; set; }
            public long DataSize { get; set; }
            public long HashOffset { get; set; }
            public int HashBlockSize { get; set; }

            /// <summary>
            /// A boolean indicating whether the hashes are good, or null of they have not been checked
            /// </summary>
            public bool? HashCheck { get; set; }
        }

        public class IvfcLevel
        {
            public static async Task<IvfcLevel> Load(IBinaryDataAccessor romfsData, IvfcLevelLocation location)
            {
                var header = new IvfcLevelHeader(await romfsData.ReadAsync(location.DataOffset, 0x28));
                var level = new IvfcLevel(romfsData.GetDataReference(location.DataOffset, location.DataSize), header);
                await level.Initialize();
                return level;
            }

            public IvfcLevel(IBinaryDataAccessor data, IvfcLevelHeader header)
            {
                LevelData = data ?? throw new ArgumentNullException(nameof(data));
                Header = header ?? throw new ArgumentNullException(nameof(header));
            }

            public async Task Initialize()
            {
                DirectoryHashKeyTable = await LevelData.ReadAsync(Header.DirectoryHashTableOffset, Header.DirectoryHashTableLength);
                RootDirectoryMetadataTable = await DirectoryMetadata.Load(LevelData, Header, 0);
                FileHashKeyTable = await LevelData.ReadAsync(Header.FileHashTableOffset, Header.FileHashTableLength);

                var rootFiles = new List<FileMetadata>();
                var currentRootFile = await FileMetadata.Load(LevelData, Header, 0);
                if (currentRootFile.Name.Length > 0)
                {
                    rootFiles.Add(currentRootFile);
                    while (currentRootFile.NextSiblingFileOffset > 0)
                    {
                        currentRootFile = await FileMetadata.Load(LevelData, Header, currentRootFile.NextSiblingFileOffset);
                        rootFiles.Add(currentRootFile);
                    }
                }
                RootFiles = rootFiles.ToArray();
            }

            private IBinaryDataAccessor LevelData { get; }

            public IvfcLevelHeader Header { get; } // Offset: 0, size: 0x28

            public byte[] DirectoryHashKeyTable { get; private set; }
            public DirectoryMetadata RootDirectoryMetadataTable { get; private set; }
            public byte[] FileHashKeyTable { get; private set; }
            public FileMetadata[] RootFiles { get; private set; }

            /// <remarks>
            /// Source code: https://www.3dbrew.org/wiki/RomFS
            /// </remarks>
            private static uint GetHashTableLength(uint numEntries)
            {
                uint count = numEntries;
                if (numEntries < 3)
                    count = 3;
                else if (numEntries < 19)
                    count |= 1;
                else
                {
                    while (count % 2 == 0
                        || count % 3 == 0
                        || count % 5 == 0
                        || count % 7 == 0
                        || count % 11 == 0
                        || count % 13 == 0
                        || count % 17 == 0)
                    {
                        count++;
                    }
                }
                return count;
            }

            /// <remarks>
            /// Source code: https://www.3dbrew.org/wiki/RomFS
            /// </remarks>
            private static uint CalcPathHash(byte[] name, uint parentOffset)
            {
                uint hash = parentOffset ^ 123456789;
                for (int i = 0; i < name.Length; i += 2)
                {
                    hash = (hash >> 5) | (hash << 27);
                    hash ^= (ushort)((name[i]) | (name[i + 1] << 8));
                }
                return hash;
            }
        }

        #endregion
    }
}
