using DistanceTracker.API.DTOs;
using DistanceTracker.API.Models;
using DistanceTracker.API.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using DistanceTracker.API.Services;

namespace DistanceTracker.API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class TripsController : ControllerBase
    {
        private readonly DistanceTrackerContext _context;
        private readonly IGeocodingService _geocodingService;
        public TripsController(DistanceTrackerContext context, IGeocodingService geocodingService)
        {
            _context = context;
            _geocodingService = geocodingService;
        }

        // POST: TripsController/Ct
        [HttpPost]
        public async Task<ActionResult<Trip>>   CreateTrip(CreateTripDto dto)
        {
            var tripId= Guid.NewGuid();
            var trip = new Trip
            {
                Id = tripId,
                Date = dto.Date,
                TotalDistance = 0,
                TripStops = new List<TripStop>(),
            };
            for(int i=0; i< dto.Stops.Count; i++)
            {
                var address = dto.Stops[i];
                var (latitude, longitude) = await _geocodingService.GeocodeAddressAsync(address);
                var tripStop = new TripStop
                {
                    Id = Guid.NewGuid(),
                    TripId = tripId,
                    Address = dto.Stops[i],
                    Latitude = latitude,
                    Longitude = longitude,
                    Order = i,
                };
                trip.TripStops.Add(tripStop);
            }
            _context.Trips.Add(trip);
            await _context.SaveChangesAsync();
            var response = new TripResponseDTO
            {
                Id = trip.Id,
                Date = trip.Date,
                TotalDistance = trip.TotalDistance,
                Notes = trip.Notes,
                Stops = trip.TripStops.Select(s=> new TripStopDTO
                {
                    Id = s.Id,
                    Address = s.Address,
                    Latitude = s.Latitude,
                    Longitude = s.Longitude,
                    Order = s.Order,
                    DistanceToNext = s.DistanceToNext
                }).ToList()
            };
            return CreatedAtAction(nameof(GetTrip), new { id = trip.Id }, response);
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
            var response = new TripResponseDTO
            {
                Id = trip.Id,
                Date = trip.Date,
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
    }
}
