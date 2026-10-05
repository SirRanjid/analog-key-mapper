using System;
using System.Collections.Generic;
using Tk75.Mapping;

namespace Tk75.App
{
    // UI-thread-owned connection intent and retry timing. Device operations and
    // the single in-flight connection attempt remain the caller's responsibility.
    public sealed class ControllerConnectionPolicy
    {
        sealed class Route
        {
            internal ControllerKind Kind;
            internal bool Eligible;
            internal bool? ManualChoice;
            internal bool PreviouslyConnected;
            internal int Failures;
            internal DateTime RetryAt;
        }

        readonly Dictionary<string, Route> routes = new Dictionary<string, Route>(StringComparer.Ordinal);
        readonly List<string> order = new List<string>();
        bool automatic, stopped;

        public void Configure(Profile profile, bool automatic)
        {
            List<ControllerDefinition> definitions = ControllerRouting.EffectiveControllers(profile);
            var eligible = new HashSet<string>(StringComparer.Ordinal);
            foreach (Binding binding in profile.Bindings)
                if (binding.Enabled) eligible.Add(binding.ControllerId);
            var retained = new HashSet<string>(StringComparer.Ordinal);
            order.Clear();
            foreach (ControllerDefinition definition in definitions)
            {
                Route route;
                if (!routes.TryGetValue(definition.Id, out route))
                {
                    route = new Route { Kind = definition.Kind };
                    routes.Add(definition.Id, route);
                }
                if (route.Kind != definition.Kind || !eligible.Contains(definition.Id)) ResetConnectionState(route);
                route.Kind = definition.Kind;
                route.Eligible = eligible.Contains(definition.Id);
                retained.Add(definition.Id);
                order.Add(definition.Id);
            }
            foreach (string id in new List<string>(routes.Keys))
                if (!retained.Contains(id)) routes.Remove(id);
            this.automatic = automatic;
        }

        public void SetDesired(string id, bool connect)
        {
            Route route;
            if (id == null || !routes.TryGetValue(id, out route)) return;
            route.ManualChoice = connect;
            ResetConnectionState(route);
        }

        public void StopAll()
        {
            stopped = true;
            foreach (Route route in routes.Values)
            {
                route.ManualChoice = false;
                ResetConnectionState(route);
            }
        }

        public bool IsDesired(string id)
        {
            Route route;
            return id != null && routes.TryGetValue(id, out route) && route.Eligible &&
                (route.ManualChoice ?? (automatic && !stopped));
        }

        public string Next(DateTime now, Func<string, bool> enabled, Func<string, bool> connecting)
        {
            if (enabled == null) throw new ArgumentNullException("enabled");
            if (connecting == null) throw new ArgumentNullException("connecting");
            string next = null;
            foreach (string id in order)
            {
                Route route = routes[id];
                if (enabled(id)) { ResetRetry(route); route.PreviouslyConnected = true; continue; }
                if (connecting(id)) continue;
                // A worker can lose its output immediately after a successful
                // connect. Treat that loss like a failed attempt, not work due
                // on every UI tick, and keep scanning the other controllers.
                if (route.PreviouslyConnected) Failed(id, now);
                if (next == null && IsDesired(id) && now >= route.RetryAt) next = id;
            }
            return next;
        }

        public void Failed(string id, DateTime now)
        {
            Route route;
            if (id == null || !routes.TryGetValue(id, out route)) return;
            route.PreviouslyConnected = false;
            if (!IsDesired(id)) return;
            route.Failures = Math.Min(route.Failures + 1, 5);
            route.RetryAt = now.AddSeconds(Math.Min(1 << route.Failures, 30));
        }

        public void Succeeded(string id)
        {
            Route route;
            if (id != null && routes.TryGetValue(id, out route))
            {
                ResetRetry(route);
                route.PreviouslyConnected = true;
            }
        }

        static void ResetConnectionState(Route route)
        { ResetRetry(route); route.PreviouslyConnected = false; }

        static void ResetRetry(Route route)
        { route.Failures = 0; route.RetryAt = DateTime.MinValue; }
    }
}
