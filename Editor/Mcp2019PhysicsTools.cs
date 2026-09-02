#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace UnityMcp2019
{
    internal static class Mcp2019PhysicsTools
    {
        internal static string Execute(string argumentsJson)
        {
            PhysicsArguments arguments = string.IsNullOrEmpty(argumentsJson) || argumentsJson == "{}"
                ? new PhysicsArguments()
                : JsonUtility.FromJson<PhysicsArguments>(argumentsJson) ?? new PhysicsArguments();
            string action = (arguments.Action ?? string.Empty).Trim().ToLowerInvariant();
            bool is2D = string.Equals(arguments.Dimension, "2d", StringComparison.OrdinalIgnoreCase);
            try
            {
                PhysicsResult result;
                switch (action)
                {
                    case "ping": result = Ping(); break;
                    case "get_settings": result = GetSettings(is2D); break;
                    case "set_settings": result = SetSettings(is2D, arguments.Settings); break;
                    case "get_collision_matrix": result = GetCollisionMatrix(is2D, arguments); break;
                    case "set_collision_matrix": result = SetCollisionMatrix(is2D, arguments); break;
                    case "create_physics_material": result = CreateMaterial(is2D, arguments); break;
                    case "configure_physics_material": result = ConfigureMaterial(is2D, arguments); break;
                    case "assign_physics_material": result = AssignMaterial(is2D, arguments); break;
                    case "add_joint": result = AddJoint(is2D, arguments); break;
                    case "configure_joint": result = ConfigureJoint(is2D, arguments); break;
                    case "remove_joint": result = RemoveJoint(is2D, arguments); break;
                    case "raycast": result = Raycast(is2D, arguments, false); break;
                    case "raycast_all": result = Raycast(is2D, arguments, true); break;
                    case "linecast": result = Linecast(is2D, arguments); break;
                    case "shapecast": result = Shapecast(is2D, arguments); break;
                    case "overlap": result = Overlap(is2D, arguments); break;
                    case "validate": result = Validate(is2D, arguments); break;
                    case "simulate_step": result = Simulate(is2D, arguments); break;
                    case "apply_force": result = ApplyForce(is2D, arguments); break;
                    case "get_rigidbody": result = GetRigidbody(is2D, arguments); break;
                    case "configure_rigidbody": result = ConfigureRigidbody(is2D, arguments); break;
                    default: result = Fail("Unknown physics action: " + action); break;
                }
                return JsonUtility.ToJson(result);
            }
            catch (Exception exception)
            {
                return JsonUtility.ToJson(Fail(exception.GetType().Name + ": " + exception.Message));
            }
        }

        private static PhysicsResult Ping()
        {
            return Ok("Unity physics is available.", new PhysicsData
            {
                UnityVersion = Application.unityVersion,
                Supports3D = true, Supports2D = true,
                AutoSimulation3D = Physics.autoSimulation,
                AutoSimulation2D = Physics2D.autoSimulation
            });
        }

        private static PhysicsResult GetSettings(bool is2D)
        {
            PhysicsSettingsRecord settings = is2D
                ? new PhysicsSettingsRecord
                {
                    Gravity = ToArray(Physics2D.gravity),
                    DefaultContactOffset = Physics2D.defaultContactOffset,
                    VelocityIterations = Physics2D.velocityIterations,
                    PositionIterations = Physics2D.positionIterations,
                    QueriesHitTriggers = Physics2D.queriesHitTriggers,
                    AutoSimulation = Physics2D.autoSimulation
                }
                : new PhysicsSettingsRecord
                {
                    Gravity = ToArray(Physics.gravity),
                    DefaultContactOffset = Physics.defaultContactOffset,
                    SleepThreshold = Physics.sleepThreshold,
                    BounceThreshold = Physics.bounceThreshold,
                    QueriesHitTriggers = Physics.queriesHitTriggers,
                    QueriesHitBackfaces = Physics.queriesHitBackfaces,
                    AutoSimulation = Physics.autoSimulation,
                    DefaultSolverIterations = Physics.defaultSolverIterations,
                    DefaultSolverVelocityIterations = Physics.defaultSolverVelocityIterations
                };
            return Ok("Physics settings read.", new PhysicsData { Dimension = is2D ? "2d" : "3d", Settings = settings });
        }

        private static PhysicsResult SetSettings(bool is2D, SerializedPatch[] patches)
        {
            foreach (SerializedPatch patch in patches ?? new SerializedPatch[0])
            {
                string key = NormalizeKey(patch.Path);
                if (is2D)
                {
                    if (key == "gravity") Physics2D.gravity = ToVector2(patch);
                    else if (key == "defaultcontactoffset") Physics2D.defaultContactOffset = Number(patch);
                    else if (key == "velocityiterations") Physics2D.velocityIterations = Integer(patch);
                    else if (key == "positioniterations") Physics2D.positionIterations = Integer(patch);
                    else if (key == "querieshittriggers") Physics2D.queriesHitTriggers = Boolean(patch);
                    else if (key == "autosimulation") Physics2D.autoSimulation = Boolean(patch);
                    else return Fail("Unsupported 2D physics setting: " + patch.Path);
                }
                else
                {
                    if (key == "gravity") Physics.gravity = ToVector3(patch);
                    else if (key == "defaultcontactoffset") Physics.defaultContactOffset = Number(patch);
                    else if (key == "sleepthreshold") Physics.sleepThreshold = Number(patch);
                    else if (key == "bouncethreshold") Physics.bounceThreshold = Number(patch);
                    else if (key == "querieshittriggers") Physics.queriesHitTriggers = Boolean(patch);
                    else if (key == "querieshitbackfaces") Physics.queriesHitBackfaces = Boolean(patch);
                    else if (key == "autosimulation") Physics.autoSimulation = Boolean(patch);
                    else if (key == "defaultsolveriterations") Physics.defaultSolverIterations = Integer(patch);
                    else if (key == "defaultsolvervelocityiterations") Physics.defaultSolverVelocityIterations = Integer(patch);
                    else return Fail("Unsupported 3D physics setting: " + patch.Path);
                }
            }
            return GetSettings(is2D);
        }

        private static PhysicsResult GetCollisionMatrix(bool is2D, PhysicsArguments arguments)
        {
            List<CollisionPairRecord> pairs = new List<CollisionPairRecord>();
            if (!string.IsNullOrEmpty(arguments.LayerA) && !string.IsNullOrEmpty(arguments.LayerB))
            {
                int a = ResolveLayer(arguments.LayerA); int b = ResolveLayer(arguments.LayerB);
                pairs.Add(new CollisionPairRecord { LayerA = LayerName(a), LayerAIndex = a, LayerB = LayerName(b), LayerBIndex = b, Collide = is2D ? !Physics2D.GetIgnoreLayerCollision(a, b) : !Physics.GetIgnoreLayerCollision(a, b) });
            }
            else
            {
                for (int a = 0; a < 32; a++)
                for (int b = a; b < 32; b++)
                    pairs.Add(new CollisionPairRecord { LayerA = LayerName(a), LayerAIndex = a, LayerB = LayerName(b), LayerBIndex = b, Collide = is2D ? !Physics2D.GetIgnoreLayerCollision(a, b) : !Physics.GetIgnoreLayerCollision(a, b) });
            }
            return Ok("Collision matrix read.", new PhysicsData { Dimension = is2D ? "2d" : "3d", CollisionPairs = pairs.ToArray(), Count = pairs.Count });
        }

        private static PhysicsResult SetCollisionMatrix(bool is2D, PhysicsArguments arguments)
        {
            if (string.IsNullOrEmpty(arguments.LayerA) || string.IsNullOrEmpty(arguments.LayerB) || !arguments.HasCollide)
                return Fail("layer_a, layer_b, and collide are required.");
            int a = ResolveLayer(arguments.LayerA); int b = ResolveLayer(arguments.LayerB);
            if (is2D) Physics2D.IgnoreLayerCollision(a, b, !arguments.Collide);
            else Physics.IgnoreLayerCollision(a, b, !arguments.Collide);
            return GetCollisionMatrix(is2D, arguments);
        }

        private static PhysicsResult CreateMaterial(bool is2D, PhysicsArguments arguments)
        {
            string path = NormalizeAssetPath(arguments.Path, is2D ? ".physicsMaterial2D" : ".physicMaterial", arguments.Name);
            if (AssetDatabase.LoadMainAssetAtPath(path) != null) return Fail("Asset already exists at '" + path + "'.");
            EnsureFolder(Path.GetDirectoryName(path).Replace('\\', '/'));
            if (is2D)
            {
                PhysicsMaterial2D material = new PhysicsMaterial2D(Path.GetFileNameWithoutExtension(path));
                if (arguments.HasFriction) material.friction = arguments.Friction;
                if (arguments.HasBounciness) material.bounciness = arguments.Bounciness;
                AssetDatabase.CreateAsset(material, path);
            }
            else
            {
                PhysicMaterial material = new PhysicMaterial(Path.GetFileNameWithoutExtension(path));
                if (arguments.HasDynamicFriction) material.dynamicFriction = arguments.DynamicFriction;
                if (arguments.HasStaticFriction) material.staticFriction = arguments.StaticFriction;
                if (arguments.HasBounciness) material.bounciness = arguments.Bounciness;
                if (!string.IsNullOrEmpty(arguments.FrictionCombine)) material.frictionCombine = ParseCombine(arguments.FrictionCombine);
                if (!string.IsNullOrEmpty(arguments.BounceCombine)) material.bounceCombine = ParseCombine(arguments.BounceCombine);
                AssetDatabase.CreateAsset(material, path);
            }
            AssetDatabase.SaveAssets();
            return Ok("Physics material created.", new PhysicsData { Path = path, Dimension = is2D ? "2d" : "3d" });
        }

        private static PhysicsResult ConfigureMaterial(bool is2D, PhysicsArguments arguments)
        {
            string path = string.IsNullOrEmpty(arguments.Path) ? arguments.MaterialPath : arguments.Path;
            if (is2D)
            {
                PhysicsMaterial2D material = AssetDatabase.LoadAssetAtPath<PhysicsMaterial2D>(path);
                if (material == null) return Fail("PhysicsMaterial2D not found.");
                Undo.RecordObject(material, "MCP Configure Physics Material 2D");
                if (arguments.HasFriction) material.friction = arguments.Friction;
                if (arguments.HasBounciness) material.bounciness = arguments.Bounciness;
                ApplyPatches(material, arguments.Properties); Save(material);
            }
            else
            {
                PhysicMaterial material = AssetDatabase.LoadAssetAtPath<PhysicMaterial>(path);
                if (material == null) return Fail("PhysicMaterial not found.");
                Undo.RecordObject(material, "MCP Configure Physics Material");
                if (arguments.HasDynamicFriction) material.dynamicFriction = arguments.DynamicFriction;
                if (arguments.HasStaticFriction) material.staticFriction = arguments.StaticFriction;
                if (arguments.HasBounciness) material.bounciness = arguments.Bounciness;
                if (!string.IsNullOrEmpty(arguments.FrictionCombine)) material.frictionCombine = ParseCombine(arguments.FrictionCombine);
                if (!string.IsNullOrEmpty(arguments.BounceCombine)) material.bounceCombine = ParseCombine(arguments.BounceCombine);
                ApplyPatches(material, arguments.Properties); Save(material);
            }
            return Ok("Physics material configured.", new PhysicsData { Path = path, Dimension = is2D ? "2d" : "3d" });
        }

        private static PhysicsResult AssignMaterial(bool is2D, PhysicsArguments arguments)
        {
            GameObject gameObject = ResolveGameObject(arguments.Target, arguments.SearchMethod);
            if (gameObject == null) return Fail("Target GameObject not found.");
            if (is2D)
            {
                Collider2D collider = SelectComponent<Collider2D>(gameObject, arguments.ColliderType, arguments.ComponentIndex);
                PhysicsMaterial2D material = AssetDatabase.LoadAssetAtPath<PhysicsMaterial2D>(arguments.MaterialPath);
                if (collider == null || material == null) return Fail("Collider2D or PhysicsMaterial2D not found.");
                Undo.RecordObject(collider, "MCP Assign Physics Material 2D"); collider.sharedMaterial = material; EditorUtility.SetDirty(collider);
            }
            else
            {
                Collider collider = SelectComponent<Collider>(gameObject, arguments.ColliderType, arguments.ComponentIndex);
                PhysicMaterial material = AssetDatabase.LoadAssetAtPath<PhysicMaterial>(arguments.MaterialPath);
                if (collider == null || material == null) return Fail("Collider or PhysicMaterial not found.");
                Undo.RecordObject(collider, "MCP Assign Physics Material"); collider.sharedMaterial = material; EditorUtility.SetDirty(collider);
            }
            return Ok("Physics material assigned.", new PhysicsData { Target = gameObject.name, Path = arguments.MaterialPath });
        }

        private static PhysicsResult AddJoint(bool is2D, PhysicsArguments arguments)
        {
            GameObject gameObject = ResolveGameObject(arguments.Target, arguments.SearchMethod);
            if (gameObject == null) return Fail("Target GameObject not found.");
            Type type = ResolveJointType(is2D, arguments.JointType);
            if (type == null) return Fail("Unknown joint_type: " + arguments.JointType);
            Component joint = Undo.AddComponent(gameObject, type);
            ConfigureConnectedBody(joint, is2D, arguments.ConnectedBody);
            ApplyPatches(joint, arguments.Properties);
            ApplyPatches(joint, arguments.Motor); ApplyPatches(joint, arguments.Limits);
            ApplyPatches(joint, arguments.Spring); ApplyPatches(joint, arguments.Drive);
            EditorUtility.SetDirty(joint);
            return Ok("Joint added.", ComponentData(gameObject, joint));
        }

        private static PhysicsResult ConfigureJoint(bool is2D, PhysicsArguments arguments)
        {
            GameObject gameObject = ResolveGameObject(arguments.Target, arguments.SearchMethod);
            Type type = ResolveJointType(is2D, arguments.JointType);
            Component joint = gameObject == null || type == null ? null : GetComponent(gameObject, type, arguments.ComponentIndex);
            if (joint == null) return Fail("Joint not found.");
            Undo.RecordObject(joint, "MCP Configure Joint");
            ConfigureConnectedBody(joint, is2D, arguments.ConnectedBody);
            ApplyPatches(joint, arguments.Properties); ApplyPatches(joint, arguments.Motor);
            ApplyPatches(joint, arguments.Limits); ApplyPatches(joint, arguments.Spring); ApplyPatches(joint, arguments.Drive);
            EditorUtility.SetDirty(joint);
            return Ok("Joint configured.", ComponentData(gameObject, joint));
        }

        private static PhysicsResult RemoveJoint(bool is2D, PhysicsArguments arguments)
        {
            GameObject gameObject = ResolveGameObject(arguments.Target, arguments.SearchMethod);
            Type type = ResolveJointType(is2D, arguments.JointType);
            Component joint = gameObject == null || type == null ? null : GetComponent(gameObject, type, arguments.ComponentIndex);
            if (joint == null) return Fail("Joint not found.");
            string name = joint.GetType().Name; Undo.DestroyObjectImmediate(joint);
            return Ok("Joint removed.", new PhysicsData { Target = gameObject.name, ComponentType = name });
        }

        private static PhysicsResult Raycast(bool is2D, PhysicsArguments arguments, bool all)
        {
            int mask = ResolveMask(arguments.LayerMask);
            float distance = arguments.HasMaxDistance ? arguments.MaxDistance : Mathf.Infinity;
            if (is2D)
            {
                Vector2 origin = Vector2Arg(arguments.Origin, "origin"); Vector2 direction = Vector2Arg(arguments.Direction, "direction").normalized;
                RaycastHit2D[] hits = all ? Physics2D.RaycastAll(origin, direction, distance, mask) : Single(Physics2D.Raycast(origin, direction, distance, mask));
                return Hits(hits.Select(ToHit).Where(item => item != null).ToArray(), "2d");
            }
            else
            {
                Vector3 origin = Vector3Arg(arguments.Origin, "origin"); Vector3 direction = Vector3Arg(arguments.Direction, "direction").normalized;
                QueryTriggerInteraction query = ParseQuery(arguments.QueryTriggerInteraction);
                RaycastHit[] hits = all ? Physics.RaycastAll(origin, direction, distance, mask, query)
                    : (Physics.Raycast(origin, direction, out RaycastHit hit, distance, mask, query) ? new[] { hit } : new RaycastHit[0]);
                return Hits(hits.Select(ToHit).ToArray(), "3d");
            }
        }

        private static PhysicsResult Linecast(bool is2D, PhysicsArguments arguments)
        {
            int mask = ResolveMask(arguments.LayerMask);
            if (is2D)
            {
                RaycastHit2D hit = Physics2D.Linecast(Vector2Arg(arguments.Start, "start"), Vector2Arg(arguments.End, "end"), mask);
                return Hits(Single(hit).Select(ToHit).Where(item => item != null).ToArray(), "2d");
            }
            RaycastHit hit3D;
            bool found = Physics.Linecast(Vector3Arg(arguments.Start, "start"), Vector3Arg(arguments.End, "end"), out hit3D, mask, ParseQuery(arguments.QueryTriggerInteraction));
            return Hits(found ? new[] { ToHit(hit3D) } : new HitRecord[0], "3d");
        }

        private static PhysicsResult Shapecast(bool is2D, PhysicsArguments arguments)
        {
            string shape = (arguments.Shape ?? "sphere").ToLowerInvariant();
            int mask = ResolveMask(arguments.LayerMask);
            float distance = arguments.HasMaxDistance ? arguments.MaxDistance : Mathf.Infinity;
            if (is2D)
            {
                Vector2 origin = Vector2Arg(arguments.Origin, "origin"); Vector2 direction = Vector2Arg(arguments.Direction, "direction").normalized;
                RaycastHit2D[] hits;
                if (shape == "box") hits = Physics2D.BoxCastAll(origin, Size2(arguments), arguments.HasAngle ? arguments.Angle : 0f, direction, distance, mask);
                else if (shape == "capsule") hits = Physics2D.CapsuleCastAll(origin, Size2(arguments), CapsuleDirection2D.Vertical, arguments.HasAngle ? arguments.Angle : 0f, direction, distance, mask);
                else hits = Physics2D.CircleCastAll(origin, Radius(arguments), direction, distance, mask);
                return Hits(hits.Select(ToHit).Where(item => item != null).ToArray(), "2d");
            }
            else
            {
                Vector3 origin = Vector3Arg(arguments.Origin, "origin"); Vector3 direction = Vector3Arg(arguments.Direction, "direction").normalized;
                RaycastHit[] hits;
                if (shape == "box") hits = Physics.BoxCastAll(origin, Size3(arguments), direction, Quaternion.identity, distance, mask, ParseQuery(arguments.QueryTriggerInteraction));
                else if (shape == "capsule") hits = Physics.CapsuleCastAll(Vector3Arg(arguments.Point1, "point1"), Vector3Arg(arguments.Point2, "point2"), Radius(arguments), direction, distance, mask, ParseQuery(arguments.QueryTriggerInteraction));
                else hits = Physics.SphereCastAll(origin, Radius(arguments), direction, distance, mask, ParseQuery(arguments.QueryTriggerInteraction));
                return Hits(hits.Select(ToHit).ToArray(), "3d");
            }
        }

        private static PhysicsResult Overlap(bool is2D, PhysicsArguments arguments)
        {
            string shape = (arguments.Shape ?? "sphere").ToLowerInvariant(); int mask = ResolveMask(arguments.LayerMask);
            if (is2D)
            {
                Vector2 position = Vector2Arg(arguments.Position, "position"); Collider2D[] colliders;
                if (shape == "box") colliders = Physics2D.OverlapBoxAll(position, Size2(arguments), arguments.HasAngle ? arguments.Angle : 0f, mask);
                else if (shape == "capsule") colliders = Physics2D.OverlapCapsuleAll(position, Size2(arguments), CapsuleDirection2D.Vertical, arguments.HasAngle ? arguments.Angle : 0f, mask);
                else colliders = Physics2D.OverlapCircleAll(position, Radius(arguments), mask);
                return Overlaps(colliders.Select(ToCollider).ToArray(), "2d");
            }
            Vector3 center = Vector3Arg(arguments.Position, "position"); Collider[] results;
            if (shape == "box") results = Physics.OverlapBox(center, Size3(arguments), Quaternion.identity, mask, ParseQuery(arguments.QueryTriggerInteraction));
            else if (shape == "capsule") results = Physics.OverlapCapsule(Vector3Arg(arguments.Point1, "point1"), Vector3Arg(arguments.Point2, "point2"), Radius(arguments), mask, ParseQuery(arguments.QueryTriggerInteraction));
            else results = Physics.OverlapSphere(center, Radius(arguments), mask, ParseQuery(arguments.QueryTriggerInteraction));
            return Overlaps(results.Select(ToCollider).ToArray(), "3d");
        }

        private static PhysicsResult Validate(bool is2D, PhysicsArguments arguments)
        {
            List<ValidationRecord> records = new List<ValidationRecord>();
            foreach (GameObject gameObject in SceneObjects())
            {
                if (is2D)
                {
                    Rigidbody2D body = gameObject.GetComponent<Rigidbody2D>(); Collider2D collider = gameObject.GetComponent<Collider2D>();
                    if (body != null && collider == null) records.Add(Validation(gameObject, "warning", "Rigidbody2D has no Collider2D."));
                    if (collider != null && !collider.enabled) records.Add(Validation(gameObject, "info", "Collider2D is disabled."));
                }
                else
                {
                    Rigidbody body = gameObject.GetComponent<Rigidbody>(); Collider collider = gameObject.GetComponent<Collider>();
                    if (body != null && collider == null) records.Add(Validation(gameObject, "warning", "Rigidbody has no Collider."));
                    if (collider != null && !collider.enabled) records.Add(Validation(gameObject, "info", "Collider is disabled."));
                }
            }
            int offset = arguments.HasCursor ? Mathf.Max(0, arguments.Cursor) : 0;
            int size = arguments.HasPageSize ? Mathf.Clamp(arguments.PageSize, 1, 500) : 50;
            ValidationRecord[] page = records.Skip(offset).Take(size).ToArray();
            return Ok("Physics validation complete.", new PhysicsData
            {
                Dimension = is2D ? "2d" : "3d", Validation = page,
                Count = page.Length, Total = records.Count, HasMore = offset + page.Length < records.Count,
                NextCursor = offset + page.Length < records.Count ? (offset + page.Length).ToString() : string.Empty
            });
        }

        private static PhysicsResult Simulate(bool is2D, PhysicsArguments arguments)
        {
            if (Application.isPlaying) return Fail("simulate_step is intended for Edit Mode.");
            int steps = arguments.HasSteps ? Mathf.Clamp(arguments.Steps, 1, 100) : 1;
            float step = arguments.HasStepSize ? Mathf.Clamp(arguments.StepSize, 0.0001f, 1f) : Time.fixedDeltaTime;
            if (is2D)
            {
                bool previous = Physics2D.autoSimulation; Physics2D.autoSimulation = false;
                try { for (int index = 0; index < steps; index++) Physics2D.Simulate(step); }
                finally { Physics2D.autoSimulation = previous; }
            }
            else
            {
                bool previous = Physics.autoSimulation; Physics.autoSimulation = false;
                try { for (int index = 0; index < steps; index++) Physics.Simulate(step); }
                finally { Physics.autoSimulation = previous; }
            }
            return Ok("Physics simulation advanced.", new PhysicsData { Dimension = is2D ? "2d" : "3d", Steps = steps, StepSize = step });
        }

        private static PhysicsResult ApplyForce(bool is2D, PhysicsArguments arguments)
        {
            if (!Application.isPlaying) return Fail("apply_force requires Play Mode.");
            GameObject gameObject = ResolveGameObject(arguments.Target, arguments.SearchMethod);
            if (gameObject == null) return Fail("Target GameObject not found.");
            if (is2D)
            {
                Rigidbody2D body = gameObject.GetComponent<Rigidbody2D>(); if (body == null) return Fail("Rigidbody2D not found.");
                ForceMode2D mode = string.Equals(arguments.ForceMode, "impulse", StringComparison.OrdinalIgnoreCase) ? ForceMode2D.Impulse : ForceMode2D.Force;
                if (arguments.HasForce) body.AddForce(Vector2Arg(arguments.Force, "force"), mode);
                if (arguments.HasTorque) body.AddTorque(arguments.Torque[0], mode);
            }
            else
            {
                Rigidbody body = gameObject.GetComponent<Rigidbody>(); if (body == null) return Fail("Rigidbody not found.");
                ForceMode mode = ParseForceMode(arguments.ForceMode);
                if (string.Equals(arguments.ForceType, "explosion", StringComparison.OrdinalIgnoreCase))
                    body.AddExplosionForce(arguments.HasExplosionForce ? arguments.ExplosionForce : 0f, Vector3Arg(arguments.ExplosionPosition, "explosion_position"), arguments.HasExplosionRadius ? arguments.ExplosionRadius : 1f, arguments.HasUpwardsModifier ? arguments.UpwardsModifier : 0f, mode);
                else if (arguments.HasForce) body.AddForce(Vector3Arg(arguments.Force, "force"), mode);
                if (arguments.HasTorque) body.AddTorque(Vector3Arg(arguments.Torque, "torque"), mode);
            }
            return Ok("Force applied.", new PhysicsData { Target = gameObject.name, Dimension = is2D ? "2d" : "3d" });
        }

        private static PhysicsResult GetRigidbody(bool is2D, PhysicsArguments arguments)
        {
            GameObject gameObject = ResolveGameObject(arguments.Target, arguments.SearchMethod);
            if (gameObject == null) return Fail("Target GameObject not found.");
            RigidbodyRecord record;
            if (is2D)
            {
                Rigidbody2D body = gameObject.GetComponent<Rigidbody2D>(); if (body == null) return Fail("Rigidbody2D not found.");
                record = new RigidbodyRecord { Type = "Rigidbody2D", Mass = body.mass, Drag = body.drag, AngularDrag = body.angularDrag, UseGravity = body.gravityScale != 0f, GravityScale = body.gravityScale, IsKinematic = body.bodyType == RigidbodyType2D.Kinematic, Velocity = ToArray(body.velocity), AngularVelocity = body.angularVelocity };
            }
            else
            {
                Rigidbody body = gameObject.GetComponent<Rigidbody>(); if (body == null) return Fail("Rigidbody not found.");
                record = new RigidbodyRecord { Type = "Rigidbody", Mass = body.mass, Drag = body.drag, AngularDrag = body.angularDrag, UseGravity = body.useGravity, IsKinematic = body.isKinematic, Velocity = ToArray(body.velocity), AngularVelocityVector = ToArray(body.angularVelocity) };
            }
            return Ok("Rigidbody information read.", new PhysicsData { Target = gameObject.name, Rigidbody = record });
        }

        private static PhysicsResult ConfigureRigidbody(bool is2D, PhysicsArguments arguments)
        {
            GameObject gameObject = ResolveGameObject(arguments.Target, arguments.SearchMethod);
            if (gameObject == null) return Fail("Target GameObject not found.");
            Component body = is2D ? (Component)gameObject.GetComponent<Rigidbody2D>() : gameObject.GetComponent<Rigidbody>();
            if (body == null) body = is2D ? (Component)Undo.AddComponent<Rigidbody2D>(gameObject) : Undo.AddComponent<Rigidbody>(gameObject);
            Undo.RecordObject(body, "MCP Configure Rigidbody"); ApplyPatches(body, arguments.Properties); EditorUtility.SetDirty(body);
            return GetRigidbody(is2D, arguments);
        }

        private static PhysicsResult Hits(HitRecord[] hits, string dimension)
        { return Ok("Physics query returned " + hits.Length + " hit(s).", new PhysicsData { Dimension = dimension, Hits = hits, Count = hits.Length }); }
        private static PhysicsResult Overlaps(ColliderRecord[] colliders, string dimension)
        { return Ok("Physics overlap returned " + colliders.Length + " collider(s).", new PhysicsData { Dimension = dimension, Colliders = colliders, Count = colliders.Length }); }

        private static HitRecord ToHit(RaycastHit hit)
        {
            return new HitRecord { Target = hit.collider == null ? string.Empty : hit.collider.gameObject.name, InstanceId = hit.collider == null ? 0 : hit.collider.gameObject.GetInstanceID(), ColliderType = hit.collider == null ? string.Empty : hit.collider.GetType().Name, Point = ToArray(hit.point), Normal = ToArray(hit.normal), Distance = hit.distance };
        }
        private static HitRecord ToHit(RaycastHit2D hit)
        {
            return hit.collider == null ? null : new HitRecord { Target = hit.collider.gameObject.name, InstanceId = hit.collider.gameObject.GetInstanceID(), ColliderType = hit.collider.GetType().Name, Point = ToArray(hit.point), Normal = ToArray(hit.normal), Distance = hit.distance };
        }
        private static ColliderRecord ToCollider(Collider collider)
        { return new ColliderRecord { Target = collider.gameObject.name, InstanceId = collider.gameObject.GetInstanceID(), ColliderType = collider.GetType().Name, IsTrigger = collider.isTrigger }; }
        private static ColliderRecord ToCollider(Collider2D collider)
        { return new ColliderRecord { Target = collider.gameObject.name, InstanceId = collider.gameObject.GetInstanceID(), ColliderType = collider.GetType().Name, IsTrigger = collider.isTrigger }; }
        private static RaycastHit2D[] Single(RaycastHit2D hit) { return hit.collider == null ? new RaycastHit2D[0] : new[] { hit }; }

        private static PhysicsData ComponentData(GameObject gameObject, Component component)
        { return new PhysicsData { Target = gameObject.name, InstanceId = gameObject.GetInstanceID(), ComponentType = component.GetType().Name }; }
        private static ValidationRecord Validation(GameObject gameObject, string severity, string message)
        { return new ValidationRecord { Target = gameObject.name, InstanceId = gameObject.GetInstanceID(), Severity = severity, Message = message }; }

        private static void ConfigureConnectedBody(Component joint, bool is2D, string target)
        {
            if (string.IsNullOrEmpty(target)) return;
            GameObject connected = ResolveGameObject(target, "by_name"); if (connected == null) throw new ArgumentException("Connected body target not found.");
            if (is2D) ((Joint2D)joint).connectedBody = connected.GetComponent<Rigidbody2D>();
            else ((Joint)joint).connectedBody = connected.GetComponent<Rigidbody>();
        }

        private static Type ResolveJointType(bool is2D, string value)
        {
            string type = (value ?? string.Empty).Replace("_", string.Empty).ToLowerInvariant();
            if (is2D)
            {
                switch (type)
                {
                    case "distance": return typeof(DistanceJoint2D); case "fixed": return typeof(FixedJoint2D);
                    case "friction": return typeof(FrictionJoint2D); case "hinge": return typeof(HingeJoint2D);
                    case "relative": return typeof(RelativeJoint2D); case "slider": return typeof(SliderJoint2D);
                    case "spring": return typeof(SpringJoint2D); case "target": return typeof(TargetJoint2D);
                    case "wheel": return typeof(WheelJoint2D); default: return null;
                }
            }
            switch (type)
            {
                case "fixed": return typeof(FixedJoint); case "hinge": return typeof(HingeJoint);
                case "spring": return typeof(SpringJoint); case "character": return typeof(CharacterJoint);
                case "configurable": return typeof(ConfigurableJoint); default: return null;
            }
        }

        private static T SelectComponent<T>(GameObject gameObject, string typeName, int index) where T : Component
        {
            T[] all = gameObject.GetComponents<T>();
            IEnumerable<T> filtered = string.IsNullOrEmpty(typeName) ? all : all.Where(item => string.Equals(item.GetType().Name, typeName, StringComparison.OrdinalIgnoreCase));
            return filtered.Skip(Mathf.Max(0, index)).FirstOrDefault();
        }
        private static Component GetComponent(GameObject gameObject, Type type, int index)
        { return gameObject.GetComponents(type).Cast<Component>().Skip(Mathf.Max(0, index)).FirstOrDefault(); }

        private static IEnumerable<GameObject> SceneObjects()
        { return Resources.FindObjectsOfTypeAll<GameObject>().Where(item => item != null && !EditorUtility.IsPersistent(item) && item.scene.IsValid() && item.scene.isLoaded); }
        private static GameObject ResolveGameObject(string target, string method)
        {
            if (string.IsNullOrWhiteSpace(target)) return null; int id;
            if (int.TryParse(target, out id)) return EditorUtility.InstanceIDToObject(id) as GameObject;
            IEnumerable<GameObject> objects = SceneObjects();
            if (string.Equals(method, "by_path", StringComparison.OrdinalIgnoreCase)) return objects.FirstOrDefault(item => PathOf(item) == target);
            return objects.FirstOrDefault(item => string.Equals(item.name, target, StringComparison.OrdinalIgnoreCase));
        }
        private static string PathOf(GameObject gameObject)
        { List<string> parts = new List<string>(); for (Transform t = gameObject.transform; t != null; t = t.parent) parts.Add(t.name); parts.Reverse(); return string.Join("/", parts.ToArray()); }

        private static int ResolveLayer(string value)
        { int result; if (!int.TryParse(value, out result)) result = LayerMask.NameToLayer(value); if (result < 0 || result > 31) throw new ArgumentException("Unknown layer: " + value); return result; }
        private static string LayerName(int layer) { string name = LayerMask.LayerToName(layer); return string.IsNullOrEmpty(name) ? layer.ToString() : name; }
        private static int ResolveMask(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return Physics.DefaultRaycastLayers;
            int mask; if (int.TryParse(value, out mask)) return mask;
            mask = 0; foreach (string name in value.Split(',')) { int layer = LayerMask.NameToLayer(name.Trim()); if (layer < 0) throw new ArgumentException("Unknown layer: " + name); mask |= 1 << layer; } return mask;
        }
        private static QueryTriggerInteraction ParseQuery(string value)
        { QueryTriggerInteraction result; return Enum.TryParse(value ?? "UseGlobal", true, out result) ? result : QueryTriggerInteraction.UseGlobal; }
        private static ForceMode ParseForceMode(string value)
        { ForceMode result; return Enum.TryParse(value ?? "Force", true, out result) ? result : ForceMode.Force; }
        private static PhysicMaterialCombine ParseCombine(string value)
        { PhysicMaterialCombine result; if (!Enum.TryParse(value, true, out result)) throw new ArgumentException("Unknown combine mode: " + value); return result; }

        private static float Radius(PhysicsArguments a) { return a.HasSize ? (a.SizeScalar > 0f ? a.SizeScalar : (a.Size != null && a.Size.Length > 0 ? a.Size[0] : 0.5f)) : 0.5f; }
        private static Vector2 Size2(PhysicsArguments a) { return a.Size != null && a.Size.Length >= 2 ? new Vector2(a.Size[0], a.Size[1]) : Vector2.one * Radius(a); }
        private static Vector3 Size3(PhysicsArguments a) { return a.Size != null && a.Size.Length >= 3 ? new Vector3(a.Size[0], a.Size[1], a.Size[2]) : Vector3.one * Radius(a); }
        private static Vector2 Vector2Arg(float[] values, string name) { if (values == null || values.Length < 2) throw new ArgumentException(name + " requires [x,y]."); return new Vector2(values[0], values[1]); }
        private static Vector3 Vector3Arg(float[] values, string name) { if (values == null || values.Length < 3) throw new ArgumentException(name + " requires [x,y,z]."); return new Vector3(values[0], values[1], values[2]); }

        private static string NormalizeAssetPath(string path, string extension, string name)
        {
            string result = string.IsNullOrWhiteSpace(path) ? "Assets/Physics/" + (string.IsNullOrWhiteSpace(name) ? "PhysicsMaterial" : name) : path.Replace('\\', '/');
            if (!result.StartsWith("Assets/", StringComparison.Ordinal) || result.Contains("../")) throw new ArgumentException("Physics material path must stay under Assets/.");
            if (!result.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) result += extension; return result;
        }
        private static void EnsureFolder(string path) { if (string.IsNullOrEmpty(path) || path == "Assets" || AssetDatabase.IsValidFolder(path)) return; string parent = Path.GetDirectoryName(path).Replace('\\', '/'); EnsureFolder(parent); AssetDatabase.CreateFolder(parent, Path.GetFileName(path)); }
        private static void Save(UnityEngine.Object target) { EditorUtility.SetDirty(target); AssetDatabase.SaveAssets(); }

        private static void ApplyPatches(UnityEngine.Object target, SerializedPatch[] patches)
        {
            if (target == null) return; SerializedObject serialized = new SerializedObject(target);
            foreach (SerializedPatch patch in patches ?? new SerializedPatch[0])
            {
                SerializedProperty property = serialized.FindProperty(patch.Path) ?? serialized.FindProperty("m_" + patch.Path);
                if (property == null) continue;
                if (patch.Kind == "bool" && property.propertyType == SerializedPropertyType.Boolean) property.boolValue = patch.BoolValue;
                else if (patch.Kind == "int" && property.propertyType == SerializedPropertyType.Integer) property.intValue = patch.IntValue;
                else if ((patch.Kind == "float" || patch.Kind == "int") && property.propertyType == SerializedPropertyType.Float) property.floatValue = Number(patch);
                else if (patch.Kind == "string" && property.propertyType == SerializedPropertyType.String) property.stringValue = patch.StringValue;
                else if (patch.Kind == "vector" || patch.Kind == "vector2" || patch.Kind == "vector3")
                {
                    if (property.propertyType == SerializedPropertyType.Vector2) property.vector2Value = ToVector2(patch);
                    else if (property.propertyType == SerializedPropertyType.Vector3) property.vector3Value = ToVector3(patch);
                }
            }
            serialized.ApplyModifiedProperties();
        }

        private static string NormalizeKey(string value) { return (value ?? string.Empty).Replace("_", string.Empty).Replace("-", string.Empty).ToLowerInvariant(); }
        private static float Number(SerializedPatch p) { return p.Kind == "int" ? p.IntValue : p.FloatValue; }
        private static int Integer(SerializedPatch p) { return p.Kind == "float" ? Mathf.RoundToInt(p.FloatValue) : p.IntValue; }
        private static bool Boolean(SerializedPatch p) { return p.BoolValue; }
        private static Vector2 ToVector2(SerializedPatch p) { float[] v = p.VectorValue ?? new float[0]; return new Vector2(v.Length > 0 ? v[0] : 0f, v.Length > 1 ? v[1] : 0f); }
        private static Vector3 ToVector3(SerializedPatch p) { float[] v = p.VectorValue ?? new float[0]; return new Vector3(v.Length > 0 ? v[0] : 0f, v.Length > 1 ? v[1] : 0f, v.Length > 2 ? v[2] : 0f); }
        private static float[] ToArray(Vector2 value) { return new[] { value.x, value.y }; }
        private static float[] ToArray(Vector3 value) { return new[] { value.x, value.y, value.z }; }

        private static PhysicsResult Ok(string message, PhysicsData data = null) { return new PhysicsResult { Success = true, Message = message, Data = data }; }
        private static PhysicsResult Fail(string message) { return new PhysicsResult { Success = false, Message = message }; }

        [Serializable] private sealed class PhysicsArguments
        {
            public string Action, Dimension, LayerA, LayerB, Name, Path, FrictionCombine, BounceCombine, MaterialPath;
            public string Target, ColliderType, SearchMethod, JointType, ConnectedBody, LayerMask, QueryTriggerInteraction, Shape, ForceMode, ForceType;
            public bool Collide, HasCollide;
            public float DynamicFriction, StaticFriction, Bounciness, Friction, MaxDistance, Height, Angle, ExplosionRadius, ExplosionForce, UpwardsModifier, StepSize, SizeScalar;
            public bool HasDynamicFriction, HasStaticFriction, HasBounciness, HasFriction, HasMaxDistance, HasHeight, HasAngle, HasExplosionRadius, HasExplosionForce, HasUpwardsModifier, HasStepSize, HasSize;
            public int CapsuleDirection, Steps, PageSize, Cursor, ComponentIndex;
            public bool HasCapsuleDirection, HasSteps, HasPageSize, HasCursor, HasComponentIndex;
            public float[] Origin, Direction, Position, Size, Start, End, Point1, Point2, Force, Torque, ExplosionPosition;
            public bool HasOrigin, HasDirection, HasPosition, HasStart, HasEnd, HasPoint1, HasPoint2, HasForce, HasTorque, HasExplosionPosition;
            public SerializedPatch[] Settings, Properties, Motor, Limits, Spring, Drive;
        }
        [Serializable] private sealed class SerializedPatch { public string Path, Kind, StringValue; public bool BoolValue; public int IntValue; public float FloatValue; public float[] VectorValue; }
        [Serializable] private sealed class PhysicsResult { public bool Success; public string Message; public PhysicsData Data; }
        [Serializable] private sealed class PhysicsData
        {
            public string UnityVersion, Dimension, Path, Target, ComponentType, NextCursor;
            public bool Supports3D, Supports2D, AutoSimulation3D, AutoSimulation2D, HasMore;
            public int InstanceId, Count, Total, Steps; public float StepSize;
            public PhysicsSettingsRecord Settings; public CollisionPairRecord[] CollisionPairs;
            public HitRecord[] Hits; public ColliderRecord[] Colliders; public ValidationRecord[] Validation;
            public RigidbodyRecord Rigidbody;
        }
        [Serializable] private sealed class PhysicsSettingsRecord
        {
            public float[] Gravity; public float DefaultContactOffset, SleepThreshold, BounceThreshold;
            public bool QueriesHitTriggers, QueriesHitBackfaces, AutoSimulation;
            public int VelocityIterations, PositionIterations, DefaultSolverIterations, DefaultSolverVelocityIterations;
        }
        [Serializable] private sealed class CollisionPairRecord { public string LayerA, LayerB; public int LayerAIndex, LayerBIndex; public bool Collide; }
        [Serializable] private sealed class HitRecord { public string Target, ColliderType; public int InstanceId; public float[] Point, Normal; public float Distance; }
        [Serializable] private sealed class ColliderRecord { public string Target, ColliderType; public int InstanceId; public bool IsTrigger; }
        [Serializable] private sealed class ValidationRecord { public string Target, Severity, Message; public int InstanceId; }
        [Serializable] private sealed class RigidbodyRecord
        {
            public string Type; public float Mass, Drag, AngularDrag, GravityScale, AngularVelocity;
            public bool UseGravity, IsKinematic; public float[] Velocity, AngularVelocityVector;
        }
    }
}
#endif
