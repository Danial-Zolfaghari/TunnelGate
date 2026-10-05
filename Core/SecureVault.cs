using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TunnelGate.Models;

namespace TunnelGate.Core;

public sealed class SecureVault
{
    private const int Iterations = 310_000;
    private const int MaxFailures = 5;
    private static readonly TimeSpan LockDuration = TimeSpan.FromHours(12);
    private readonly object _gate = new();
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private byte[]? _masterKey;

    public VaultData? Current { get; private set; }
    public bool IsUnlocked => _masterKey is not null && Current is not null;
    public bool HasVault => File.Exists(AppPaths.VaultFile);

    public event Action? Changed;

    public LockoutState GetLockout()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(AppPaths.LockoutFile)) return new();
                var state = JsonSerializer.Deserialize<LockoutState>(File.ReadAllText(AppPaths.LockoutFile), _json) ?? new();
                if (state.LockUntilUtc <= DateTimeOffset.UtcNow)
                {
                    if (state.FailedCount != 0 || state.LockUntilUtc != DateTimeOffset.MinValue) ResetLockout();
                    return new();
                }
                return state;
            }
            catch { return new(); }
        }
    }

    public void Create(string password)
    {
        if (string.IsNullOrWhiteSpace(password)) throw new InvalidOperationException("Vault password is required.");
        lock (_gate)
        {
            if (HasVault) throw new InvalidOperationException("Vault already exists.");
            Current = VaultData.CreateDefault();
            var salt = RandomNumberGenerator.GetBytes(16);
            _masterKey = DeriveKey(password, salt);
            WriteVault(Current, _masterKey, salt);
            TrySaveSessionKey(_masterKey);
            ResetLockout();
            Changed?.Invoke();
        }
    }

    public bool Unlock(string password)
    {
        if (string.IsNullOrWhiteSpace(password)) return false;
        lock (_gate)
        {
            var lockout = GetLockout();
            if (lockout.IsLocked) throw new InvalidOperationException($"Vault locked. Try again after {lockout.LockUntilUtc.ToLocalTime():yyyy-MM-dd HH:mm}.");
            try
            {
                var envelope = ReadEnvelope();
                var salt = Convert.FromBase64String(envelope.Salt);
                var key = DeriveKey(password, salt);
                var vault = DecryptEnvelope(envelope, key);
                _masterKey = key;
                Current = EnsureDefaults(vault);
                TrySaveSessionKey(key);
                ResetLockout();
                Changed?.Invoke();
                return true;
            }
            catch (CryptographicException)
            {
                RecordFailure();
                return false;
            }
            catch (JsonException)
            {
                RecordFailure();
                return false;
            }
        }
    }

    public bool TryRestoreSession()
    {
        lock (_gate)
        {
            if (!HasVault || !File.Exists(AppPaths.SessionFile)) return false;
            try
            {
                var protectedKey = File.ReadAllBytes(AppPaths.SessionFile);
                var key = Dpapi.Unprotect(protectedKey, Encoding.UTF8.GetBytes("TunnelGate/session/v3"));
                var vault = DecryptEnvelope(ReadEnvelope(), key);
                _masterKey = key;
                Current = EnsureDefaults(vault);
                Changed?.Invoke();
                return true;
            }
            catch
            {
                ClearSession();
                return false;
            }
        }
    }

    public void Save()
    {
        lock (_gate)
        {
            RequireUnlocked();
            var env = ReadEnvelope();
            var salt = Convert.FromBase64String(env.Salt);
            WriteVault(Current!, _masterKey!, salt);
            Changed?.Invoke();
        }
    }

    public void ChangePassword(string oldPassword, string newPassword)
    {
        if (string.IsNullOrWhiteSpace(newPassword)) throw new InvalidOperationException("New password is required.");
        lock (_gate)
        {
            RequireUnlocked();
            var env = ReadEnvelope();
            var oldKey = DeriveKey(oldPassword, Convert.FromBase64String(env.Salt));
            try { _ = DecryptEnvelope(env, oldKey); }
            catch (CryptographicException) { throw new InvalidOperationException("Current password is incorrect."); }
            var newSalt = RandomNumberGenerator.GetBytes(16);
            var newKey = DeriveKey(newPassword, newSalt);
            WriteVault(Current!, newKey, newSalt);
            _masterKey = newKey;
            TrySaveSessionKey(newKey);
            ResetLockout();
        }
    }

    public VaultProfile ActiveProfile()
    {
        RequireUnlocked();
        var vault = Current!;
        var profile = vault.ActiveProfile;
        if (profile is not null) return profile;
        if (vault.Profiles.Count == 0)
        {
            var cat = vault.Categories.FirstOrDefault();
            if (cat is null)
            {
                cat = new VaultCategory { Name = "Default" };
                vault.Categories.Add(cat);
            }
            profile = new VaultProfile { Name = "Default", CategoryUid = cat.Uid };
            vault.Profiles.Add(profile);
        }
        profile = vault.Profiles[0];
        vault.ActiveProfileUid = profile.Uid;
        Save();
        return profile;
    }

    public void SetActiveProfile(string uid)
    {
        RequireUnlocked();
        if (Current!.Profiles.All(p => p.Uid != uid)) throw new InvalidOperationException("Profile not found.");
        Current.ActiveProfileUid = uid;
        Save();
    }

    public VaultCategory AddCategory(string name)
    {
        RequireUnlocked();
        if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("Name is required.");
        var c = new VaultCategory { Name = name.Trim(), SortOrder = Current!.Categories.Count };
        Current.Categories.Add(c);
        Save();
        return c;
    }

    public VaultProfile AddProfile(string categoryUid, string name)
    {
        RequireUnlocked();
        if (Current!.Categories.All(c => c.Uid != categoryUid)) throw new InvalidOperationException("Category not found.");
        if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("Name is required.");
        var p = new VaultProfile { CategoryUid = categoryUid, Name = name.Trim() };
        Current.Profiles.Add(p);
        Current.ActiveProfileUid = p.Uid;
        Save();
        return p;
    }

    public void DeleteProfile(string uid)
    {
        RequireUnlocked();
        var vault = Current!;
        vault.Profiles.RemoveAll(p => p.Uid == uid);
        if (vault.ActiveProfileUid == uid) vault.ActiveProfileUid = vault.Profiles.FirstOrDefault()?.Uid ?? "";
        EnsureDefaults(vault);
        Save();
    }

    public void DeleteCategory(string uid)
    {
        RequireUnlocked();
        var vault = Current!;
        var removedProfileIds = vault.Profiles.Where(p => p.CategoryUid == uid).Select(p => p.Uid).ToHashSet();
        vault.Profiles.RemoveAll(p => p.CategoryUid == uid);
        vault.Categories.RemoveAll(c => c.Uid == uid);
        if (removedProfileIds.Contains(vault.ActiveProfileUid)) vault.ActiveProfileUid = vault.Profiles.FirstOrDefault()?.Uid ?? "";
        EnsureDefaults(vault);
        Save();
    }

    public void Lock()
    {
        lock (_gate)
        {
            if (_masterKey is not null) CryptographicOperations.ZeroMemory(_masterKey);
            _masterKey = null;
            Current = null;
            ClearSession();
        }
    }

    public string ExportBackup(string password)
    {
        RequireUnlocked();
        var persisted = DeepClone(Current!);
        foreach (var profile in persisted.Profiles)
            foreach (var tunnel in profile.Settings.Tunnels)
                if (!tunnel.SavePassword) tunnel.Password = "";
        return BackupCodec.Export(persisted, password);
    }

    public BackupPreview PreviewBackup(string text, string password) => BackupCodec.Preview(text, password);

    public void ImportBackup(string text, string password, bool replace)
    {
        RequireUnlocked();
        var incoming = BackupCodec.Import(text, password);
        if (replace)
        {
            Current = EnsureDefaults(incoming);
        }
        else
        {
            Merge(Current!, incoming);
        }
        Save();
    }

    public void ImportLegacyJson(string text, bool replace)
    {
        RequireUnlocked();
        var incoming = JsonSerializer.Deserialize<VaultData>(text, new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true
        }) ?? throw new InvalidOperationException("Legacy migration JSON is invalid.");
        incoming = EnsureDefaults(incoming);
        if (replace) Current = incoming;
        else Merge(Current!, incoming);
        Save();
    }

    private void Merge(VaultData current, VaultData incoming)
    {
        var categoryMap = new Dictionary<string, string>();
        foreach (var cat in incoming.Categories)
        {
            var nc = new VaultCategory { Name = cat.Name, Enabled = cat.Enabled, SortOrder = current.Categories.Count };
            current.Categories.Add(nc);
            categoryMap[cat.Uid] = nc.Uid;
        }
        string? first = null;
        foreach (var profile in incoming.Profiles)
        {
            var np = DeepClone(profile);
            np.Uid = Guid.NewGuid().ToString("N");
            np.CategoryUid = categoryMap.TryGetValue(profile.CategoryUid, out var mapped)
                ? mapped
                : current.Categories.First().Uid;
            foreach (var t in np.Settings.Tunnels) t.Uid = Guid.NewGuid().ToString("N");
            current.Profiles.Add(np);
            first ??= np.Uid;
        }
        if (first is not null) current.ActiveProfileUid = first;
    }

    private VaultEnvelope ReadEnvelope()
    {
        if (!HasVault) throw new InvalidOperationException("Vault does not exist.");
        return JsonSerializer.Deserialize<VaultEnvelope>(File.ReadAllText(AppPaths.VaultFile), _json)
               ?? throw new InvalidOperationException("Vault is corrupted.");
    }

    private void WriteVault(VaultData vault, byte[] key, byte[] salt)
    {
        var persisted = DeepClone(vault);
        foreach (var profile in persisted.Profiles)
            foreach (var tunnel in profile.Settings.Tunnels)
                if (!tunnel.SavePassword) tunnel.Password = "";

        var plain = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(persisted, _json));
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, plain, cipher, tag, Encoding.UTF8.GetBytes("TunnelGate/v3"));
        CryptographicOperations.ZeroMemory(plain);

        var envelope = new VaultEnvelope
        {
            Version = 3,
            Salt = Convert.ToBase64String(salt),
            Nonce = Convert.ToBase64String(nonce),
            Tag = Convert.ToBase64String(tag),
            Ciphertext = Convert.ToBase64String(cipher)
        };
        var temp = AppPaths.VaultFile + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(envelope, _json), Encoding.UTF8);
        File.Move(temp, AppPaths.VaultFile, true);
    }

    private VaultData DecryptEnvelope(VaultEnvelope envelope, byte[] key)
    {
        if (envelope.Version != 3) throw new CryptographicException("Unsupported vault version.");
        var nonce = Convert.FromBase64String(envelope.Nonce);
        var tag = Convert.FromBase64String(envelope.Tag);
        var cipher = Convert.FromBase64String(envelope.Ciphertext);
        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(key, 16);
        aes.Decrypt(nonce, cipher, tag, plain, Encoding.UTF8.GetBytes("TunnelGate/v3"));
        try
        {
            return JsonSerializer.Deserialize<VaultData>(plain, _json) ?? throw new CryptographicException("Vault payload is empty.");
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    private static byte[] DeriveKey(string password, byte[] salt)
        => Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, Iterations, HashAlgorithmName.SHA256, 32);

    private static VaultData EnsureDefaults(VaultData vault)
    {
        vault.Categories ??= [];
        vault.Profiles ??= [];
        if (vault.Categories.Count == 0) vault.Categories.Add(new VaultCategory { Name = "Default" });
        if (vault.Profiles.Count == 0)
        {
            var p = new VaultProfile { Name = "Default", CategoryUid = vault.Categories[0].Uid };
            vault.Profiles.Add(p);
            vault.ActiveProfileUid = p.Uid;
        }
        if (vault.Profiles.All(p => p.Uid != vault.ActiveProfileUid)) vault.ActiveProfileUid = vault.Profiles[0].Uid;
        foreach (var p in vault.Profiles)
        {
            p.Settings ??= new ProfileSettings();
            p.Settings.Tunnels ??= [];
            if (vault.Categories.All(c => c.Uid != p.CategoryUid)) p.CategoryUid = vault.Categories[0].Uid;
        }
        return vault;
    }

    private static void TrySaveSessionKey(byte[] key)
    {
        try
        {
            var protectedBytes = Dpapi.Protect(key, Encoding.UTF8.GetBytes("TunnelGate/session/v3"));
            File.WriteAllBytes(AppPaths.SessionFile, protectedBytes);
        }
        catch
        {
            // Session restore is a convenience feature. A DPAPI/file failure must never
            // turn a valid vault create/unlock into a failed password attempt.
            ClearSession();
        }
    }

    private static void ClearSession()
    {
        try { if (File.Exists(AppPaths.SessionFile)) File.Delete(AppPaths.SessionFile); } catch { }
    }

    private void RecordFailure()
    {
        var state = GetLockout();
        state.FailedCount++;
        if (state.FailedCount >= MaxFailures) state.LockUntilUtc = DateTimeOffset.UtcNow.Add(LockDuration);
        File.WriteAllText(AppPaths.LockoutFile, JsonSerializer.Serialize(state, _json));
        ClearSession();
    }

    private void ResetLockout()
    {
        try { File.WriteAllText(AppPaths.LockoutFile, JsonSerializer.Serialize(new LockoutState(), _json)); } catch { }
    }

    private void RequireUnlocked()
    {
        if (!IsUnlocked) throw new InvalidOperationException("Vault is locked.");
    }

    private T DeepClone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, _json), _json)!;

    private sealed class VaultEnvelope
    {
        public int Version { get; set; }
        public string Salt { get; set; } = "";
        public string Nonce { get; set; } = "";
        public string Tag { get; set; } = "";
        public string Ciphertext { get; set; } = "";
    }
}

