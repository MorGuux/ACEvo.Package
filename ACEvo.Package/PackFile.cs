﻿using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;

using ACEvo.Package.Hashing;

using Syroot.BinaryData.Memory;

namespace ACEvo.Package;

public class PackFile : IDisposable
{
    private readonly ILogger _logger;

    private const ulong KEY = 0x9F9721A97D1135C1uL;
    private const long FILE_TABLE_SIZE = 0x2000000;  // 32mb
    private const long MAX_FILE_COUNT = FILE_TABLE_SIZE / 0x100;

    private Dictionary<string, PackFileEntry> _fileTable = [];
    private Dictionary<ulong, PackFileEntry> _fileTableKeys = [];

    private FileStream _packStream;

    public int FileCount => _fileTable.Count;

    public PackFile(FileStream stream, ILoggerFactory loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(stream, nameof(stream));

        _packStream = stream;

        _logger = loggerFactory.CreateLogger(GetType().ToString());
    }

    public static PackFile Open(string file, ILoggerFactory loggerFactory = null)
    {
        var fs = File.Open(file, FileMode.Open, FileAccess.ReadWrite);
        fs.Position = fs.Length - FILE_TABLE_SIZE;

        byte[] fileTableBytes = new byte[FILE_TABLE_SIZE];
        fs.ReadExactly(fileTableBytes);
        Xor(fileTableBytes, KEY);

        SpanReader sr = new SpanReader(fileTableBytes);

        var packFile = new PackFile(fs, loggerFactory);
        for (int i = 0; i < MAX_FILE_COUNT; i++)
        {
            byte[] entryBytes = sr.ReadBytes(0x100);
            var entry = MemoryMarshal.Cast<byte, PackFileEntry>(entryBytes)[0];
            if (entry.PathHash == 0)
                break; // Based on logic

            unsafe
            {
                string name = Encoding.ASCII.GetString(entry.FileNameBuffer, entry.FileNameLength);
                packFile._fileTable.Add(name, entry);
                packFile._fileTableKeys.Add(entry.PathHash, entry);
            }
        }

        fs.Position = fs.Length - FILE_TABLE_SIZE;

        return packFile;
    }

    public static void Dump(string file, string outputFileName)
    {
        const int bufferSize = 0x20000; // Define a chunk size (e.g., 4 KB)
        byte[] buffer = new byte[bufferSize];

        using (var inputStream = File.Open(file, FileMode.Open, FileAccess.Read))
        using (var outputStream = File.Open(outputFileName, FileMode.Create, FileAccess.Write))
        {
            int bytesRead;
            while ((bytesRead = inputStream.Read(buffer, 0, buffer.Length)) > 0)
            {
                Xor(buffer, KEY);
                outputStream.Write(buffer, 0, bytesRead);
            }
        }
    }

    public void ExtractAll(string outputDir)
    {
        _fileCounter = 0;
        foreach (KeyValuePair<string, PackFileEntry> file in _fileTable.OrderBy(e => e.Value.FileOffset)) // Let's be friendly to hard drive and only extract sequentially.
        {
            Extract(file.Key, file.Value, outputDir);
            _fileCounter++;
        }
    }

    public bool ExtractFile(string gamePath, string outputDir)
    {
        ulong hash = HashPath(gamePath);

        if (!_fileTableKeys.TryGetValue(hash, out PackFileEntry entry))
            return false;

        Extract(gamePath, entry, outputDir);
        return true;
    }

    private int? _fileCounter;

