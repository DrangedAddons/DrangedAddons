using System.Security.Cryptography;
using System.Text;

namespace DrangedAddons;

/// <summary>
/// The user's GitHub token, encrypted with Windows (DPAPI, current user): only this Windows account on this PC can
/// read it back. Kept in %AppData%\DrangedAddons\github-token.bin.
/// </summary>
public static class TokenStore
{
    const string FileName = "github-token.bin";
    static readonly byte[] Entropy = Encoding.UTF8.GetBytes("DrangedAddons GitHub token");
    static string FilePath => Path.Combine(Storage.Dir, FileName);

    public static string? Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            var bytes = ProtectedData.Unprotect(File.ReadAllBytes(FilePath), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return null; // unreadable (e.g. copied from another PC): behave as if there's none
        }
    }

    public static void Save(string token)
    {
        Directory.CreateDirectory(Storage.Dir);
        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(token.Trim()), Entropy, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(FilePath, bytes);
    }

    public static void Remove()
    {
        if (File.Exists(FilePath)) File.Delete(FilePath);
    }
}
