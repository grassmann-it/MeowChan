using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using MeowChan.Secrets;

namespace MeowChan.PluginHost.Secrets;

internal sealed class MacBooruSecretStore : NativeBooruSecretStore
{
    private const string Security = "/System/Library/Frameworks/Security.framework/Security";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const uint Utf8 = 0x08000100;
    private const int DuplicateItem = -25299;

    protected override SecretStoreRead ReadNative(string target, CancellationToken token)
    {
        using Query query = new(target);
        if (query.Status != SecretStoreStatus.Available) return new(query.Status);

        query.Set("kSecReturnData", BooleanTrue);
        int status = SecItemCopyMatching(query.Value, out IntPtr result);
        using CfHandle data = new(result);
        if (status != 0) return new(StatusOf(status));

        nint length = CFDataGetLength(result);
        if (length is <= 0 or > SecretStoreRead.MaximumBytes) return new(SecretStoreStatus.Unavailable);
        byte[] secret = new byte[(int)length];
        Marshal.Copy(CFDataGetBytePtr(result), secret, 0, secret.Length);
        return new(SecretStoreStatus.Available, secret);
    }

    protected override SecretStoreStatus WriteNative(string target, byte[] secret, CancellationToken token)
    {
        using Query query = new(target);
        if (query.Status != SecretStoreStatus.Available) return query.Status;
        using CfHandle data = new(CFDataCreate(IntPtr.Zero, secret, secret.Length));
        if (data.IsInvalid) return SecretStoreStatus.Unavailable;

        query.Set("kSecValueData", data.DangerousGetHandle());
        query.Set("kSecUseKeychain", query.Keychain);
        query.Remove("kSecMatchSearchList");
        int status = SecItemAdd(query.Value, IntPtr.Zero);
        if (status != DuplicateItem) return StatusOf(status);

        query.Remove("kSecValueData");
        query.Remove("kSecUseKeychain");
        query.MatchKeychain();
        using CfHandle update = Dictionary();
        if (update.IsInvalid) return SecretStoreStatus.Unavailable;
        CFDictionarySetValue(update.DangerousGetHandle(), Symbol(Security, "kSecValueData"), data.DangerousGetHandle());
        return StatusOf(SecItemUpdate(query.Value, update.DangerousGetHandle()));
    }

    protected override SecretStoreStatus DeleteNative(string target, CancellationToken token)
    {
        using Query query = new(target);
        return query.Status == SecretStoreStatus.Available ? StatusOf(SecItemDelete(query.Value)) : query.Status;
    }

    private static SecretStoreStatus StatusOf(int status) => status switch
    {
        0 => SecretStoreStatus.Available,
        -25300 => SecretStoreStatus.Missing,  // errSecItemNotFound
        -25293 or -25308 or -25315 => SecretStoreStatus.Locked,
        _ => SecretStoreStatus.Unavailable
    };

    private sealed class Query : IDisposable
    {
        private readonly CfHandle _dictionary;
        private readonly CfHandle _keychain = new();
        private readonly CfHandle _searchList = new();
        private readonly CfHandle _service = new();
        private readonly CfHandle _account = new();

        internal IntPtr Value => _dictionary.DangerousGetHandle();
        internal IntPtr Keychain => _keychain.DangerousGetHandle();
        internal SecretStoreStatus Status { get; }