public sealed class LockoutState
{
    public int FailedCount { get; set; }
    public DateTimeOffset LockUntilUtc { get; set; } = DateTimeOffset.MinValue;
    public bool IsLocked => LockUntilUtc > DateTimeOffset.UtcNow;
}

public sealed class BackupPreview
{
    public string App { get; set; } = "TunnelGate";
    public DateTimeOffset ExportedAt { get; set; }
    public int Categories { get; set; }
    public int Profiles { get; set; }
    public int Tunnels { get; set; }
}

internal static class BackupCodec
{
    private const int Iterations = 240_000;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static string Export(VaultData vault, string password)
    {
        if (string.IsNullOrWhiteSpace(password)) throw new InvalidOperationException("Backup password is required.");
        var salt = RandomNumberGenerator.GetBytes(16);
        var key = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, Iterations, HashAlgorithmName.SHA256, 32);
        var payload = new BackupPayload { ExportedAt = DateTimeOffset.UtcNow, Vault = vault };
        var plain = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload, Json));
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, plain, cipher, tag, Encoding.UTF8.GetBytes("TunnelGate/backup/v3"));
        var wrapper = new BackupEnvelope
        {
            App = "TunnelGate",
            Version = 3,
            Kdf = "PBKDF2-SHA256",
            Iterations = Iterations,
            Salt = Convert.ToBase64String(salt),
            Nonce = Convert.ToBase64String(nonce),
            Tag = Convert.ToBase64String(tag),
            Ciphertext = Convert.ToBase64String(cipher)
        };
        return JsonSerializer.Serialize(wrapper, Json);
    }

    public static BackupPreview Preview(string text, string password)
    {
        var payload = Decode(text, password);
        return new BackupPreview
        {
            App = "TunnelGate",
            ExportedAt = payload.ExportedAt,
            Categories = payload.Vault.Categories.Count,
            Profiles = payload.Vault.Profiles.Count,
            Tunnels = payload.Vault.Profiles.Sum(p => p.Settings.Tunnels.Count)
        };
    }

    public static VaultData Import(string text, string password) => Decode(text, password).Vault;

    private static BackupPayload Decode(string text, string password)
    {
        var env = JsonSerializer.Deserialize<BackupEnvelope>(text, Json) ?? throw new InvalidOperationException("Invalid backup file.");
        if (env.Version != 3 || env.App != "TunnelGate") throw new InvalidOperationException("Unsupported backup file.");
        var key = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), Convert.FromBase64String(env.Salt), env.Iterations, HashAlgorithmName.SHA256, 32);
        var cipher = Convert.FromBase64String(env.Ciphertext);
        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(key, 16);
        aes.Decrypt(Convert.FromBase64String(env.Nonce), cipher, Convert.FromBase64String(env.Tag), plain, Encoding.UTF8.GetBytes("TunnelGate/backup/v3"));
        return JsonSerializer.Deserialize<BackupPayload>(plain, Json) ?? throw new InvalidOperationException("Backup payload is empty.");
    }

    private sealed class BackupEnvelope
    {
        public string App { get; set; } = "TunnelGate";
        public int Version { get; set; }
        public string Kdf { get; set; } = "";
        public int Iterations { get; set; }
        public string Salt { get; set; } = "";
        public string Nonce { get; set; } = "";
        public string Tag { get; set; } = "";
        public string Ciphertext { get; set; } = "";
    }

    private sealed class BackupPayload
    {
        public DateTimeOffset ExportedAt { get; set; }
        public VaultData Vault { get; set; } = new();
    }
}

