using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using UpkManager.Contracts;
using UpkManager.Helpers;
using UpkManager.Models.UpkFile.Compression;
using UpkManager.Models.UpkFile;
using UpkManager.Models.UpkFile.Tables;
using UpkManager.Indexing;
using System;
using System.Reflection;

#nullable enable

namespace UpkManager.Repository {

  public sealed class UpkFileRepository : IUpkFileRepository 
  {
        private readonly Dictionary<string, CacheEntry> _headerCache = [];
        private readonly Queue<string> _cacheOrder = new();
        private const int MaxCacheSize = 10;

        public UpkFilePackageSystem? PackageIndex { get; private set; }

        #region IUpkFileRepository Implementation

        public async Task<UnrealHeader> LoadUpkFile(string filename)
        {
            DateTime writeTimeUtc = File.GetLastWriteTimeUtc(filename);
            if (_headerCache.TryGetValue(filename, out CacheEntry? cachedEntry) &&
                cachedEntry.WriteTimeUtc == writeTimeUtc)
            {
                return cachedEntry.Header;
            }

            if (cachedEntry != null)
                _headerCache.Remove(filename);

            byte[] data = await Task.Run(() => File.ReadAllBytes(filename));

            var reader = ByteArrayReader.CreateNew(data, 0);

            UnrealHeader header = new (reader) 
            {
                FullFilename = filename,
                FileSize     = data.LongLength,
                Repository   = this
            };

            AddToCache(filename, header, writeTimeUtc);

            return header;
        }

        // Drops every cached header (and the whole-file ByteArrayReader each one
        // holds). Callers that do a large burst of loads from the same few UPKs —
        // e.g. decoding a full hero roster's icons out of the shared ICO packages —
        // reuse this repository so each UPK is read ONCE, then call this when the
        // burst is done so the (potentially hundreds of MB of) file bytes are
        // released back for collection instead of pinned for the session.
        public void ClearHeaderCache()
        {
            _headerCache.Clear();
            _cacheOrder.Clear();
        }

        private void AddToCache(string fullPath, UnrealHeader header, DateTime writeTimeUtc)
        {
            if (!_headerCache.ContainsKey(fullPath) && _headerCache.Count >= MaxCacheSize)
            {
                string oldestKey = _cacheOrder.Dequeue();
                _headerCache.Remove(oldestKey);
            }

            if (!_headerCache.ContainsKey(fullPath))
                _cacheOrder.Enqueue(fullPath);

            _headerCache[fullPath] = new CacheEntry(header, writeTimeUtc);
        }

        public async Task SaveUpkFile(UnrealHeader Header, string Filename)
            => await SaveUpkFile(Header, Filename, null).ConfigureAwait(false);

        public async Task SaveUpkFile(UnrealHeader Header, string Filename, Action<string>? log = null) 
        {
            if (Header == null) return;

            SanitizeHeaderForWrite(Header, log);

            if (Header.CompressedChunks.Count > 0)
            {
                await SaveCompressedUpkFile(Header, Filename, log).ConfigureAwait(false);
                return;
            }

            FileStream stream = new (Filename, FileMode.Create);

            int headerSize = Header.GetBuilderSize();

            ByteArrayWriter writer = ByteArrayWriter.CreateNew(headerSize);

            await Header.WriteBuffer(writer, 0);

            await stream.WriteAsync(writer.GetBytes(), 0, writer.Index);

            foreach (UnrealExportTableEntry export in Header.ExportTable) 
            {
                ByteArrayWriter objectWriter = await export.WriteObjectBuffer();

                await stream.WriteAsync(objectWriter.GetBytes(), 0, objectWriter.Index);
            }

            await stream.FlushAsync();

            stream.Close();
        }

