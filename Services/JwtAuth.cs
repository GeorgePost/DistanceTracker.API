using DistanceTracker.API.Models;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;

namespace DistanceTracker.API.Services
{
    public class JwtAuth
    {
        private readonly IConfiguration _Configuration;
        public JwtAuth(IConfiguration configuration)
        {
            _Configuration = configuration;
        }
        public string Create(ApplicationUser user)
        {
            var handler = new JwtSecurityTokenHandler();
            var privateKey = Encoding.UTF8.GetBytes(_Configuration["Jwt:SigningKey"]);
            var credentials = new SigningCredentials(
                new SymmetricSecurityKey(privateKey),
                SecurityAlgorithms.HmacSha256);
            var tokenDescriptor = new SecurityTokenDescriptor
            {
                SigningCredentials = credentials,
                Expires = DateTime.UtcNow.AddHours(1),
                Subject = GenerateClaims(user),
                Audience=_Configuration["Jwt:Audience"],
                Issuer=_Configuration["Jwt:Issuer"]
            };
            var token = handler.CreateToken(tokenDescriptor);
            return handler.WriteToken(token);

        }
        public static ClaimsIdentity GenerateClaims(ApplicationUser user)
        {
            var ci = new ClaimsIdentity();
            ci.AddClaim(new Claim(ClaimTypes.NameIdentifier, user.Id));
            ci.AddClaim(new Claim(ClaimTypes.Name, user.UserName));
            ci.AddClaim(new Claim(ClaimTypes.Email, user.Email));
            return ci;
        }
    }
}
