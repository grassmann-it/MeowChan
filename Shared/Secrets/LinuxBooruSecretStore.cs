using System.Runtime.InteropServices;
using MeowChan.Secrets;

namespace MeowChan.PluginHost.Secrets;

internal sealed class LinuxBooruSecretStore : NativeBooruSecretStore
{
    private const string Secret = "libsecret-1.so.0";
    private const string Glib = "libglib-2.0.so.0";
    private const string GObject = "libgobject-2.0.so.0";
    private const string Gio = "libgio-2.0.so.0";
    private const int SearchAll = 2;
    private const int LoadUnlockedSecrets = 8;
    private const int ReplaceItem = 2;

    protected override SecretStoreRead ReadNative(string target, CancellationToken token) =>
        WithCollection(target, token, (collection, attributes, cancel) =>
    {
        byte[]? bytes = null;
        SecretStoreStatus status = WithItem(collection, attributes, SearchAll | LoadUnlockedSecrets, cancel, item =>
        {
            IntPtr value = secret_item_get_secret(item);
            if (value == IntPtr.Zero) return SecretStoreStatus.Unavailable;
            try
            {
                IntPtr data = secret_value_get(value, out nuint length);
                if (data == IntPtr.Zero || length is 0 or > SecretStoreRead.MaximumBytes)
                    return SecretStoreStatus.Unavailable;
                bytes = new byte[(int)length];
                Marshal.Copy(data, bytes, 0, bytes.Length);
                return SecretStoreStatus.Available;
            }
            finally { secret_value_unref(value); }
        });
        return new SecretStoreRead(status, bytes);
    }, static status => new(status));

    protected override SecretStoreStatus WriteNative(string target, byte[] secret, CancellationToken token) =>
        WithCollection(target, token, (collection, attributes, cancel) =>
    {
        IntPtr value = secret_value_new(secret, secret.Length, "application/octet-stream");
        if (value == IntPtr.Zero) return SecretStoreStatus.Unavailable;
        try
        {
            IntPtr item = secret_item_create_sync(collection, IntPtr.Zero, attributes, "MeowChan Booru API key",
                value, ReplaceItem, cancel, out IntPtr error);
            if (item != IntPtr.Zero) g_object_unref(item);
            return StatusOf(error, item == IntPtr.Zero ? SecretStoreStatus.Unavailable : SecretStoreStatus.Available);
        }
        finally { secret_value_unref(value); }
    }, static status => status);

    protected override SecretStoreStatus DeleteNative(string target, CancellationToken token) =>
        WithCollection(target, token, (collection, attributes, cancel) =>
            WithItem(collection, attributes, SearchAll, cancel, item =>
                StatusOfAfterDelete(item, cancel)), static status => status);

    private static SecretStoreStatus StatusOfAfterDelete(IntPtr item, IntPtr cancel)
    {
        int removed = secret_item_delete_sync(item, cancel, out IntPtr error);
        return StatusOf(error, removed != 0 ? SecretStoreStatus.Available : SecretStoreStatus.Unavailable);
    }

    private static T WithCollection<T>(string target, CancellationToken token,
        Func<IntPtr, IntPtr, IntPtr, T> action, Func<SecretStoreStatus, T> failure)
    {
        // Require an existing user bus. Never start a session bus through X11 autolaunch.
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS")))
            return failure(SecretStoreStatus.Unavailable);

        IntPtr cancel = g_cancellable_new();
        try
        {
            using CancellationTokenRegistration registration = token.Register(() => g_cancellable_cancel(cancel));
            IntPtr service = secret_service_open_sync(0, IntPtr.Zero, 0, cancel, out IntPtr error);
            if (service == IntPtr.Zero) return failure(StatusOf(error, SecretStoreStatus.Unavailable));
            try
            {
                IntPtr collection = secret_collection_for_alias_sync(service, "default", 0, cancel, out error);
                if (collection == IntPtr.Zero) return failure(StatusOf(error, SecretStoreStatus.Unavailable));
                try
                {
                    string? path = Marshal.PtrToStringUTF8(g_dbus_proxy_get_object_path(collection));
                    if (path is null or "/org/freedesktop/secrets/collection/session")
                        return failure(SecretStoreStatus.Unavailable);
                    if (secret_collection_get_locked(collection) != 0)
                        return failure(SecretStoreStatus.Locked);
                    return WithAttributes(target, collection, cancel, action);
                }
                finally { g_object_unref(collection); }
            }
            finally { g_object_unref(service); }
        }
        finally { g_object_unref(cancel); }
    }