    public void Extract(string key, PackFileEntry entry, string outputDir)
    {
        if (_fileCounter is not null)
            _logger?.LogInformation("[{fileNumber}/{fileCount}] Extracting '{path}' (0x{packSize:X} bytes)...", _fileCounter + 1, _fileTable.Count, key, entry.FileSize);
        else
            _logger?.LogInformation("Extracting '{path}' (0x{packSize:X} bytes)...", key, entry.FileSize);

        _packStream.Position = entry.FileOffset;

        string outputFile = Path.Combine(outputDir, key);
        Directory.CreateDirectory(Path.GetDirectoryName(outputFile));

        if (entry.Flags.HasFlag(FileFlags.IsDirectory))
            return;

        if (entry.FileSize > 0)
        {
            byte[] data = ArrayPool<byte>.Shared.Rent((int)entry.FileSize);
            _packStream.ReadExactly(data);

            if (entry.Flags.HasFlag(FileFlags.Encrypted))
                Xor(data.AsSpan(0, (int)entry.FileSize), KEY);

            using var outputStream = File.Create(outputFile);
            outputStream.Write(data.AsSpan(0, (int)entry.FileSize));

            ArrayPool<byte>.Shared.Return(data);
        }
        else
            File.WriteAllBytes(outputFile, []);

    }

    public void ListFiles(string outputPath, bool log = false)
    {
        using var sw = new StreamWriter(outputPath);
        foreach (var file in _fileTable.OrderBy(e => e.Key))
        {
            sw.WriteLine($"{file.Key} - offset:{file.Value.FileOffset:X8}, size:{file.Value.FileSize:X8} hash: {file.Value.PathHash} ({file.Value.Flags})");

            if (log)
            {
                _logger?.LogInformation("{path} - offset:{offset:X8}, size:{size:X8} hash:{hash:X8} ({flags})",
                    file.Key,
                    file.Value.FileOffset,
                    file.Value.FileSize,
                    file.Value.PathHash,
                    file.Value.Flags);
            }
        }
    }

    public bool FileExists(string fileToCheck, bool log = false)
    {
        var hash = HashPath(fileToCheck);
        return _fileTableKeys.TryGetValue(hash, out _);
    }

    private static ulong HashPath(string path)
    {
        byte[] bytes = Encoding.Unicode.GetBytes(path.ToLower().Replace('/', '\\'));
        return FNV1A64.Hash(bytes);
    }

    private static void Xor(Span<byte> data, ulong key)
    {
        while (data.Length >= 8)
        {
            Span<ulong> ulongs = MemoryMarshal.Cast<byte, ulong>(data);
            ulongs[0] ^= key;
            data = data[8..];
        }

        while (data.Length > 0)
        {
            data[0] ^= (byte)key;
            data = data[1..];

            key >>= 8;
        }
    }

    public void Dispose()
    {
        ((IDisposable)_packStream).Dispose();
        GC.SuppressFinalize(this);
    }

    public static PackFile Create(string outputFile, ILoggerFactory loggerFactory = null)
    {
        var fs = File.Create(outputFile);
        return new PackFile(fs, loggerFactory);
    }

