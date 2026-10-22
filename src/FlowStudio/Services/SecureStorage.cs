using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace FlowStudio.Services;

/// <summary>Per-Windows-user secret storage backed by DPAPI. Does not require a NuGet package.</summary>
public sealed class SecureStorage
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("FlowStudio.Native.Secrets.v1");
    private readonly string _directory;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SecureStorage(string? directory = null)
    {
        _directory = Path.GetFullPath(directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FlowStudio", "Secrets"));
    }

    public async Task SaveStringAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        var path = GetPath(key);
        byte[]? plaintext = null;
        byte[]? encrypted = null;
        string? temporaryPath = null;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            plaintext = Encoding.UTF8.GetBytes(value);
            encrypted = Protect(plaintext);
            Directory.CreateDirectory(_directory);
            temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await File.WriteAllBytesAsync(temporaryPath, encrypted, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (plaintext is not null)
                CryptographicOperations.ZeroMemory(plaintext);
            if (encrypted is not null)
                CryptographicOperations.ZeroMemory(encrypted);
            if (temporaryPath is not null)
            {
                try { File.Delete(temporaryPath); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            _gate.Release();
        }
    }

    public async Task<string?> ReadStringAsync(string key, CancellationToken cancellationToken = default)
    {
        var path = GetPath(key);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        byte[]? encrypted = null;
        byte[]? plaintext = null;
        try
        {
            if (!File.Exists(path))
                return null;
            encrypted = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            plaintext = Unprotect(encrypted);
            return Encoding.UTF8.GetString(plaintext);
        }
        finally
        {
            if (encrypted is not null)
                CryptographicOperations.ZeroMemory(encrypted);
            if (plaintext is not null)
                CryptographicOperations.ZeroMemory(plaintext);
            _gate.Release();
        }
    }

    public void Delete(string key)
    {
        var path = GetPath(key);
        _gate.Wait();
        try { File.Delete(path); }
        finally { _gate.Release(); }
    }

    public static byte[] Protect(byte[] plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        return Transform(plaintext, protect: true);
    }

    public static byte[] Unprotect(byte[] encrypted)
    {
        ArgumentNullException.ThrowIfNull(encrypted);
        return Transform(encrypted, protect: false);
    }

    private string GetPath(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return Path.Combine(_directory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))) + ".bin");
    }

    private static byte[] Transform(byte[] input, bool protect)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("DPAPI доступен только в Windows.");
        var inputBlob = CreateBlob(input);
        var entropyBlob = CreateBlob(Entropy);
        DataBlob outputBlob = default;
        try
        {
            const int uiForbidden = 0x1;
            var success = protect
                ? CryptProtectData(ref inputBlob, "Flow Studio", ref entropyBlob, IntPtr.Zero, IntPtr.Zero, uiForbidden, out outputBlob)
                : CryptUnprotectData(ref inputBlob, IntPtr.Zero, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, uiForbidden, out outputBlob);
            if (!success)
                throw new CryptographicException("Windows DPAPI не смогла обработать сохранённые данные.", new Win32Exception(Marshal.GetLastWin32Error()));
            if (outputBlob.Length < 0)
                throw new CryptographicException("Windows DPAPI вернула неверную длину данных.");
            var output = new byte[outputBlob.Length];
            if (output.Length > 0)
                Marshal.Copy(outputBlob.Data, output, 0, output.Length);
            return output;
        }
        finally
        {
            ZeroAndFree(ref inputBlob, localAllocated: false);
            ZeroAndFree(ref entropyBlob, localAllocated: false);
            ZeroAndFree(ref outputBlob, localAllocated: true);
        }
    }

    private static DataBlob CreateBlob(byte[] bytes)
    {
        var blob = new DataBlob { Length = bytes.Length, Data = Marshal.AllocHGlobal(Math.Max(bytes.Length, 1)) };
        if (bytes.Length > 0)
            Marshal.Copy(bytes, 0, blob.Data, bytes.Length);
        return blob;
    }

    private static void ZeroAndFree(ref DataBlob blob, bool localAllocated)
    {
        if (blob.Data == IntPtr.Zero)
            return;
        // Avoid a second managed copy of secret bytes when clearing unmanaged memory.
        for (var offset = 0; offset < blob.Length; offset++)
            Marshal.WriteByte(blob.Data, offset, 0);
        if (localAllocated)
            _ = LocalFree(blob.Data);
        else
            Marshal.FreeHGlobal(blob.Data);
        blob = default;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Length;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob data, string description, ref DataBlob entropy,
        IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob data, IntPtr description, ref DataBlob entropy,
        IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr memory);
}
