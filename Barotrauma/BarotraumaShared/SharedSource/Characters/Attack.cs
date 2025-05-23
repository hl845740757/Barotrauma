﻿using Barotrauma.Items.Components;
using Microsoft.Xna.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using Barotrauma.Extensions;

namespace Barotrauma
{
    public enum HitDetection
    {
        Distance,
        Contact,
        None
    }

    public enum AttackContext
    {
        Any,
        Water,
        Ground,
        Inside,
        Outside,
        NotDefined
    }

    [Flags]
    public enum AttackTarget
    {
        Any = 0,
        /// <summary>
        /// Characters only
        /// </summary>
        Character = 1,
        /// <summary>
        /// Structures and hulls, but also items (for backwards support)!
        /// </summary>
        Structure = 2,
        /// <summary>
        /// Items only
        /// </summary>
        Item = 4
    }

    public enum AIBehaviorAfterAttack
    {
        FallBack,
        FallBackUntilCanAttack,
        PursueIfCanAttack,
        Pursue,
        Eat,
        FollowThrough,
        FollowThroughWithoutObstacleAvoidance,
        FollowThroughUntilCanAttack,
        IdleUntilCanAttack,
        Reverse,
        ReverseUntilCanAttack
    }

    struct AttackResult
    {
        public float Damage
        {
            get;
            private set;
        }
        public readonly List<Affliction> Afflictions;

        public readonly Limb HitLimb;

        public readonly List<DamageModifier> AppliedDamageModifiers;

        public AttackResult(List<Affliction> afflictions, Limb hitLimb, List<DamageModifier> appliedDamageModifiers = null)
        {
            HitLimb = hitLimb;
            Afflictions = new List<Affliction>();

            foreach (Affliction affliction in afflictions)
            {
                Afflictions.Add(affliction.Prefab.Instantiate(affliction.Strength, affliction.Source));
            }
            AppliedDamageModifiers = appliedDamageModifiers;
            Damage = Afflictions.Sum(a => a.GetVitalityDecrease(null));
        }

        public AttackResult(float damage, List<DamageModifier> appliedDamageModifiers = null)
        {
            Damage = damage;
            HitLimb = null;
            Afflictions = null;
            AppliedDamageModifiers = appliedDamageModifiers;
        }
    }

    /// <summary>
    /// Attacks are used to deal damage to characters, structures and items.
    /// They can be defined in the weapon components of the items or the limb definitions of the characters.
    /// The limb attacks can also be used by the player, when they control a monster or have some appendage, like a husk stinger.
    /// </summary>
    partial class Attack : ISerializableEntity
    {
        [Serialize(AttackContext.Any, IsPropertySaveable.Yes, description: "The attack will be used only in this context."), Editable]
        public AttackContext Context { get; private set; }

        [Serialize(AttackTarget.Any, IsPropertySaveable.Yes, description: "Does the attack target only specific targets?"), Editable]
        public AttackTarget TargetType { get; private set; }

        [Serialize(LimbType.None, IsPropertySaveable.Yes, description: "To which limb is the attack aimed at? If not defined or set to none, the closest limb is used (default)."), Editable]
        public LimbType TargetLimbType { get; private set; }

        [Serialize(HitDetection.Distance, IsPropertySaveable.Yes, description: "Collision detection is more accurate, but it only affects targets that are in contact with the limb."), Editable]
        public HitDetection HitDetectionType { get; private set; }

        [Serialize(AIBehaviorAfterAttack.FallBack, IsPropertySaveable.Yes, description: "The preferred AI behavior after the attack."), Editable]
        public AIBehaviorAfterAttack AfterAttack { get; set; }

        [Serialize(0f, IsPropertySaveable.Yes, description: "A delay before reacting after performing an attack."), Editable]
        public float AfterAttackDelay { get; set; }

        [Serialize(AIBehaviorAfterAttack.FallBack, IsPropertySaveable.Yes, 
            description: "Secondary AI behavior after the attack. The character first executes the AfterAttack behavior, then after AfterAttackSecondaryDelay passes, switches to this one. Ignored if AfterAttackSecondaryDelay is 0 or less."), Editable]
        public AIBehaviorAfterAttack AfterAttackSecondary { get; set; }

        [Serialize(0.0f, IsPropertySaveable.Yes, description: "How long the character executes the AfterAttack before switching to AfterAttackSecondary. The secondary behavior is ignored if this value is 0 or less."), Editable]
        public float AfterAttackSecondaryDelay { get; set; }

        [Serialize(false, IsPropertySaveable.Yes, description: "Should the AI try to turn around when aiming with this attack?"), Editable]
        public bool Reverse { get; private set; }

        [Serialize(true, IsPropertySaveable.Yes, description: "Should the rope attached to this limb snap upon choosing a new attack?"), Editable]
        public bool SnapRopeOnNewAttack { get; private set; }

