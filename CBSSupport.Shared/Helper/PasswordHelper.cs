using System.Security.Cryptography;

namespace CBSSupport.Shared.Helpers
{
    public static class PasswordHelper
    {
        private const int Iterations = 350_000;
        private const int SaltSize = 16; 
        private const int KeySize = 32; 


        /// <param name="password">The password to hash.</param>

        public static (string hash, string salt) HashPassword(string password)
        {
            byte[] saltBytes = RandomNumberGenerator.GetBytes(SaltSize);

            var pbkdf2 = new Rfc2898DeriveBytes(password, saltBytes, Iterations, HashAlgorithmName.SHA256);
            byte[] hashBytes = pbkdf2.GetBytes(KeySize);

            return (Convert.ToBase64String(hashBytes), Convert.ToBase64String(saltBytes));
        }

        /// <param name="password">The password to check.</param>
        /// <param name="base64Hash">The stored hash from the 'password_hash' column.</param>
        /// <param name="base64Salt">The stored salt from the 'password_salt' column.</param>
        public static bool VerifyPassword(string password, string base64Hash, string base64Salt)
        {
            try
            {
                byte[] saltBytes = Convert.FromBase64String(base64Salt);

                var pbkdf2 = new Rfc2898DeriveBytes(password, saltBytes, Iterations, HashAlgorithmName.SHA256);
                byte[] hashToCompare = pbkdf2.GetBytes(KeySize);

                byte[] storedHashBytes = Convert.FromBase64String(base64Hash);

                return CryptographicOperations.FixedTimeEquals(hashToCompare, storedHashBytes);
            }
            catch
            {
                return false;
            }
        }
    }
}