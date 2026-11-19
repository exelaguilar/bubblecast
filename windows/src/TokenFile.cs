using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Bubblecast
{
    /// <summary>
    /// Stores the pairing token encrypted with Windows DPAPI (current-user scope), so other users and
    /// offline copies of the profile can't read it. A plain-text token from an earlier version is
    /// accepted once and rewritten encrypted.
    /// </summary>
    public static class TokenFile
    {
        private const string Prefix = "dpapi:";
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Bubblecast pairing token");

        public static string Protect(string token)
        {
            byte[] encrypted = ProtectedData.Protect(Encoding.ASCII.GetBytes(token), Entropy, DataProtectionScope.CurrentUser);
            return Prefix + Convert.ToBase64String(encrypted);
        }

        /// <summary>Reads file contents. <paramref name="needsUpgrade"/> is true for a plain-text token.</summary>
        public static bool TryRead(string contents, out string token, out bool needsUpgrade)
        {
            token = null;
            needsUpgrade = false;
            if (contents == null) return false;
            string trimmed = contents.Trim();
            if (trimmed.StartsWith(Prefix, StringComparison.Ordinal))
            {
                try
                {
                    byte[] decrypted = ProtectedData.Unprotect(Convert.FromBase64String(trimmed.Substring(Prefix.Length)), Entropy, DataProtectionScope.CurrentUser);
                    string value = Encoding.ASCII.GetString(decrypted);
                    if (!TokenValidation.IsValid(value)) return false;
                    token = value;
                    return true;
                }
                catch (FormatException) { return false; }
                catch (CryptographicException) { return false; }
            }
            if (TokenValidation.IsValid(contents))
            {
                token = contents;
                needsUpgrade = true;
                return true;
            }
            return false;
        }

        public static string LoadOrCreate(string path)
        {
            if (File.Exists(path))
            {
                string token;
                bool needsUpgrade;
                if (!TryRead(File.ReadAllText(path, Encoding.ASCII), out token, out needsUpgrade))
                    throw new InvalidDataException("The pairing token file can't be read. Delete token.txt in %LOCALAPPDATA%\\Bubblecast to generate a new pairing token, then update the Mac.");
                if (needsUpgrade) Write(path, token);
                return token;
            }
            string created = NewToken();
            Write(path, created);
            return created;
        }

        public static string NewToken()
        {
            byte[] bytes = new byte[32];
            using (RandomNumberGenerator rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
        }

        /// <summary>Writes the token encrypted, replacing any existing file atomically.</summary>
        public static void Write(string path, string token)
        {
            string temporaryPath = path + ".tmp";
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            File.WriteAllText(temporaryPath, Protect(token), Encoding.ASCII);
            if (File.Exists(path)) File.Replace(temporaryPath, path, null);
            else File.Move(temporaryPath, path);
        }
    }
}