internal static class Dpapi
{
    private const int CryptProtectUiForbidden = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct DATA_BLOB { public int cbData; public IntPtr pbData; }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DATA_BLOB pDataIn, string? szDataDescr, ref DATA_BLOB pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, out DATA_BLOB pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref DATA_BLOB pDataIn, IntPtr ppszDataDescr, ref DATA_BLOB pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, out DATA_BLOB pDataOut);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr hMem);

    public static byte[] Protect(byte[] data, byte[] entropy) => Transform(data, entropy, true);
    public static byte[] Unprotect(byte[] data, byte[] entropy) => Transform(data, entropy, false);

    private static byte[] Transform(byte[] data, byte[] entropy, bool protect)
    {
        var inBlob = ToBlob(data);
        var entropyBlob = ToBlob(entropy);
        try
        {
            DATA_BLOB outBlob;
            bool ok = protect
                ? CryptProtectData(ref inBlob, null, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out outBlob)
                : CryptUnprotectData(ref inBlob, IntPtr.Zero, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out outBlob);
            if (!ok) throw new CryptographicException(Marshal.GetLastWin32Error());
            try
            {
                var output = new byte[outBlob.cbData];
                Marshal.Copy(outBlob.pbData, output, 0, output.Length);
                return output;
            }
            finally { LocalFree(outBlob.pbData); }
        }
        finally
        {
            Marshal.FreeHGlobal(inBlob.pbData);
            Marshal.FreeHGlobal(entropyBlob.pbData);
        }
    }

    private static DATA_BLOB ToBlob(byte[] data)
    {
        var ptr = Marshal.AllocHGlobal(data.Length);
        Marshal.Copy(data, 0, ptr, data.Length);
        return new DATA_BLOB { cbData = data.Length, pbData = ptr };
    }
}
