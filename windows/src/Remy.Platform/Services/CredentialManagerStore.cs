using System.Runtime.InteropServices;
using System.Text;
using Remy.Core.Contracts;
using Remy.Platform.Native;

namespace Remy.Platform.Services;

/// <summary>Windows Credential Manager generic credentials, target "Remy/&lt;service&gt;/&lt;account&gt;", UTF-16 blob.</summary>
public sealed class CredentialManagerStore : ICredentialStore
{
    public const string SaveFailedMessage = "Could not save the API key to Credential Manager. Try entering it again.";
    const int ErrorNotFound = 1168;

    public static string TargetName(string service, string account) => $"Remy/{service}/{account}";

    public string? Read(string service, string account)
    {
        if (!NativeMethods.CredReadW(TargetName(service, account), NativeMethods.CRED_TYPE_GENERIC, 0, out var ptr)) return null;
        try
        {
            var cred = Marshal.PtrToStructure<NativeMethods.CREDENTIAL>(ptr);
            if (cred.CredentialBlob == 0 || cred.CredentialBlobSize == 0) return "";
            return Marshal.PtrToStringUni(cred.CredentialBlob, (int)cred.CredentialBlobSize / 2);
        }
        finally { NativeMethods.CredFree(ptr); }
    }

    public void Write(string service, string account, string? secret)
    {
        if (string.IsNullOrWhiteSpace(secret)) { Delete(service, account); return; }

        var blob = Encoding.Unicode.GetBytes(secret);
        var blobPtr = Marshal.AllocHGlobal(blob.Length);
        var targetPtr = Marshal.StringToHGlobalUni(TargetName(service, account));
        var userPtr = Marshal.StringToHGlobalUni(account);
        try
        {
            Marshal.Copy(blob, 0, blobPtr, blob.Length);
            var cred = new NativeMethods.CREDENTIAL
            {
                Type = NativeMethods.CRED_TYPE_GENERIC,
                TargetName = targetPtr,
                CredentialBlobSize = (uint)blob.Length,
                CredentialBlob = blobPtr,
                Persist = NativeMethods.CRED_PERSIST_LOCAL_MACHINE,
                UserName = userPtr,
            };
            if (!NativeMethods.CredWriteW(ref cred, 0)) throw new InvalidOperationException(SaveFailedMessage);
        }
        finally
        {
            Array.Clear(blob);
            Marshal.FreeHGlobal(blobPtr);
            Marshal.FreeHGlobal(targetPtr);
            Marshal.FreeHGlobal(userPtr);
        }
    }

    public void Delete(string service, string account)
    {
        if (!NativeMethods.CredDeleteW(TargetName(service, account), NativeMethods.CRED_TYPE_GENERIC, 0)
            && Marshal.GetLastWin32Error() != ErrorNotFound)
            throw new InvalidOperationException(SaveFailedMessage);
    }
}
