using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AirePeek.Core;

namespace AirePeek.Ssh;

/// <summary>Only ordinary passwords belong here. Callers own and must clear returned character buffers.</summary>
public interface IPasswordStore
{
    char[]? Read(SshEndpoint endpoint);
    void Save(SshEndpoint endpoint, ReadOnlySpan<char> password);
    void Delete(SshEndpoint endpoint);
}

public interface IPasswordStorageStatus { string? PasswordStorageWarning { get; } }
public sealed class PasswordStoreException() : InvalidOperationException("The system password store is unavailable. No password was written to portable files.");

public static class PasswordVault
{
    public static void Forget(ConnectionProfile profile)
    {
        var store = new OsPasswordStore();
        bool failed = false;
        foreach (var endpoint in new[] { profile.Target, profile.Gateway }.OfType<SshEndpoint>().Distinct())
            try { store.Delete(endpoint); } catch (PasswordStoreException) { failed = true; }
        if (failed) throw new PasswordStoreException();
    }
}

/// <summary>Current-user Windows Credential Manager or macOS Keychain. No shell, plaintext file or portable secret fallback.</summary>
public sealed class OsPasswordStore : IPasswordStore
{
    private const uint Generic = 1, LocalMachinePersistence = 2;
    private const int NotFound = 1168, KeychainNotFound = -25300, MaximumBlobSize = 2560;
    private const string Security = "/System/Library/Frameworks/Security.framework/Security";
    private static readonly byte[] Service = Encoding.UTF8.GetBytes("HPCPeek SSH");
    private static readonly UTF8Encoding Utf8 = new(false, true);
    public static bool IsSupported => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();

