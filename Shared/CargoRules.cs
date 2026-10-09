using System;
using System.Collections.Generic;
using System.Globalization;

namespace CargoCore
{

    public sealed class TrailerDefinition
    {
        public string Model, Strategy;
        public string[] CargoModels;
        public int RampDoor;
        public int RampSpeed = 5, RampTimeoutSeconds = 20;
        public bool RampInstantClose;
        public float MinX, MaxX, MinY, MaxY, MinZ, MaxZ, MaxSpeed, MaxAngle, MaxWidth, MaxLength, MaxHeight;
        public static TrailerDefinition Parse(string text)
        {
            var values = new Dictionary<string, string>();
            foreach (var line in text.Split('\n'))
            {
                var s = line.Trim();
                if (s.Length == 0 || s.StartsWith("#"))
                    continue;
                var parts = s.Split(new[] { '=' }, 2);
                if (parts.Length != 2 || values.ContainsKey(parts[0].Trim()))
                    throw new FormatException("Invalid trailer configuration: " + s);
                values.Add(parts[0].Trim(), parts[1].Trim());
            }
            Func<string, float> f = k => float.Parse(values[k], CultureInfo.InvariantCulture);
            var d = new TrailerDefinition
            {
                Model = values["model"],
                Strategy = values["strategy"],
                CargoModels = values["cargoModels"].Split(','),
                RampDoor = int.Parse(values["rampDoor"]),
                MinX = f("minX"),
                MaxX = f("maxX"),
                MinY = f("minY"),
                MaxY = f("maxY"),
                MinZ = f("minZ"),
                MaxZ = f("maxZ"),
                MaxSpeed = f("maxSpeed"),
                MaxAngle = f("maxAngle"),
                MaxWidth = f("maxWidth"),
                MaxLength = f("maxLength"),
                MaxHeight = f("maxHeight")
            };
            if (values.ContainsKey("rampSpeed"))
                d.RampSpeed = int.Parse(values["rampSpeed"]);
            if (values.ContainsKey("rampTimeoutSeconds"))
                d.RampTimeoutSeconds = int.Parse(values["rampTimeoutSeconds"]);
            if (values.ContainsKey("rampInstantClose"))
                d.RampInstantClose = bool.Parse(values["rampInstantClose"]);
            if (d.RampSpeed < 1 || d.RampSpeed > 5 || d.RampTimeoutSeconds < 3 || d.RampTimeoutSeconds > 60)
                throw new FormatException("Ramp speed must be 1-5 and timeout 3-60 seconds");
            if (d.Strategy != "carrier" && d.Strategy != "entity")
                throw new FormatException("Unknown attachment strategy");
            foreach (var n in new[] { d.MinX, d.MaxX, d.MinY, d.MaxY, d.MinZ, d.MaxZ, d.MaxSpeed, d.MaxAngle, d.MaxWidth, d.MaxLength, d.MaxHeight })
                if (!Rules.Finite(n))
                    throw new FormatException("Configuration must be finite");
            if (d.MinX >= d.MaxX || d.MinY >= d.MaxY || d.MinZ >= d.MaxZ || d.MaxSpeed <= 0 || d.MaxAngle <= 0 || d.MaxWidth <= 0 || d.MaxLength <= 0 || d.MaxHeight <= 0)
                throw new FormatException("Invalid loading zone or limits");
            return d;
        }
    }
    public static class Rules
    {
        public static bool Finite(float n)
        {
            return !float.IsNaN(n) && !float.IsInfinity(n);
        }

        public static float Angle(float a)
        {
            return (a % 360 + 540) % 360 - 180;
        }

        public static float[] RelativeRotation(float cargoPitch, float cargoRoll, float cargoYaw, float trailerPitch, float trailerRoll, float trailerYaw)
        {
            return new[] { Angle(cargoPitch - trailerPitch), Angle(cargoRoll - trailerRoll), Angle(cargoYaw - trailerYaw) };
        }

        public static float[] Relative(float dx, float dy, float dz, float heading)
        {
            var r = heading * Math.PI / 180;
            return new[] { (float)(dx * Math.Cos(r) + dy * Math.Sin(r)), (float)(-dx * Math.Sin(r) + dy * Math.Cos(r)), dz };
        }

        public static string Validate(TrailerDefinition d, float x, float y, float z, float angle, float speed)
        {
            foreach (var n in new[] { x, y, z, angle, speed })
                if (!Finite(n))
                    return "Entity position unavailable";
            if (speed > d.MaxSpeed)
                return "Trailer and cargo must be stationary";
            if (x < d.MinX || x > d.MaxX)
                return "Vehicle is outside loading width; center it on the deck";
            if (y < d.MinY || y > d.MaxY)
                return "Vehicle is outside loading length; move along the deck";
            if (z < d.MinZ || z > d.MaxZ)
                return "Vehicle is outside loading height; use /cargo_debug";
            if (Math.Abs(Angle(angle)) > d.MaxAngle)
                return "Align the vehicle with the trailer";
            return null;
        }
    }
    public sealed class Assignment
    {
        public int Cargo, Trailer, Revision;
        public bool Attached;
        public float X, Y, Z, RX, RY, RZ;
        public string Encode()
        {
            return string.Join("|", new[] { Cargo.ToString(), Trailer.ToString(), Revision.ToString(), Attached ? "1" : "0", F(X), F(Y), F(Z), F(RX), F(RY), F(RZ) });
        }

        static string F(float n)
        {
            return n.ToString("R", CultureInfo.InvariantCulture);
        }

        public static Assignment Decode(string s)
        {
            var p = s.Split('|');
            if (p.Length != 10)
                throw new FormatException("Invalid assignment");
            Func<int, float> f = i => float.Parse(p[i], CultureInfo.InvariantCulture);
            return new Assignment
            {
                Cargo = int.Parse(p[0]),
                Trailer = int.Parse(p[1]),
                Revision = int.Parse(p[2]),
                Attached = p[3] == "1",
                X = f(4),
                Y = f(5),
                Z = f(6),
                RX = f(7),
                RY = f(8),
                RZ = f(9)
            };
        }
    }
}