        [Serialize(false, IsPropertySaveable.Yes, description: "Should the AI try to steer away from the target when aiming with this attack? Best combined with PassiveAggressive behavior."), Editable]
        public bool Retreat { get; private set; }

        private float _range;
        [Serialize(0.0f, IsPropertySaveable.Yes, description: "The min distance from the attack limb to the target before the AI tries to attack."), Editable(MinValueFloat = 0.0f, MaxValueFloat = 10000.0f)]
        public float Range
        {
            get => _range * RangeMultiplier;
            set => _range = value;
        }

        private float _damageRange;
        [Serialize(0.0f, IsPropertySaveable.Yes, description: "The min distance from the attack limb to the target to do damage. In distance-based hit detection, the hit will be registered as soon as the target is within the damage range, unless the attack duration has expired."), Editable(MinValueFloat = 0.0f, MaxValueFloat = 10000.0f)]
        public float DamageRange
        {
            get => _damageRange * RangeMultiplier;
            set => _damageRange = value;
        }

        [Serialize(0.0f, IsPropertySaveable.Yes, description: "Used by enemy AI to determine the minimum range required for the attack to hit."), Editable(MinValueFloat = 0.0f, MaxValueFloat = 10000.0f)]
        public float MinRange { get; private set; }

        [Serialize(0.25f, IsPropertySaveable.Yes, description: "An approximation of the attack duration. Effectively defines the time window in which the hit can be registered. If set to too low value, it's possible that the attack won't hit the target in time."), Editable(MinValueFloat = 0.0f, MaxValueFloat = 10.0f, DecimalCount = 2)]
        public float Duration { get; private set; }

        [Serialize(5f, IsPropertySaveable.Yes, description: "How long the AI must wait before it can use this attack again."), Editable(MinValueFloat = 0.0f, MaxValueFloat = 100.0f, DecimalCount = 2)]
        public float CoolDown { get; set; } = 5;


        [Serialize(0f, IsPropertySaveable.Yes, description: "When the attack cooldown is running and when there are other valid attacks possible for the character to use, the secondary cooldown is used instead of the regular cooldown. Does not have an effect, if set to 0 or less than the regular cooldown value."), Editable(MinValueFloat = 0.0f, MaxValueFloat = 100.0f, DecimalCount = 2)]
        public float SecondaryCoolDown { get; set; } = 0;

        [Serialize(0f, IsPropertySaveable.Yes, description: "A random factor applied to all cooldowns. Example: 0.1 -> adds a random value between -10% and 10% of the cooldown. Min 0 (default), Max 1 (could disable or double the cooldown in extreme cases)."), Editable(MinValueFloat = 0, MaxValueFloat = 1, DecimalCount = 2)]
        public float CoolDownRandomFactor { get; private set; } = 0;

        [Serialize(false, IsPropertySaveable.Yes, description: "When set to true, causes the enemy AI to use the fast movement animations when the attack is on cooldown."), Editable]
        public bool FullSpeedAfterAttack { get; private set; }

        private float _structureDamage;
        [Serialize(0.0f, IsPropertySaveable.Yes, description: "How much damage the attack does to submarine walls."), Editable(MinValueFloat = 0.0f, MaxValueFloat = 10000.0f)]
        public float StructureDamage
        {
            get => _structureDamage * DamageMultiplier;
            set => _structureDamage = value;
        }

        [Serialize(false, IsPropertySaveable.Yes, description: "If the attack causes an explosion of wall damage shrapnel, should some of the shrapnel be launched as projectiles that can go through walls?"), Editable]
        public bool CreateWallDamageProjectiles { get; private set; }

        [Serialize(true, IsPropertySaveable.Yes, description: "Whether or not damaging structures with the attack causes damage particles to emit."), Editable]
        public bool EmitStructureDamageParticles { get; private set; }

        private float _itemDamage;
        [Serialize(0.0f, IsPropertySaveable.Yes, description: "How much damage the attack does to items."), Editable(MinValueFloat = 0.0f, MaxValueFloat = 1000.0f)]
        public float ItemDamage
        {
            get =>_itemDamage * DamageMultiplier;
            set => _itemDamage = value;
        }

        [Serialize(0.0f, IsPropertySaveable.Yes, description: "Percentage of damage mitigation ignored when hitting armored body parts (deflecting limbs)."), Editable(MinValueFloat = 0.0f, MaxValueFloat = 1f)]
        public float Penetration { get; private set; }

        /// <summary>
        /// Used for multiplying all the damage.
        /// </summary>
        public float DamageMultiplier
        {
            get => _damageMultiplier ?? initialDamageMultiplier;
            set
            {
                if (!_damageMultiplier.HasValue)
                {
                    SetInitialDamageMultiplier(value);
                }
                _damageMultiplier = value;
            }
        }
        private float? _damageMultiplier;
        private float initialDamageMultiplier = 1.0f;
        public void ResetDamageMultiplier() => _damageMultiplier = initialDamageMultiplier;
        public void SetInitialDamageMultiplier(float value) => initialDamageMultiplier = value;

