using DistanceTracker.API.Data;
using DistanceTracker.API.DTOs;
using DistanceTracker.API.Models;
using DistanceTracker.API.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace DistanceTracker.API.Controllers
{
    [ApiController]
    [Authorize]
    [Route("api/[controller]")]
    public class TripsController : ControllerBase
    {
        private readonly DistanceTrackerContext _context;
        private readonly IGeocodingService _geocodingService;
        private readonly IDistanceService _distanceService;
        private readonly ITripCalculationPolicy _calcPolicy;
        public TripsController(DistanceTrackerContext context, IGeocodingService geocodingService, IDistanceService distanceService, ITripCalculationPolicy calcPolicy)
        {
            _context = context;
            _geocodingService = geocodingService;
            _distanceService = distanceService;
            _calcPolicy = calcPolicy;
        }

        // POST: TripsController/Ct
        [EnableRateLimiting("TripsWritePolicy")]
        [HttpPost]
        public async Task<ActionResult<Trip>> CreateTrip(CreateTripDto dto)
        {
            var tripId = Guid.NewGuid();
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (userId == null)
            {
                return Unauthorized();
            }
            var trip = new Trip
            {
                Id = tripId,
                DateUTC = dto.Date.ToUniversalTime(),
                TotalDistance = 0,
                TripStops = new List<TripStop>(),
                UserId = userId
            };
            for (int i = 0; i < dto.Stops.Count; i++)
            {
                var address = dto.Stops[i].Trim().ToLowerInvariant();
                var (latitude, longitude) = await _geocodingService.GeocodeAddressAsync(address);
                var tripStop = new TripStop
                {
                    Id = Guid.NewGuid(),
                    TripId = tripId,
                    Address = dto.Stops[i],
                    Latitude = latitude,
                    Longitude = longitude,
                    DistanceToNext = null,
                    Order = i,
                };
                trip.TripStops.Add(tripStop);
            }
            //var latitudeLongitudeList = trip.TripStops
            //    .Select(s => (s.Latitude, s.Longitude))
            //    .ToList();
            //var distances = await _distanceService.CalculateRouteDistancesAsync(latitudeLongitudeList);
            //decimal totalDistance = 0;
            //for (int i = 0; i < distances.Count; i++)
            //{
            //    trip.TripStops[i].DistanceToNext = distances[i];
            //    totalDistance += distances[i];
            //}
            //trip.TotalDistance = totalDistance;
            _context.Trips.Add(trip);
            await _context.SaveChangesAsync();
            var response = new TripResponseDTO
            {
                Id = trip.Id,
                Date = trip.DateUTC,
                TotalDistance = trip.TotalDistance,
                Notes = trip.Notes,
                Stops = trip.TripStops.Select(s => new TripStopDTO
                {
                    Id = s.Id,
                    Address = s.Address,
                    Latitude = s.Latitude,
                    Longitude = s.Longitude,
                    Order = s.Order,
                    DistanceToNext = s.DistanceToNext
                }).ToList(),

            };
            return CreatedAtAction(nameof(GetTrip), new { id = trip.Id }, response);
        }
        [EnableRateLimiting("TripsWritePolicy")]
        [HttpPost("{id}/calculate")]
        public async Task<ActionResult<Trip>> CalculateTrip(Guid id)
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            var user = await _context.Users.FirstOrDefaultAsync(u => u.Id == userId);
            if(user == null)
            {
                return Unauthorized();
            }
            try
            {
                await _calcPolicy.EnsureCanCalculateAsync(user);

                var trip = await _context.Trips
                    .Include(t => t.TripStops)
                    .FirstOrDefaultAsync(t => t.Id == id && t.UserId == userId);
                if (trip == null)
                {
                    return NotFound();
                }
                var latitudeLongitudeList = trip.TripStops
                    .Select(s => (s.Latitude, s.Longitude))
                    .ToList();
                var distances = await _distanceService.CalculateRouteDistancesAsync(latitudeLongitudeList);
                decimal totalDistance = 0;
                for (int i = 0; i < distances.Count; i++)
                {
                    trip.TripStops[i].DistanceToNext = distances[i];
                    totalDistance += distances[i];
                }
                trip.TotalDistance = totalDistance;
                trip.LastCalculatedAtUTC = DateTime.UtcNow;
                await _context.SaveChangesAsync();
            }
            catch
            {
                return StatusCode(403, "Calculation limit reached. Please try again later.");
            }
            return NoContent();
        }
        [HttpGet("{id}")]
        public async Task<ActionResult<TripResponseDTO>> GetTrip(Guid id)
        {
            var trip = await _context.Trips
                .Include(t => t.TripStops)
                .FirstOrDefaultAsync(t => t.Id == id);

            if (trip == null)
            {
                return NotFound();
            }
            var UserId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (trip.UserId != UserId)
            {
                return Forbid();
            }
            var response = new TripResponseDTO
            {
                Id = trip.Id,
                Date = trip.DateUTC,
                TotalDistance = trip.TotalDistance,
                Notes = trip.Notes,
                Stops = trip.TripStops.Select(s => new TripStopDTO
                {
                    Id = s.Id,
                    Address = s.Address,
                    Latitude = s.Latitude,
                    Longitude = s.Longitude,
                    Order = s.Order,
                    DistanceToNext = s.DistanceToNext
                }).ToList()
            };
            return response;
        }
        [HttpGet]
        public async Task<ActionResult<List<TripResponseDTO>>> GetTrips(DateTime? StartDate, DateTime? EndDate)
        {
            var UserId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            List<Trip> trips;
            if (StartDate.HasValue ^ EndDate.HasValue)
            {
                return BadRequest("Both StartDate and EndDate must be provided together.");
            }
            if (StartDate!=null && EndDate!=null)
            {
                if(StartDate>= EndDate)
                {
                    return BadRequest("StartDate must be earlier than EndDate.");
                }
                if (StartDate?.Kind != DateTimeKind.Utc || EndDate?.Kind != DateTimeKind.Utc)
                {
                    return BadRequest("Dates must be in UTC format.");
                }
                var startUtc = StartDate.Value.ToUniversalTime();
                var endUtc = EndDate.Value.ToUniversalTime();
                trips = await _context.Trips
                .Where(t => t.UserId == UserId)
                .Where(t=> t.DateUTC >= startUtc)
                .Where(t=> t.DateUTC <= endUtc)
                .OrderByDescending(t=> t.DateUTC)
                .Include(t => t.TripStops)
                .ToListAsync();
            }
            else
            {
                trips = await _context.Trips
                .Where(t => t.UserId == UserId)
                .OrderByDescending(t => t.DateUTC)
                .Include(t => t.TripStops)
                .ToListAsync();
            }
                
            
            var response = trips.Select(trip => new TripResponseDTO
            {
                Id = trip.Id,
                Date = trip.DateUTC,
                TotalDistance = trip.TotalDistance,
                Notes = trip.Notes,
                Stops = trip.TripStops.Select(s => new TripStopDTO
                {
                    Id = s.Id,
                    Address = s.Address,
                    Latitude = s.Latitude,
                    Longitude = s.Longitude,
                    Order = s.Order,
                    DistanceToNext = s.DistanceToNext
                }).ToList()
            }).ToList();
            return response;
        }
    }
}