        private static async Task SaveCompressedUpkFile(UnrealHeader header, string filename, Action<string>? log = null)
        {
            byte[] body = await BuildExportBodyAsync(header, log).ConfigureAwait(false);
            ByteArrayReader bodyReader = ByteArrayReader.CreateNew(body, 0);

            var chunkHeader = new UnrealCompressedChunkHeader();
            int chunkPayloadSize = await chunkHeader.BuildCompressedChunkHeader(bodyReader, header.CompressionFlags).ConfigureAwait(false);

            var chunk = new WritableCompressedChunk();
            header.CompressedChunks.Clear();
            header.CompressedChunks.Add(chunk);

            int headerSize = header.GetBuilderSize();

            chunk.Configure(headerSize, body.Length, headerSize, chunkPayloadSize, chunkHeader);

            ByteArrayWriter headerWriter = ByteArrayWriter.CreateNew(headerSize);
            await header.WriteBuffer(headerWriter, 0).ConfigureAwait(false);

            ByteArrayWriter chunkWriter = ByteArrayWriter.CreateNew(chunkPayloadSize);
            await chunkHeader.WriteCompressedChunkHeader(chunkWriter, 0).ConfigureAwait(false);

            using FileStream stream = new(filename, FileMode.Create);
            await stream.WriteAsync(headerWriter.GetBytes(), 0, headerWriter.Index).ConfigureAwait(false);
            await stream.WriteAsync(chunkWriter.GetBytes(), 0, chunkWriter.Index).ConfigureAwait(false);
            await stream.FlushAsync().ConfigureAwait(false);
        }

        private static async Task<byte[]> BuildExportBodyAsync(UnrealHeader header, Action<string>? log = null)
        {
            List<byte[]> buffers = [];

            foreach (UnrealExportTableEntry export in header.ExportTable)
            {
                try
                {
                    ByteArrayWriter objectWriter = await export.WriteObjectBuffer().ConfigureAwait(false);
                    buffers.Add(objectWriter.GetBytes().Take(objectWriter.Index).ToArray());
                }
                catch (Exception ex)
                {
                    log?.Invoke($"Warning: export body skipped for {export.GetPathName()}: {ex.Message}");
                }
            }

            int totalSize = buffers.Sum(static buffer => buffer.Length);
            byte[] body = new byte[totalSize];

            int cursor = 0;
            foreach (byte[] buffer in buffers)
            {
                Buffer.BlockCopy(buffer, 0, body, cursor, buffer.Length);
                cursor += buffer.Length;
            }

            return body;
        }

        public void LoadPackageIndex(string indexPath)
        {
            PackageIndex = UpkFilePackageSystem.LoadFromFile(indexPath);
        }

        private static void SanitizeHeaderForWrite(UnrealHeader header, Action<string>? log)
        {
            List<UnrealExportTableEntry> originalExports = header.ExportTable.ToList();
            List<int> originalDepends = header.DependsTable.ToList();
            List<UnrealExportTableEntry> keptExports = [];
            List<int> keptDepends = [];
            Dictionary<int, int> remap = [];
            int removedExports = 0;

            for (int i = 0; i < originalExports.Count; i++)
            {
                UnrealExportTableEntry export = originalExports[i];
                int dependsValue = i < originalDepends.Count ? originalDepends[i] : 0;

                bool keep = TryPrepareExport(export, log);
                if (!keep)
                {
                    removedExports++;
                    continue;
                }

                keptExports.Add(export);
                remap[export.TableIndex] = keptExports.Count;
                keptDepends.Add(dependsValue);
            }

            if (removedExports <= 0)
                return;

            log?.Invoke($"Info: pruned {removedExports} unresolved export(s) before write.");

            header.ExportTable.Clear();
            header.ExportTable.AddRange(keptExports);

            for (int i = 0; i < header.ExportTable.Count; i++)
            {
                UnrealExportTableEntry export = header.ExportTable[i];
                export.TableIndex = i + 1;
                RemapExportReferences(export, remap);
            }

            header.DependsTable.Clear();
            foreach (int dependsValue in keptDepends)
            {
                header.DependsTable.Add(RemapExportValue(dependsValue, remap));
            }
        }