        /// <summary>
        /// Used for multiplying all the ranges.
        /// </summary>
        public float RangeMultiplier { get; set; } = 1;

        /// <summary>
        /// Used for multiplying the physics forces.
        /// </summary>
        public float ImpactMultiplier { get; set; } = 1;

        [Serialize(0.0f, IsPropertySaveable.Yes, description: "How much damage the attack does to level walls."), Editable(MinValueFloat = 0.0f, MaxValueFloat = 1000.0f)]
        public float LevelWallDamage
        {
            get => _levelWallDamage * DamageMultiplier;
            set => _levelWallDamage = value;
        }
        private float _levelWallDamage;

        [Serialize(false, IsPropertySaveable.Yes, description: "Sets whether or not the attack is ranged or not."), Editable]
        public bool Ranged { get; set; }

        [Serialize(false, IsPropertySaveable.Yes, description:"When enabled the attack will not be launched when there's a friendly character in the way. Only affects ranged attacks."), Editable]
        public bool AvoidFriendlyFire { get; set; }

        [Serialize(20f, IsPropertySaveable.Yes, description: "Used by enemy AI to determine how accurately the attack needs to be aimed for the attack to trigger. Only affects ranged attacks."), Editable]
        public float RequiredAngle { get; set; }

        [Serialize(0f, IsPropertySaveable.Yes, description: "By default uses the same value as RequiredAngle. Use if you want to allow selecting the attack but not shooting until the angle is smaller. Only affects ranged attacks."), Editable]
        public float RequiredAngleToShoot { get; set; }

        [Serialize(0f, IsPropertySaveable.Yes, description: "How much the attack limb is rotated towards the target. Default 0 = no rotation. Only affects ranged attacks."), Editable]
        public float AimRotationTorque { get; set; }

        [Serialize(-1, IsPropertySaveable.Yes, description: "Reference to the limb we apply the aim rotation to. By default same as the attack limb. Only affects ranged attacks."), Editable]
        public int RotationLimbIndex { get; set; }

        [Serialize(0f, IsPropertySaveable.Yes, description:"How much the held weapon is swayed back and forth while aiming. Only affects monsters using ranged weapons (items). Default 0 means the weapon is not swayed at all."), Editable]
        public float SwayAmount { get; set; }

        [Serialize(5f, IsPropertySaveable.Yes, description: "How fast the held weapon is swayed back and forth while aiming. Only affects monsters using ranged weapons (items)."), Editable]
        public float SwayFrequency { get; set; }

        [Serialize(0.0f, IsPropertySaveable.No, description: "Legacy functionality. Behaves otherwise the same as stuns defined as afflictions, but explosions only apply the stun once instead of dividing it between the limbs.")]
        public float Stun { get; set; }

        [Serialize(false, IsPropertySaveable.Yes, description: "Can damage only Humans."), Editable]
        public bool OnlyHumans { get; set; }

        [Serialize("", IsPropertySaveable.Yes, description: "List of limb indices to apply the force into."), Editable]
        public string ApplyForceOnLimbs
        {
            get
            {
                return string.Join(", ", ForceOnLimbIndices);
            }
            set
            {
                ForceOnLimbIndices.Clear();
                if (string.IsNullOrEmpty(value)) { return; }
                foreach (string limbIndexStr in value.Split(','))
                {
                    if (int.TryParse(limbIndexStr.Trim(), out int limbIndex))
                    {
                        ForceOnLimbIndices.Add(limbIndex);
                    }
                }
            }
        }

        [Serialize(0.0f, IsPropertySaveable.Yes, description: "Applied to the attacking limb (or limbs defined using ApplyForceOnLimbs). The direction of the force is towards the target that's being attacked."), Editable(MinValueFloat = -1000.0f, MaxValueFloat = 1000.0f)]
        public float Force { get; private set; }

        [Serialize("0.0, 0.0", IsPropertySaveable.Yes, description: "Applied to the main limb. In world space coordinates(i.e. 0, 1 pushes the character upwards a bit). The attacker's facing direction is taken into account."), Editable]
        public Vector2 RootForceWorldStart { get; private set; }

        [Serialize("0.0, 0.0", IsPropertySaveable.Yes, description: "Applied to the main limb. In world space coordinates(i.e. 0, 1 pushes the character upwards a bit). The attacker's facing direction is taken into account."), Editable]
        public Vector2 RootForceWorldMiddle { get; private set; }

        [Serialize("0.0, 0.0", IsPropertySaveable.Yes, description: "Applied to the main limb. In world space coordinates(i.e. 0, 1 pushes the character upwards a bit). The attacker's facing direction is taken into account."), Editable]
        public Vector2 RootForceWorldEnd { get; private set; }

        public bool HasRootForce => RootForceWorldStart != Vector2.Zero || RootForceWorldMiddle != Vector2.Zero || RootForceWorldEnd != Vector2.Zero;

