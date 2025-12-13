using DistanceTracker.API.DTOs;
using DistanceTracker.API.Models;
using DistanceTracker.API.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using DistanceTracker.API.Services;
using Microsoft.AspNetCore.Authorization;

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
        public TripsController(DistanceTrackerContext context, IGeocodingService geocodingService, IDistanceService distanceService)
        {
            _context = context;
            _geocodingService = geocodingService;
            _distanceService = distanceService;
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
                    DistanceToNext = null,
                    Order = i,
                };
                trip.TripStops.Add(tripStop);
            }
            var latitudeLongitudeList = trip.TripStops
                .Select(s => (s.Latitude, s.Longitude))
                .ToList();
            var distances = await _distanceService.CalculateRouteDistancesAsync(latitudeLongitudeList);
            decimal totalDistance = 0;
            for(int i=0; i< distances.Count; i++)
            {
                trip.TripStops[i].DistanceToNext = distances[i];
                totalDistance += distances[i];
            }
            trip.TotalDistance = totalDistance;
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
