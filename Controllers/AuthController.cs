using DistanceTracker.API.DTOs;
using DistanceTracker.API.Models;
using DistanceTracker.API.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using System.Data.SqlTypes;
using System.Security.Claims;

namespace DistanceTracker.API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class AuthController : ControllerBase
    {
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly JwtAuth _jwtAuth;
        public AuthController(UserManager<ApplicationUser> userManager, JwtAuth jwtAuth)
        {
            _userManager = userManager;
            _jwtAuth = jwtAuth;
        }
        private async Task<IdentityResult> PasswordValidatorAsync(string password)
        {
            var passwordValidator = new PasswordValidator<ApplicationUser>();
            return await passwordValidator.ValidateAsync(_userManager, null!, password);
        }

        [EnableRateLimiting("RegisterPolicy")]
        [HttpPost("register")]
        public async Task<IActionResult> Register(RegisterUserDTO dto)
        {
            IdentityResult validation = await PasswordValidatorAsync(dto.Password);
            if (!validation.Succeeded)
            {
                var errors = validation.Errors.Select(e => e.Description).ToList();
                return BadRequest(errors);
            }
            var user = new ApplicationUser
            {
                UserName = dto.UserName,
                Email = dto.Email,
                Trips = []
            };
            var result= await _userManager.CreateAsync(user, dto.Password);
            var token = _jwtAuth.Create(user);
            if (result.Succeeded)
            {
                var userDto = new AuthResponseDTO
                {
                    User = new ApplicationUserDTO
                    {
                        UserId = Guid.Parse(user.Id),
                        UserName = user.UserName,
                        UserEmail = user.Email
                    },
                    Token=token

                };
                return Ok(userDto);
            }
            else
            {
                var errors = result.Errors.Select(e => e.Description).ToList();
                return BadRequest(errors);
            }

        }
        [EnableRateLimiting("LoginPolicy")]
        [HttpPost("login")]
        public async Task<IActionResult> Login(LoginUserDTO dto)
        {
            var user = await _userManager.FindByEmailAsync(dto.Email);
            if (user == null)
            {
                return Unauthorized("Invalid email or password.");
            }
            var passwordValid = await _userManager.CheckPasswordAsync(user, dto.Password);
            if (!passwordValid)
            {
                return Unauthorized("Invalid email or password.");
            }
            var token = _jwtAuth.Create(user);
            var userDto = new AuthResponseDTO
            {
                User= new ApplicationUserDTO{
                    UserId = Guid.Parse(user.Id),
                    UserName = user.UserName,
                    UserEmail = user.Email,
                },
                Token= token
            };
            return Ok(userDto);
        }


    }
}