        [Serialize(TransitionMode.Linear, IsPropertySaveable.Yes, description:"Applied to the main limb. The transition smoothing of the applied force."), Editable]
        public TransitionMode RootTransitionEasing { get; private set; }

        [Serialize(0.0f, IsPropertySaveable.Yes, description: "Applied to the attacking limb (or limbs defined using ApplyForceOnLimbs)"), Editable(MinValueFloat = -10000.0f, MaxValueFloat = 10000.0f)]
        public float Torque { get; private set; }

        [Serialize(false, IsPropertySaveable.Yes, description: "Only apply the force once during the attacks lifetime."), Editable]
        public bool ApplyForcesOnlyOnce { get; private set; }

        [Serialize(0.0f, IsPropertySaveable.Yes, description: "Applied to the target the attack hits. The direction of the impulse is from this limb towards the target (use negative values to pull the target closer)."), Editable(MinValueFloat = -1000.0f, MaxValueFloat = 1000.0f)]
        public float TargetImpulse { get; private set; }

        [Serialize("0.0, 0.0", IsPropertySaveable.Yes, description: "Applied to the target, in world space coordinates(i.e. 0, -1 pushes the target downwards). The attacker's facing direction is taken into account."), Editable]
        public Vector2 TargetImpulseWorld { get; private set; }

        [Serialize(0.0f, IsPropertySaveable.Yes, description: "Applied to the target the attack hits. The direction of the force is from this limb towards the target (use negative values to pull the target closer)."), Editable(-1000.0f, 1000.0f)]
        public float TargetForce { get; private set; }

        [Serialize("0.0, 0.0", IsPropertySaveable.Yes, description: "Applied to the target, in world space coordinates(i.e. 0, -1 pushes the target downwards). The attacker's facing direction is taken into account."), Editable]
        public Vector2 TargetForceWorld { get; private set; }

        [Serialize(1.0f, IsPropertySaveable.Yes, description: "Affects the strength of the impact effects the limb causes when it hits a submarine."), Editable(MinValueFloat = 0.0f, MaxValueFloat = 100.0f)]
        public float SubmarineImpactMultiplier { get; private set; }

        [Serialize(0.0f, IsPropertySaveable.Yes, description: "How likely the attack causes target limbs to be severed."), Editable(MinValueFloat = 0.0f, MaxValueFloat = 10.0f)]
        public float SeverLimbsProbability { get; set; }

        // TODO: disabled because not synced
        //[Serialize(0.0f, IsPropertySaveable.Yes), Editable(MinValueFloat = 0.0f, MaxValueFloat = 1.0f)]
        //public float StickChance { get; set; }
        public float StickChance => 0f;

        [Serialize(0.0f, IsPropertySaveable.Yes, description: "Used by enemy AI to determine the priority when selecting attacks. When random attacks are disabled on the character it is multiplied with distance to determine the which attack to use. Only attacks that are currently valid are taken into consideration when making the decision."), Editable(MinValueFloat = 0.0f, MaxValueFloat = 1.0f)]
        public float Priority { get; private set; }

        [Serialize(false, IsPropertySaveable.Yes, description: "Triggers the 'blink' animation on the attacking limbs when the attack executes. Used e.g. by abyss monsters to make their jaws close when attacking."), Editable]
        public bool Blink { get; private set; }

        public IEnumerable<StatusEffect> StatusEffects
        {
            get { return statusEffects; }
        }

        public string Name => "Attack";

        public Dictionary<Identifier, SerializableProperty> SerializableProperties
        {
            get;
            private set;
        } = new Dictionary<Identifier, SerializableProperty>();

        //the indices of the limbs Force is applied on
        //(if none, force is applied only to the limb the attack is attached to)
        public readonly List<int> ForceOnLimbIndices = new List<int>();

        public readonly Dictionary<Affliction, XElement> Afflictions = new Dictionary<Affliction, XElement>();

        /// <summary>
        /// Only affects ai decision making. All the conditionals has to be met in order to select the attack. TODO: allow to define conditionals using any (implemented in StatusEffect -> move from there to PropertyConditional?)
        /// </summary>
        public List<PropertyConditional> Conditionals { get; private set; } = new List<PropertyConditional>();

        /// <summary>
        /// StatusEffects to apply when the attack triggers.
        /// StatusEffect types of 'OnUse' are executed always, 'OnFailure' only when the attack doesn't deal damage and 'OnSuccess' executes when some damage is dealt.
        /// </summary>
        private readonly List<StatusEffect> statusEffects = new List<StatusEffect>();

        public void SetUser(Character user)
        {
            if (statusEffects == null) { return; }
            foreach (StatusEffect statusEffect in statusEffects)
            {
                statusEffect.SetUser(user);
            }
        }

        // used for talents/ability conditions
        public Item SourceItem { get; set; }

