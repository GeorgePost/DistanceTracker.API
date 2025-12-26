using DistanceTracker.API.DTOs;
using DistanceTracker.API.Models;
using DistanceTracker.API.Services;
using DistanceTracker.API.Services.Email;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;


namespace DistanceTracker.API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class AuthController : ControllerBase
    {
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly JwtAuth _jwtAuth;
        private readonly IEmailService _emailService;
        public AuthController(UserManager<ApplicationUser> userManager, JwtAuth jwtAuth, IEmailService emailService)
        {
            _userManager = userManager;
            _jwtAuth = jwtAuth;
            _emailService = emailService;
        }
        private async Task<IdentityResult> PasswordValidatorAsync(string password)
        {
            var passwordValidator = new PasswordValidator<ApplicationUser>();
            var tempUser = new ApplicationUser();
            return await passwordValidator.ValidateAsync(_userManager, tempUser, password);
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
                Email = dto.Email.Trim().ToLowerInvariant(),
                Trips = [],
                EmailConfirmed = false,
            };
            var result= await _userManager.CreateAsync(user, dto.Password);
            //var token = _jwtAuth.Create(user);
            if (result.Succeeded)
            {
                var token = await  _userManager.GenerateEmailConfirmationTokenAsync(user);
                await _emailService.SendEmailConfirmationAsync(
                    user,
                    token
                );
                return Ok("If the Email exists, a confirmation has been sent.");
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
            dto.Email = dto.Email.Trim().ToLowerInvariant();
            var user = await _userManager.FindByEmailAsync(dto.Email);
            if (user == null)
            {
                return Unauthorized("Invalid email or password.");
            }
            if(!user.EmailConfirmed)
            {
                return Unauthorized("Email not confirmed. Please confirm your email before logging in.");
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
        [EnableRateLimiting("EmailPolicy")]
        [HttpPost("forgot-password")]
        [AllowAnonymous]
        public async Task<IActionResult> ForgotPassword(ForgotPasswordDTO Dto)
        {
            Dto.Email = Dto.Email.Trim().ToLowerInvariant();
            var user = await _userManager.FindByEmailAsync(Dto.Email);

            if (user != null)
            {
                var token = await _userManager.GeneratePasswordResetTokenAsync(user);

                // TODO: Send token via email (SendGrid / Azure Email)
                // For now: log or temporarily return it in dev
                await _emailService.SendEmailAsync(
                    user.Email!,
                    "Reset your password",
                    $"Your reset token:\n\n{token}"
                );
            }

            // Always return OK to prevent account enumeration
            return Ok("A confirmation email has been sent.");
        }
        [EnableRateLimiting("EmailPolicy")]
        [HttpPost("reset-password")]
        [AllowAnonymous]
        public async Task<IActionResult> ResetPassword(ResetPasswordDTO dto)
        {
            dto.Email = dto.Email.Trim().ToLowerInvariant();
            var user = await _userManager.FindByEmailAsync(dto.Email);
            if (user == null)
            {
                return BadRequest("Invalid email or token.");
            }
            var result = await _userManager.ResetPasswordAsync(user, dto.Token, dto.NewPassword);
            //To do: Send confirmation email
            if (result.Succeeded)
            {
                return Ok("Password reset successful.");
            }
            return BadRequest("Invalid email or token.");
        }
        [EnableRateLimiting("EmailPolicy")]
        [HttpPost("confirm-email")]
        [AllowAnonymous]
        public async Task<IActionResult> ConfirmEmail(ConfirmEmailDTO dto)
        {
            var user = await _userManager.FindByIdAsync(dto.UserId);
            if (user == null)
            {
                return BadRequest("Invalid email or token.");
            }
            var result = await _userManager.ConfirmEmailAsync(user, dto.Token);
            if (result.Succeeded)
            {
                return Ok("Email confirmed successfully.");
            }
            return BadRequest("Invalid email or token.");
        }

    }
}