        internal Query(string target)
        {
            _dictionary = Dictionary();
            try
            {
                int status = SecKeychainCopyDefault(out IntPtr keychain);
                _keychain.Assign(keychain);
                Status = StatusOf(status);
                if (Status != SecretStoreStatus.Available) return;
                status = SecKeychainGetStatus(keychain, out uint flags);
                Status = status != 0 ? StatusOf(status)
                    : (flags & 1) == 0 ? SecretStoreStatus.Locked : SecretStoreStatus.Available;
                if (Status != SecretStoreStatus.Available) return;

                _searchList.Assign(CFArrayCreate(IntPtr.Zero, [keychain], 1, FoundationExport("kCFTypeArrayCallBacks")));
                _service.Assign(CFStringCreateWithCString(IntPtr.Zero, "MeowChan Booru API key\0"u8.ToArray(), Utf8));
                _account.Assign(CFStringCreateWithCString(IntPtr.Zero, Encoding.UTF8.GetBytes(target + "\0"), Utf8));
                if (_dictionary.IsInvalid || _searchList.IsInvalid || _service.IsInvalid || _account.IsInvalid)
                {
                    Status = SecretStoreStatus.Unavailable;
                    return;
                }

                Set("kSecClass", Symbol(Security, "kSecClassGenericPassword"));
                Set("kSecAttrService", _service.DangerousGetHandle());
                Set("kSecAttrAccount", _account.DangerousGetHandle());
                Set("kSecUseAuthenticationUI", Symbol(Security, "kSecUseAuthenticationUIFail"));
                MatchKeychain();
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        internal void MatchKeychain() => Set("kSecMatchSearchList", _searchList.DangerousGetHandle());
        internal void Set(string name, IntPtr value) => CFDictionarySetValue(Value, Symbol(Security, name), value);
        internal void Remove(string name) => CFDictionaryRemoveValue(Value, Symbol(Security, name));

        public void Dispose()
        {
            _dictionary.Dispose();
            _searchList.Dispose();
            _keychain.Dispose();
            _service.Dispose();
            _account.Dispose();
        }
    }

    private sealed class CfHandle() : SafeHandleZeroOrMinusOneIsInvalid(ownsHandle: true)
    {
        internal CfHandle(IntPtr value) : this() => SetHandle(value);
        internal void Assign(IntPtr value) => SetHandle(value);
        protected override bool ReleaseHandle()
        {
            CFRelease(handle);
            return true;
        }
    }

    private static readonly Lazy<IntPtr> SecurityLibrary = new(() => NativeLibrary.Load(Security));
    private static readonly Lazy<IntPtr> FoundationLibrary = new(() => NativeLibrary.Load(CoreFoundation));

    private static IntPtr Symbol(string library, string name) =>
        Marshal.ReadIntPtr(NativeLibrary.GetExport(library == Security ? SecurityLibrary.Value : FoundationLibrary.Value, name));

    private static IntPtr BooleanTrue => Symbol(CoreFoundation, "kCFBooleanTrue");

    private static IntPtr FoundationExport(string name) => NativeLibrary.GetExport(FoundationLibrary.Value, name);

    private static CfHandle Dictionary() => new(CFDictionaryCreateMutable(IntPtr.Zero, 0,
        FoundationExport("kCFTypeDictionaryKeyCallBacks"), FoundationExport("kCFTypeDictionaryValueCallBacks")));

    [DllImport(Security)]
    private static extern int SecKeychainCopyDefault(out IntPtr keychain);
    [DllImport(Security)]
    private static extern int SecKeychainGetStatus(IntPtr keychain, out uint flags);
    [DllImport(Security)]
    private static extern int SecItemCopyMatching(IntPtr query, out IntPtr result);
    [DllImport(Security)]
    private static extern int SecItemAdd(IntPtr query, IntPtr result);
    [DllImport(Security)]
    private static extern int SecItemUpdate(IntPtr query, IntPtr update);
    [DllImport(Security)]
    private static extern int SecItemDelete(IntPtr query);
    [DllImport(CoreFoundation)]
    private static extern IntPtr CFDictionaryCreateMutable(IntPtr allocator, nint capacity, IntPtr keys, IntPtr values);
    [DllImport(CoreFoundation)]
    private static extern void CFDictionarySetValue(IntPtr dictionary, IntPtr key, IntPtr value);
    [DllImport(CoreFoundation)]
    private static extern void CFDictionaryRemoveValue(IntPtr dictionary, IntPtr key);
    [DllImport(CoreFoundation)]
    private static extern IntPtr CFArrayCreate(IntPtr allocator, IntPtr[] values, nint count, IntPtr callbacks);
    [DllImport(CoreFoundation)]
    private static extern IntPtr CFStringCreateWithCString(IntPtr allocator, byte[] text, uint encoding);
    [DllImport(CoreFoundation)]
    private static extern IntPtr CFDataCreate(IntPtr allocator, byte[] data, nint length);
    [DllImport(CoreFoundation)]
    private static extern nint CFDataGetLength(IntPtr data);
    [DllImport(CoreFoundation)]
    private static extern IntPtr CFDataGetBytePtr(IntPtr data);
    [DllImport(CoreFoundation)]
    private static extern void CFRelease(IntPtr value);
}