        public List<Affliction> GetMultipliedAfflictions(float multiplier)
        {
            List<Affliction> multipliedAfflictions = new List<Affliction>();
            foreach (Affliction affliction in Afflictions.Keys)
            {
                multipliedAfflictions.Add(affliction.CreateMultiplied(multiplier, affliction));
            }
            return multipliedAfflictions;
        }

        public float GetStructureDamage(float deltaTime)
        {
            return (Duration == 0.0f) ? StructureDamage : StructureDamage * deltaTime;
        }

        public float GetLevelWallDamage(float deltaTime)
        {
            return (Duration == 0.0f) ? LevelWallDamage : LevelWallDamage * deltaTime;
        }

        public float GetItemDamage(float deltaTime, float multiplier = 1)
        {
            float dmg = ItemDamage * multiplier;
            return (Duration == 0.0f) ? dmg : dmg * deltaTime;
        }

        public float GetTotalDamage(bool includeStructureDamage = false)
        {
            float totalDamage = includeStructureDamage ? StructureDamage : 0.0f;
            foreach (Affliction affliction in Afflictions.Keys)
            {
                totalDamage += affliction.GetVitalityDecrease(null);
            }
            return totalDamage * DamageMultiplier;
        }

        public Attack(float damage, float bleedingDamage, float burnDamage, float structureDamage, float itemDamage, float range = 0.0f)
        {
            if (damage > 0.0f) { Afflictions.Add(AfflictionPrefab.InternalDamage.Instantiate(damage), null); }
            if (bleedingDamage > 0.0f) { Afflictions.Add(AfflictionPrefab.Bleeding.Instantiate(bleedingDamage), null); }
            if (burnDamage > 0.0f) { Afflictions.Add(AfflictionPrefab.Burn.Instantiate(burnDamage), null); }

            Range = range;
            DamageRange = range;
            StructureDamage = LevelWallDamage = structureDamage;
            ItemDamage = itemDamage;
        }

        public Attack(ContentXElement element, string parentDebugName, Item sourceItem) : this(element, parentDebugName)
        {
            SourceItem = sourceItem;
        }
        
        public Attack(ContentXElement element, string parentDebugName)
        {
            Deserialize(element, parentDebugName);

            if (element.GetAttribute("damage") != null ||
                element.GetAttribute("bluntdamage") != null ||
                element.GetAttribute("burndamage") != null ||
                element.GetAttribute("bleedingdamage") != null)
            {
                DebugConsole.ThrowError("Error in Attack (" + parentDebugName + ") - Define damage as afflictions instead of using the damage attribute (e.g. <Affliction identifier=\"internaldamage\" strength=\"10\" />).",
                    contentPackage: element.ContentPackage);
            }

            //if level wall damage is not defined, default to the structure damage
            if (element.GetAttribute("LevelWallDamage") == null)
            {
                LevelWallDamage = _structureDamage;
            }

            InitProjSpecific(element);

            foreach (var subElement in element.Elements())
            {
                switch (subElement.Name.ToString().ToLowerInvariant())
                {
                    case "statuseffect":
                        statusEffects.Add(StatusEffect.Load(subElement, parentDebugName));
                        break;
                    case "affliction":
                        AfflictionPrefab afflictionPrefab;
                        if (subElement.GetAttribute("name") != null)
                        {
                            DebugConsole.ThrowError("Error in Attack (" + parentDebugName + ") - define afflictions using identifiers instead of names.",
                                    contentPackage: element.ContentPackage);
                            string afflictionName = subElement.GetAttributeString("name", "").ToLowerInvariant();
                            afflictionPrefab = AfflictionPrefab.List.FirstOrDefault(ap => ap.Name.Equals(afflictionName, System.StringComparison.OrdinalIgnoreCase));
                            if (afflictionPrefab == null)
                            {
                                DebugConsole.ThrowError("Error in Attack (" + parentDebugName + ") - Affliction prefab \"" + afflictionName + "\" not found.",
                                    contentPackage: element.ContentPackage);
                                continue;
                            }
                        }
                        else
                        {
                            Identifier afflictionIdentifier = subElement.GetAttributeIdentifier("identifier", "");
                            if (!AfflictionPrefab.Prefabs.TryGet(afflictionIdentifier, out afflictionPrefab))
                            {
                                DebugConsole.ThrowError("Error in Attack (" + parentDebugName + ") - Affliction prefab \"" + afflictionIdentifier + "\" not found.",
                                    contentPackage: element.ContentPackage);
                                continue;
                            }
                        }
                        break;
                    case "conditional":
                        Conditionals.AddRange(PropertyConditional.FromXElement(subElement));
                        break;
                }
            }

            if (SecondaryCoolDown > CoolDown)
            {
                DebugConsole.AddWarning($"Potentially misconfigured attack in {parentDebugName}. Secondary cooldown should not be longer than the primary cooldown.",
                    contentPackage: element.ContentPackage);
            }
        }
        partial void InitProjSpecific(ContentXElement element);

