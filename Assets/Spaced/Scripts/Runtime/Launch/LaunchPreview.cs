using System.Collections.Generic;
using System.Threading.Tasks;
using Unity.Mathematics;

namespace Spaced
{
    /// <summary>
    /// A "launch now" flight worked out before it flies: the rocket's track from the pad to payload release and the
    /// orbit the satellite is then put in, exactly as <see cref="LaunchManager"/> will deploy it.
    /// </summary>
    public sealed class LaunchPath
    {
        public double LiftoffJd;
        /// <summary>Payload release; <see cref="Final"/> and the release state are valid then.</summary>
        public double ReleaseJd;
        /// <summary>The orbit the satellite is put in (it raises itself there first if the rocket drops it low).</summary>
        public OrbitElements Final;
        /// <summary>Upper stage position and velocity at release (inertial, km and km/s).</summary>
        public double3 ReleasePositionKm, ReleaseVelocityKmS;
        /// <summary>The rocket over the ground, pad to release (Earth-fixed, km), one sample every <see cref="LaunchPreview.SampleSeconds"/>.</summary>
        public double3[] AscentEcef;
        public bool RaisesOrbit;

        /// <summary>
        /// The same flight lifting off at another time. Relative to the turning Earth a flight doesn't depend on when it
        /// starts, so in space everything just turns with the planet.
        /// </summary>
        public LaunchPath At(double liftoffJd)
        {
            if (liftoffJd == LiftoffJd) return this;
            double dt = (liftoffJd - LiftoffJd) * MathConst.SecondsPerDay;
            double turn = EarthConstants.RotationRate * dt;
            var path = (LaunchPath)MemberwiseClone();
            path.LiftoffJd = liftoffJd;
            path.ReleaseJd = ReleaseJd + dt / MathConst.SecondsPerDay;
            path.Final.Raan = OrbitMath.WrapAngle(Final.Raan + turn);
            path.ReleasePositionKm = RotateZ(ReleasePositionKm, turn);
            path.ReleaseVelocityKmS = RotateZ(ReleaseVelocityKmS, turn);
            return path;
        }

        static double3 RotateZ(double3 v, double angle)
        {
            math.sincos(angle, out double s, out double c);
            return new double3(c * v.x - s * v.y, s * v.x + c * v.y, v.z);
        }
    }

    /// <summary>
    /// Where a launch being planned will end up, before it flies. "Launch now" flights are flown through the real
    /// <see cref="RocketFlight"/> simulation (in the background, ~40 ms) and remembered, so the preview, the ground
    /// track and a fast-forwarded launch all agree with a watched one.
    /// </summary>
    public static class LaunchPreview
    {
        public const double SampleSeconds = 20;
        const int MaxCached = 64;

        static readonly Dictionary<string, LaunchPath> Cache = new Dictionary<string, LaunchPath>();
        static readonly object Lock = new object();
        static Task _running;

        static string Key(LaunchVehicle vehicle, LaunchSite site, double payloadKg, double perigeeKm, double apogeeKm, double inclinationDeg) =>
            $"{vehicle.Kind}/{vehicle.Recovery}/{site.ShortName}/{math.round(payloadKg / 10)}/{math.round(perigeeKm)}/{math.round(apogeeKm)}/{math.round(inclinationDeg * 100)}";

        /// <summary>
        /// The flight, if it has been worked out already; otherwise starts working it out in the background and returns
        /// null (ask again next frame).
        /// </summary>
        public static LaunchPath FlyAsync(LaunchVehicle vehicle, LaunchSite site, double payloadKg, double perigeeKm, double apogeeKm,
            double inclinationDeg, double liftoffJd)
        {
            string key = Key(vehicle, site, payloadKg, perigeeKm, apogeeKm, inclinationDeg);
            lock (Lock)
            {
                if (Cache.TryGetValue(key, out var cached)) return cached?.At(liftoffJd);
                if (_running != null && !_running.IsCompleted) return null;
                _running = Task.Run(() => Fly(vehicle, site, payloadKg, perigeeKm, apogeeKm, inclinationDeg, liftoffJd));
                return null;
            }
        }

        /// <summary>The flight (worked out now if needed); null if the rocket can't deliver it.</summary>
        public static LaunchPath Fly(LaunchVehicle vehicle, LaunchSite site, double payloadKg, double perigeeKm, double apogeeKm,
            double inclinationDeg, double liftoffJd)
        {
            string key = Key(vehicle, site, payloadKg, perigeeKm, apogeeKm, inclinationDeg);
            lock (Lock)
                if (Cache.TryGetValue(key, out var cached)) return cached?.At(liftoffJd);

            var path = Simulate(vehicle, site, payloadKg, perigeeKm, apogeeKm, inclinationDeg, liftoffJd);
            lock (Lock)
            {
                if (Cache.Count >= MaxCached) Cache.Clear();
                Cache[key] = path;
            }
            return path;
        }

        static LaunchPath Simulate(LaunchVehicle vehicle, LaunchSite site, double payloadKg, double perigeeKm, double apogeeKm,
            double inclinationDeg, double liftoffJd)
        {
            // Same plane and target as LaunchManager gives a "launch now" mission (it aims at the mean altitude).
            double3 normal = LaunchPlanner.PlaneForImmediateLaunch(site, inclinationDeg, liftoffJd, out _);
            double targetKm = 0.5 * (perigeeKm + apogeeKm);
            var flight = new RocketFlight(vehicle, site, liftoffJd, payloadKg, targetKm, normal);

            var samples = new List<double3>();
            while (flight.MissionTime < 0) flight.Advance(SampleSeconds);
            samples.Add(math.normalize(Astro.GeoToEcef(site.LatitudeDeg, site.LongitudeDeg, 0)) * EarthConstants.RadiusKm);
            while (!flight.PayloadReleased && !flight.Finished && flight.MissionTime < 6 * 3600)
            {
                flight.Advance(SampleSeconds);
                samples.Add(Astro.EciToEcef(flight.Position / 1000.0, flight.JulianDate));
            }
            if (!flight.PayloadReleased) return null;

            double3 r = flight.Position / 1000.0, v = flight.Velocity / 1000.0;
            if (!OrbitMath.FromStateVector(r, v, out var stage)) return null;
            // As LaunchManager.DeployPayload: the wanted shape, in the plane and at the spot the rocket delivered.
            var final = OrbitMath.FromAltitudes(perigeeKm, apogeeKm, stage.I * MathConst.Rad2Deg, stage.Raan * MathConst.Rad2Deg, 0, 0);
            final.M = OrbitMath.WrapAngle(stage.ArgP + stage.M);
            return new LaunchPath
            {
                LiftoffJd = liftoffJd,
                ReleaseJd = flight.JulianDate,
                Final = final,
                ReleasePositionKm = r,
                ReleaseVelocityKmS = v,
                AscentEcef = samples.ToArray(),
                RaisesOrbit = flight.PayloadRaisesOrbit,
            };
        }

        /// <summary>Ground position (Earth-fixed, km, raised by <paramref name="liftKm"/>) of an orbit at a time after its epoch.</summary>
        public static double3 GroundPoint(in OrbitElements elements, double epochJd, double seconds, double liftKm)
        {
            var el = elements;
            if (seconds != 0) OrbitMath.Advance(ref el, 0, seconds);
            OrbitMath.ToStateVector(el, out double3 r, out _);
            return math.normalize(Astro.EciToEcef(r, epochJd + seconds / MathConst.SecondsPerDay)) * (EarthConstants.RadiusKm + liftKm);
        }
    }
}