    internal static string ScopeKey(SshEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        // Preserve v0.5 credential identities without reflection-based JSON serialization.
        using var identity = new MemoryStream();
        using (var writer = new Utf8JsonWriter(identity))
        {
            writer.WriteStartArray();
            writer.WriteStringValue(endpoint.Host.Trim().ToLowerInvariant());
            writer.WriteStringValue(endpoint.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
            writer.WriteStringValue(endpoint.Username);
            writer.WriteEndArray();
        }
        return "HPCPeek/SSH/v1/" + Convert.ToHexString(SHA256.HashData(identity.GetBuffer().AsSpan(0, (int)identity.Length)));
    }

    public char[]? Read(SshEndpoint endpoint)
    {
        try
        {
            if (OperatingSystem.IsWindows()) return ReadWindows(endpoint);
            if (OperatingSystem.IsMacOS()) return ReadMac(endpoint);
            throw new PasswordStoreException();
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or DecoderFallbackException or OverflowException)
        { throw new PasswordStoreException(); }
    }

    public void Save(SshEndpoint endpoint, ReadOnlySpan<char> password)
    {
        byte[]? bytes = null;
        try
        {
            if (password.IsEmpty || Utf8.GetByteCount(password) > MaximumBlobSize) throw new PasswordStoreException();
            bytes = new byte[Utf8.GetByteCount(password)];
            Utf8.GetBytes(password, bytes);
            if (OperatingSystem.IsWindows()) SaveWindows(endpoint, bytes);
            else if (OperatingSystem.IsMacOS()) SaveMac(endpoint, bytes);
            else throw new PasswordStoreException();
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or EncoderFallbackException)
        { throw new PasswordStoreException(); }
        finally { if (bytes is not null) CryptographicOperations.ZeroMemory(bytes); }
    }

    public void Delete(SshEndpoint endpoint)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                if (!CredDeleteW(ScopeKey(endpoint), Generic, 0) && Marshal.GetLastPInvokeError() != NotFound) throw new PasswordStoreException();
            }
            else if (OperatingSystem.IsMacOS())
            {
                var account = Encoding.UTF8.GetBytes(ScopeKey(endpoint));
                int result = FindMacItem(account, out var item);
                try { if (result != KeychainNotFound && (result != 0 || SecKeychainItemDelete(item) != 0)) throw new PasswordStoreException(); }
                finally { Release(item); }
            }
            else throw new PasswordStoreException();
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException)
        { throw new PasswordStoreException(); }
    }

    private static char[]? ReadWindows(SshEndpoint endpoint)
    {
        if (!CredReadW(ScopeKey(endpoint), Generic, 0, out var pointer))
        {
            if (Marshal.GetLastPInvokeError() == NotFound) return null;
            throw new PasswordStoreException();
        }
        var credential = Marshal.PtrToStructure<Credential>(pointer);
        try { return Decode(credential.CredentialBlob, credential.CredentialBlobSize); }
        finally { ClearNative(credential.CredentialBlob, credential.CredentialBlobSize); CredFree(pointer); }
    }

    private static void SaveWindows(SshEndpoint endpoint, byte[] bytes)
    {
        var pointer = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, pointer, bytes.Length);
            var credential = new Credential { Type = Generic, TargetName = ScopeKey(endpoint), UserName = endpoint.Username,
                Comment = "HPCPeek SSH password", CredentialBlobSize = (uint)bytes.Length, CredentialBlob = pointer, Persist = LocalMachinePersistence };
            if (!CredWriteW(ref credential, 0)) throw new PasswordStoreException();
        }
        finally { ClearNative(pointer, (uint)bytes.Length); Marshal.FreeHGlobal(pointer); }
    }

    private static char[]? ReadMac(SshEndpoint endpoint)
    {
        var account = Encoding.UTF8.GetBytes(ScopeKey(endpoint));
        int result = SecKeychainFindGenericPassword(IntPtr.Zero, (uint)Service.Length, Service, (uint)account.Length, account, out var length, out var data, out var item);
        try
        {
            if (result == KeychainNotFound) return null;
            if (result != 0) throw new PasswordStoreException();
            return Decode(data, length);
        }
        finally
        {
            if (data != IntPtr.Zero) { ClearNative(data, length); SecKeychainItemFreeContent(IntPtr.Zero, data); }
            Release(item);
        }
    }

    private static void SaveMac(SshEndpoint endpoint, byte[] bytes)
    {
        var account = Encoding.UTF8.GetBytes(ScopeKey(endpoint));
        int result = FindMacItem(account, out var item);
        try
        {
            if (result == 0) result = SecKeychainItemModifyAttributesAndData(item, IntPtr.Zero, (uint)bytes.Length, bytes);
            else if (result == KeychainNotFound) result = SecKeychainAddGenericPassword(IntPtr.Zero, (uint)Service.Length, Service, (uint)account.Length, account, (uint)bytes.Length, bytes, out item);
            if (result != 0) throw new PasswordStoreException();
        }
        finally { Release(item); }
    }

    private static int FindMacItem(byte[] account, out IntPtr item) =>
        SecKeychainFindGenericPasswordItem(IntPtr.Zero, (uint)Service.Length, Service, (uint)account.Length, account, IntPtr.Zero, IntPtr.Zero, out item);
    private static void Release(IntPtr item) { if (item != IntPtr.Zero) CFRelease(item); }
    private static char[] Decode(IntPtr data, uint length)
    {
        if (data == IntPtr.Zero || length is 0 or > MaximumBlobSize) throw new PasswordStoreException();
        var bytes = new byte[checked((int)length)];
        try { Marshal.Copy(data, bytes, 0, bytes.Length); return Utf8.GetChars(bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    private static void ClearNative(IntPtr data, uint length)
    {
        if (data == IntPtr.Zero || length > MaximumBlobSize) return;
        for (int i = 0; i < length; i++) Marshal.WriteByte(data, i, 0);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags, Type;
        [MarshalAs(UnmanagedType.LPWStr)] public string? TargetName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist, AttributeCount;
        public IntPtr Attributes;
        [MarshalAs(UnmanagedType.LPWStr)] public string? TargetAlias;
        [MarshalAs(UnmanagedType.LPWStr)] public string? UserName;
    }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredReadW(string target, uint type, uint flags, out IntPtr credential);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredWriteW(ref Credential credential, uint flags);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredDeleteW(string target, uint type, uint flags);
    [DllImport("advapi32.dll")] private static extern void CredFree(IntPtr credential);
    [DllImport(Security)] private static extern int SecKeychainFindGenericPassword(IntPtr keychain, uint serviceLength, byte[] service, uint accountLength, byte[] account, out uint passwordLength, out IntPtr password, out IntPtr item);
    [DllImport(Security, EntryPoint = "SecKeychainFindGenericPassword")] private static extern int SecKeychainFindGenericPasswordItem(IntPtr keychain, uint serviceLength, byte[] service, uint accountLength, byte[] account, IntPtr passwordLength, IntPtr password, out IntPtr item);
    [DllImport(Security)] private static extern int SecKeychainAddGenericPassword(IntPtr keychain, uint serviceLength, byte[] service, uint accountLength, byte[] account, uint passwordLength, byte[] password, out IntPtr item);
    [DllImport(Security)] private static extern int SecKeychainItemModifyAttributesAndData(IntPtr item, IntPtr attributes, uint length, byte[] data);
    [DllImport(Security)] private static extern int SecKeychainItemDelete(IntPtr item);
    [DllImport(Security)] private static extern int SecKeychainItemFreeContent(IntPtr attributes, IntPtr data);
    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")] private static extern void CFRelease(IntPtr item);
}