        public void ReloadAfflictions(ContentXElement element, string parentDebugName)
        {
            Afflictions.Clear();
            foreach (var subElement in element.GetChildElements("affliction"))
            {
                Affliction affliction;
                Identifier afflictionIdentifier = subElement.GetAttributeIdentifier("identifier", "");
                if (!AfflictionPrefab.Prefabs.TryGet(afflictionIdentifier, out AfflictionPrefab afflictionPrefab))
                {
                    DebugConsole.ThrowError($"Error in an Attack defined in \"{parentDebugName}\" - could not find an affliction with the identifier \"{afflictionIdentifier}\".",
                        contentPackage: element.ContentPackage);
                    continue;
                }
                affliction = afflictionPrefab.Instantiate(0.0f);
                affliction.Deserialize(subElement);
                // add the affliction anyway, so that it can be shown in the editor.
                Afflictions.Add(affliction, subElement);
            }
        }

        public void Serialize(ContentXElement element)
        {
            SerializableProperty.SerializeProperties(this, element, true);
            foreach (var affliction in Afflictions)
            {
                if (affliction.Value != null)
                {
                    affliction.Key.Serialize(affliction.Value);
                }
            }
        }

        public void Deserialize(ContentXElement element, string parentDebugName)
        {
            SerializableProperties = SerializableProperty.DeserializeProperties(this, element);
            ReloadAfflictions(element, parentDebugName);
        }
        
        public AttackResult DoDamage(Character attacker, IDamageable target, Vector2 worldPosition, float deltaTime, bool playSound = true, PhysicsBody sourceBody = null, Limb sourceLimb = null)
        {
            Character targetCharacter = target as Character;
            if (OnlyHumans)
            {
                if (targetCharacter != null && !targetCharacter.IsHuman)
                {
                    return new AttackResult();
                }
            }

            SetUser(attacker);

            DamageParticles(deltaTime, worldPosition);

            Vector2 impulseDirection = GetImpulseDirection(target as ISpatialEntity, worldPosition, SourceItem);
            var attackResult = target?.AddDamage(attacker, worldPosition, this, impulseDirection, deltaTime, playSound) ?? new AttackResult();
            var conditionalEffectType = attackResult.Damage > 0.0f ? ActionType.OnSuccess : ActionType.OnFailure;
            var additionalEffectType = ActionType.OnUse;
            if (targetCharacter != null && targetCharacter.IsDead)
            {
                additionalEffectType = ActionType.OnEating;
            }

            foreach (StatusEffect effect in statusEffects)
            {
                effect.sourceBody = sourceBody;
                if (effect.HasTargetType(StatusEffect.TargetType.This) || effect.HasTargetType(StatusEffect.TargetType.Character))
                {
                    var t = sourceLimb ?? attacker as ISerializableEntity;
                    if (additionalEffectType != ActionType.OnEating)
                    {
                        effect.Apply(conditionalEffectType, deltaTime, attacker, t, worldPosition);
                    }
                    effect.Apply(additionalEffectType, deltaTime, attacker, t, worldPosition);
                }
                if (effect.HasTargetType(StatusEffect.TargetType.Parent))
                {
                    if (additionalEffectType != ActionType.OnEating)
                    {
                        effect.Apply(conditionalEffectType, deltaTime, attacker, attacker);
                    }
                    effect.Apply(additionalEffectType, deltaTime, attacker, attacker);
                }
                if (targetCharacter != null)
                {
                    if (effect.HasTargetType(StatusEffect.TargetType.Limb))
                    {
                        if (additionalEffectType != ActionType.OnEating)
                        {
                            effect.Apply(conditionalEffectType, deltaTime, targetCharacter, attackResult.HitLimb);
                        }
                        effect.Apply(additionalEffectType, deltaTime, targetCharacter, attackResult.HitLimb);
                    }                    
                    if (effect.HasTargetType(StatusEffect.TargetType.AllLimbs))
                    {
                        var targets = targetCharacter.AnimController.Limbs;
                        if (additionalEffectType != ActionType.OnEating)
                        {
                            effect.Apply(conditionalEffectType, deltaTime, targetCharacter, targets);
                        }
                        effect.Apply(additionalEffectType, deltaTime, targetCharacter, targets);
                    }
                }
                if (target is Entity targetEntity)
                {
                    if (effect.HasTargetType(StatusEffect.TargetType.NearbyItems) ||
                        effect.HasTargetType(StatusEffect.TargetType.NearbyCharacters))
                    {
                        targets.Clear();
                        effect.AddNearbyTargets(worldPosition, targets);
                        if (additionalEffectType != ActionType.OnEating)
                        {
                            effect.Apply(conditionalEffectType, deltaTime, targetEntity, targets);
                        }
                        effect.Apply(additionalEffectType, deltaTime, targetEntity, targets);
                    }
                    if (effect.HasTargetType(StatusEffect.TargetType.UseTarget))
                    {
                        if (additionalEffectType != ActionType.OnEating)
                        {
                            effect.Apply(conditionalEffectType, deltaTime, targetEntity, targetEntity as ISerializableEntity, worldPosition);
                        }
                        effect.Apply(additionalEffectType, deltaTime, targetEntity, targetEntity as ISerializableEntity, worldPosition);
                    }
                }
                if (effect.HasTargetType(StatusEffect.TargetType.Contained))
                {
                    targets.Clear();
                    targets.AddRange(attacker.Inventory.AllItems);
                    if (additionalEffectType != ActionType.OnEating)
                    {
                        effect.Apply(conditionalEffectType, deltaTime, attacker, targets);
                    }
                    effect.Apply(additionalEffectType, deltaTime, attacker, targets);
                }
            }

            return attackResult;
        }