    private static T WithAttributes<T>(string target, IntPtr collection, IntPtr cancel,
        Func<IntPtr, IntPtr, IntPtr, T> action)
    {
        // With no schema, libsecret serializes this table as D-Bus string pairs.
        IntPtr attributes = g_hash_table_new(IntPtr.Zero, IntPtr.Zero);
        IntPtr name = IntPtr.Zero;
        IntPtr value = IntPtr.Zero;
        try
        {
            name = Marshal.StringToCoTaskMemUTF8("org.meowchan.booru.reference");
            value = Marshal.StringToCoTaskMemUTF8(target);
            g_hash_table_insert(attributes, name, value);
            return action(collection, attributes, cancel);
        }
        finally
        {
            g_hash_table_unref(attributes);
            Marshal.FreeCoTaskMem(value);
            Marshal.FreeCoTaskMem(name);
        }
    }

    private static SecretStoreStatus WithItem(IntPtr collection, IntPtr attributes, int flags, IntPtr cancel,
        Func<IntPtr, SecretStoreStatus> action)
    {
        // Search never requests an unlock. A locked collection or item keeps the request anonymous.
        IntPtr items = secret_collection_search_sync(collection, IntPtr.Zero, attributes, flags, cancel, out IntPtr error);
        if (items == IntPtr.Zero) return StatusOf(error, SecretStoreStatus.Missing);
        try
        {
            IntPtr item = Marshal.ReadIntPtr(items);
            if (Marshal.ReadIntPtr(items, IntPtr.Size) != IntPtr.Zero) return SecretStoreStatus.Unavailable;
            return secret_item_get_locked(item) != 0 ? SecretStoreStatus.Locked : action(item);
        }
        finally
        {
            for (IntPtr node = items; node != IntPtr.Zero; node = Marshal.ReadIntPtr(node, IntPtr.Size))
                g_object_unref(Marshal.ReadIntPtr(node));
            g_list_free(items);
        }
    }

    private static SecretStoreStatus StatusOf(IntPtr error, SecretStoreStatus success)
    {
        if (error == IntPtr.Zero) return success;
        try
        {
            // GError contains a GQuark followed by a gint. Its message stays inside GLib.
            return unchecked((uint)Marshal.ReadInt32(error)) == secret_error_get_quark()
                && Marshal.ReadInt32(error, sizeof(uint)) == 2
                ? SecretStoreStatus.Locked : SecretStoreStatus.Unavailable;
        }
        finally { g_error_free(error); }
    }

    [DllImport(GObject)] private static extern void g_object_unref(IntPtr value);
    [DllImport(Glib)] private static extern void g_error_free(IntPtr value);
    [DllImport(Glib)] private static extern void g_list_free(IntPtr list);
    [DllImport(Glib)] private static extern IntPtr g_hash_table_new(IntPtr hash, IntPtr equal);
    [DllImport(Glib)] private static extern int g_hash_table_insert(IntPtr table, IntPtr key, IntPtr value);
    [DllImport(Glib)] private static extern void g_hash_table_unref(IntPtr table);
    [DllImport(Gio)] private static extern IntPtr g_cancellable_new();
    [DllImport(Gio)] private static extern void g_cancellable_cancel(IntPtr value);
    [DllImport(Gio)] private static extern IntPtr g_dbus_proxy_get_object_path(IntPtr proxy);
    [DllImport(Secret)] private static extern uint secret_error_get_quark();
    [DllImport(Secret)] private static extern IntPtr secret_service_open_sync(nuint type, IntPtr bus, int flags,
        IntPtr cancel, out IntPtr error);
    [DllImport(Secret)] private static extern IntPtr secret_collection_for_alias_sync(IntPtr service,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string alias, int flags, IntPtr cancel, out IntPtr error);
    [DllImport(Secret)] private static extern int secret_collection_get_locked(IntPtr collection);
    [DllImport(Secret)] private static extern IntPtr secret_collection_search_sync(IntPtr collection, IntPtr schema,
        IntPtr attributes, int flags, IntPtr cancel, out IntPtr error);
    [DllImport(Secret)] private static extern IntPtr secret_item_create_sync(IntPtr collection, IntPtr schema,
        IntPtr attributes, [MarshalAs(UnmanagedType.LPUTF8Str)] string label, IntPtr value, int flags,
        IntPtr cancel, out IntPtr error);
    [DllImport(Secret)] private static extern int secret_item_get_locked(IntPtr item);
    [DllImport(Secret)] private static extern int secret_item_delete_sync(IntPtr item, IntPtr cancel, out IntPtr error);
    [DllImport(Secret)] private static extern IntPtr secret_item_get_secret(IntPtr item);
    [DllImport(Secret)] private static extern IntPtr secret_value_new(byte[] secret, nint length,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string contentType);
    [DllImport(Secret)] private static extern IntPtr secret_value_get(IntPtr value, out nuint length);
    [DllImport(Secret)] private static extern void secret_value_unref(IntPtr value);
}
