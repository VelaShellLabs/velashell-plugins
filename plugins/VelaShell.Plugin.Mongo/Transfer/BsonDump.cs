using System.Buffers.Binary;
using System.IO.Compression;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Bson.Serialization;
using VelaShell.Plugin.Mongo.Core;

namespace VelaShell.Plugin.Mongo.Transfer;

/// <summary>
/// <c>mongodump</c> 兼容的转储布局:<c>&lt;目录&gt;/&lt;库&gt;/&lt;集合&gt;.bson</c>(裸 BSON 文档首尾相接)
/// + <c>&lt;集合&gt;.metadata.json</c>(<c>{options, indexes, uuid, collectionName, type}</c>,Canonical EJSON)。
/// gzip 模式下两个文件都带 <c>.gz</c> 后缀 —— 与 <c>mongodump --gzip</c> 一致,<c>mongorestore --gzip</c> 直接认。
/// </summary>
internal static class BsonDump
{
    /// <summary>某个集合在转储目录里的数据文件路径。</summary>
    public static string DataPath(string root, string database, string collection, bool gzip) =>
        Path.Combine(root, TransferText.SafeFileName(database), TransferText.SafeFileName(collection) + (gzip ? ".bson.gz" : ".bson"));

    /// <summary>某个集合的元数据文件路径。</summary>
    public static string MetadataPath(string root, string database, string collection, bool gzip) =>
        Path.Combine(root, TransferText.SafeFileName(database), TransferText.SafeFileName(collection) + (gzip ? ".metadata.json.gz" : ".metadata.json"));

    /// <summary>
    /// 拼元数据。索引规格去掉 <c>ns</c>(4.4 之前的服务器会带,mongorestore 到新版本时会被拒)。
    /// 视图的 <c>type</c> 是 <c>view</c>,没有索引,也没有数据文件。
    /// </summary>
    /// <param name="info">集合信息(<c>listCollections</c> 的一行)。</param>
    /// <param name="indexes">索引规格(<c>listIndexes</c> 原文)。</param>
    /// <param name="uuid">集合 UUID(<c>listCollections</c> 的 <c>info.uuid</c>);没有为 <see langword="null" />。</param>
    public static BsonDocument Metadata(CollectionInfo info, IReadOnlyList<BsonDocument> indexes, Guid? uuid)
    {
        var cleaned = new BsonArray();
        foreach (BsonDocument index in indexes)
        {
            BsonDocument copy = index.DeepClone().AsBsonDocument;
            copy.Remove("ns");
            cleaned.Add(copy);
        }
        var metadata = new BsonDocument
        {
            { "indexes", cleaned },
            { "collectionName", info.Name },
            { "type", info.Kind switch
                {
                    CollectionKind.View => "view",
                    CollectionKind.TimeSeries => "timeseries",
                    _ => "collection"
                }
            }
        };
        if (uuid is { } id)
        {
            metadata.InsertAt(1, new BsonElement("uuid", id.ToString("N")));
        }
        metadata.InsertAt(0, new BsonElement("options", info.Options.DeepClone()));
        return metadata;
    }

    /// <summary>元数据 → 文本(Canonical EJSON,单行,与 mongodump 的写法一致)。</summary>
    public static string MetadataJson(BsonDocument metadata) =>
        metadata.ToJson(new JsonWriterSettings { OutputMode = JsonOutputMode.CanonicalExtendedJson, Indent = false });

    /// <summary>打开一个写出流(gzip 时套一层压缩)。</summary>
    public static Stream OpenWrite(string path, bool gzip)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        Stream file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
        return gzip ? new GZipStream(file, CompressionLevel.Fastest, leaveOpen: false) : file;
    }

    /// <summary>打开一个读入流(按扩展名认 gzip)。</summary>
    public static Stream OpenRead(string path)
    {
        Stream file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        return path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) ? new GZipStream(file, CompressionMode.Decompress) : file;
    }

    /// <summary>把一份文档的 BSON 字节写进流。</summary>
    public static int Write(Stream stream, BsonDocument document)
    {
        byte[] bytes = document.ToBson();
        stream.Write(bytes);
        return bytes.Length;
    }

    /// <summary>
    /// 从裸 BSON 流里读一份文档;到流尾返回 <see langword="null" />。
    /// 文档长度前缀不合法(截断、不是 BSON)时抛 <see cref="InvalidDataException" />。
    /// </summary>
    public static BsonDocument? Read(Stream stream)
    {
        byte[]? bytes = ReadRaw(stream);
        return bytes is null ? null : BsonSerializer.Deserialize<BsonDocument>(bytes);
    }

    /// <summary>读一份文档的原始字节。</summary>
    public static byte[]? ReadRaw(Stream stream)
    {
        Span<byte> prefix = stackalloc byte[4];
        int got = stream.ReadAtLeast(prefix, 4, throwOnEndOfStream: false);
        if (got == 0)
        {
            return null;
        }
        if (got < 4)
        {
            throw new InvalidDataException("Truncated BSON document length.");
        }
        int length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        // BSON 单文档上限 16 MB,留点余量;小于 5 字节的不可能是文档。
        if (length < 5 || length > 48 * 1024 * 1024)
        {
            throw new InvalidDataException($"Invalid BSON document length {length}.");
        }
        byte[] bytes = new byte[length];
        prefix.CopyTo(bytes);
        int read = stream.ReadAtLeast(bytes.AsSpan(4), length - 4, throwOnEndOfStream: false);
        if (read < length - 4)
        {
            throw new InvalidDataException("Truncated BSON document.");
        }
        return bytes;
    }

    /// <summary>数一个 .bson 文件里有多少份文档(只读长度前缀,未压缩时直接跳着走)。</summary>
    public static long Count(string path, CancellationToken cancellationToken = default)
    {
        using Stream stream = OpenRead(path);
        long count = 0;
        if (stream.CanSeek)
        {
            Span<byte> prefix = stackalloc byte[4];
            while (stream.Position < stream.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (stream.ReadAtLeast(prefix, 4, throwOnEndOfStream: false) < 4)
                {
                    break;
                }
                int length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
                if (length < 5)
                {
                    break;
                }
                stream.Seek(length - 4, SeekOrigin.Current);
                count++;
            }
            return count;
        }
        while (ReadRaw(stream) is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            count++;
        }
        return count;
    }
}
