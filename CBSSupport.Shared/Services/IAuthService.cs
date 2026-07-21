using CBSSupport.Shared.Models;
using System.Threading.Tasks;

namespace CBSSupport.Shared.Services
{
    public interface IAuthService
    {
        Task<AdminUser?> ValidateUserAsync(string username, string password);

        Task<ClientUser?> ValidateClientUserAsync(long clientCode, string username, string password);

        Task<AdminUserDto?> GetAdminUserByIdAsync(long userId);
    }
}
