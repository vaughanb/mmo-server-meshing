// Server Meshing test rig — ride: a driver that stops on a line and gets off with a passenger aboard (DESIGN 1.56).
//
// ride runs -meshRideSteps (comma-separated) in order, each logged as `Ride step <n> <step>: ...`:
//   mount          use the bag's mount item (the two-seat horse for a passenger run)
//   passenger<s>   wait up to s seconds for seat 1 of the ridden vehicle to be taken
//   to<x>          ride along the lane until the character passes x, then stop at once (WalkTo's 1.5 m arrival
//                  tolerance cannot place a horse astride a line: the driver past it and the seat behind not)
//   hold<s>        stand s seconds, the position logged every 2 s
//   dismount       get off (the kit's exit; a summoned mount is destroyed and sets its passengers down)
// The RESULT reads `RESULT ride: steps=<done>/<all> ...` and passes when every step did what it said.
using System.Globalization;
using System.Collections;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    public partial class ServerMeshAutoClient
    {
        public const string ARG_RIDE_STEPS = "-meshRideSteps";

        [Header("Ride (DESIGN 1.56)")]
        [Tooltip("ride mode: the steps, comma-separated: mount, passenger<s>, to<x>, hold<s>, dismount.")]
        public string rideSteps = "mount,passenger30,to8,hold10,dismount";

        private void ReadRideArgs(string[] args)
        {
            string steps = ReadArg(args, ARG_RIDE_STEPS, null);
            if (!string.IsNullOrEmpty(steps))
                rideSteps = steps;
        }

        private IEnumerator RunRide(float deadline)
        {
            Phase = "ride";
            string[] steps = rideSteps.Split(',');
            int done = 0, before = ClientMigrationController.CompletedCount;
            string failed = null;
            for (int i = 0; i < steps.Length && failed == null && Time.realtimeSinceStartup < deadline; ++i)
            {
                string step = steps[i].Trim();
                bool ok = false;
                if (step == "mount")
                {
                    yield return MountUp();
                    BasePlayerCharacterEntity self = GameInstance.PlayingCharacterEntity;
                    ok = self != null && self.PassengingVehicleEntity != null;
                    Log($"Ride step {i + 1} mount: {(ok ? $"on '{self.PassengingVehicleEntity.Entity.name}' at {self.EntityTransform.position}" : "not mounted")}");
                }
                else if (step.StartsWith("passenger") && TryFloat(step.Substring(9), out float wait))
                {
                    float end = Time.realtimeSinceStartup + wait;
                    VehicleEntity vehicle = null;
                    while (Time.realtimeSinceStartup < end)
                    {
                        BasePlayerCharacterEntity self = GameInstance.PlayingCharacterEntity;
                        vehicle = self != null ? self.PassengingVehicleEntity as VehicleEntity : null;
                        if (vehicle != null && !vehicle.IsSeatAvailable(1))
                            break;
                        yield return new WaitForSecondsRealtime(0.25f);
                    }
                    ok = vehicle != null && !vehicle.IsSeatAvailable(1);
                    Log($"Ride step {i + 1} {step}: {(ok ? "seat 1 taken" : $"no passenger in {wait:0} s")}");
                }
                else if (step.StartsWith("to") && TryFloat(step.Substring(2), out float x))
                {
                    Vector3 stop = Vector3.zero;
                    yield return RideTo(x, deadline, (reached, at) => { ok = reached; stop = at; });
                    Log($"Ride step {i + 1} {step}: {(ok ? "stopped" : "did not get there")} at {stop}");
                }
                else if (step.StartsWith("hold") && TryFloat(step.Substring(4), out float seconds))
                {
                    float end = Time.realtimeSinceStartup + seconds, nextLog = 0f;
                    while (Time.realtimeSinceStartup < end)
                    {
                        BasePlayerCharacterEntity self = GameInstance.PlayingCharacterEntity;
                        if (self != null && Time.realtimeSinceStartup >= nextLog)
                        {
                            nextLog = Time.realtimeSinceStartup + 2f;
                            VehicleEntity vehicle = self.PassengingVehicleEntity as VehicleEntity;
                            Log($"ride hold: pos={self.EntityTransform.position} vehicle={(vehicle != null ? $"{vehicle.name} #{vehicle.ObjectId} seat1Free={vehicle.IsSeatAvailable(1)}" : "none")} migrations={ClientMigrationController.CompletedCount - before}");
                        }
                        yield return new WaitForSecondsRealtime(0.25f);
                    }
                    ok = true;
                    Log($"Ride step {i + 1} {step}: done");
                }
                else if (step == "dismount")
                {
                    BasePlayerCharacterEntity self = GameInstance.PlayingCharacterEntity;
                    if (self != null && self.PassengingVehicleEntity != null)
                    {
                        self.CallCmdExitVehicle();
                        float end = Time.realtimeSinceStartup + 10f;
                        while (Time.realtimeSinceStartup < end && GameInstance.PlayingCharacterEntity != null && GameInstance.PlayingCharacterEntity.PassengingVehicleEntity != null)
                            yield return null;
                        self = GameInstance.PlayingCharacterEntity;
                        ok = self != null && self.PassengingVehicleEntity == null;
                    }
                    Log($"Ride step {i + 1} dismount: {(ok ? $"on foot at {GameInstance.PlayingCharacterEntity.EntityTransform.position}" : "still mounted")}");
                }
                else
                    Log($"Ride step {i + 1} '{step}' is not a step");
                if (ok)
                    done++;
                else
                    failed = step;
            }
            Finished = true;
            Passed = failed == null && done == steps.Length;
            Log($"RESULT ride: steps={done}/{steps.Length}{(failed != null ? $" failed={failed}" : "")} migrations={ClientMigrationController.CompletedCount - before} maxGap={_maxGapMs}ms{ReloginSuffix(Relogins)} -> {(Passed ? "PASS" : "FAIL")}");
        }

        /// <summary>Rides along the lane (walkTargetZ) toward x and stops the moment the character is past it.</summary>
        private IEnumerator RideTo(float x, float deadline, System.Action<bool, Vector3> onDone)
        {
            BasePlayerCharacterEntity p0 = GameInstance.PlayingCharacterEntity;
            if (p0 == null)
            {
                onDone(false, Vector3.zero);
                yield break;
            }
            float sign = Mathf.Sign(x - p0.EntityTransform.position.x);
            float z = walkTargetZ != 0f ? walkTargetZ : p0.EntityTransform.position.z;
            float nextOrder = 0f, end = Mathf.Min(deadline, Time.realtimeSinceStartup + 60f);
            while (Time.realtimeSinceStartup < end)
            {
                BasePlayerCharacterEntity p = GameInstance.PlayingCharacterEntity;
                if (p != null)
                {
                    Vector3 pos = p.EntityTransform.position;
                    if ((pos.x - x) * sign >= 0f)
                    {
                        p.StopMove();
                        yield return new WaitForSecondsRealtime(1f); // what the stop leaves of the run-on
                        p = GameInstance.PlayingCharacterEntity;
                        onDone(true, p != null ? p.EntityTransform.position : pos);
                        yield break;
                    }
                    if (Time.realtimeSinceStartup >= nextOrder)
                    {
                        nextOrder = Time.realtimeSinceStartup + 0.5f;
                        p.PointClickMovement(new Vector3(x + sign * 20f, pos.y, z)); // aimed past the stop, so the walk never ends short of it
                    }
                }
                yield return null;
            }
            onDone(false, GameInstance.PlayingCharacterEntity != null ? GameInstance.PlayingCharacterEntity.EntityTransform.position : Vector3.zero);
        }

        private static bool TryFloat(string text, out float value)
        {
            return float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }
    }
}