        readonly List<ISerializableEntity> targets = new List<ISerializableEntity>();
        public AttackResult DoDamageToLimb(Character attacker, Limb targetLimb, Vector2 worldPosition, float deltaTime, bool playSound = true, PhysicsBody sourceBody = null, Limb sourceLimb = null)
        {
            if (targetLimb == null)
            {
                return new AttackResult();
            }

            if (OnlyHumans)
            {
                if (targetLimb.character != null && !targetLimb.character.IsHuman)
                {
                    return new AttackResult();
                }
            }

            SetUser(attacker);

            DamageParticles(deltaTime, worldPosition);

            float penetration = Penetration;

            RangedWeapon weapon = 
                SourceItem?.GetComponent<RangedWeapon>() ??
                SourceItem?.GetComponent<Projectile>()?.Launcher?.GetComponent<RangedWeapon>();
            float? penetrationValue = weapon?.Penetration;
            if (penetrationValue.HasValue)
            {
                penetration += penetrationValue.Value;
            }

            Vector2 impulseDirection = GetImpulseDirection(targetLimb, worldPosition, SourceItem);
            var attackResult = targetLimb.character.ApplyAttack(attacker, worldPosition, this, deltaTime, impulseDirection, playSound, targetLimb, penetration);
            var conditionalEffectType = attackResult.Damage > 0.0f ? ActionType.OnSuccess : ActionType.OnFailure;

            foreach (StatusEffect effect in statusEffects)
            {
                effect.sourceBody = sourceBody;
                if (effect.HasTargetType(StatusEffect.TargetType.This) || effect.HasTargetType(StatusEffect.TargetType.Character))
                {
                    effect.Apply(conditionalEffectType, deltaTime, attacker, sourceLimb ?? attacker as ISerializableEntity);
                    effect.Apply(ActionType.OnUse, deltaTime, attacker, sourceLimb ?? attacker as ISerializableEntity);
                }
                if (effect.HasTargetType(StatusEffect.TargetType.Parent))
                {
                    effect.Apply(conditionalEffectType, deltaTime, attacker, attacker);
                    effect.Apply(ActionType.OnUse, deltaTime, attacker, attacker);
                }
                if (effect.HasTargetType(StatusEffect.TargetType.UseTarget))
                {
                    effect.Apply(conditionalEffectType, deltaTime, targetLimb.character, targetLimb.character);
                    effect.Apply(ActionType.OnUse, deltaTime, targetLimb.character, targetLimb.character);
                }
                if (effect.HasTargetType(StatusEffect.TargetType.Limb))
                {
                    effect.Apply(conditionalEffectType, deltaTime, targetLimb.character, targetLimb);
                    effect.Apply(ActionType.OnUse, deltaTime, targetLimb.character, targetLimb);
                }
                if (effect.HasTargetType(StatusEffect.TargetType.AllLimbs))
                {
                    var targets = targetLimb.character.AnimController.Limbs;
                    effect.Apply(conditionalEffectType, deltaTime, targetLimb.character, targets);
                    effect.Apply(ActionType.OnUse, deltaTime, targetLimb.character, targets);
                }
                if (effect.HasTargetType(StatusEffect.TargetType.NearbyItems) ||
                    effect.HasTargetType(StatusEffect.TargetType.NearbyCharacters))
                {
                    targets.Clear();
                    effect.AddNearbyTargets(worldPosition, targets);                
                    effect.Apply(conditionalEffectType, deltaTime, targetLimb.character, targets);
                    effect.Apply(ActionType.OnUse, deltaTime, targetLimb.character, targets);
                }
                if (effect.HasTargetType(StatusEffect.TargetType.Contained))
                {
                    targets.Clear();
                    targets.AddRange(attacker.Inventory.AllItems);
                    effect.Apply(conditionalEffectType, deltaTime, attacker, targets);
                    effect.Apply(ActionType.OnUse, deltaTime, attacker, targets);
                }
            }

            return attackResult;
        }

