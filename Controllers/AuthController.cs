using DistanceTracker.API.DTOs;
using DistanceTracker.API.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using System.Data.SqlTypes;

namespace DistanceTracker.API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class AuthController : ControllerBase
    {
        private readonly UserManager<ApplicationUser> _userManager;

        public AuthController(UserManager<ApplicationUser> userManager)
        {
            _userManager = userManager;
        }
        private async Task<IdentityResult> PasswordValidatorAsync(string password)
        {
            var passwordValidator = new PasswordValidator<ApplicationUser>();
            return await passwordValidator.ValidateAsync(_userManager, null!, password);
        }
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
                Email = dto.Email
            };
            var result= await _userManager.CreateAsync(user, dto.Password);
            if (result.Succeeded)
            {
                var userDto = new ApplicationUserDTO
                {
                    UserId = Guid.Parse(user.Id),
                    UserName = user.UserName,
                    UserEmail = user.Email
                };
                return Ok(userDto);
            }
            else
            {
                var errors = result.Errors.Select(e => e.Description).ToList();
                return BadRequest(errors);
            }

        }
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
            var userDto = new ApplicationUserDTO
            {
                UserId = Guid.Parse(user.Id),
                UserName = user.UserName,
                UserEmail = user.Email
            };
            return Ok(userDto);
        }


    }
}