    public void AddFile(string gamePath, string inputFile, string backupPath = "")
    {
        ArgumentNullException.ThrowIfNull(gamePath);
        ArgumentNullException.ThrowIfNull(inputFile);

        var fileInfo = new FileInfo(inputFile);
        if (!fileInfo.Exists)
            throw new FileNotFoundException("Input file not found", inputFile);

        PackFileEntry entry;
        var referencePosition = _packStream.Position;
        bool fileWrittenInPlace = false;

        //Check if file already exists in the pack, if so, overwrite it
        if (_fileTable.TryGetValue(gamePath, out PackFileEntry value))
        {
            if (!string.IsNullOrEmpty(backupPath))
            {
                // Backup the file
                _logger?.LogInformation("File '{path}' already exists, backing up before overwrite", gamePath);
                ExtractFile(gamePath, backupPath);
            }
            else
            {
                _logger?.LogInformation("File '{path}' already exists, overwriting", gamePath);
            }

            var oldFileAddress = value.FileOffset;
            var oldFileSize = value.FileSize;

            //If the new file is smaller than the old one, insert the new file in the same place, otherwise append it
            if (fileInfo.Length <= oldFileSize)
            {
                fileWrittenInPlace = true;
                //Nullify the old file data in the pack
                _packStream.Position = oldFileAddress;
                _packStream.Write(new byte[oldFileSize]);

                //Revert the position to the old file address, ready for the new file to use it's space
                _packStream.Position = oldFileAddress;

                _logger?.LogInformation("Using original memory location for '{path}' (0x{oldFileLocation:X})", gamePath, oldFileAddress);
            }
            else
            {
                _packStream.Position = _packStream.Length;
            }

            //Update the file table entry with the new file length, and unencrypted flag
            value.FileSize = fileInfo.Length;

            entry = value;
        }
        else
        {
            entry = new PackFileEntry
            {
                FileOffset = _packStream.Position,
                FileSize = fileInfo.Length,
                PathHash = HashPath(gamePath),
                Flags = 0,
                FileNameLength = (byte)Math.Min(gamePath.Length, 0xF0)
            };
        }

        unsafe
        {
            Encoding.ASCII.GetBytes(gamePath).AsSpan(0, entry.FileNameLength)
                .CopyTo(MemoryMarshal.CreateSpan(ref entry.FileNameBuffer[0], entry.FileNameLength));
        }

        byte[] fileData = File.ReadAllBytes(inputFile);

        //If the entry is to be encrypted, XOR the file data
        if (entry.Flags.HasFlag(FileFlags.Encrypted))
            Xor(fileData, KEY);

        _packStream.Write(fileData);

        _fileTable[gamePath] = entry;
        _fileTableKeys[entry.PathHash] = entry;

        //If the file was overwritten into the previous file's memory space, revert the file stream position to the reference position
        if (fileWrittenInPlace)
            _packStream.Position = referencePosition;

        _logger?.LogInformation("Added file '{path}' (0x{size:X} bytes)", gamePath, fileData.Length);
    }

    public void AddDirectory(string gamePath)
    {
        ArgumentNullException.ThrowIfNull(gamePath);

        //Check if directory already exists in the pack
        if (_fileTable.ContainsKey(gamePath))
        {
            _logger?.LogInformation("Directory '{path}' already exists", gamePath);
            return;
        }

        var entry = new PackFileEntry
        {
            FileOffset = 0,
            FileSize = 0,
            PathHash = HashPath(gamePath),
            Flags = FileFlags.IsDirectory,
            FileNameLength = (byte)Math.Min(gamePath.Length, 0xF0)
        };

        unsafe
        {
            Encoding.ASCII.GetBytes(gamePath).AsSpan(0, entry.FileNameLength)
                .CopyTo(MemoryMarshal.CreateSpan(ref entry.FileNameBuffer[0], entry.FileNameLength));
        }

        _fileTable[gamePath] = entry;
        _fileTableKeys[entry.PathHash] = entry;

        _logger?.LogInformation("Added directory '{path}'", gamePath);
    }

    public void Finalize(bool overwriteFileTable)
    {
        // Write file table
        byte[] fileTableBytes = new byte[FILE_TABLE_SIZE];
        int offset = 0;

        foreach (var entry in _fileTable.Values.OrderBy(e => e.PathHash))
        {
            var entrySpan = MemoryMarshal.Cast<PackFileEntry, byte>(new[] { entry });
            entrySpan.CopyTo(fileTableBytes.AsSpan(offset));
            offset += 0x100;

            if (offset >= FILE_TABLE_SIZE)
                throw new InvalidOperationException("Too many files in pack");
        }

        Xor(fileTableBytes, KEY);

        var fileTablePosition = _packStream.Length - FILE_TABLE_SIZE;

        //If packStream position greater than pack length - file table size, then file table is probably corrupted, so just write over it
        if (overwriteFileTable)
        {
            if (_packStream.Position <= fileTablePosition)
            {
                //File table hasn't been written into, so just overwrite it (instead of appending it to the end of the file)
                _packStream.Position = fileTablePosition;
            }
        }
        else
        {
            //Write file table at the end of the pack
            _packStream.Position = _packStream.Length;
        }

        _packStream.Write(fileTableBytes);
        _packStream.Flush();
    }
}