        private Vector2 GetImpulseDirection(ISpatialEntity target, Vector2 sourceWorldPosition, Item sourceItem)
        {
            Vector2 impulseDirection = Vector2.Zero;
            if (target != null)
            {
                impulseDirection = target.WorldPosition - sourceWorldPosition;
            }

            if (sourceItem?.body != null && sourceItem.body.Enabled && sourceItem.body.LinearVelocity.LengthSquared() > 0.0f)
            {
                impulseDirection = sourceItem.body.LinearVelocity;
            }
            else
            {
                var projectileComponent = sourceItem?.GetComponent<Projectile>();
                if (projectileComponent != null)
                {
                    impulseDirection = new Vector2(MathF.Cos(SourceItem.Rotation), MathF.Sin(SourceItem.Rotation));
                }
            }

            if (impulseDirection.LengthSquared() > 0.0001f)
            {
                impulseDirection = Vector2.Normalize(impulseDirection);
            }
            return impulseDirection;
        }

        public float AttackTimer { get; private set; }
        public float CoolDownTimer { get; set; }
        public float CurrentRandomCoolDown { get; private set; }
        public float SecondaryCoolDownTimer { get; set; }
        public bool IsRunning { get; private set; }

        public float AfterAttackTimer { get; set; }

        public void UpdateCoolDown(float deltaTime)
        {
            CoolDownTimer -= deltaTime;
            SecondaryCoolDownTimer -= deltaTime;
            if (CoolDownTimer < 0) { CoolDownTimer = 0; }
            if (SecondaryCoolDownTimer < 0) { SecondaryCoolDownTimer = 0; }
        }

        public void UpdateAttackTimer(float deltaTime, Character character)
        {
            IsRunning = true;
            AttackTimer += deltaTime;
            if (AttackTimer >= Duration)
            {
                ResetAttackTimer();
                SetCoolDown(applyRandom: !character.IsPlayer);
            }
        }

        public void ResetAttackTimer()
        {
            AfterAttackTimer = 0;
            AttackTimer = 0;
            IsRunning = false;
        }

        public void SetCoolDown(bool applyRandom)
        {
            if (applyRandom)
            {
                float randomFraction = CoolDown * CoolDownRandomFactor;
                CurrentRandomCoolDown = MathHelper.Lerp(-randomFraction, randomFraction, Rand.Value());
                CoolDownTimer = CoolDown + CurrentRandomCoolDown;
                randomFraction = SecondaryCoolDown * CoolDownRandomFactor;
                SecondaryCoolDownTimer = SecondaryCoolDown + MathHelper.Lerp(-randomFraction, randomFraction, Rand.Value());
            }
            else
            {
                CoolDownTimer = CoolDown;
                SecondaryCoolDownTimer = SecondaryCoolDown;
                CurrentRandomCoolDown = 0;
            }
        }

        public void ResetCoolDown()
        {
            CoolDownTimer = 0;
            SecondaryCoolDownTimer = 0;
            CurrentRandomCoolDown = 0;
        }

        partial void DamageParticles(float deltaTime, Vector2 worldPosition);

        public bool IsValidContext(AttackContext context) => Context == context || Context == AttackContext.Any || Context == AttackContext.NotDefined;

        public bool IsValidContext(IEnumerable<AttackContext> contexts)
        {
            foreach (var context in contexts)
            {
                switch (context)
                {
                    case AttackContext.Ground:
                        if (Context == AttackContext.Water)
                        {
                            return false;
                        }
                        break;
                    case AttackContext.Water:
                        if (Context == AttackContext.Ground)
                        {
                            return false;
                        }
                        break;
                    case AttackContext.Inside:
                        if (Context == AttackContext.Outside)
                        {
                            return false;
                        }
                        break;
                    case AttackContext.Outside:
                        if (Context == AttackContext.Inside)
                        {
                            return false;
                        }
                        break;
                    default:
                        continue;
                }
            }
            return true;
        }

        public bool IsValidTarget(AttackTarget targetType) => TargetType == AttackTarget.Any || TargetType.HasAnyFlag(targetType);

        public bool IsValidTarget(Entity target)
        {
            return TargetType switch
            {
                AttackTarget.Character => target is Character,
                AttackTarget.Structure => target is Structure or Hull or Item, // Items are intentionally included for backwards-support.
                AttackTarget.Item => target is Item,
                _ => IsValidTarget(GetAttackTargetTypeFromEntity(target))
            };
        }
        
        private static AttackTarget GetAttackTargetTypeFromEntity(Entity entity)
        {
            return entity switch
            {
                Character => AttackTarget.Character,
                Item => AttackTarget.Item,
                Structure => AttackTarget.Structure,
                Hull => AttackTarget.Structure,
                _ => AttackTarget.Any
            };
        }

        public Vector2 CalculateAttackPhase(TransitionMode easing = TransitionMode.Linear)
        {
            float t = AttackTimer / Duration;
            return MathUtils.Bezier(RootForceWorldStart, RootForceWorldMiddle, RootForceWorldEnd, ToolBox.GetEasing(easing, t));
        }
    }
}