        private static bool TryPrepareExport(UnrealExportTableEntry export, Action<string>? log)
        {
            bool canPreserveRaw = export.UnrealObjectReader != null && export.SerialDataSize > 0;

            if (canPreserveRaw)
            {
                SetObject(export, "UnrealObject", null);
                log?.Invoke($"Info: export preserved as raw bytes: {export.GetPathName()}");
                return true;
            }

            try
            {
                if (export.UnrealObject == null)
                    export.ParseUnrealObject(false, false).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                if (!canPreserveRaw)
                {
                    log?.Invoke($"Warning: unresolved export skipped during sanitize: {export.GetPathName()}: {ex.Message}");
                    return false;
                }

                log?.Invoke($"Warning: unresolved export preserved as raw bytes: {export.GetPathName()}: {ex.Message}");
            }

            if (export.UnrealObject == null && !canPreserveRaw)
            {
                log?.Invoke($"Warning: unresolved export skipped during sanitize: {export.GetPathName()}");
                return false;
            }

            ByteArrayWriter bodyWriter = export.WriteObjectBuffer().GetAwaiter().GetResult();
            if (bodyWriter == null || bodyWriter.Index <= 0)
            {
                if (!canPreserveRaw)
                {
                    log?.Invoke($"Warning: non-serializable export skipped during sanitize: {export.GetPathName()}");
                    return false;
                }

                log?.Invoke($"Warning: export preserved using raw bytes: {export.GetPathName()}");
            }

            return true;
        }

        private static void RemapExportReferences(UnrealExportTableEntry export, IReadOnlyDictionary<int, int> remap)
        {
            SetReference(export, "ClassReference", RemapExportValue(GetReference(export, "ClassReference"), remap));
            SetReference(export, "SuperReference", RemapExportValue(GetReference(export, "SuperReference"), remap));
            SetReference(export, "OuterReference", RemapExportValue(GetReference(export, "OuterReference"), remap));
            SetReference(export, "ArchetypeReference", RemapExportValue(GetReference(export, "ArchetypeReference"), remap));
        }

        private static int RemapExportValue(int value, IReadOnlyDictionary<int, int> remap)
        {
            if (value <= 0)
                return value;

            return remap.TryGetValue(value, out int mappedValue) ? mappedValue : 0;
        }

        private static int GetReference(UnrealExportTableEntry export, string propertyName)
        {
            PropertyInfo? property = typeof(UnrealExportTableEntry).GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            object? value = property?.GetValue(export);
            return value is int reference ? reference : 0;
        }

        private static void SetReference(UnrealExportTableEntry export, string propertyName, int value)
        {
            PropertyInfo? property = typeof(UnrealExportTableEntry).GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            property?.SetValue(export, value);
        }

        private static void SetObject(UnrealExportTableEntry export, string propertyName, object? value)
        {
            PropertyInfo? property = typeof(UnrealExportTableEntry).GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            property?.SetValue(export, value);
        }

        public UnrealObjectTableEntryBase GetExportEntry(string pathName, string root)
        {
            return Task.Run(async () =>
                {
                    UnrealObjectTableEntryBase? entry = null;

                    var location = PackageIndex?.GetFirstLocation(pathName, LocationFilter.MinSize);
                    if (location == null) return entry!;

                    string fullPath = Path.Combine(root, location.UpkFileName);
                    int exportIndex = location.ExportIndex;

                    var header = await LoadUpkFile(fullPath);            
                    await header.ReadHeaderAsync(null);

                    entry = header.ExportTable.Find( e => e.TableIndex ==  exportIndex);
                    return entry!;
                }).GetAwaiter().GetResult();
        }

        #endregion IUpkFileRepository Implementation

        private sealed class WritableCompressedChunk : UnrealCompressedChunk
        {
            public void Configure(int uncompressedOffset, int uncompressedSize, int compressedOffset, int compressedSize, UnrealCompressedChunkHeader header)
            {
                UncompressedOffset = uncompressedOffset;
                UncompressedSize = uncompressedSize;
                CompressedOffset = compressedOffset;
                CompressedSize = compressedSize;
                Header = header;
            }
        }

        private sealed record CacheEntry(UnrealHeader Header, DateTime WriteTimeUtc);

    }

}

#nullable restore
