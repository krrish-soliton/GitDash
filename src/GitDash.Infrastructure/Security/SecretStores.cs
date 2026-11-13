using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using GitDash.Application.Abstractions;
using GitDash.Infrastructure.Storage;

namespace GitDash.Infrastructure.Security;

public static class SecretStoreFactory
{
    /// <summary>Picks the strongest store the operating system offers.</summary>
    public static ISecretStore Create(AppPaths paths)
    {
        if (OperatingSystem.IsWindows()) return new DpapiSecretStore(paths);
        if (OperatingSystem.IsMacOS()) return new MacKeychainSecretStore();
        return new LocalKeyFileSecretStore(paths);
    }
}

/// <summary>
/// Windows: tokens are encrypted with DPAPI under the signed-in Windows user, so the files are
/// useless to other users and on other machines.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiSecretStore(AppPaths paths) : ISecretStore
{
    private const int UiForbidden = 0x1;
    // Unchanged on purpose: tokens saved before the rename stay readable.
    private static readonly byte[] Entropy = "GithubDash.v1.token"u8.ToArray();

    public async Task SaveAsync(string key, string secret, CancellationToken ct = default)
    {
        var plain = Encoding.UTF8.GetBytes(secret);
        try
        {
            Directory.CreateDirectory(paths.SecretsFolder);
            await AppPaths.WriteAtomicAsync(FileFor(key), Transform(plain, protect: true), ct).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    public async Task<string?> ReadAsync(string key, CancellationToken ct = default)
    {
        var file = FileFor(key);
        if (!File.Exists(file)) return null;
        byte[]? plain = null;
        try
        {
            plain = Transform(await File.ReadAllBytesAsync(file, ct).ConfigureAwait(false), protect: false);
            return Encoding.UTF8.GetString(plain);
        }
        catch (CryptographicException)
        {
            return null; // Encrypted by another user or machine: treat as signed out.
        }
        finally
        {
            if (plain is not null) CryptographicOperations.ZeroMemory(plain);
        }
    }

    public Task DeleteAsync(string key, CancellationToken ct = default)
    {
        File.Delete(FileFor(key));
        return Task.CompletedTask;
    }

    private string FileFor(string key) =>
        Path.Combine(paths.SecretsFolder, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..32] + ".bin");

    private static byte[] Transform(byte[] input, bool protect)
    {
        var inHandle = GCHandle.Alloc(input, GCHandleType.Pinned);
        var entropyHandle = GCHandle.Alloc(Entropy, GCHandleType.Pinned);
        try
        {
            var inBlob = new DataBlob { Size = input.Length, Data = inHandle.AddrOfPinnedObject() };
            var entropyBlob = new DataBlob { Size = Entropy.Length, Data = entropyHandle.AddrOfPinnedObject() };
            var ok = protect
                ? CryptProtectData(ref inBlob, null, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, UiForbidden, out var outBlob)
                : CryptUnprotectData(ref inBlob, IntPtr.Zero, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, UiForbidden, out outBlob);
            if (!ok) throw new CryptographicException(Marshal.GetLastWin32Error());

            try
            {
                var output = new byte[outBlob.Size];
                Marshal.Copy(outBlob.Data, output, 0, outBlob.Size);
                return output;
            }
            finally
            {
                if (!protect)
                {
                    // Wipe the plaintext copy the OS allocated before handing the memory back.
                    Marshal.Copy(new byte[outBlob.Size], 0, outBlob.Data, outBlob.Size);
                }

                LocalFree(outBlob.Data);
            }
        }
        finally
        {
            inHandle.Free();
            entropyHandle.Free();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob dataIn, string? description, ref DataBlob entropy,
        IntPtr reserved, IntPtr prompt, int flags, out DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob dataIn, IntPtr description, ref DataBlob entropy,
        IntPtr reserved, IntPtr prompt, int flags, out DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr handle);
}

/// <summary>macOS: tokens live in the user's login Keychain as generic passwords for service "GitDash".</summary>
[SupportedOSPlatform("macos")]
public sealed class MacKeychainSecretStore : ISecretStore
{
    private const string SecurityLib = "/System/Library/Frameworks/Security.framework/Security";
    private const string CoreFoundationLib = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const int ErrSecItemNotFound = -25300;
    // Unchanged on purpose: tokens saved before the rename stay in the same Keychain entry.
    private static readonly byte[] Service = "GithubDash"u8.ToArray();

    public Task SaveAsync(string key, string secret, CancellationToken ct = default)
    {
        Remove(key);
        var account = Encoding.UTF8.GetBytes(key);
        var password = Encoding.UTF8.GetBytes(secret);
        try
        {
            var status = SecKeychainAddGenericPassword(
                IntPtr.Zero, (uint)Service.Length, Service, (uint)account.Length, account,
                (uint)password.Length, password, IntPtr.Zero);
            if (status != 0) throw new CryptographicException($"Keychain refused to store the token (status {status}).");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(password);
        }

        return Task.CompletedTask;
    }

    public Task<string?> ReadAsync(string key, CancellationToken ct = default)
    {
        var account = Encoding.UTF8.GetBytes(key);
        var status = SecKeychainFindGenericPassword(
            IntPtr.Zero, (uint)Service.Length, Service, (uint)account.Length, account,
            out var length, out var data, IntPtr.Zero);
        if (status == ErrSecItemNotFound) return Task.FromResult<string?>(null);
        if (status != 0) throw new CryptographicException($"Keychain could not read the token (status {status}).");

        try
        {
            var bytes = new byte[length];
            Marshal.Copy(data, bytes, 0, (int)length);
            try
            {
                return Task.FromResult<string?>(Encoding.UTF8.GetString(bytes));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }
        finally
        {
            _ = SecKeychainItemFreeContent(IntPtr.Zero, data);
        }
    }

    public Task DeleteAsync(string key, CancellationToken ct = default)
    {
        Remove(key);
        return Task.CompletedTask;
    }

    private static void Remove(string key)
    {
        var account = Encoding.UTF8.GetBytes(key);
        var status = SecKeychainFindGenericPassword(
            IntPtr.Zero, (uint)Service.Length, Service, (uint)account.Length, account,
            IntPtr.Zero, IntPtr.Zero, out var item);
        if (status != 0 || item == IntPtr.Zero) return;
        try
        {
            _ = SecKeychainItemDelete(item);
        }
        finally
        {
            CFRelease(item);
        }
    }

    [DllImport(SecurityLib)]
    private static extern int SecKeychainAddGenericPassword(
        IntPtr keychain, uint serviceLength, byte[] service, uint accountLength, byte[] account,
        uint passwordLength, byte[] password, IntPtr itemRef);

    [DllImport(SecurityLib)]
    private static extern int SecKeychainFindGenericPassword(
        IntPtr keychain, uint serviceLength, byte[] service, uint accountLength, byte[] account,
        out uint passwordLength, out IntPtr passwordData, IntPtr itemRef);

    [DllImport(SecurityLib)]
    private static extern int SecKeychainFindGenericPassword(
        IntPtr keychain, uint serviceLength, byte[] service, uint accountLength, byte[] account,
        IntPtr passwordLength, IntPtr passwordData, out IntPtr itemRef);

    [DllImport(SecurityLib)]
    private static extern int SecKeychainItemDelete(IntPtr itemRef);

    [DllImport(SecurityLib)]
    private static extern int SecKeychainItemFreeContent(IntPtr attributes, IntPtr data);

    [DllImport(CoreFoundationLib)]
    private static extern void CFRelease(IntPtr handle);
}

/// <summary>
/// Fallback for platforms without a system keystore API wired up (Linux development machines).
/// AES-GCM with a random key kept in a user-only (0600) file. This protects against casual reading
/// and backups of a single file, but NOT against another process running as the same user.
/// Windows and macOS never use this store.
/// </summary>
public sealed class LocalKeyFileSecretStore(AppPaths paths) : ISecretStore
{
    private const int NonceSize = 12;
    private const int TagSize = 16;

    public async Task SaveAsync(string key, string secret, CancellationToken ct = default)
    {
        var plain = Encoding.UTF8.GetBytes(secret);
        var masterKey = await GetKeyAsync(ct).ConfigureAwait(false);
        try
        {
            var output = new byte[NonceSize + TagSize + plain.Length];
            RandomNumberGenerator.Fill(output.AsSpan(0, NonceSize));
            using var aes = new AesGcm(masterKey, TagSize);
            aes.Encrypt(
                output.AsSpan(0, NonceSize), plain, output.AsSpan(NonceSize + TagSize),
                output.AsSpan(NonceSize, TagSize), Encoding.UTF8.GetBytes(key));
            await AppPaths.WriteAtomicAsync(FileFor(key), output, ct).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
            CryptographicOperations.ZeroMemory(masterKey);
        }
    }

    public async Task<string?> ReadAsync(string key, CancellationToken ct = default)
    {
        var file = FileFor(key);
        if (!File.Exists(file)) return null;
        var input = await File.ReadAllBytesAsync(file, ct).ConfigureAwait(false);
        if (input.Length < NonceSize + TagSize) return null;

        var masterKey = await GetKeyAsync(ct).ConfigureAwait(false);
        var plain = new byte[input.Length - NonceSize - TagSize];
        try
        {
            using var aes = new AesGcm(masterKey, TagSize);
            aes.Decrypt(
                input.AsSpan(0, NonceSize), input.AsSpan(NonceSize + TagSize),
                input.AsSpan(NonceSize, TagSize), plain, Encoding.UTF8.GetBytes(key));
            return Encoding.UTF8.GetString(plain);
        }
        catch (CryptographicException)
        {
            return null; // Tampered with, or the key file was replaced.
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
            CryptographicOperations.ZeroMemory(masterKey);
        }
    }

    public Task DeleteAsync(string key, CancellationToken ct = default)
    {
        File.Delete(FileFor(key));
        return Task.CompletedTask;
    }

    private string FileFor(string key)
    {
        Directory.CreateDirectory(paths.SecretsFolder);
        AppPaths.Restrict(paths.SecretsFolder);
        return Path.Combine(
            paths.SecretsFolder, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..32] + ".bin");
    }

    private async Task<byte[]> GetKeyAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(paths.SecretsFolder);
        AppPaths.Restrict(paths.SecretsFolder);
        var file = Path.Combine(paths.SecretsFolder, "local.key");
        if (File.Exists(file))
        {
            var existing = await File.ReadAllBytesAsync(file, ct).ConfigureAwait(false);
            if (existing.Length == 32) return existing;
        }

        var created = RandomNumberGenerator.GetBytes(32);
        await AppPaths.WriteAtomicAsync(file, created, ct).ConfigureAwait(false);
        return created;
    }
}

/// <summary>Demo mode: nothing touches the disk.</summary>
public sealed class InMemorySecretStore : ISecretStore
{
    private readonly Dictionary<string, string> _secrets = [];

    public Task SaveAsync(string key, string secret, CancellationToken ct = default)
    {
        lock (_secrets) _secrets[key] = secret;
        return Task.CompletedTask;
    }

    public Task<string?> ReadAsync(string key, CancellationToken ct = default)
    {
        lock (_secrets) return Task.FromResult(_secrets.GetValueOrDefault(key));
    }

    public Task DeleteAsync(string key, CancellationToken ct = default)
    {
        lock (_secrets) _secrets.Remove(key);
        return Task.CompletedTask;
    }
}
