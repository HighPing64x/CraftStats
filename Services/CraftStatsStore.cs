using System.Security.Cryptography;
using System.Text.Json;

namespace CraftStats;

public sealed class CraftStatsStore
{
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };
    public string BaseDirectory { get; }
    public string SnapshotPath { get; }
    public string SnapshotBackupPath { get; }
    public string SnapshotTempPath { get; }
    public string ExportDirectory { get; }

    public CraftStatsStore()
    {
        BaseDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CraftStats");
        SnapshotPath = Path.Combine(BaseDirectory, "snapshot.craftstats");
        SnapshotBackupPath = Path.Combine(BaseDirectory, "snapshot.backup.craftstats");
        SnapshotTempPath = Path.Combine(BaseDirectory, "snapshot.tmp.craftstats");
        ExportDirectory = Path.Combine(BaseDirectory, "exports");
        Directory.CreateDirectory(BaseDirectory);
        Directory.CreateDirectory(ExportDirectory);
    }

    public async Task SaveAsync(CraftStatsSnapshot snapshot, CancellationToken token = default)
    {
        var json = JsonSerializer.Serialize(snapshot, _jsonOptions);
        _ = JsonSerializer.Deserialize<CraftStatsSnapshot>(json, _jsonOptions)
            ?? throw new InvalidDataException("无法生成有效的 CraftStats 快照。");

        await File.WriteAllTextAsync(SnapshotTempPath, json, Encoding.UTF8, token);
        _ = await LoadFromPathAsync(SnapshotTempPath, token)
            ?? throw new InvalidDataException("无法验证临时 CraftStats 快照。");

        if (File.Exists(SnapshotPath))
        {
            File.Copy(SnapshotPath, SnapshotBackupPath, overwrite: true);
            File.Replace(SnapshotTempPath, SnapshotPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(SnapshotTempPath, SnapshotPath, overwrite: true);
        }
    }

    public async Task<CraftStatsSnapshot?> LoadAsync(CancellationToken token = default)
    {
        if (!File.Exists(SnapshotPath))
            return File.Exists(SnapshotBackupPath) ? await LoadFromPathAsync(SnapshotBackupPath, token) : null;

        try
        {
            return await LoadFromPathAsync(SnapshotPath, token);
        }
        catch (JsonException) when (File.Exists(SnapshotBackupPath))
        {
            var backup = await LoadFromPathAsync(SnapshotBackupPath, token);
            File.Copy(SnapshotPath, Path.Combine(BaseDirectory, $"snapshot.corrupt-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.craftstats"), overwrite: false);
            File.Copy(SnapshotBackupPath, SnapshotPath, overwrite: true);
            return backup;
        }
    }

    private async Task<CraftStatsSnapshot?> LoadFromPathAsync(string path, CancellationToken token)
    {
        var json = await File.ReadAllTextAsync(path, Encoding.UTF8, token);
        return JsonSerializer.Deserialize<CraftStatsSnapshot>(json, _jsonOptions);
    }

    public async Task<string> ExportEncryptedAsync(CraftStatsSnapshot snapshot, string passphrase, CancellationToken token = default)
    {
        Directory.CreateDirectory(ExportDirectory);
        var plain = JsonSerializer.SerializeToUtf8Bytes(snapshot, _jsonOptions);
        var salt = RandomNumberGenerator.GetBytes(16);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var cipher = new byte[plain.Length];
        var key = Rfc2898DeriveBytes.Pbkdf2(passphrase, salt, 150_000, HashAlgorithmName.SHA256, 32);
        using var aes = new AesGcm(key, tag.Length);
        aes.Encrypt(nonce, plain, cipher, tag);

        await using var output = new MemoryStream();
        await output.WriteAsync(Encoding.ASCII.GetBytes("CRAFTSTATS2"), token);
        await output.WriteAsync(salt, token);
        await output.WriteAsync(nonce, token);
        await output.WriteAsync(tag, token);
        await output.WriteAsync(cipher, token);
        var path = Path.Combine(ExportDirectory, $"export-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.craftstats");
        await File.WriteAllBytesAsync(path, output.ToArray(), token);
        return path;
    }

    public static async Task<CraftStatsSnapshot> DecryptAsync(string path, string passphrase, CancellationToken token = default)
    {
        var bytes = await File.ReadAllBytesAsync(path, token);
        const int magicLength = 11;
        if (bytes.Length < magicLength + 32) throw new InvalidDataException("无效的 .craftstats 文件。");
        var magic = Encoding.ASCII.GetString(bytes, 0, magicLength);
        if (magic == "CRAFTSTATS2")
            return DecryptV2(bytes, passphrase);
        if (magic != "CRAFTSTATS1") throw new InvalidDataException("无效的 .craftstats 文件。");
        return await DecryptV1(bytes, passphrase);
    }

    private static CraftStatsSnapshot DecryptV2(byte[] bytes, string passphrase)
    {
        const int magicLength = 11;
        if (bytes.Length < magicLength + 16 + 12 + 16) throw new InvalidDataException("无效的 .craftstats 文件。");
        var salt = bytes.AsSpan(magicLength, 16).ToArray();
        var nonce = bytes.AsSpan(magicLength + 16, 12).ToArray();
        var tag = bytes.AsSpan(magicLength + 28, 16).ToArray();
        var cipher = bytes.AsSpan(magicLength + 44).ToArray();
        var plain = new byte[cipher.Length];
        var key = Rfc2898DeriveBytes.Pbkdf2(passphrase, salt, 150_000, HashAlgorithmName.SHA256, 32);
        using var aes = new AesGcm(key, tag.Length);
        aes.Decrypt(nonce, cipher, tag, plain);
        return JsonSerializer.Deserialize<CraftStatsSnapshot>(plain) ?? new CraftStatsSnapshot();
    }

    private static async Task<CraftStatsSnapshot> DecryptV1(byte[] bytes, string passphrase)
    {
        const int magicLength = 11;
        var salt = bytes.AsSpan(magicLength, 16).ToArray();
        var iv = bytes.AsSpan(magicLength + 16, 16).ToArray();
        var cipher = bytes.AsSpan(magicLength + 32).ToArray();
        var key = Rfc2898DeriveBytes.Pbkdf2(passphrase, salt, 150_000, HashAlgorithmName.SHA256, 32);
        using var aes = Aes.Create();
        aes.Key = key;
        aes.IV = iv;
        await using var input = new MemoryStream(cipher);
        await using var crypto = new CryptoStream(input, aes.CreateDecryptor(), CryptoStreamMode.Read);
        using var reader = new StreamReader(crypto, Encoding.UTF8);
        var json = await reader.ReadToEndAsync();
        return JsonSerializer.Deserialize<CraftStatsSnapshot>(json) ?? new CraftStatsSnapshot();
    }
}
